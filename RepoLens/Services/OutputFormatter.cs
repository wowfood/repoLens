using System.Text;
using DevContext.Core;

namespace DevContext.Services;

public static class OutputFormatter
{
    private const int DetailLimit = 20;

    public static string FormatStatus(StatusReport status)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Repository");
        builder.AppendLine($"  Branch: {status.Manifest.Branch ?? "(detached or unborn)"}");
        builder.AppendLine($"  Baseline: {status.Manifest.BaselineId}");
        builder.AppendLine($"  HEAD at baseline: {status.Manifest.HeadCommit ?? "(no commit)"}");
        builder.AppendLine($"  Working tree was dirty at baseline: {YesNo(status.Manifest.WorkingTreeDirty)}");
        builder.AppendLine($"  SDK: {status.Manifest.SdkVersion}");
        if (status.Manifest.RepositoryIndexCacheHit is not null)
        {
            builder.AppendLine(
                $"  Repository index cache: {(status.Manifest.RepositoryIndexCacheHit.Value ? "HIT" : "MISS")}");
        }
        builder.AppendLine();

        var productionCount = status.Repository.Projects.Count(project => !project.IsTestProject);
        var testCount = status.Repository.Projects.Count(project => project.IsTestProject);
        var frameworks = status.Repository.Projects
            .SelectMany(project => project.TargetFrameworks)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        builder.AppendLine("Solution");
        builder.AppendLine($"  Projects: {status.Repository.Projects.Count}");
        builder.AppendLine($"  Production: {productionCount}");
        builder.AppendLine($"  Tests: {testCount}");
        builder.AppendLine($"  Frameworks: {(frameworks.Length == 0 ? "unknown" : string.Join(", ", frameworks))}");
        builder.AppendLine();

        var errors = status.Analysis.Diagnostics.Count(diagnostic => diagnostic.Severity == "error");
        var warnings = status.Analysis.Diagnostics.Count(diagnostic => diagnostic.Severity == "warning");
        builder.AppendLine("Baseline health");
        builder.AppendLine($"  Build: {State(status.Build.State)}");
        builder.AppendLine(
            $"  Tests: {status.Tests.Total:N0} total / {status.Tests.Passed:N0} passed / " +
            $"{status.Tests.Failed:N0} failed / {status.Tests.Skipped:N0} skipped ({State(status.Tests.State)})");
        builder.AppendLine($"  Test mode: {FormatTestMode(status.Tests)}");
        builder.AppendLine($"  Static analysis: {errors} errors / {warnings} warnings");
        builder.AppendLine($"  Format verification: {State(status.Analysis.DotnetFormat.State)}");
        builder.AppendLine($"  Qodana: {State(status.Analysis.Qodana.State)}");

