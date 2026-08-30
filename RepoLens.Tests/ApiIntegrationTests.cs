using System.Diagnostics;
using System.Runtime.Versioning;
using DevContext.Configuration;
using DevContext.Core;

namespace DevContext.Tests;

[TestClass]
public sealed class ApiIntegrationTests
{
    [TestMethod]
    public async Task Affected_ReportsFilesCommittedAfterTheBaseline()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await RunGitAsync(repository, "init", "--quiet");
            await RunGitAsync(repository, "config", "user.email", "dev-context@example.test");
            await RunGitAsync(repository, "config", "user.name", "Dev Context Tests");
            await File.WriteAllTextAsync(Path.Combine(repository, "Tracked.cs"), "public class Before;");
            await RunGitAsync(repository, "add", "--all");
            await RunGitAsync(repository, "commit", "--quiet", "-m", "initial");

            var api = await DevContextApi.OpenAsync(
                repository,
                new DevContextConfig { Tests = new TestConfig { Enabled = false } });
            await api.BaselineAsync();
            await File.WriteAllTextAsync(Path.Combine(repository, "Tracked.cs"), "public class After;");
            await RunGitAsync(repository, "add", "--all");
            await RunGitAsync(repository, "commit", "--quiet", "-m", "change after baseline");

            var affected = await api.AffectedAsync();

            CollectionAssert.AreEqual(new[] { "Tracked.cs" }, affected.ChangedFiles.ToArray());
            Assert.AreEqual(GitComparisonState.Comparable, affected.GitComparison);
            Assert.AreEqual(GitChangeProvenance.Committed, affected.Changes.Single().Provenance);

