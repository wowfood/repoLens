using System.Diagnostics;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record CaptureBundle(
    BaselineManifest Manifest,
    GitSnapshot Git,
    BuildSnapshot Build,
    TestSnapshot Tests,
    AnalysisSnapshot Analysis,
    RepositoryIndex Repository,
    SymbolIndex Symbols,
    DependencyIndex Dependencies,
    AffectedReport? Affected,
    IReadOnlyDictionary<string, string> RawLogs);

internal enum CapturePurpose
{
    Baseline,
    Verification
}

internal sealed record BaselineReference(
    StatusReport Status,
    SymbolIndex Symbols,
    DependencyIndex Dependencies);

internal sealed class BaselineCaptureService(
    IProcessRunner processRunner,
    GitService gitService,
    RepositoryGraphService graphService,
    BuildService buildService,
    TestService testService,
    AnalysisService analysisService)
{
    public async Task<CaptureBundle> CaptureAsync(
        string repositoryRoot,
        DevContextConfig config,
        string? baselineId,
        CapturePurpose purpose,
        BaselineReference? baseline,
        CancellationToken cancellationToken)
    {
        var id = baselineId ?? CreateBaselineId();
        var timings = new List<StageTiming>();
        var rawLogs = new Dictionary<string, string>(StringComparer.Ordinal);

        var (git, gitTiming) = await TimedAsync(
            "git",
            () => gitService.CaptureAsync(repositoryRoot, cancellationToken));
        timings.Add(gitTiming);

        var (graph, graphTiming) = await TimedAsync(
            "repository-graph",
            () => graphService.BuildAsync(repositoryRoot, config, cancellationToken));
        timings.Add(graphTiming);

        var ((build, buildLog), buildTiming) = await TimedAsync(
            "build",
            () => buildService.CaptureAsync(repositoryRoot, config, cancellationToken));
        timings.Add(buildTiming);
        rawLogs["build.log"] = buildLog;

        AffectedReport? affected = null;
        if (purpose == CapturePurpose.Verification
            && config.Tests.VerifyMode is "affected-first" or "affected-only")
        {
            if (baseline is null)
            {
                throw new InvalidOperationException(
                    "Affected test execution requires a stored baseline and indexes.");
            }

            affected = AffectedCalculator.Calculate(
                baseline.Status,
                baseline.Symbols,
                baseline.Dependencies,
                git,
                graph);
        }

        var testPlan = new TestExecutionPlan(
            purpose == CapturePurpose.Baseline
                ? config.Tests.BaselineMode
                : config.Tests.VerifyMode,
            affected);

        var ((tests, testLog), testTiming) = await TimedAsync(
            "tests",
            () => testService.CaptureAsync(
                repositoryRoot,
                config,
                graph.Repository,
                id,
                testPlan,
                cancellationToken));
        timings.Add(testTiming);
        rawLogs["tests.log"] = testLog;

        var ((analysis, analysisLog), analysisTiming) = await TimedAsync(
            "analysis",
            () => analysisService.CaptureAsync(repositoryRoot, config, build, id, cancellationToken));
        timings.Add(analysisTiming);
        rawLogs["analysis.log"] = analysisLog;

        var sdkResult = await processRunner.RunAsync(
            "dotnet",
            ["--version"],
            repositoryRoot,
            cancellationToken);

        var manifest = new BaselineManifest
        {
            BaselineId = id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RepositoryRoot = repositoryRoot.Replace('\\', '/'),
            Branch = git.Branch,
            HeadCommit = git.HeadCommit,
            WorkingTreeDirty = git.Files.Count > 0,
            SdkVersion = sdkResult.State == ExecutionState.Succeeded
                ? sdkResult.StandardOutput.Trim()
                : "unavailable",
            Timings = timings,
            RepositoryInputHash = graph.InputHash,
            RepositoryIndexCacheHit = graph.CacheHit
        };

        return new CaptureBundle(
            manifest,
            git,
            build,
            tests,
            analysis,
            graph.Repository,
            graph.Symbols,
            graph.Dependencies,
            affected,
            rawLogs);
    }

    private static string CreateBaselineId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..27];

    private static async Task<(T Value, StageTiming Timing)> TimedAsync<T>(
        string stage,
        Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = await action();
        stopwatch.Stop();
        return (value, new StageTiming(stage, stopwatch.ElapsedMilliseconds));
    }
}