        var failures = status.Tests.Outcomes.Where(outcome => TestService.IsFailed(outcome.Outcome)).ToArray();
        AppendItems(builder, "Existing failures", failures.Select(outcome => outcome.Name));
        AppendItems(builder, "Existing modified files", status.Git.Files.Select(file => file.Path));
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatVerification(VerificationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Verification");
        builder.AppendLine($"  Baseline: {report.BaselineId}");
        builder.AppendLine($"  Build: {State(report.CurrentBuild.State)}");
        builder.AppendLine(
            $"  Tests: {report.CurrentTests.Total:N0} total / {report.CurrentTests.Passed:N0} passed / " +
            $"{report.CurrentTests.Failed:N0} failed / {report.CurrentTests.Skipped:N0} skipped " +
            $"({State(report.CurrentTests.State)})");
        builder.AppendLine($"  Test mode: {FormatTestMode(report.CurrentTests)}");
        builder.AppendLine($"  Format verification: {State(report.CurrentAnalysis.DotnetFormat.State)}");
        builder.AppendLine($"  Qodana: {State(report.CurrentAnalysis.Qodana.State)}");
        builder.AppendLine($"  Execution failures: {YesNo(report.HasExecutionFailures)}");
        builder.AppendLine($"  Regressions: {YesNo(report.HasRegressions)}");
        AppendItems(builder, "Files changed since baseline", report.ChangedFiles);
        AppendItems(builder, "Declarations changed since baseline", report.ChangedSymbols.Select(symbol =>
            $"{symbol.Kind} {FormatSymbolName(symbol)} ({symbol.File}:{symbol.Line})"));
        AppendItems(builder, "New diagnostics", report.NewDiagnostics.Select(FormatDiagnostic));
        AppendItems(builder, "Resolved diagnostics", report.ResolvedDiagnostics.Select(FormatDiagnostic));
        AppendItems(builder, "New failing tests", report.NewFailingTests.Select(test => test.Name));
        AppendItems(builder, "Existing failing tests", report.ExistingFailingTests.Select(test => test.Name));
        AppendItems(builder, "Resolved failing tests", report.ResolvedFailingTests.Select(test => test.Name));
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatAffected(AffectedReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Affected code");
        AppendItems(builder, "Changed files", report.ChangedFiles);
        AppendItems(builder, "Projects", report.Projects);
        AppendItems(builder, "Changed declarations", report.ChangedSymbols.Select(symbol =>
            $"{symbol.Kind} {FormatSymbolName(symbol)} ({symbol.File}:{symbol.Line})"));
        AppendItems(builder, "Symbols", report.Symbols.Select(symbol =>
            $"{symbol.Kind} {FormatSymbolName(symbol)} ({symbol.File}:{symbol.Line})"));
        AppendItems(builder, "Likely affected test projects", report.Tests);
        AppendItems(builder, "Likely affected test cases", report.TestCases);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatCleanup(CleanupReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Cleanup");
        builder.AppendLine($"  State: {State(report.State)}");
        builder.AppendLine($"  Command: {report.Command ?? "(none)"}");
        builder.AppendLine($"  Duration: {report.DurationMilliseconds} ms");
        if (!string.IsNullOrWhiteSpace(report.Detail))
        {
            builder.AppendLine($"  Detail: {report.Detail}");
        }

        AppendItems(builder, "Files modified by cleanup", report.ModifiedFiles);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatDoctor(DoctorReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Repository diagnostics");
        builder.AppendLine($"  Repository: {report.RepositoryRoot}");
        builder.AppendLine($"  SDK: {report.SdkVersion ?? "unavailable"}");
        builder.AppendLine($"  Baseline: {(report.BaselineExists ? "available" : "not created")}");
        builder.AppendLine($"  Overall: {(report.IsHealthy ? "READY" : "ACTION REQUIRED")}");

        builder.AppendLine();
        builder.AppendLine("Checks");
        foreach (var check in report.Checks)
        {
            builder.AppendLine($"  [{check.State.ToString().ToUpperInvariant()}] {check.Name}: {check.Detail}");
            if (!string.IsNullOrWhiteSpace(check.Recommendation))
            {
                builder.AppendLine($"    Recommendation: {check.Recommendation}");
            }
        }

        AppendItems(builder, "Projects", report.Projects.Select(project =>
            $"{project.Name} ({project.Path}) [{string.Join(", ", project.TargetFrameworks)}]"));
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatOwnership(OwnershipExplanation report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Project ownership");
        builder.AppendLine($"  Requested: {report.RequestedPath}");
        builder.AppendLine($"  Normalized: {report.NormalizedPath}");
        builder.AppendLine($"  Exists: {YesNo(report.Exists)}");
        builder.AppendLine($"  Within repository: {YesNo(report.IsWithinRepository)}");
        builder.AppendLine($"  Shared input: {YesNo(report.IsSharedInput)}");
        AppendItems(builder, "Owners", report.Owners.Select(owner =>
            $"{owner.ProjectPath} — {owner.Reason}" +
            (owner.ItemTypes.Count == 0 ? string.Empty : $" ({string.Join(", ", owner.ItemTypes)})")));
        AppendItems(builder, "Affected projects", report.AffectedProjects);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendItems(StringBuilder builder, string heading, IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        builder.AppendLine();
        builder.AppendLine(heading);
        if (items.Length == 0)
        {
            builder.AppendLine("  (none)");
            return;
        }

        foreach (var item in items.Take(DetailLimit))
        {
            builder.AppendLine($"  {item}");
        }

        if (items.Length > DetailLimit)
        {
            builder.AppendLine($"  ... and {items.Length - DetailLimit} more");
        }
    }

    private static string FormatDiagnostic(DiagnosticRecord diagnostic) =>
        $"{diagnostic.Severity} [{diagnostic.Tool}] {diagnostic.Rule}: {diagnostic.Message}" +
        (diagnostic.File is null ? string.Empty : $" ({diagnostic.File}:{diagnostic.Line})");

    private static string State(ExecutionState state) => state.ToString().ToUpperInvariant();
    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string FormatTestMode(TestSnapshot tests)
    {
        var completeness = tests.IsComplete ? "complete" : "targeted/incomplete";
        var fullSuite = tests.RanFullSuiteAfterTargetedTests ? ", full suite confirmed" : string.Empty;
        return $"{tests.Mode} ({completeness}{fullSuite})";
    }

    private static string FormatSymbolName(SymbolRecord symbol) =>
        string.Join('.', new[] { symbol.Namespace, symbol.ContainingType, symbol.Name }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
}
