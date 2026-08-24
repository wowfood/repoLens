using System.Globalization;
using System.Xml.Linq;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record TestExecutionPlan(string Mode, AffectedReport? Affected);

internal sealed record TestBatch(
    ExecutionState State,
    long DurationMilliseconds,
    IReadOnlyList<TestOutcomeRecord> Outcomes,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> CoverageFiles,
    string? Detail,
    string RawLog);

internal sealed class TestService(IProcessRunner processRunner)
{
    public async Task<(TestSnapshot Tests, string RawLog)> CaptureAsync(
        string repositoryRoot,
        DevContextConfig config,
        RepositoryIndex projects,
        string runId,
        TestExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        if (!config.Tests.Enabled || plan.Mode == "none")
        {
            return (Skipped(
                plan.Mode,
                "Test execution is disabled by repository configuration.",
                false,
                config.Tests.CollectCoverage), string.Empty);
        }

        var testProjects = projects.Projects.Where(project => project.IsTestProject).ToArray();
        if (testProjects.Length == 0)
        {
            return (Skipped(
                plan.Mode,
                "No test projects were discovered.",
                true,
                config.Tests.CollectCoverage), string.Empty);
        }

        var resultRoot = Path.Combine(ContextPaths.Runs(repositoryRoot), runId, "tests");
        var coverageRoot = Path.Combine(ContextPaths.Runs(repositoryRoot), runId, "coverage");
        Directory.CreateDirectory(resultRoot);
        try
        {
            if (plan.Mode == "all")
            {
                var allBatch = await RunBatchAsync(
                    repositoryRoot,
                    testProjects,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    Path.Combine(resultRoot, "all"),
                    coverageRoot,
                    "all",
                    config.Tests.CollectCoverage,
                    cancellationToken);
                return (ToSnapshot(
                    allBatch,
                    plan.Mode,
                    true,
                    false,
                    config.Tests.CollectCoverage), allBatch.RawLog);
            }

            var affectedProjects = testProjects
                .Where(project => plan.Affected?.Tests.Contains(
                    project.Path,
                    StringComparer.OrdinalIgnoreCase) == true)
                .ToArray();
            if (affectedProjects.Length == 0)
            {
                if (plan.Mode == "affected-only")
                {
                    return (Skipped(
                        plan.Mode,
                        "No affected test projects were identified.",
                        false,
                        config.Tests.CollectCoverage), string.Empty);
                }

                var fullWithoutTargets = await RunBatchAsync(
                    repositoryRoot,
                    testProjects,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    Path.Combine(resultRoot, "full"),
                    coverageRoot,
                    "full",
                    config.Tests.CollectCoverage,
                    cancellationToken);
                return (ToSnapshot(
                    fullWithoutTargets,
                    plan.Mode,
                    true,
                    false,
                    config.Tests.CollectCoverage), fullWithoutTargets.RawLog);
            }

            var filters = BuildFilters(plan.Affected!, affectedProjects);
            var targeted = await RunBatchAsync(
                repositoryRoot,
                affectedProjects,
                filters,
                Path.Combine(resultRoot, "targeted"),
                coverageRoot,
                "targeted",
                config.Tests.CollectCoverage,
                cancellationToken);
            var targetedFailed = targeted.State is ExecutionState.Failed or ExecutionState.Unavailable
                                 || targeted.Outcomes.Any(outcome => IsFailed(outcome.Outcome));
            if (plan.Mode == "affected-only" || targetedFailed)
            {
                return (ToSnapshot(
                    targeted,
                    plan.Mode,
                    false,
                    false,
                    config.Tests.CollectCoverage), targeted.RawLog);
            }

            var fullSuite = await RunBatchAsync(
                repositoryRoot,
                testProjects,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                Path.Combine(resultRoot, "full"),
                coverageRoot,
                "full",
                config.Tests.CollectCoverage,
                cancellationToken);
            var combined = fullSuite with
            {
                DurationMilliseconds = targeted.DurationMilliseconds + fullSuite.DurationMilliseconds,
                Projects = targeted.Projects.Concat(fullSuite.Projects)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                RawLog = string.Join(
                    Environment.NewLine,
                    new[] { targeted.RawLog, fullSuite.RawLog }
                        .Where(value => !string.IsNullOrWhiteSpace(value)))
            };
            return (ToSnapshot(
                combined,
                plan.Mode,
                true,
                true,
                config.Tests.CollectCoverage), combined.RawLog);
        }
        finally
        {
            if (!config.Storage.RetainRawLogs && Directory.Exists(resultRoot))
            {
                Directory.Delete(resultRoot, true);
            }
        }
    }

