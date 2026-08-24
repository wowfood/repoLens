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
    private readonly IProcessRunner processRunner;
    private readonly ProjectIndexer projectIndexer;
    private readonly RepositoryFileFilter fileFilter;
    private RepositoryGraph? inMemoryGraph;
    private string? inMemoryRepositoryRoot;

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
        var inventory = await fileFilter.GetFilesAsync(repositoryRoot, config.Indexing, cancellationToken);
        var sdkVersion = await ReadSdkVersionAsync(repositoryRoot, cancellationToken);
        var inputHash = await ComputeInputHashAsync(
            repositoryRoot,
            config,
            inventory,
            sdkVersion,
            cancellationToken);
        if (config.Cache.Enabled)
        {
            var memoryGraph = Volatile.Read(ref inMemoryGraph);
            if (memoryGraph is not null
                && string.Equals(
                    Volatile.Read(ref inMemoryRepositoryRoot),
                    repositoryRoot,
                    StringComparison.OrdinalIgnoreCase)
                && memoryGraph.InputHash.Equals(inputHash, StringComparison.Ordinal))
            {
                return memoryGraph with
                {
                    CacheHit = true,
                    ProjectCacheHits = memoryGraph.Repository.Projects.Count,
                    ProjectCacheMisses = 0
                };
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
            cancellationToken);
        var projectHashes = await ComputeProjectInputHashesAsync(
            repositoryRoot,
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

    public void ClearMemoryCache()
    {
        Volatile.Write(ref inMemoryGraph, null);
        Volatile.Write(ref inMemoryRepositoryRoot, null);
    }

    private void Remember(string repositoryRoot, RepositoryGraph graph)
    {
        Volatile.Write(ref inMemoryRepositoryRoot, repositoryRoot);
        Volatile.Write(ref inMemoryGraph, graph);
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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, $"schema:{SchemaVersions.Current}\n");
        Append(hash, $"sdk:{sdkVersion}\n");
        Append(hash, JsonSerializer.Serialize(config, JsonDefaults.Options));

        foreach (var relative in inventory.RelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, $"\nfile:{relative}\n");
            var path = RepositoryFileFilter.ToFullPath(repositoryRoot, relative);
            if (!IsRepositoryInput(path))
            {
                continue;
            }

            await using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<string> ReadSdkVersionAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var sdk = await processRunner.RunAsync(
            "dotnet",
            ["--version"],
            repositoryRoot,
            cancellationToken);
        return sdk.StandardOutput.Trim();
    }

    private static async Task<IReadOnlyDictionary<string, string>> ComputeProjectInputHashesAsync(
        string repositoryRoot,
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

            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }

            Directory.Move(pendingPath, cachePath);
        }
        finally
        {
            if (Directory.Exists(pendingPath))
            {
                Directory.Delete(pendingPath, true);
            }
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
