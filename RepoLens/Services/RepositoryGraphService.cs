using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record RepositoryGraph(
    RepositoryIndex Repository,
    SymbolIndex Symbols,
    DependencyIndex Dependencies,
    string InputHash,
    bool CacheHit,
    int ProjectCacheHits = 0,
    int ProjectCacheMisses = 0);

internal sealed record RepositoryGraphCacheManifest
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string InputHash { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

internal sealed record ProjectGraphCacheEntry
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string ProjectPath { get; init; }
    public required string EvaluationInputHash { get; init; }
    public required string InputHash { get; init; }
    public required ProjectRecord Project { get; init; }
    public required SymbolIndex Symbols { get; init; }
    public required DependencyIndex Dependencies { get; init; }
}

internal sealed class RepositoryGraphService
{
    /// <summary>
    /// The repository root and its graph as one value, so publishing them is a single reference
    /// write. Two separate fields could be observed half-updated — a reader seeing the new root
    /// beside the previous graph, which is the one combination that is silently wrong rather than
    /// merely stale.
    /// </summary>
    private sealed record MemoizedGraph(string RepositoryRoot, RepositoryGraph Graph);

    private readonly IProcessRunner processRunner;
    private readonly ProjectIndexer projectIndexer;
    private readonly RepositoryFileFilter fileFilter;

    /// <summary>
    /// Serializes graph construction. Without it two concurrent MCP calls arriving on a cold cache
    /// each run a full MSBuild evaluation and Roslyn compilation of the whole repository, which is
    /// the most expensive thing this process does. One gate rather than one per repository root:
    /// the service is created per <see cref="DevContext.DevContextApi"/> instance, which is itself
    /// per repository, so a shared gate never serializes unrelated work in practice.
    /// </summary>
    private readonly SemaphoreSlim buildGate = new(1, 1);

    /// <summary>
    /// Content hashes keyed by size and modification time, so a warm call re-reads only what
    /// changed instead of the whole repository. Shared by the repository input hash and both
    /// per-project evaluation hash passes, which is also what stops the same project files being
    /// streamed three times in one build.
    /// </summary>
    private readonly FileFingerprintCache fingerprints = new();

    /// <summary>
    /// The SDK version for this process. Reading it costs a process spawn -- about 140 ms, a fifth of
    /// a warm call -- and it is asked for on every single build. Memoized per repository root because
    /// a <c>global.json</c> can pin a different SDK per directory; an SDK installed or removed while
    /// this process is running is not picked up until it restarts, which is the deliberate trade.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<string>> sdkVersions =
        new(StringComparer.OrdinalIgnoreCase);

    private MemoizedGraph? memoized;

    public RepositoryGraphService(
        IProcessRunner processRunner,
        ProjectIndexer projectIndexer,
        RepositoryFileFilter? fileFilter = null)
    {
        this.processRunner = processRunner;
        this.projectIndexer = projectIndexer;
        this.fileFilter = fileFilter ?? new RepositoryFileFilter(processRunner);
    }

    public async Task<RepositoryGraph> BuildAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        if (config.Cache.Enabled)
        {
            await fingerprints.LoadAsync(repositoryRoot, cancellationToken);
        }

        var inventory = await fileFilter.GetFilesAsync(repositoryRoot, config.Indexing, cancellationToken);
        var sdkVersion = await ReadSdkVersionAsync(repositoryRoot, cancellationToken);
        var inputHash = await ComputeInputHashAsync(
            repositoryRoot,
            config,
            inventory,
            sdkVersion,
            cancellationToken);
        if (config.Cache.Enabled && TryMemoized(repositoryRoot, inputHash) is { } warm)
        {
            return warm;
        }