    private async Task<TestBatch> RunBatchAsync(
        string repositoryRoot,
        IReadOnlyList<ProjectRecord> testProjects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        string resultDirectory,
        string coverageDirectory,
        string batchName,
        bool collectCoverage,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(resultDirectory);
        var outcomes = new Dictionary<string, TestOutcomeRecord>(StringComparer.Ordinal);
        var logs = new List<string>();
        var executedProjects = new List<string>();
        var coverageFiles = new List<string>();
        var totalDuration = 0L;
        var state = ExecutionState.Succeeded;
        string? detail = null;

        foreach (var project in testProjects)
        {
            var projectPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                project.Path.Replace('/', Path.DirectorySeparatorChar)));
            var resultFile = SanitizeFileName(project.Name) + ".trx";
            var projectResultDirectory = Path.Combine(
                resultDirectory,
                $"{SanitizeFileName(project.Name)}-{Hashing.Text(project.Path)[..8]}");
            Directory.CreateDirectory(projectResultDirectory);
            var arguments = new List<string>
            {
                "test",
                projectPath,
                "--no-build",
                "--nologo",
                "--logger",
                $"trx;LogFileName={resultFile}",
                "--results-directory",
                projectResultDirectory
            };
            if (collectCoverage)
            {
                arguments.Add("--collect");
                arguments.Add("XPlat Code Coverage");
            }
            if (filters.TryGetValue(project.Path, out var testCases) && testCases.Count > 0)
            {
                arguments.Add("--filter");
                arguments.Add(BuildFilterExpression(testCases));
            }

            var result = await processRunner.RunAsync(
                "dotnet",
                arguments,
                repositoryRoot,
                cancellationToken);
            executedProjects.Add(project.Path);
            totalDuration += result.DurationMilliseconds;
            logs.Add(result.Command);
            logs.Add(result.StandardOutput);
            logs.Add(result.StandardError);
            if (result.State != ExecutionState.Succeeded)
            {
                state = result.State;
                detail ??= FirstUsefulLine(result.StandardError, result.StandardOutput);
            }

