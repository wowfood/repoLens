using System.Text.Json;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

/// <summary>
/// Covers the acceptance conditions of the retrieval benchmark itself. Every assertion here exists
/// because the corresponding condition previously either did not run or could not fail.
/// </summary>
[TestClass]
public sealed class EvidenceBenchmarkTests
{
    [TestMethod]
    public async Task PrecisionFloor_FailsWhenTheBundleIsPaddedWithUnexpectedFiles()
    {
        await using var repository = await BenchmarkRepository.CreateAsync();

        var lenient = await repository.RunAsync(BaseCase() with { MinPrecision = 0d });
        var strict = await repository.RunAsync(BaseCase() with { MinPrecision = 1d });

        // Recall alone cannot distinguish these two runs: both retrieve the expected file. Only the
        // precision floor sees that the strict case also dragged in files nobody asked for.
        Assert.AreEqual(1d, lenient.Cases[0].FileRecall);
        Assert.AreEqual(1d, strict.Cases[0].FileRecall);
        Assert.IsTrue(lenient.Passed, string.Join(" ", lenient.Cases[0].FailureReasons));
        Assert.IsFalse(strict.Passed);
        Assert.IsNotEmpty(strict.Cases[0].UnexpectedFiles);
        Assert.IsTrue(
            strict.Cases[0].FailureReasons.Any(reason => reason.Contains("Precision", StringComparison.Ordinal)),
            string.Join(" ", strict.Cases[0].FailureReasons));
    }

    [TestMethod]
    public async Task RelationshipExpectation_ResolvesEndpointsToFilesAndFailsWhenTheEdgeIsAbsent()
    {
        await using var repository = await BenchmarkRepository.CreateAsync();

        var present = await repository.RunAsync(BaseCase() with
        {
            ExpectedRelationships =
            [
                "method-call: src/Sample/DashboardViewModel.cs -> src/Sample/ISongCatalog.cs"
            ]
        });
        var wrongEndpoint = await repository.RunAsync(BaseCase() with
        {
            ExpectedRelationships =
            [
                "method-call: src/Sample/ISongCatalog.cs -> src/Sample/DashboardViewModel.cs"
            ]
        });
        var wrongKind = await repository.RunAsync(BaseCase() with
        {
            ExpectedRelationships =
            [
                // The two types are joined by a primary-constructor parameter in the graph, but that
                // edge is between the type symbols, not the two method symbols this bundle selected.
                "constructor-parameter: src/Sample/DashboardViewModel.cs -> src/Sample/ISongCatalog.cs"
            ]
        });

        Assert.IsTrue(present.Passed, string.Join(" ", present.Cases[0].FailureReasons));
        Assert.IsFalse(wrongEndpoint.Passed, "A reversed edge must not satisfy a direction-aware expectation.");
        Assert.IsFalse(wrongKind.Passed, "A different relationship kind between the same files must not satisfy it.");
    }

    [TestMethod]
    public async Task BareRelationshipExpectation_MatchesOnKindAlone()
    {
        await using var repository = await BenchmarkRepository.CreateAsync();

        var kindOnly = await repository.RunAsync(BaseCase() with
        {
            ExpectedRelationships = ["method-call"]
        });
        var absentKind = await repository.RunAsync(BaseCase() with
        {
            ExpectedRelationships = ["tests-covering"]
        });

        Assert.IsTrue(kindOnly.Passed, string.Join(" ", kindOnly.Cases[0].FailureReasons));
        Assert.IsFalse(absentKind.Passed);
        CollectionAssert.Contains(absentKind.Cases[0].MissingRelationships.ToArray(), "tests-covering");
    }

    [TestMethod]
    public async Task TokenCeiling_IsSeparateFromTheQueryBudget()
    {
        await using var repository = await BenchmarkRepository.CreateAsync();

        var atBudget = await repository.RunAsync(BaseCase());
        var observed = atBudget.Cases[0].ApproximateTokens;

        // A bundle can never exceed the budget it was handed, so asserting against MaxTokens alone
        // detects nothing. Growth is only visible against a ceiling set below the budget.
        Assert.IsTrue(atBudget.Passed, string.Join(" ", atBudget.Cases[0].FailureReasons));
        Assert.IsLessThanOrEqualTo(BaseCase().MaxTokens, observed);

        var tightened = await repository.RunAsync(BaseCase() with { MaxApproximateTokens = observed - 1 });

        Assert.IsFalse(tightened.Passed);
        Assert.IsTrue(
            tightened.Cases[0].FailureReasons.Any(reason => reason.Contains("Approximate tokens", StringComparison.Ordinal)),
            string.Join(" ", tightened.Cases[0].FailureReasons));
    }

    [TestMethod]
    public async Task AdvisoryCase_ReportsItsGapWithoutFailingTheRun()
    {
        await using var repository = await BenchmarkRepository.CreateAsync();

        var blocking = await repository.RunAsync(BaseCase() with
        {
            ExpectedFiles = ["src/Sample/NotHere.cs"]
        });
        var advisory = await repository.RunAsync(BaseCase() with
        {
            ExpectedFiles = ["src/Sample/NotHere.cs"],
            Advisory = true
        });

        Assert.IsFalse(blocking.Passed);
        Assert.AreEqual(0, blocking.AdvisoryFailures);

        // The advisory case still measures and reports the gap; it just does not stop the build.
        Assert.IsTrue(advisory.Passed);
        Assert.AreEqual(1, advisory.AdvisoryFailures);
        Assert.IsNotEmpty(advisory.Cases[0].FailureReasons);
        Assert.IsTrue(advisory.Cases[0].Advisory);
    }

