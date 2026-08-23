using System.Text.Json;
using DevContext.Infrastructure;

namespace DevContext.Configuration;

public sealed record DevContextConfig
{
    public int Version { get; init; } = 1;
    public string? Solution { get; init; }
    public TestConfig Tests { get; init; } = new();
    public AnalysisConfig Analysis { get; init; } = new();
    public CleanupConfig Cleanup { get; init; } = new();
    public StorageConfig Storage { get; init; } = new();
    public CacheConfig Cache { get; init; } = new();
    public IndexingConfig Indexing { get; init; } = new();
}

public sealed record TestConfig
{
    public bool Enabled { get; init; } = true;
    public string BaselineMode { get; init; } = "all";
    public string VerifyMode { get; init; } = "affected-first";
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
    public int MaxSourceFileBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxEvidenceFileBytes { get; init; } = 512 * 1024;
    public int MaxEvidenceFilesScanned { get; init; } = 20_000;
}

internal static class ConfigLoader
{
    public static async Task<(DevContextConfig Config, bool IsNew)> LoadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var path = ContextPaths.Config(repositoryRoot);
        if (!File.Exists(path))
        {
            var solution = Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(candidate => Path.GetRelativePath(repositoryRoot, candidate).Replace('\\', '/'))
                .FirstOrDefault();
            var newConfig = new DevContextConfig { Solution = solution };
            Validate(newConfig);
            return (newConfig, true);
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

        if (config.Version != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported configuration version {config.Version}; expected version 1.");
        }

        Validate(config);

        return (config, false);
    }

    internal static void Validate(DevContextConfig config)
    {
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

        if (config.Indexing.MaxSourceFileBytes < 1024
            || config.Indexing.MaxEvidenceFileBytes < 1024
            || config.Indexing.MaxEvidenceFilesScanned < 1)
        {
            throw new InvalidOperationException(
                "indexing file-size limits must be at least 1024 bytes and maxEvidenceFilesScanned must be positive.");
        }
    }
}
