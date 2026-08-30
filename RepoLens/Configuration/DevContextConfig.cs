using System.Text.Json;
using DevContext.Infrastructure;

namespace DevContext.Configuration;

public sealed record DevContextConfig
{
    public int Version { get; init; } = ConfigLoader.CurrentVersion;
    public string? Solution { get; init; }
    public TestConfig Tests { get; init; } = new();
    public AnalysisConfig Analysis { get; init; } = new();
    public CleanupConfig Cleanup { get; init; } = new();
    public StorageConfig Storage { get; init; } = new();
    public CacheConfig Cache { get; init; } = new();
    public IndexingConfig Indexing { get; init; } = new();
    public ExecutionConfig Execution { get; init; } = new();
}

public sealed record ExecutionConfig
{
    /// <summary>
    /// Wall-clock ceiling for a single external process. Without one, a hung <c>dotnet build</c>,
    /// <c>dotnet test</c>, <c>git</c>, or analyzer blocks forever — and inside an MCP session that
    /// hangs the agent with no way to recover. The default is generous because a real test suite
    /// legitimately runs for minutes; it exists to bound a hang, not to police slowness.
    /// </summary>
    public int ProcessTimeoutSeconds { get; init; } = 900;
}

public sealed record TestConfig
{
    public bool Enabled { get; init; } = true;
    public string BaselineMode { get; init; } = "all";
    public string VerifyMode { get; init; } = "affected-first";
    public bool CollectCoverage { get; init; }
}

public sealed record AnalysisConfig
{
    public bool Roslyn { get; init; } = true;
    public bool DotnetFormat { get; init; }
    public bool Qodana { get; init; }
    public string QodanaCommand { get; init; } = "qodana";
    public bool FailOnNewWarnings { get; init; } = true;
}

public sealed record CleanupConfig
{
    public string Command { get; init; } = "dotnet format";
    public bool Enabled { get; init; }
}

public sealed record StorageConfig
{
    public bool RetainRawLogs { get; init; }
}

public sealed record CacheConfig
{
    public bool Enabled { get; init; } = true;
}

public sealed record IndexingConfig
{
    public bool ExecuteSourceGenerators { get; init; } = true;
    public bool RespectGitignore { get; init; } = true;
    public IReadOnlyList<string> Exclude { get; init; } = [];
    public int MaxParallelism { get; init; } = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
    public int MaxSourceFileBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxEvidenceFileBytes { get; init; } = 512 * 1024;
    public int MaxEvidenceFilesScanned { get; init; } = 20_000;
}

internal static class ConfigLoader
{
    public const int CurrentVersion = 2;
    public const int MinimumReadableVersion = 1;

    public static async Task<(DevContextConfig Config, bool IsNew, bool RequiresSave)> LoadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var path = ContextPaths.Config(repositoryRoot);
        if (!File.Exists(path))
        {
            var solution = Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(repositoryRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(candidate => Path.GetRelativePath(repositoryRoot, candidate).Replace('\\', '/'))
                .FirstOrDefault();
            var newConfig = new DevContextConfig { Solution = solution };
            Validate(newConfig);
            return (newConfig, true, false);
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<DevContextConfig>(
            stream,
            JsonDefaults.Options,
            cancellationToken);

        if (config is null)
        {
            throw new InvalidOperationException($"Configuration is empty: {path}");
        }

        var migrated = Migrate(config, out var requiresSave);
        Validate(migrated);

        return (migrated, false, requiresSave);
    }

    public static DevContextConfig Migrate(DevContextConfig config, out bool requiresSave)
    {
        if (config.Version is < MinimumReadableVersion or > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported configuration version {config.Version}; this version reads " +
                $"versions {MinimumReadableVersion}-{CurrentVersion}.");
        }

        requiresSave = config.Version < CurrentVersion;
        return requiresSave ? config with { Version = CurrentVersion } : config;
    }

    internal static void Validate(DevContextConfig config)
    {
        if (config.Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Configuration must be migrated to version {CurrentVersion} before validation.");
        }

        if (config.Tests.BaselineMode is not ("all" or "none"))
        {
            throw new InvalidOperationException(
                "tests.baselineMode must be either 'all' or 'none'.");
        }

        if (config.Tests.VerifyMode is not ("all" or "affected-first" or "affected-only" or "none"))
        {
            throw new InvalidOperationException(
                "tests.verifyMode must be 'all', 'affected-first', 'affected-only', or 'none'.");
        }

        if (config.Cleanup.Enabled && string.IsNullOrWhiteSpace(config.Cleanup.Command))
        {
            throw new InvalidOperationException(
                "cleanup.command must be non-empty when cleanup is enabled.");
        }

        if (config.Analysis.Qodana && string.IsNullOrWhiteSpace(config.Analysis.QodanaCommand))
        {
            throw new InvalidOperationException(
                "analysis.qodanaCommand must be non-empty when Qodana is enabled.");
        }

        if (config.Indexing.MaxParallelism is < 1 or > 64)
        {
            throw new InvalidOperationException("indexing.maxParallelism must be between 1 and 64.");
        }

        if (config.Indexing.MaxSourceFileBytes < 1024
            || config.Indexing.MaxEvidenceFileBytes < 1024
            || config.Indexing.MaxEvidenceFilesScanned < 1)
        {
            throw new InvalidOperationException(
                "indexing file-size limits must be at least 1024 bytes and maxEvidenceFilesScanned must be positive.");
        }

        if (config.Execution.ProcessTimeoutSeconds is < 1 or > 86_400)
        {
            throw new InvalidOperationException(
                "execution.processTimeoutSeconds must be between 1 and 86400.");
        }

        if (config.Indexing.Exclude is null)
        {
            throw new InvalidOperationException("indexing.exclude must be an array of repository-relative globs.");
        }

        if (!string.IsNullOrWhiteSpace(config.Solution)
            && Path.GetExtension(config.Solution).ToLowerInvariant() is not (".sln" or ".slnx" or ".slnf"))
        {
            throw new InvalidOperationException("solution must reference a .sln, .slnx, or .slnf file.");
        }

        foreach (var pattern in config.Indexing.Exclude)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new InvalidOperationException("indexing.exclude entries must be non-empty repository-relative globs.");
            }

            var normalized = pattern.Replace('\\', '/');
            if (Path.IsPathRooted(pattern)
                || normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'
                || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"indexing.exclude entries must not escape the repository: {pattern}");
            }
        }
    }
}