    [TestMethod]
    public async Task Determinism_ComparesABundleRebuiltFromThePersistedGraph()
    {
        await using var repository = await BenchmarkRepository.CreateAsync(cacheEnabled: true);

        var report = await repository.RunAsync(BaseCase());

        // The warm run alone only proves one in-memory graph yields one bundle. The benchmark also
        // drops the memory cache and rebuilds, which is the run that can catch a round-trip bug.
        Assert.IsTrue(report.Cases[0].Deterministic, string.Join(" ", report.Cases[0].FailureReasons));
    }

    [TestMethod]
    public void SelfCorpus_ExpectsFilesThatStillExist()
    {
        AssertExpectedFilesExist(
            Path.Combine(RepositoryRoot(), "RepoLens", "benchmarks", "evidence-corpus.json"),
            RepositoryRoot());
    }

    [TestMethod]
    public void FixtureCorpus_ExpectsFilesThatStillExist()
    {
        var fixture = Path.Combine(RepositoryRoot(), "RepoLens.Tests", "Fixtures", "BenchmarkRepo");
        AssertExpectedFilesExist(Path.Combine(fixture, "corpus.json"), fixture, ".fixture");
    }

    /// <summary>
    /// A corpus whose expected paths have been renamed away reports a recall collapse that looks
    /// like a retrieval regression. Failing here instead names the real cause.
    /// </summary>
    private static void AssertExpectedFilesExist(string corpusPath, string root, string suffix = "")
    {
        Assert.IsTrue(File.Exists(corpusPath), $"The corpus was not found at {corpusPath}.");
        var cases = JsonSerializer.Deserialize<IReadOnlyList<EvidenceBenchmarkCase>>(
            File.ReadAllText(corpusPath),
            JsonDefaults.Options);
        Assert.IsNotNull(cases);
        Assert.IsNotEmpty(cases);

        var missing = cases
            .SelectMany(benchmarkCase => benchmarkCase.ExpectedFiles
                .Select(file => (benchmarkCase.Name, File: file)))
            .Where(entry => !File.Exists(Path.Combine(root, entry.File.Replace('/', Path.DirectorySeparatorChar) + suffix)))
            .Select(entry => $"{entry.Name}: {entry.File}")
            .ToArray();

        Assert.IsEmpty(missing, $"Corpus cases expect files that no longer exist: {string.Join(", ", missing)}");
    }

    private static EvidenceBenchmarkCase BaseCase() => new()
    {
        Name = "dashboard",
        Query = "refresh dashboard songs",
        ExpectedFiles = ["src/Sample/DashboardViewModel.cs"],
        MaxTokens = 1400,
        MaxResults = 8,
        GraphDepth = 1
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RepoLens.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "The repository root containing RepoLens.sln was not found.");
        return directory.FullName;
    }

    /// <summary>
    /// A small repository with one production project and one test project, enough for the ranker to
    /// have both relevant and irrelevant files to choose between.
    /// </summary>
    private sealed class BenchmarkRepository : IAsyncDisposable
    {
        private readonly EvidenceBenchmarkService benchmark;
        private readonly DevContextConfig configuration;

        private BenchmarkRepository(
            string root,
            EvidenceBenchmarkService benchmark,
            DevContextConfig configuration)
        {
            Root = root;
            this.benchmark = benchmark;
            this.configuration = configuration;
        }

        public string Root { get; }

        public static async Task<BenchmarkRepository> CreateAsync(bool cacheEnabled = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"repolens-benchmark-tests-{Guid.NewGuid():N}");
            var production = Path.Combine(root, "src", "Sample");
            var tests = Path.Combine(root, "tests", "Sample.Tests");
            Directory.CreateDirectory(production);
            Directory.CreateDirectory(tests);
            await File.WriteAllTextAsync(
                Path.Combine(production, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(
                Path.Combine(production, "ISongCatalog.cs"),
                "namespace Sample; public interface ISongCatalog { Task<string[]> GetSongsAsync(); }");
            await File.WriteAllTextAsync(
                Path.Combine(production, "DashboardViewModel.cs"),
                """
                namespace Sample;
                public sealed class DashboardViewModel(ISongCatalog songs)
                {
                    public string[] CurrentSongs { get; private set; } = [];
                    public async Task RefreshDashboardAsync()
                    {
                        CurrentSongs = await songs.GetSongsAsync();
                    }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(production, "Unrelated.cs"),
                """
                namespace Sample;
                /// <summary>Padding so that retrieving everything is not automatically precise.</summary>
                public sealed class DashboardPreferences
                {
                    public bool ShowSongs { get; set; }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(tests, "Sample.Tests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../../src/Sample/Sample.csproj" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(tests, "DashboardTests.cs"),
                """
                namespace Sample.Tests;
                public sealed class DashboardTests
                {
                    public async Task RefreshDashboardLoadsSongs(DashboardViewModel dashboard)
                    {
                        await dashboard.RefreshDashboardAsync();
                    }
                }
                """);

            IProcessRunner runner = new ProcessRunner();
            var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
            Assert.AreEqual(ExecutionState.Succeeded, git.State, git.StandardError);

            var files = new RepositoryFileFilter(runner);
            var graph = new RepositoryGraphService(runner, new ProjectIndexer(runner, files), files);
            var evidence = new EvidenceQueryService(graph, new GitService(runner), new ContextStore(), files);
            return new BenchmarkRepository(
                root,
                new EvidenceBenchmarkService(evidence, graph),
                new DevContextConfig { Cache = new CacheConfig { Enabled = cacheEnabled } });
        }

        public Task<EvidenceBenchmarkReport> RunAsync(EvidenceBenchmarkCase benchmarkCase) =>
            benchmark.RunAsync(Root, configuration, [benchmarkCase], CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // A leaked temporary directory must not turn into a spurious test failure.
            }

            return ValueTask.CompletedTask;
        }
    }
}
