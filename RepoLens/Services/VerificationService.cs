using DevContext.Configuration;
using DevContext.Core;

namespace DevContext.Services;

internal sealed class VerificationService(
    BaselineCaptureService captureService,
    ContextStore store)
{
    public async Task<VerificationReport> VerifyAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var baseline = await store.ReadStatusAsync(repositoryRoot, cancellationToken);
        var (baselineSymbols, baselineDependencies) = await store.ReadIndexesAsync(
            repositoryRoot,
            cancellationToken);
        var current = await captureService.CaptureAsync(
            repositoryRoot,
            config,
            baseline.Manifest.BaselineId,
            CapturePurpose.Verification,
            new BaselineReference(baseline, baselineSymbols, baselineDependencies),
            cancellationToken);

        var changedFiles = GitService.ChangedSince(baseline.Git, current.Git);
        var baselineDiagnostics = baseline.Analysis.Diagnostics.ToDictionary(
            diagnostic => diagnostic.Identity,
            StringComparer.Ordinal);
        var currentDiagnostics = current.Analysis.Diagnostics.ToDictionary(
            diagnostic => diagnostic.Identity,
            StringComparer.Ordinal);
        var newDiagnostics = currentDiagnostics.Keys
            .Except(baselineDiagnostics.Keys, StringComparer.Ordinal)
            .Select(identity => currentDiagnostics[identity])
            .OrderBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ToArray();
        var resolvedDiagnostics = baselineDiagnostics.Keys
            .Except(currentDiagnostics.Keys, StringComparer.Ordinal)
            .Select(identity => baselineDiagnostics[identity])
            .OrderBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ToArray();

        var baselineFailures = baseline.Tests.Outcomes
            .Where(outcome => TestService.IsFailed(outcome.Outcome))
            .ToDictionary(outcome => outcome.Identity, StringComparer.Ordinal);
        var currentFailures = current.Tests.Outcomes
            .Where(outcome => TestService.IsFailed(outcome.Outcome))
            .ToDictionary(outcome => outcome.Identity, StringComparer.Ordinal);
        var currentOutcomes = current.Tests.Outcomes.ToDictionary(
            outcome => outcome.Identity,
            StringComparer.Ordinal);
        var newFailures = currentFailures.Keys
            .Except(baselineFailures.Keys, StringComparer.Ordinal)
            .Select(identity => currentFailures[identity])
            .OrderBy(outcome => outcome.Name, StringComparer.Ordinal)
            .ToArray();
        var existingFailures = currentFailures.Keys
            .Intersect(baselineFailures.Keys, StringComparer.Ordinal)
            .Select(identity => currentFailures[identity])
            .OrderBy(outcome => outcome.Name, StringComparer.Ordinal)
            .ToArray();
        var resolvedFailures = baselineFailures.Keys
            .Where(identity => !currentFailures.ContainsKey(identity)
                               && (current.Tests.IsComplete || currentOutcomes.ContainsKey(identity)))
            .Select(identity => baselineFailures[identity])
            .OrderBy(outcome => outcome.Name, StringComparer.Ordinal)
            .ToArray();

        var buildRegressed = baseline.Build.State == ExecutionState.Succeeded
                             && current.Build.State != ExecutionState.Succeeded;
        var formatRegressed = baseline.Analysis.DotnetFormat.State == ExecutionState.Succeeded
                              && current.Analysis.DotnetFormat.State != ExecutionState.Succeeded;
        var qodanaRegressed = baseline.Analysis.Qodana.State == ExecutionState.Succeeded
                              && current.Analysis.Qodana.State != ExecutionState.Succeeded;
        var policyDiagnostics = newDiagnostics.Any(diagnostic =>
            diagnostic.Severity == "error"
            || config.Analysis.FailOnNewWarnings && diagnostic.Severity == "warning");
        var executionFailed = current.Build.State == ExecutionState.Unavailable
                              || current.Tests.State == ExecutionState.Unavailable
                              || current.Tests.State == ExecutionState.Failed
                              && current.Tests.Outcomes.Count == 0
                              || config.Analysis.DotnetFormat
                              && current.Analysis.DotnetFormat.State is ExecutionState.Failed or ExecutionState.Unavailable
                              || config.Analysis.Qodana
                              && current.Analysis.Qodana.State is ExecutionState.Failed or ExecutionState.Unavailable;

        var report = new VerificationReport
        {
            BaselineId = baseline.Manifest.BaselineId,
            VerifiedAtUtc = DateTimeOffset.UtcNow,
            ChangedFiles = changedFiles,
            ChangedSymbols = current.Affected?.ChangedSymbols ?? [],
            NewDiagnostics = newDiagnostics,
            ResolvedDiagnostics = resolvedDiagnostics,
            NewFailingTests = newFailures,
            ExistingFailingTests = existingFailures,
            ResolvedFailingTests = resolvedFailures,
            CurrentBuild = current.Build,
            CurrentTests = current.Tests,
            CurrentAnalysis = current.Analysis,
            HasRegressions = buildRegressed || formatRegressed || qodanaRegressed
                             || policyDiagnostics || newFailures.Length > 0,
            HasExecutionFailures = executionFailed
        };

        await store.SaveCurrentAsync(repositoryRoot, current, config, report, cancellationToken);
        return report;
    }
}
