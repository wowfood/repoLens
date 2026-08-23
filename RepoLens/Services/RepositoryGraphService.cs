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
    bool CacheHit);

internal sealed record RepositoryGraphCacheManifest
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string InputHash { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

internal sealed class RepositoryGraphService(
    IProcessRunner processRunner,
    ProjectIndexer projectIndexer)
{
    public async Task<RepositoryGraph> BuildAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var inputHash = await ComputeInputHashAsync(repositoryRoot, config, cancellationToken);
        if (config.Cache.Enabled)
        {
            var cached = await TryReadCacheAsync(repositoryRoot, inputHash, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }

        var repository = await projectIndexer.BuildAsync(repositoryRoot, config, cancellationToken);
        var (symbols, dependencies) = await SymbolIndexer.BuildAsync(
            repositoryRoot,
            repository,
            config.Indexing,
            cancellationToken);
        var graph = new RepositoryGraph(repository, symbols, dependencies, inputHash, false);

        if (config.Cache.Enabled)
        {
            await WriteCacheAsync(repositoryRoot, graph, cancellationToken);
        }

        return graph;
    }

    internal async Task<string> ComputeInputHashAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var sdk = await processRunner.RunAsync(
            "dotnet",
            ["--version"],
            repositoryRoot,
            cancellationToken);
        var repositoryFiles = Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .OrderBy(path => Path.GetRelativePath(repositoryRoot, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, $"schema:{SchemaVersions.Current}\n");
        Append(hash, $"sdk:{sdk.StandardOutput.Trim()}\n");
        Append(hash, JsonSerializer.Serialize(config, JsonDefaults.Options));

        foreach (var path in repositoryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            Append(hash, $"\nfile:{relative}\n");
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
            return new RepositoryGraph(repository, symbols, dependencies, expectedInputHash, true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(
        string repositoryRoot,
        RepositoryGraph graph,
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

    private static bool IsRepositoryInput(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName is "global.json" or "NuGet.Config" or "packages.lock.json"
            || fileName.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ruleset", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(path).ToLowerInvariant() is
            ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx"
            or ".razor" or ".cshtml" or ".xaml" or ".resx" or ".editorconfig"
            or ".globalconfig" or ".ruleset";
    }

    private static bool IsGeneratedPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is
                "bin" or "obj" or ".git" or ".idea" or ContextPaths.DirectoryName);

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
