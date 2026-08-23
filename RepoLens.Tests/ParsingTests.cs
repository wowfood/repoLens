using System.Text.Json;
using DevContext.Services;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Configuration;

namespace DevContext.Tests;

[TestClass]
public sealed class ParsingTests
{
    [TestMethod]
    public void PersistedSchema_ReadsDeclaredCompatibilityWindowAndRejectsOutsideIt()
    {
        foreach (var version in Enumerable.Range(
                     SchemaVersions.MinimumReadable,
                     SchemaVersions.Current - SchemaVersions.MinimumReadable + 1))
        {
            var symbols = JsonSerializer.Deserialize<SymbolIndex>(
                $$"""{"schemaVersion":{{version}},"symbols":[]}""",
                JsonDefaults.Options);
            Assert.IsNotNull(symbols);
            SchemaVersions.EnsureReadable(symbols.SchemaVersion, "symbols.json");
        }

        Assert.Throws<InvalidDataException>(() =>
            SchemaVersions.EnsureReadable(SchemaVersions.MinimumReadable - 1, "symbols.json"));
        Assert.Throws<InvalidDataException>(() =>
            SchemaVersions.EnsureReadable(SchemaVersions.Current + 1, "symbols.json"));
    }

    [TestMethod]
    public async Task JsonPersistence_RoundTripsRequiredNullableProperties()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-{Guid.NewGuid():N}.json");
        var manifest = new BaselineManifest
        {
            BaselineId = "baseline",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RepositoryRoot = "C:/repo",
            Branch = null,
            HeadCommit = null,
            WorkingTreeDirty = false,
            SdkVersion = "10.0.204",
            Timings = []
        };

        try
        {
            await JsonFile.WriteAsync(path, manifest, CancellationToken.None);
            var restored = await JsonFile.ReadAsync<BaselineManifest>(path, CancellationToken.None);

            Assert.IsNull(restored.Branch);
            Assert.IsNull(restored.HeadCommit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void BuildDiagnostics_AreNormalizedWithStableIdentity()
    {
        const string output = @"C:\work\src\Foo.cs(47,12): warning CA2007: Consider calling ConfigureAwait [C:\work\Foo.csproj]";

        var first = BuildService.ParseDiagnostics(output, @"C:\work").Single();
        var second = BuildService.ParseDiagnostics(output, @"C:\work").Single();

        Assert.AreEqual("warning", first.Severity);
        Assert.AreEqual("CA2007", first.Rule);
        Assert.AreEqual("src/Foo.cs", first.File);
        Assert.AreEqual(47, first.Line);
        Assert.AreEqual(first.Identity, second.Identity);
    }

    [TestMethod]
    public void CleanupCommand_SupportsQuotedArguments()
    {
        var result = CleanupService.SplitCommand("dotnet format \"My Solution.sln\" --verbosity minimal");

        CollectionAssert.AreEqual(
            new[] { "dotnet", "format", "My Solution.sln", "--verbosity", "minimal" },
            result.ToArray());
    }

    [TestMethod]
    public void TrxParser_PersistsIndividualFailureDetails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(path,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions>
                    <UnitTest id="test-id" name="ShouldWork">
                      <TestMethod className="ExampleTests" name="ShouldWork" />
                    </UnitTest>
                  </TestDefinitions>
                  <Results>
                    <UnitTestResult testId="test-id" testName="ShouldWork" outcome="Failed" duration="00:00:00.125">
                      <Output><ErrorInfo><Message>Expected true.</Message></ErrorInfo></Output>
                    </UnitTestResult>
                  </Results>
                </TestRun>
                """);

            var result = TestService.ParseTrx(path).Single();

            Assert.AreEqual("ExampleTests", result.ClassName);
            Assert.AreEqual("Failed", result.Outcome);
            Assert.AreEqual(125, result.DurationMilliseconds);
            Assert.AreEqual("Expected true.", result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void DotnetFormatReport_IsNormalizedIntoStableDiagnostics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-format-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """
                [
                  {
                    "FileName": "Foo.cs",
                    "FilePath": "C:\\work\\src\\Foo.cs",
                    "FileChanges": [
                      {
                        "LineNumber": 4,
                        "CharNumber": 8,
                        "DiagnosticId": "WHITESPACE",
                        "FormatDescription": "Fix whitespace formatting."
                      }
                    ]
                  }
                ]
                """);

            var diagnostic = AnalysisService.ParseFormatReport(path, @"C:\work").Single();

            Assert.AreEqual("dotnet-format", diagnostic.Tool);
            Assert.AreEqual("WHITESPACE", diagnostic.Rule);
            Assert.AreEqual("src/Foo.cs", diagnostic.File);
            Assert.AreEqual(4, diagnostic.Line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void SarifReport_IsNormalizedIntoStableDiagnostics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-qodana-{Guid.NewGuid():N}.sarif.json");
        try
        {
            File.WriteAllText(path,
                """
                {
                  "version": "2.1.0",
                  "runs": [
                    {
                      "results": [
                        {
                          "ruleId": "CA2007",
                          "level": "warning",
                          "message": { "text": "Configure await explicitly." },
                          "locations": [
                            {
                              "physicalLocation": {
                                "artifactLocation": { "uri": "src/Foo.cs" },
                                "region": { "startLine": 12, "startColumn": 3 }
                              }
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """);

            var diagnostic = AnalysisService.ParseSarif(path, @"C:\work", "qodana").Single();

            Assert.AreEqual("qodana", diagnostic.Tool);
            Assert.AreEqual("warning", diagnostic.Severity);
            Assert.AreEqual("CA2007", diagnostic.Rule);
            Assert.AreEqual("src/Foo.cs", diagnostic.File);
            Assert.AreEqual(12, diagnostic.Line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task OptionalQodana_UnavailableCommandRemainsAProviderState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = new AnalysisService(new UnavailableProcessRunner());
            var config = new DevContextConfig
            {
                Analysis = new AnalysisConfig { Qodana = true, QodanaCommand = "qodana" }
            };
            var build = new BuildSnapshot
            {
                State = ExecutionState.Succeeded,
                ExitCode = 0,
                DurationMilliseconds = 1,
                Command = "dotnet build",
                Diagnostics = []
            };

            var (analysis, _) = await service.CaptureAsync(
                root,
                config,
                build,
                "test-run",
                CancellationToken.None);

            Assert.AreEqual(ExecutionState.Unavailable, analysis.Qodana.State);
            Assert.IsEmpty(analysis.Diagnostics);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class UnavailableProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(new ProcessResult(
            ExecutionState.Unavailable,
            null,
            string.Empty,
            "command unavailable",
            1,
            executable));
    }
}