            if (collectCoverage)
            {
                Directory.CreateDirectory(coverageDirectory);
                var reports = Directory.EnumerateFiles(
                        projectResultDirectory,
                        "*.cobertura.xml",
                        SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (var reportIndex = 0; reportIndex < reports.Length; reportIndex++)
                {
                    var destination = Path.Combine(
                        coverageDirectory,
                        $"{batchName}-{SanitizeFileName(project.Name)}-{Hashing.Text(project.Path)[..8]}-" +
                        $"{reportIndex + 1}.cobertura.xml");
                    File.Copy(reports[reportIndex], destination, true);
                    coverageFiles.Add(Path.GetRelativePath(repositoryRoot, destination).Replace('\\', '/'));
                }
            }

            var trxPath = Directory.EnumerateFiles(projectResultDirectory, resultFile, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (trxPath is null)
            {
                continue;
            }

            foreach (var outcome in ParseTrx(trxPath))
            {
                outcomes[outcome.Identity] = outcome;
            }
        }

        return new TestBatch(
            state,
            totalDuration,
            outcomes.Values.OrderBy(outcome => outcome.Identity, StringComparer.Ordinal).ToArray(),
            executedProjects.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            coverageFiles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            detail,
            string.Join(Environment.NewLine, logs.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildFilters(
        AffectedReport affected,
        IReadOnlyList<ProjectRecord> projects)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var tests = affected.Symbols
                .Where(symbol => symbol.Kind == "test"
                                 && symbol.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
                .Select(symbol => string.Join('.', new[]
                {
                    symbol.Namespace,
                    symbol.ContainingType,
                    symbol.Name
                }.Where(part => !string.IsNullOrWhiteSpace(part))))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (tests.Length > 0)
            {
                result[project.Path] = tests;
            }
        }

        return result;
    }

    private static string BuildFilterExpression(IEnumerable<string> testCases) =>
        string.Join('|', testCases.Select(test => $"FullyQualifiedName={EscapeFilterValue(test)}"));

    private static string EscapeFilterValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal);

    private static TestSnapshot ToSnapshot(
        TestBatch batch,
        string mode,
        bool isComplete,
        bool ranFullSuiteAfterTargetedTests,
        bool coverageRequested)
    {
        return new TestSnapshot
        {
            State = batch.State,
            Total = batch.Outcomes.Count,
            Passed = batch.Outcomes.Count(outcome => IsPassed(outcome.Outcome)),
            Failed = batch.Outcomes.Count(outcome => IsFailed(outcome.Outcome)),
            Skipped = batch.Outcomes.Count(outcome => IsSkipped(outcome.Outcome)),
            DurationMilliseconds = batch.DurationMilliseconds,
            Outcomes = batch.Outcomes,
            Detail = batch.Detail,
            Mode = mode,
            IsComplete = isComplete,
            RanFullSuiteAfterTargetedTests = ranFullSuiteAfterTargetedTests,
            ProjectsExecuted = batch.Projects,
            CoverageRequested = coverageRequested,
            CoverageFiles = batch.CoverageFiles,
            CoverageDetail = coverageRequested && batch.CoverageFiles.Count == 0
                ? "XPlat Code Coverage produced no Cobertura report. Ensure each test project references a compatible Coverlet collector."
                : null
        };
    }

    internal static IReadOnlyList<TestOutcomeRecord> ParseTrx(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest")
            .Select(element => new
            {
                Id = element.Attribute("id")?.Value,
                ClassName = element.Descendants()
                    .FirstOrDefault(child => child.Name.LocalName == "TestMethod")
                    ?.Attribute("className")?.Value
            })
            .Where(item => item.Id is not null)
            .ToDictionary(item => item.Id!, item => item.ClassName, StringComparer.Ordinal);

        return document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult")
            .Select(element =>
            {
                var id = element.Attribute("testId")?.Value;
                var name = element.Attribute("testName")?.Value ?? "unknown test";
                definitions.TryGetValue(id ?? string.Empty, out var className);
                var outcome = element.Attribute("outcome")?.Value ?? "Unknown";
                var duration = ParseDuration(element.Attribute("duration")?.Value);
                var error = element.Descendants()
                    .FirstOrDefault(child => child.Name.LocalName == "Message")?.Value;
                var identity = Hashing.Text(string.Join('|', className, name));
                return new TestOutcomeRecord(identity, name, className, outcome, duration, error);
            })
            .OrderBy(outcome => outcome.Identity, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool IsFailed(string outcome) =>
        outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Error", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Timeout", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Aborted", StringComparison.OrdinalIgnoreCase);

    private static bool IsPassed(string outcome) =>
        outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkipped(string outcome) =>
        outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Inconclusive", StringComparison.OrdinalIgnoreCase);

    private static long ParseDuration(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? (long)duration.TotalMilliseconds
            : 0;

    private static TestSnapshot Skipped(
        string mode,
        string detail,
        bool isComplete,
        bool coverageRequested) => new()
        {
            State = ExecutionState.Skipped,
            Total = 0,
            Passed = 0,
            Failed = 0,
            Skipped = 0,
            DurationMilliseconds = 0,
            Outcomes = [],
            Detail = detail,
            Mode = mode,
            IsComplete = isComplete,
            ProjectsExecuted = [],
            CoverageRequested = coverageRequested,
            CoverageFiles = [],
            CoverageDetail = coverageRequested ? "Coverage was not collected because tests did not run." : null
        };

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string FirstUsefulLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
        ?? values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault()
        ?? "Test command failed without output.";
}