        // Everything past here is expensive, so only one caller runs it at a time. The check above is
        // repeated inside the gate because the caller that held it may have produced exactly the
        // graph this one is waiting for.
        await buildGate.WaitAsync(cancellationToken);
        try
        {
            var graph = await BuildUncachedAsync(
                repositoryRoot,
                config,
                inventory,
                sdkVersion,
                inputHash,
                cancellationToken);
            if (config.Cache.Enabled)
            {
                await fingerprints.SaveAsync(repositoryRoot, cancellationToken);
            }

            return graph;
        }
        finally
        {
            buildGate.Release();
        }
    }

    private async Task<RepositoryGraph> BuildUncachedAsync(
        string repositoryRoot,
        DevContextConfig config,
        RepositoryFileInventory inventory,
        string sdkVersion,
        string inputHash,
        CancellationToken cancellationToken)
    {
        if (config.Cache.Enabled)
        {
            if (TryMemoized(repositoryRoot, inputHash) is { } warm)
            {
                return warm;
            }

            var cached = await TryReadCacheAsync(repositoryRoot, inputHash, cancellationToken);
            if (cached is not null)
            {
                Remember(repositoryRoot, cached);
                return cached;
            }
        }

        var availableProjectPaths = inventory.RelativePaths
            .Where(IsSupportedProjectPath)
            .ToArray();
        var previousEntries = config.Cache.Enabled
            ? await TryReadProjectEntriesAsync(repositoryRoot, availableProjectPaths, cancellationToken)
            : new Dictionary<string, ProjectGraphCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var previousProjects = previousEntries.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Project,
            StringComparer.OrdinalIgnoreCase);
        var preliminaryEvaluationHashes = await ComputeProjectEvaluationInputHashesAsync(
            repositoryRoot,
            availableProjectPaths,
            previousProjects,
            inventory,
            config,
            sdkVersion,
            config.Cache.Enabled ? fingerprints : null,
            cancellationToken);
        var reusableProjectRecords = previousEntries
            .Where(entry => preliminaryEvaluationHashes.TryGetValue(entry.Key, out var hash)
                            && entry.Value.EvaluationInputHash.Equals(hash, StringComparison.Ordinal))
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Project,
                StringComparer.OrdinalIgnoreCase);
        var projectBuild = await projectIndexer.BuildAsync(
            repositoryRoot,
            config,
            inventory,
            reusableProjectRecords,
            cancellationToken);
        var repository = projectBuild.Repository;
        var currentProjects = repository.Projects.ToDictionary(
            project => project.Path,
            StringComparer.OrdinalIgnoreCase);
        var projectEvaluationHashes = await ComputeProjectEvaluationInputHashesAsync(
            repositoryRoot,
            repository.Projects.Select(project => project.Path).ToArray(),
            currentProjects,
            inventory,
            config,
            sdkVersion,
            config.Cache.Enabled ? fingerprints : null,
            cancellationToken);
        var projectHashes = await ComputeProjectInputHashesAsync(
            repositoryRoot,
            config.Cache.Enabled ? fingerprints : null,
            repository,
            config.Indexing,
            sdkVersion,
            cancellationToken);
        var cachedProjects = previousEntries
            .Where(entry => projectHashes.TryGetValue(entry.Key, out var hash)
                            && entry.Value.InputHash.Equals(hash, StringComparison.Ordinal)
                            && !projectBuild.EvaluatedProjects.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var projectDependencies = ProjectDependencies(repository);
        var directlyInvalidated = repository.Projects
            .Select(project => project.Path)
            .Where(path => !cachedProjects.ContainsKey(path))
            .ToArray();
        var invalidated = ProjectOwnershipResolver.ExpandAffectedProjects(
            directlyInvalidated,
            projectDependencies);
        var reusedProjects = cachedProjects
            .Where(entry => !invalidated.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var projectsToIndex = repository.Projects
            .Select(project => project.Path)
            .Where(path => !reusedProjects.ContainsKey(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = projectsToIndex.Count == 0
            ? EmptyGraphIndexes()
            : await SymbolIndexer.BuildAsync(
                repositoryRoot,
                repository,
                config.Indexing,
                projectsToIndex,
                cancellationToken);
        var (symbols, dependencies) = MergeProjectIndexes(
            repository,
            reusedProjects.Values,
            fresh.Symbols,
            fresh.Dependencies);
        var graph = new RepositoryGraph(
            repository,
            symbols,
            dependencies,
            inputHash,
            false,
            reusedProjects.Count,
            projectsToIndex.Count);

        if (config.Cache.Enabled)
        {
            await WriteCacheAsync(
                repositoryRoot,
                graph,
                projectEvaluationHashes,
                projectHashes,
                cancellationToken);
            Remember(repositoryRoot, graph);
        }

        return graph;
    }

    public void ClearMemoryCache() => Volatile.Write(ref memoized, null);

    private void Remember(string repositoryRoot, RepositoryGraph graph) =>
        Volatile.Write(ref memoized, new MemoizedGraph(repositoryRoot, graph));

    private RepositoryGraph? TryMemoized(string repositoryRoot, string inputHash)
    {
        var current = Volatile.Read(ref memoized);
        if (current is null
            || !string.Equals(current.RepositoryRoot, repositoryRoot, StringComparison.OrdinalIgnoreCase)
            || !current.Graph.InputHash.Equals(inputHash, StringComparison.Ordinal))
        {
            return null;
        }

        return current.Graph with
        {
            CacheHit = true,
            ProjectCacheHits = current.Graph.Repository.Projects.Count,
            ProjectCacheMisses = 0
        };
    }

    internal async Task<string> ComputeInputHashAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var inventory = await fileFilter.GetFilesAsync(repositoryRoot, config.Indexing, cancellationToken);
        var sdkVersion = await ReadSdkVersionAsync(repositoryRoot, cancellationToken);
        return await ComputeInputHashAsync(
            repositoryRoot,
            config,
            inventory,
            sdkVersion,
            cancellationToken);
    }

    private async Task<string> ComputeInputHashAsync(
        string repositoryRoot,
        DevContextConfig config,
        RepositoryFileInventory inventory,
        string sdkVersion,
        CancellationToken cancellationToken)
    {
        // Hashed in parallel and folded back in inventory order, so the result does not depend on
        // how the work was scheduled. Streaming each file inline was both serial and unconditional:
        // the whole repository was read before the in-memory cache could even be consulted.
        var contentHashes = await ParallelWork.SelectAsync(
            inventory.RelativePaths,
            Math.Max(1, config.Indexing.MaxParallelism),
            async (relative, token) =>
            {
                var path = RepositoryFileFilter.ToFullPath(repositoryRoot, relative);
                if (!IsRepositoryInput(path))
                {
                    return null;
                }

                return config.Cache.Enabled
                    ? await fingerprints.HashAsync(path, token)
                    : await Hashing.FileAsync(path, token);
            },
            cancellationToken);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, $"schema:{SchemaVersions.Current}\n");
        Append(hash, $"sdk:{sdkVersion}\n");
        Append(hash, CacheKeyConfiguration(config));

        for (var index = 0; index < inventory.RelativePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, $"\nfile:{inventory.RelativePaths[index]}\n");
            if (contentHashes[index] is { } contentHash)
            {
                Append(hash, contentHash);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private Task<string> ReadSdkVersionAsync(
        string repositoryRoot,
        CancellationToken cancellationToken) =>
        sdkVersions.GetOrAdd(
            repositoryRoot,
            static (root, state) => state.Service.ProbeSdkVersionAsync(root, state.Token),
            (Service: this, Token: cancellationToken));

    private async Task<string> ProbeSdkVersionAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var sdk = await processRunner.RunAsync(
            "dotnet",
            ["--version"],
            repositoryRoot,
            cancellationToken);

        // The result feeds the repository input hash. Taking the output without checking the state
        // hashes an empty string whenever dotnet is missing or the call times out, quietly making two
        // different toolchains — or a broken one — share a cache entry.
        return sdk.State == ExecutionState.Succeeded
            ? sdk.StandardOutput.Trim()
            : $"unavailable:{sdk.State}";
    }

    private static async Task<IReadOnlyDictionary<string, string>> ComputeProjectInputHashesAsync(
        string repositoryRoot,
        FileFingerprintCache? fingerprints,
        RepositoryIndex repository,
        IndexingConfig indexing,
        string sdkVersion,
        CancellationToken cancellationToken)
    {
        var results = await ParallelWork.SelectAsync(
            repository.Projects,
            indexing.MaxParallelism,
            async (project, token) =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Append(hash, $"schema:{SchemaVersions.Current}\n");
                Append(hash, $"sdk:{sdkVersion}\n");
                Append(hash, SemanticIndexingConfiguration(indexing));
                Append(hash, JsonSerializer.Serialize(project, JsonDefaults.Options));
                foreach (var relative in project.ProjectFiles
                             .Append(project.Path)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var path = RepositoryFileFilter.ToFullPath(repositoryRoot, relative);
                    if (!File.Exists(path) || !IsRepositoryInput(path))
                    {
                        continue;
                    }

                    Append(hash, $"\nfile:{relative}\n");
                    await using var stream = File.OpenRead(path);
                    var buffer = new byte[64 * 1024];
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, token)) > 0)
                    {
                        hash.AppendData(buffer, 0, bytesRead);
                    }
                }

                return (project.Path, Hash: Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            },
            cancellationToken);
        return results.ToDictionary(result => result.Path, result => result.Hash, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ComputeProjectEvaluationInputHashesAsync(
        string repositoryRoot,
        IReadOnlyList<string> projectPaths,
        IReadOnlyDictionary<string, ProjectRecord> projects,
        RepositoryFileInventory inventory,
        DevContextConfig config,
        string sdkVersion,
        FileFingerprintCache? fingerprints,
        CancellationToken cancellationToken)
    {
        var projectDirectories = projectPaths
            .Select(ProjectDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sharedInputs = inventory.RelativePaths
            .Where(IsSharedProjectEvaluationInput)
            .ToArray();
        var results = await ParallelWork.SelectAsync(
            projectPaths,
            config.Indexing.MaxParallelism,
            async (projectPath, token) =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Append(hash, $"schema:{SchemaVersions.Current}\n");
                Append(hash, $"sdk:{sdkVersion}\n");
                Append(hash, $"solution:{config.Solution ?? string.Empty}\n");
                Append(hash, $"project:{projectPath}\n");
                foreach (var relative in inventory.RelativePaths
                             .Where(relative => IsInProjectDirectoryScope(
                                 relative,
                                 ProjectDirectory(projectPath),
                                 projectDirectories))
                             .Order(StringComparer.Ordinal))
                {
                    Append(hash, $"path:{relative}\n");
                }

                if (projects.TryGetValue(projectPath, out var cachedProject))
                {
                    foreach (var reference in cachedProject.ProjectReferences.Order(StringComparer.Ordinal))
                    {
                        var referencePath = RepositoryFileFilter.ToFullPath(repositoryRoot, reference);
                        Append(hash, $"reference:{reference}:{File.Exists(referencePath)}\n");
                    }
                }

                var evaluationInputs = sharedInputs
                    .Append(projectPath)
                    .Concat(cachedProject is not null
                        ? cachedProject.ProjectFiles.Where(IsProjectEvaluationInput)
                        : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal);
                foreach (var relative in evaluationInputs)
                {
                    token.ThrowIfCancellationRequested();
                    var path = RepositoryFileFilter.ToFullPath(repositoryRoot, relative);
                    if (!File.Exists(path))
                    {
                        Append(hash, $"missing:{relative}\n");
                        continue;
                    }

                    Append(hash, $"file:{relative}\n");
                    await AppendFileAsync(hash, path, token);
                }

                return (Path: projectPath, Hash: Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            },
            cancellationToken);
        return results.ToDictionary(result => result.Path, result => result.Hash, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Folds a file's content into a hash, through the fingerprint cache when one is available. The
    /// cache turns the repeated passes over the same shared inputs -- global.json, NuGet.Config,
    /// Directory.Build.props and every project file, streamed once for the preliminary evaluation
    /// hash and again after evaluation -- into a single read.
    /// </summary>
    private static async Task AppendContentAsync(
        IncrementalHash hash,
        string path,
        FileFingerprintCache? fingerprints,
        CancellationToken cancellationToken)
    {
        if (fingerprints is null)
        {
            await AppendFileAsync(hash, path, cancellationToken);
            return;
        }

        Append(hash, await fingerprints.HashAsync(path, cancellationToken));
    }

    private static async Task<RepositoryGraph?> TryReadCacheAsync(
        string repositoryRoot,
        string expectedInputHash,
        CancellationToken cancellationToken)
    {
        var cachePath = ContextPaths.Cache(repositoryRoot);
        var manifestPath = Path.Combine(cachePath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = await JsonFile.ReadAsync<RepositoryGraphCacheManifest>(
                manifestPath,
                cancellationToken);
            if (manifest.SchemaVersion != SchemaVersions.Current
                || !manifest.InputHash.Equals(expectedInputHash, StringComparison.Ordinal))
            {
                return null;
            }

            var repository = await JsonFile.ReadAsync<RepositoryIndex>(
                Path.Combine(cachePath, "projects.json"),
                cancellationToken);
            var symbols = await JsonFile.ReadAsync<SymbolIndex>(
                Path.Combine(cachePath, "symbols.json"),
                cancellationToken);
            var dependencies = await JsonFile.ReadAsync<DependencyIndex>(
                Path.Combine(cachePath, "dependencies.json"),
                cancellationToken);
            if (repository.SchemaVersion != SchemaVersions.Current
                || symbols.SchemaVersion != SchemaVersions.Current
                || dependencies.SchemaVersion != SchemaVersions.Current)
            {
                return null;
            }
            return new RepositoryGraph(
                repository,
                symbols,
                dependencies,
                expectedInputHash,
                true,
                repository.Projects.Count,
                0);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, ProjectGraphCacheEntry>> TryReadProjectEntriesAsync(
        string repositoryRoot,
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ProjectGraphCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(ContextPaths.Cache(repositoryRoot), "project-entries");
        foreach (var projectPath in projectPaths.Order(StringComparer.Ordinal))
        {
            var path = ProjectEntryPath(directory, projectPath);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var entry = await JsonFile.ReadAsync<ProjectGraphCacheEntry>(path, cancellationToken);
                if (entry.SchemaVersion == SchemaVersions.Current
                    && entry.ProjectPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase)
                    && entry.Project.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase)
                    && entry.Symbols.SchemaVersion == SchemaVersions.Current
                    && entry.Dependencies.SchemaVersion == SchemaVersions.Current)
                {
                    result[projectPath] = entry;
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidOperationException)
            {
                // A corrupt project entry is a local miss; other projects can still be reused.
            }
        }

        return result;
    }

    private static async Task WriteCacheAsync(
        string repositoryRoot,
        RepositoryGraph graph,
        IReadOnlyDictionary<string, string> projectEvaluationHashes,
        IReadOnlyDictionary<string, string> projectHashes,
        CancellationToken cancellationToken)
    {
        var contextRoot = ContextPaths.Root(repositoryRoot);
        Directory.CreateDirectory(contextRoot);
        var cachePath = ContextPaths.Cache(repositoryRoot);
        var pendingPath = Path.Combine(contextRoot, $".cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pendingPath);

        try
        {
            await JsonFile.WriteAsync(
                Path.Combine(pendingPath, "manifest.json"),
                new RepositoryGraphCacheManifest
                {
                    InputHash = graph.InputHash,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
            await JsonFile.WriteAsync(
                Path.Combine(pendingPath, "projects.json"),
                graph.Repository,
                cancellationToken);
            await JsonFile.WriteAsync(
                Path.Combine(pendingPath, "symbols.json"),
                graph.Symbols,
                cancellationToken);
            await JsonFile.WriteAsync(
                Path.Combine(pendingPath, "dependencies.json"),
                graph.Dependencies,
                cancellationToken);
            var projectDirectory = Path.Combine(pendingPath, "project-entries");
            Directory.CreateDirectory(projectDirectory);
            foreach (var project in graph.Repository.Projects.OrderBy(project => project.Path, StringComparer.Ordinal))
            {
                var entry = SliceProjectEntry(
                    project,
                    projectEvaluationHashes[project.Path],
                    projectHashes[project.Path],
                    graph.Symbols,
                    graph.Dependencies);
                await JsonFile.WriteAsync(
                    ProjectEntryPath(projectDirectory, project.Path),
                    entry,
                    cancellationToken);
            }

            await SwapCacheAsync(repositoryRoot, pendingPath, cachePath, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(pendingPath);
        }
    }

    /// <summary>
    /// Replaces the cache directory with a freshly written one.
    ///
    /// The previous implementation deleted the cache and then renamed the new one into place, with
    /// no coordination between processes and no error handling: two dev-context runs on the same
    /// repository raced, and the resulting <see cref="IOException"/> failed the whole command. It
    /// also left a window in which no cache existed at all, so a concurrent reader rebuilt from
    /// scratch.
    ///
    /// Now a lock file serializes the swap between processes, the old directory is renamed aside
    /// rather than deleted in place, and any failure leaves the existing cache untouched. The cache
    /// is an optimization: failing to update it must never fail the command that produced the graph.
    /// </summary>
    private static async Task SwapCacheAsync(
        string repositoryRoot,
        string pendingPath,
        string cachePath,
        CancellationToken cancellationToken)
    {
        using var cacheLock = await TryAcquireCacheLockAsync(repositoryRoot, cancellationToken);
        if (cacheLock is null)
        {
            return;
        }

        var discardedPath = Path.Combine(
            ContextPaths.Root(repositoryRoot),
            $".cache-discarded-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Move(cachePath, discardedPath);
            }

            Directory.Move(pendingPath, cachePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put back whatever was there before rather than leaving the repository with no cache.
            if (!Directory.Exists(cachePath) && Directory.Exists(discardedPath))
            {
                TryMoveDirectory(discardedPath, cachePath);
            }
        }
        finally
        {
            TryDeleteDirectory(discardedPath);
        }
    }

    private static async Task<FileStream?> TryAcquireCacheLockAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var lockPath = ContextPaths.CacheLock(repositoryRoot);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }

        // Another process is publishing its own cache. Its result is as valid as ours, and the graph
        // this call produced is returned either way, so give up on writing rather than failing.
        return null;
    }

    private static void TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the caller is already handling a failed swap.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory is recoverable; failing the command over it is not.
        }
    }

    private static ProjectGraphCacheEntry SliceProjectEntry(
        ProjectRecord project,
        string evaluationInputHash,
        string inputHash,
        SymbolIndex symbols,
        DependencyIndex dependencies)
    {
        var projectSymbols = symbols.Symbols
            .Where(symbol => symbol.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var symbolIds = projectSymbols.Select(symbol => symbol.Identity).ToHashSet(StringComparer.Ordinal);
        return new ProjectGraphCacheEntry
        {
            ProjectPath = project.Path,
            EvaluationInputHash = evaluationInputHash,
            InputHash = inputHash,
            Project = project,
            Symbols = new SymbolIndex
            {
                Symbols = projectSymbols,
                TypeDefinitions = symbols.TypeDefinitions
                    .Where(definition => definition.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                CompilationCompleteness = symbols.CompilationCompleteness
                    .Where(record => record.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                GeneratedSources = symbols.GeneratedSources
                    .Where(source => source.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            },
            Dependencies = new DependencyIndex
            {
                Projects = dependencies.Projects
                    .Where(dependency => dependency.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                Types = dependencies.Types
                    .Where(dependency => symbolIds.Contains(dependency.Symbol))
                    .ToArray(),
                Symbols = dependencies.Symbols
                    .Where(reference => reference.SourceProject.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            }
        };
    }

    private static (SymbolIndex Symbols, DependencyIndex Dependencies) MergeProjectIndexes(
        RepositoryIndex repository,
        IEnumerable<ProjectGraphCacheEntry> cachedEntries,
        SymbolIndex freshSymbols,
        DependencyIndex freshDependencies)
    {
        var symbolIndexes = cachedEntries.Select(entry => entry.Symbols).Append(freshSymbols).ToArray();
        var dependencyIndexes = cachedEntries.Select(entry => entry.Dependencies).Append(freshDependencies).ToArray();
        var symbols = symbolIndexes.SelectMany(index => index.Symbols)
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ThenBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .ToArray();
        return (
            new SymbolIndex
            {
                Symbols = symbols,
                TypeDefinitions = symbolIndexes.SelectMany(index => index.TypeDefinitions)
                    .DistinctBy(definition => definition.SymbolIdentity, StringComparer.Ordinal)
                    .OrderBy(definition => definition.Project, StringComparer.Ordinal)
                    .ThenBy(definition => definition.Declarations[0].File, StringComparer.Ordinal)
                    .ThenBy(definition => definition.Declarations[0].Line)
                    .ThenBy(definition => definition.SymbolIdentity, StringComparer.Ordinal)
                    .ToArray(),
                CompilationCompleteness = symbolIndexes.SelectMany(index => index.CompilationCompleteness)
                    .OrderBy(record => record.Project, StringComparer.Ordinal)
                    .ThenBy(record => record.TargetFramework, StringComparer.Ordinal)
                    .ToArray(),
                GeneratedSources = symbolIndexes.SelectMany(index => index.GeneratedSources)
                    .DistinctBy(source => source.Id, StringComparer.Ordinal)
                    .OrderBy(source => source.Project, StringComparer.Ordinal)
                    .ThenBy(source => source.TargetFramework, StringComparer.Ordinal)
                    .ThenBy(source => source.File, StringComparer.Ordinal)
                    .ToArray()
            },
            new DependencyIndex
            {
                Projects = ProjectDependencies(repository),
                Types = dependencyIndexes.SelectMany(index => index.Types)
                    .Distinct()
                    .OrderBy(dependency => dependency.Symbol, StringComparer.Ordinal)
                    .ThenBy(dependency => dependency.RelatedType, StringComparer.Ordinal)
                    .ToArray(),
                Symbols = dependencyIndexes.SelectMany(index => index.Symbols)
                    .Distinct()
                    .OrderBy(reference => reference.SourceProject, StringComparer.Ordinal)
                    .ThenBy(reference => reference.SourceSymbol, StringComparer.Ordinal)
                    .ThenBy(reference => reference.TargetSymbol, StringComparer.Ordinal)
                    .ThenBy(reference => reference.Relationship, StringComparer.Ordinal)
                    .ToArray()
            });
    }

    private static (SymbolIndex Symbols, DependencyIndex Dependencies) EmptyGraphIndexes() =>
        (new SymbolIndex { Symbols = [] }, new DependencyIndex { Projects = [], Types = [], Symbols = [] });

    private static IReadOnlyList<ProjectDependency> ProjectDependencies(RepositoryIndex repository) =>
        repository.Projects
            .SelectMany(project => project.ProjectReferences.Select(reference =>
                new ProjectDependency(project.Path, reference)))
            .OrderBy(dependency => dependency.Project, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.ReferencedProject, StringComparer.Ordinal)
            .ToArray();

    private static string ProjectEntryPath(string directory, string projectPath) =>
        Path.Combine(directory, $"{Hashing.Text(projectPath)[..24]}.json");

    /// <summary>
    /// Serializes the configuration for use as a cache key with machine-dependent scheduling knobs
    /// normalized away. <see cref="IndexingConfig.MaxParallelism"/> defaults to the processor count,
    /// so including it verbatim would give the same repository a different graph hash on every
    /// machine, and the persisted cache could never be compared or shared across runners. It changes
    /// how the work is scheduled, never what the resulting graph contains.
    /// </summary>
    private static string CacheKeyConfiguration(DevContextConfig config) =>
        JsonSerializer.Serialize(
            config with { Indexing = config.Indexing with { MaxParallelism = 0 } },
            JsonDefaults.Options);

    private static string SemanticIndexingConfiguration(IndexingConfig indexing) =>
        JsonSerializer.Serialize(
            new
            {
                indexing.ExecuteSourceGenerators,
                indexing.RespectGitignore,
                Exclude = indexing.Exclude.Order(StringComparer.Ordinal).ToArray(),
                indexing.MaxSourceFileBytes
            },
            JsonDefaults.Options);

    private static string ProjectDirectory(string projectPath)
    {
        var separator = projectPath.LastIndexOf('/');
        return separator < 0 ? string.Empty : projectPath[..separator];
    }

    private static bool IsInProjectDirectoryScope(
        string relativePath,
        string projectDirectory,
        IReadOnlyList<string> projectDirectories)
    {
        if (!IsUnderDirectory(relativePath, projectDirectory))
        {
            return false;
        }

        var mostSpecificDirectoryLength = projectDirectories
            .Where(directory => IsUnderDirectory(relativePath, directory))
            .Select(directory => directory.Length)
            .DefaultIfEmpty(-1)
            .Max();
        return projectDirectory.Length == mostSpecificDirectoryLength;
    }

    private static bool IsUnderDirectory(string relativePath, string directory) =>
        directory.Length == 0
        || relativePath.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedProjectEvaluationInput(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectEvaluationInput(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj"
            or ".props" or ".targets";

    private static bool IsSupportedProjectPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    private static bool IsRepositoryInput(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName is ".gitignore" or "global.json" or "NuGet.Config" or "packages.lock.json"
            || fileName.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ruleset", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(path).ToLowerInvariant() is
            ".cs" or ".fs" or ".vb" or ".csproj" or ".fsproj" or ".vbproj"
            or ".props" or ".targets" or ".sln" or ".slnx" or ".slnf"
            or ".razor" or ".cshtml" or ".xaml" or ".resx" or ".editorconfig"
            or ".globalconfig" or ".ruleset";
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static async Task AppendFileAsync(
        IncrementalHash hash,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }
    }
}
