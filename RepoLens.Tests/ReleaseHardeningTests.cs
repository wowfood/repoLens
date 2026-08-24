using System.Text.Json;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class ReleaseHardeningTests
{
    [TestMethod]
    public async Task ConfigurationV1_IsMigratedInMemoryAndWrittenAsV2OnSave()
    {
        var repository = CreateTemporaryDirectory();
        var configPath = ContextPaths.Config(repository);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{\"version\":1}");

        try
        {
            var (configuration, isNew, requiresSave) = await ConfigLoader.LoadAsync(
                repository,
                CancellationToken.None);

            Assert.IsFalse(isNew);
            Assert.IsTrue(requiresSave);
            Assert.AreEqual(2, configuration.Version);
            Assert.IsFalse(configuration.Tests.CollectCoverage);

            await new ContextStore().SaveConfigIfNeededAsync(
                repository,
                configuration,
                requiresSave,
                CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            Assert.AreEqual(2, document.RootElement.GetProperty("version").GetInt32());
            Assert.IsFalse(document.RootElement.GetProperty("tests").GetProperty("collectCoverage").GetBoolean());
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task CoverageCollection_PersistsCoberturaOutsideRawTestResults()
    {
        var repository = CreateTemporaryDirectory();
        var runner = new CoverageProcessRunner();
        var service = new TestService(runner);
        var configuration = new DevContextConfig
        {
            Tests = new TestConfig { CollectCoverage = true },
            Storage = new StorageConfig { RetainRawLogs = false }
        };
        var projects = new RepositoryIndex
        {
            Solution = null,
            Projects =
            [
                new ProjectRecord(
                    "Sample.Tests",
                    "tests/Sample.Tests/Sample.Tests.csproj",
                    true,
                    ["net8.0"],
                    "enable",
                    "latest",
                    new CompilerSettingsRecord(null, false, null, null, null, null, false, false),
                    [],
                    [],
                    [])
            ]
        };

        try
        {
            var (tests, _) = await service.CaptureAsync(
                repository,
                configuration,
                projects,
                "coverage-run",
                new TestExecutionPlan("all", null),
                CancellationToken.None);

            CollectionAssert.Contains(runner.Arguments.ToArray(), "--collect");
            CollectionAssert.Contains(runner.Arguments.ToArray(), "XPlat Code Coverage");
            Assert.IsTrue(tests.CoverageRequested);
            Assert.HasCount(1, tests.CoverageFiles);
            Assert.IsTrue(File.Exists(Path.Combine(repository, tests.CoverageFiles[0])));
            Assert.IsFalse(Directory.Exists(
                Path.Combine(ContextPaths.Runs(repository), "coverage-run", "tests")));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task CoverageReports_MergeCoveredLinesAcrossTestProjects()
    {
        var repository = CreateTemporaryDirectory();
        var first = Path.Combine(repository, "first.cobertura.xml");
        var second = Path.Combine(repository, "second.cobertura.xml");
        await File.WriteAllTextAsync(first, CoverageXml(1, 0));
        await File.WriteAllTextAsync(second, CoverageXml(0, 1));

        try
        {
            var coverage = RepositoryIntelligenceService.ReadCobertura(
                repository,
                ["first.cobertura.xml", "second.cobertura.xml"]);

            Assert.AreEqual(100d, coverage["src/Sample.cs"]);
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task RetainedReports_ExposeDiagnosticFailureChurnAndCoverageDeltas()
    {
        var repository = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        try
        {
            var api = await DevContextApi.OpenAsync(repository, new DevContextConfig());
            var first = Report(repository, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) with
            {
                Diagnostics = [Diagnostic("D1")],
                FailingTests = [Failure("T1")],
                Hotspots = [Hotspot(10, 50d)],
                Markdown = "first"
            };
            var second = Report(repository, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)) with
            {
                Diagnostics = [Diagnostic("D1"), Diagnostic("D2")],
                FailingTests = [],
                Hotspots = [Hotspot(15, 75d)],
                Markdown = "second"
            };
            await api.SaveReportAsync(first);
            await api.SaveReportAsync(second);

            var trend = await api.TrendAsync();

            Assert.HasCount(2, trend.Points);
            var latest = trend.Points[1];
            Assert.AreEqual(1, latest.DiagnosticDelta);
            Assert.AreEqual(-1, latest.FailingTestDelta);
            Assert.AreEqual(5L, latest.HotspotChurnDelta);
            Assert.AreEqual(25d, latest.AverageLineCoverageDelta);
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public void JsonSchemaCatalog_DescribesCurrentCoverageAndTrendContracts()
    {
        var testsSchema = DevContextApi.GetJsonSchema("tests");
        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            testsSchema["$schema"]!.GetValue<string>());
        var definitions = testsSchema["$defs"]!.AsObject();
        var testProperties = definitions[nameof(TestSnapshot)]!["properties"]!.AsObject();
        Assert.IsTrue(testProperties.ContainsKey("coverageFiles"));
        Assert.AreEqual(
            SchemaVersions.Current,
            testProperties["schemaVersion"]!["const"]!.GetValue<int>());
        Assert.IsTrue(DevContextApi.JsonSchemaDocuments.Contains("trend-point"));

        var configurationSchema = DevContextApi.GetJsonSchema("configuration");
        var configurationDefinitions = configurationSchema["$defs"]!.AsObject();
        var testConfigProperties = configurationDefinitions[nameof(TestConfig)]!["properties"]!.AsObject();
        Assert.IsTrue(testConfigProperties.ContainsKey("collectCoverage"));
        var configurationProperties = configurationDefinitions[nameof(DevContextConfig)]!["properties"]!.AsObject();
        Assert.AreEqual(2, configurationProperties["version"]!["const"]!.GetValue<int>());

        var catalog = DevContextApi.GetJsonSchema();
        Assert.HasCount(
            DevContextApi.JsonSchemaDocuments.Count,
            catalog["$defs"]!.AsObject());
    }

    [TestMethod]
    public async Task PersistedManifestFixtures_CoverEveryReadableSchemaVersion()
    {
        foreach (var version in Enumerable.Range(
                     SchemaVersions.MinimumReadable,
                     SchemaVersions.Current - SchemaVersions.MinimumReadable + 1))
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "PersistedSchemas",
                $"v{version}",
                "manifest.json");
            var manifest = await JsonFile.ReadAsync<BaselineManifest>(path, CancellationToken.None);
            SchemaVersions.EnsureReadable(manifest.SchemaVersion, Path.GetFileName(path));
            Assert.AreEqual(version, manifest.SchemaVersion);
        }
    }

    private static RepositoryContextReport Report(string repository, DateTimeOffset generatedAt) => new()
    {
        GeneratedAtUtc = generatedAt,
        RepositoryRoot = repository,
        Purpose = ContextPurpose.Risk,
        Scope = ContextScope.FullRepository,
        AnalyzedProjects = [],
        AnalyzedFiles = [],
        ChangedFiles = [],
        Diagnostics = [],
        FailingTests = [],
        ProjectDependencies = [],
        Symbols = [],
        Types = [],
        Methods = [],
        Hotspots = [],
        Markdown = string.Empty,
        ApproximateTokens = 1
    };

    private static DiagnosticRecord Diagnostic(string identity) =>
        new(identity, "test", "warning", identity, "src/Sample.cs", 1, 1, identity, "Sample");

    private static TestOutcomeRecord Failure(string identity) =>
        new(identity, identity, "SampleTests", "Failed", 1, "failure");

    private static FileHotspot Hotspot(long churn, double coverage) => new()
    {
        Rank = 1,
        Path = "src/Sample.cs",
        Project = "Sample",
        LinesOfCode = 10,
        MaximumCyclomaticComplexity = 2,
        OutgoingDependencyCount = 0,
        IncomingDependencyCount = 0,
        DiagnosticCount = 0,
        CommitCount = 1,
        ContributorCount = 1,
        Churn = churn,
        LineCoveragePercent = coverage,
        SelectionReasons = []
    };

    private static string CoverageXml(int firstHits, int secondHits) =>
        $$"""
        <coverage>
          <packages><package><classes>
            <class filename="src/Sample.cs" line-rate="0.5">
              <lines>
                <line number="1" hits="{{firstHits}}" />
                <line number="2" hits="{{secondHits}}" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CoverageProcessRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public async Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            var resultsIndex = Arguments.ToList().IndexOf("--results-directory");
            var resultsDirectory = Arguments[resultsIndex + 1];
            var loggerIndex = Arguments.ToList().IndexOf("--logger");
            var resultFile = Arguments[loggerIndex + 1].Split('=', 2)[1];
            Directory.CreateDirectory(resultsDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(resultsDirectory, resultFile),
                """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions><UnitTest id="id"><TestMethod className="Tests" /></UnitTest></TestDefinitions>
                  <Results><UnitTestResult testId="id" testName="Passes" outcome="Passed" /></Results>
                </TestRun>
                """,
                cancellationToken);
            var attachments = Path.Combine(resultsDirectory, "attachments");
            Directory.CreateDirectory(attachments);
            await File.WriteAllTextAsync(
                Path.Combine(attachments, "coverage.cobertura.xml"),
                CoverageXml(1, 0),
                cancellationToken);
            return new ProcessResult(
                ExecutionState.Succeeded,
                0,
                string.Empty,
                string.Empty,
                1,
                "dotnet test");
        }
    }
}
