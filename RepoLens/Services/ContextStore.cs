using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed class ContextStore
{
    public bool BaselineExists(string repositoryRoot) =>
        File.Exists(Path.Combine(ContextPaths.Baseline(repositoryRoot), "manifest.json"));

    public async Task SaveConfigIfNewAsync(
        string repositoryRoot,
        DevContextConfig config,
        bool isNew,
        CancellationToken cancellationToken)
    {
        if (isNew)
        {
            await JsonFile.WriteAsync(ContextPaths.Config(repositoryRoot), config, cancellationToken);
        }
    }

    public async Task SaveBaselineAsync(
        string repositoryRoot,
        CaptureBundle bundle,
        DevContextConfig config,
        bool replace,
        CancellationToken cancellationToken)
    {
        var contextRoot = ContextPaths.Root(repositoryRoot);
        Directory.CreateDirectory(contextRoot);
        var pendingRoot = Path.Combine(contextRoot, $".pending-{Guid.NewGuid():N}");
        var pendingBaseline = Path.Combine(pendingRoot, "baseline");
        var pendingIndexes = Path.Combine(pendingRoot, "indexes");
        Directory.CreateDirectory(pendingBaseline);
        Directory.CreateDirectory(pendingIndexes);

        try
        {
            await WriteSnapshotAsync(pendingBaseline, bundle, config, cancellationToken);
            await WriteIndexesAsync(pendingIndexes, bundle, cancellationToken);

            var baselinePath = ContextPaths.Baseline(repositoryRoot);
            var indexesPath = ContextPaths.Indexes(repositoryRoot);
            if (Directory.Exists(baselinePath))
            {
                if (!replace)
                {
                    throw new InvalidOperationException(
                        "A baseline already exists. Use --replace only when explicitly beginning a new logical task.");
                }

                Directory.Delete(baselinePath, true);
            }

            if (Directory.Exists(indexesPath))
            {
                Directory.Delete(indexesPath, true);
            }

            Directory.Move(pendingBaseline, baselinePath);
            Directory.Move(pendingIndexes, indexesPath);
            await File.WriteAllTextAsync(
                ContextPaths.Summary(repositoryRoot),
                OutputFormatter.FormatStatus(ToStatus(bundle)),
                cancellationToken);
        }
        finally
        {
            if (Directory.Exists(pendingRoot))
            {
                Directory.Delete(pendingRoot, true);
            }
        }
    }

    public async Task SaveCurrentAsync(
        string repositoryRoot,
        CaptureBundle bundle,
        DevContextConfig config,
        VerificationReport report,
        CancellationToken cancellationToken)
    {
        var currentPath = ContextPaths.Current(repositoryRoot);
        var pendingPath = Path.Combine(ContextPaths.Root(repositoryRoot), $".current-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pendingPath);
        try
        {
            await WriteSnapshotAsync(pendingPath, bundle, config, cancellationToken);
            await JsonFile.WriteAsync(Path.Combine(pendingPath, "verification.json"), report, cancellationToken);
            if (Directory.Exists(currentPath))
            {
                Directory.Delete(currentPath, true);
            }

            Directory.Move(pendingPath, currentPath);
        }
        finally
        {
            if (Directory.Exists(pendingPath))
            {
                Directory.Delete(pendingPath, true);
            }
        }
    }

    public async Task<StatusReport> ReadStatusAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        EnsureBaseline(repositoryRoot);
        var baseline = ContextPaths.Baseline(repositoryRoot);
        var manifest = await ReadValidatedAsync<BaselineManifest>(
            Path.Combine(baseline, "manifest.json"),
            value => value.SchemaVersion,
            cancellationToken);
        var git = await ReadValidatedAsync<GitSnapshot>(
            Path.Combine(baseline, "git.json"),
            value => value.SchemaVersion,
            cancellationToken);
        var build = await ReadValidatedAsync<BuildSnapshot>(
            Path.Combine(baseline, "build.json"),
            value => value.SchemaVersion,
            cancellationToken);
        var tests = await ReadValidatedAsync<TestSnapshot>(
            Path.Combine(baseline, "tests.json"),
            value => value.SchemaVersion,
            cancellationToken);
        var analysis = await ReadValidatedAsync<AnalysisSnapshot>(
            Path.Combine(baseline, "analysis.json"),
            value => value.SchemaVersion,
            cancellationToken);
        var repository = await ReadValidatedAsync<RepositoryIndex>(
            Path.Combine(ContextPaths.Indexes(repositoryRoot), "projects.json"),
            value => value.SchemaVersion,
            cancellationToken);
        return new StatusReport
        {
            Manifest = manifest,
            Git = git,
            Build = build,
            Tests = tests,
            Analysis = analysis,
            Repository = repository
        };
    }

    public async Task<(SymbolIndex Symbols, DependencyIndex Dependencies)> ReadIndexesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken) =>
        (
            await ReadValidatedAsync<SymbolIndex>(
                Path.Combine(ContextPaths.Indexes(repositoryRoot), "symbols.json"),
                value => value.SchemaVersion,
                cancellationToken),
            await ReadValidatedAsync<DependencyIndex>(
                Path.Combine(ContextPaths.Indexes(repositoryRoot), "dependencies.json"),
                value => value.SchemaVersion,
                cancellationToken)
        );

    public void Reset(string repositoryRoot)
    {
        foreach (var directory in new[]
                 {
                     ContextPaths.Baseline(repositoryRoot),
                     ContextPaths.Current(repositoryRoot),
                     ContextPaths.Indexes(repositoryRoot),
                     ContextPaths.Cache(repositoryRoot),
                     ContextPaths.Runs(repositoryRoot)
                 })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        var summary = ContextPaths.Summary(repositoryRoot);
        if (File.Exists(summary))
        {
            File.Delete(summary);
        }
    }

    public void EnsureBaseline(string repositoryRoot)
    {
        if (!BaselineExists(repositoryRoot))
        {
            throw new InvalidOperationException("No baseline exists. Run 'dev-context baseline' first.");
        }
    }

    private static StatusReport ToStatus(CaptureBundle bundle) => new()
    {
        Manifest = bundle.Manifest,
        Git = bundle.Git,
        Build = bundle.Build,
        Tests = bundle.Tests,
        Analysis = bundle.Analysis,
        Repository = bundle.Repository
    };

    private static async Task WriteSnapshotAsync(
        string path,
        CaptureBundle bundle,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        await JsonFile.WriteAsync(Path.Combine(path, "manifest.json"), bundle.Manifest, cancellationToken);
        await JsonFile.WriteAsync(Path.Combine(path, "git.json"), bundle.Git, cancellationToken);
        await JsonFile.WriteAsync(Path.Combine(path, "build.json"), bundle.Build, cancellationToken);
        await JsonFile.WriteAsync(Path.Combine(path, "tests.json"), bundle.Tests, cancellationToken);
        await JsonFile.WriteAsync(Path.Combine(path, "analysis.json"), bundle.Analysis, cancellationToken);

        if (!config.Storage.RetainRawLogs)
        {
            return;
        }

        var rawPath = Path.Combine(path, "raw");
        Directory.CreateDirectory(rawPath);
        foreach (var (fileName, contents) in bundle.RawLogs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(rawPath, fileName), contents, cancellationToken);
        }
    }

    private static async Task WriteIndexesAsync(
        string path,
        CaptureBundle bundle,
        CancellationToken cancellationToken)
    {
        await JsonFile.WriteAsync(Path.Combine(path, "projects.json"), bundle.Repository, cancellationToken);
        await JsonFile.WriteAsync(Path.Combine(path, "symbols.json"), bundle.Symbols, cancellationToken);
        await JsonFile.WriteAsync(Path.Combine(path, "dependencies.json"), bundle.Dependencies, cancellationToken);
    }

    private static async Task<T> ReadValidatedAsync<T>(
        string path,
        Func<T, int> schemaVersion,
        CancellationToken cancellationToken)
    {
        var value = await JsonFile.ReadAsync<T>(path, cancellationToken);
        SchemaVersions.EnsureReadable(schemaVersion(value), Path.GetFileName(path));
        return value;
    }
}