            var verification = await api.VerifyAsync();
            CollectionAssert.AreEqual(new[] { "Tracked.cs" }, verification.ChangedFiles.ToArray());
            Assert.AreEqual(GitChangeProvenance.Committed, verification.Changes.Single().Provenance);
            Assert.IsFalse(verification.HasExecutionFailures);
        }
        finally
        {
            DeleteTemporaryDirectory(repository);
        }
    }

    [TestMethod]
    public async Task PublicApi_CapturesAndManagesBaselineWithoutCliProcess()
    {
        var repository = CreateTemporaryDirectory();
        var nested = Path.Combine(repository, "src", "nested");
        Directory.CreateDirectory(nested);

        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);

            var api = await DevContextApi.OpenAsync(nested);
            Assert.AreEqual(Path.GetFullPath(repository), api.RepositoryRoot);
            Assert.IsFalse(api.BaselineExists);

            var doctor = await api.DoctorAsync();
            Assert.IsNotNull(doctor.SdkVersion);
            Assert.IsTrue(doctor.Checks.Any(check => check.Name == "Project discovery"));
            Assert.IsTrue(doctor.Checks.Any(check => check.Name == "Repository scope"));
            Assert.IsFalse(api.BaselineExists, "Diagnostics must not create baseline state.");

            var explanation = await api.ExplainAsync("src");
            Assert.IsTrue(explanation.IsWithinRepository);
            Assert.IsTrue(explanation.Exists);
            Assert.IsEmpty(explanation.Owners);
            Assert.IsFalse(api.BaselineExists, "Ownership queries must not create baseline state.");

            var capture = await api.CaptureAsync();
            Assert.AreEqual(ExecutionState.Skipped, capture.Build.State);
            Assert.IsEmpty(capture.Repository.Projects);
            Assert.IsFalse(api.BaselineExists, "Stateless capture must not create baseline state.");

            var baseline = await api.BaselineAsync();
            Assert.IsTrue(api.BaselineExists);
            Assert.AreEqual(baseline.Manifest.BaselineId, (await api.StatusAsync()).Manifest.BaselineId);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => api.BaselineAsync());

            var affected = await api.AffectedAsync();
            Assert.IsEmpty(affected.ChangedFiles);

            api.Reset();
            Assert.IsFalse(api.BaselineExists);
            Assert.IsTrue(File.Exists(Path.Combine(repository, ".dev-context", "config.json")));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public void PublicApi_TargetsNet8ForOlderConsumers()
    {
        var targetFramework = typeof(DevContextApi).Assembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .OfType<TargetFrameworkAttribute>()
            .Single();

        Assert.AreEqual(".NETCoreApp,Version=v8.0", targetFramework.FrameworkName);
        Assert.AreEqual(SchemaVersions.Current, DevContextApi.Contract.CurrentSchemaVersion);
        Assert.AreEqual(SchemaVersions.MinimumReadable, DevContextApi.Contract.MinimumReadableSchemaVersion);
        Assert.IsTrue(DevContextApi.Contract.RequiresTrustedRepository);
        StringAssert.StartsWith(DevContextApi.Contract.PackageVersion, "0.11.0");
    }

    [TestMethod]
    public async Task PublicApi_BuildsScopedRiskContextAndSavesMarkdownReport()
    {
        var repository = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(repository, "src", "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(
            Path.Combine(repository, "src", "Risky.cs"),
            """
            namespace Sample;
            public sealed class Risky
            {
                public int Choose(int value)
                {
                    if (value > 10) return 1;
                    if (value > 5) return 2;
                    return value > 0 ? 3 : 4;
                }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(repository, "coverage.xml"),
            "<coverage><packages><package><classes><class filename=\"src/Risky.cs\" line-rate=\"0.25\" /></classes></package></packages></coverage>");

        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);
            var api = await DevContextApi.OpenAsync(
                repository,
                new DevContextConfig { Cache = new CacheConfig { Enabled = false } });

            var context = await api.ContextAsync(new RepositoryContextOptions
            {
                Purpose = ContextPurpose.Risk,
                Scope = ContextScope.Project,
                Target = "Sample",
                MaxHotspots = 1,
                CoberturaPath = "coverage.xml"
            });

            Assert.IsFalse(api.BaselineExists);
            CollectionAssert.AreEqual(new[] { "src/Sample.csproj" }, context.AnalyzedProjects.ToArray());
            var hotspot = context.Hotspots.Single();
            Assert.AreEqual("src/Risky.cs", hotspot.Path);
            Assert.IsGreaterThanOrEqualTo(4, hotspot.MaximumCyclomaticComplexity);
            Assert.AreEqual(25d, hotspot.LineCoveragePercent);
            Assert.AreEqual("Sample.Risky", context.Types.Single().FullName);
            Assert.AreEqual("Choose", context.Methods.Single().Name);
            Assert.IsGreaterThanOrEqualTo(4, context.Methods.Single().CyclomaticComplexity);
            var typeDefinition = context.TypeDefinitions.Single();
            Assert.AreEqual("Sample.Risky", typeDefinition.FullName);
            Assert.AreEqual("public", typeDefinition.Accessibility);
            CollectionAssert.Contains(typeDefinition.Modifiers.ToArray(), "sealed");
            Assert.AreEqual("int", typeDefinition.Members.Single().DeclaredType);
            Assert.IsGreaterThan(0, context.ApproximateTokens);
            StringAssert.Contains(context.Markdown, "Selected because");

            var output = Path.Combine(repository, "risk-report.md");
            var artifact = await api.SaveReportAsync(context, output);
            Assert.AreEqual(output, artifact.Path);
            Assert.IsTrue(File.Exists(output));
            Assert.AreEqual(context.ApproximateTokens, artifact.ApproximateTokens);
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task EvidenceQuery_ChangedOnlyUsesImmutableBaselineFileDelta()
    {
        var repository = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(repository, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(
            Path.Combine(repository, "ChangedService.cs"),
            "namespace Sample; public sealed class ChangedService { public void Run() { } }");
        await File.WriteAllTextAsync(
            Path.Combine(repository, "UnchangedWidget.cs"),
            "namespace Sample; public sealed class UnchangedWidget { public void Render() { } }");

        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);
            var api = await DevContextApi.OpenAsync(
                repository,
                new DevContextConfig
                {
                    Tests = new TestConfig { Enabled = false },
                    Cache = new CacheConfig { Enabled = false },
                    Indexing = new IndexingConfig { ExecuteSourceGenerators = false }
                });
            await api.BaselineAsync();
            await File.AppendAllTextAsync(
                Path.Combine(repository, "ChangedService.cs"),
                Environment.NewLine + "// changed after baseline");

            var bundle = await api.QueryAsync(new EvidenceQueryOptions
            {
                Query = "changed service widget",
                ChangedOnly = true,
                MaxTokens = 900,
                MaxResults = 5
            });

            CollectionAssert.AreEqual(new[] { "ChangedService.cs" }, bundle.ChangedFiles.ToArray());
            Assert.IsNotEmpty(bundle.Blocks);
            Assert.IsTrue(bundle.Blocks.All(block => block.File == "ChangedService.cs"));
            Assert.IsFalse(bundle.Blocks.Any(block => block.File == "UnchangedWidget.cs"));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task EvidenceQuery_BenchmarkFindsFeatureDependenciesAndTestWithinTokenBudget()
    {
        var repository = CreateTemporaryDirectory();
        var production = Path.Combine(repository, "src", "Sample");
        var tests = Path.Combine(repository, "tests", "Sample.Tests");
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

            public sealed class ReferenceTargetA { public void SharedReferenceTarget() { } }
            public sealed class ReferenceTargetB { public void SharedReferenceTarget() { } }
            public sealed class ReferenceTargetC { public void SharedReferenceTarget() { } }
            public sealed class ReferenceTargetD { public void SharedReferenceTarget() { } }
            public sealed class ReferenceTargetE { public void SharedReferenceTarget() { } }
            public sealed class ReferenceTargetF { public void SharedReferenceTarget() { } }
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

        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);
            var api = await DevContextApi.OpenAsync(
                repository,
                new DevContextConfig { Cache = new CacheConfig { Enabled = false } });
            var options = new EvidenceQueryOptions
            {
                Query = "refresh dashboard songs",
                MaxTokens = 1400,
                MaxResults = 8,
                GraphDepth = 1
            };

            var bundle = await api.QueryAsync(options);
            var repeated = await api.QueryAsync(options);
            var smallestBudget = await api.QueryAsync(options with { MaxTokens = 256 });
            var absent = await api.QueryAsync(options with
            {
                Query = "∅",
                MaxTokens = 700,
                MaxResults = 4
            });
            var callers = await api.QueryReferencesAsync(new SymbolReferenceQueryOptions
            {
                Target = "RefreshDashboardAsync",
                Relation = SymbolReferenceRelation.Callers,
                MaxTokens = 700,
                MaxResults = 5
            });
            var noWriters = await api.QueryReferencesAsync(new SymbolReferenceQueryOptions
            {
                Target = "RefreshDashboardAsync",
                Relation = SymbolReferenceRelation.Writers,
                MaxTokens = 700,
                MaxResults = 5
            });
            var ambiguous = await api.QueryReferencesAsync(new SymbolReferenceQueryOptions
            {
                Target = "SharedReferenceTarget",
                Relation = SymbolReferenceRelation.Callers,
                MaxTokens = 256,
                MaxResults = 20
            });
            var benchmark = await api.BenchmarkAsync(
            [
                new EvidenceBenchmarkCase
                {
                    Name = "refresh dashboard",
                    Query = options.Query,
                    ExpectedFiles =
                    [
                        "src/Sample/DashboardViewModel.cs",
                        "src/Sample/ISongCatalog.cs",
                        "tests/Sample.Tests/DashboardTests.cs"
                    ],
                    ExpectedRelationships = ["method-call"],
                    MaxTokens = options.MaxTokens,
                    MaxResults = options.MaxResults,
                    GraphDepth = options.GraphDepth
                },
                new EvidenceBenchmarkCase
                {
                    Name = "catalog contract",
                    Query = "song catalog get songs",
                    ExpectedFiles = ["src/Sample/ISongCatalog.cs"],
                    MaxTokens = 900,
                    MaxResults = 5
                },
                new EvidenceBenchmarkCase
                {
                    Name = "no repository evidence",
                    Query = "∅",
                    ExpectedFiles = [],
                    ExpectedSufficiency = EvidenceSufficiency.Insufficient,
                    ExpectAbstention = true,
                    MaxTokens = 700,
                    MaxResults = 4
                }
            ]);

            var expectedFiles = new[]
            {
                "src/Sample/DashboardViewModel.cs",
                "src/Sample/ISongCatalog.cs",
                "tests/Sample.Tests/DashboardTests.cs"
            };
            var retrievedFiles = bundle.Blocks.Select(block => block.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recall = expectedFiles.Count(retrievedFiles.Contains) / (double)expectedFiles.Length;
            Assert.AreEqual(1d, recall, $"Expected-file recall was {recall:P0}.");
            Assert.IsLessThanOrEqualTo(options.MaxTokens, bundle.ApproximateTokens);
            Assert.IsLessThanOrEqualTo(options.MaxResults, bundle.Blocks.Count);
            Assert.IsTrue(bundle.Blocks.All(block => block.StartLine > 0 && block.EndLine >= block.StartLine));
            Assert.IsTrue(bundle.Blocks.All(block => block.ContentHash.Length > 0));
            Assert.IsTrue(bundle.Blocks.All(block => block.SelectionReasons.Count > 0));
            Assert.IsFalse(bundle.Blocks
                .SelectMany((left, index) => bundle.Blocks.Skip(index + 1).Select(right => (left, right)))
                .Any(pair => pair.left.File.Equals(pair.right.File, StringComparison.OrdinalIgnoreCase)
                             && pair.left.StartLine <= pair.right.EndLine
                             && pair.right.StartLine <= pair.left.EndLine));
            var relationship = bundle.Relationships.First(relationship =>
                relationship.Relationship == "method-call"
                && relationship.Confidence == EvidenceConfidence.SemanticResolved);
            Assert.AreEqual("roslyn-operation", relationship.Origin);
            Assert.AreEqual("net8.0", relationship.TargetFramework);
            Assert.IsNotNull(relationship.EvidenceFile);
            Assert.IsGreaterThan(0, relationship.EvidenceLine.GetValueOrDefault());
            Assert.IsGreaterThan(0, relationship.EvidenceColumn.GetValueOrDefault());
            Assert.IsGreaterThanOrEqualTo(
                relationship.EvidenceLine.GetValueOrDefault(),
                relationship.EvidenceEndLine.GetValueOrDefault());
            Assert.AreNotEqual(EvidenceSufficiency.Insufficient, bundle.Sufficiency);
            Assert.IsFalse(bundle.ShouldAbstain);
            Assert.HasCount(2, bundle.CompilationCompleteness);
            Assert.AreEqual(bundle.BundleId, repeated.BundleId);
            Assert.AreEqual(bundle.Prompt, repeated.Prompt);
            Assert.AreNotEqual(bundle.BundleId, smallestBudget.BundleId);
            Assert.IsLessThanOrEqualTo(256, smallestBudget.ApproximateTokens);
            Assert.IsFalse(bundle.Prompt.Contains(repository, StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(bundle.Prompt, "Evidence sufficiency:");
            StringAssert.EndsWith(bundle.Prompt, options.Query + Environment.NewLine);
            Assert.AreEqual(EvidenceSufficiency.Insufficient, absent.Sufficiency);
            Assert.IsTrue(absent.ShouldAbstain);
            Assert.IsEmpty(absent.Blocks);
            StringAssert.Contains(absent.Prompt, "Do not infer an implementation answer or proof of absence");
            Assert.AreEqual("RefreshDashboardAsync", callers.ResolvedSymbol?.Name);
            Assert.IsTrue(callers.Matches.Any(match =>
                match.Source.Name == "RefreshDashboardLoadsSongs"
                && match.Relationship == "method-call"));
            Assert.AreNotEqual(EvidenceSufficiency.Insufficient, callers.Sufficiency);
            Assert.IsFalse(callers.ShouldAbstain);
            Assert.IsLessThanOrEqualTo(700, callers.ApproximateTokens);
            Assert.IsEmpty(noWriters.Matches);
            if (noWriters.CompilationCompleteness.All(record =>
                    record.State == AnalysisCompletenessState.Complete))
            {
                Assert.AreEqual(EvidenceSufficiency.Sufficient, noWriters.Sufficiency);
                Assert.IsFalse(noWriters.ShouldAbstain, "Complete analysis can prove that no matching edge exists.");
            }
            else
            {
                Assert.AreEqual(EvidenceSufficiency.Insufficient, noWriters.Sufficiency);
                Assert.IsTrue(noWriters.ShouldAbstain, "Incomplete analysis cannot prove the absence of an edge.");
            }
            Assert.IsNull(ambiguous.ResolvedSymbol);
            Assert.IsTrue(ambiguous.ShouldAbstain);
            Assert.IsTrue(ambiguous.Truncated);
            Assert.IsLessThan(6, ambiguous.AmbiguousSymbols.Count);
            Assert.IsLessThanOrEqualTo(256, ambiguous.ApproximateTokens);
            Assert.IsFalse(api.BaselineExists, "Evidence queries must not create baseline state.");
            Assert.IsTrue(benchmark.Passed);
            Assert.AreEqual(1d, benchmark.MeanFileRecall);
            Assert.IsTrue(benchmark.Cases.All(result => result.Deterministic));
            Assert.IsTrue(benchmark.Cases.All(result => result.ApproximateTokens <= 1400));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repolens-api-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task RunGitAsync(string repository, params string[] arguments)
    {
        var result = await RunProcessAsync("git", arguments, repository);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        TempDirectory.Delete(path);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
