using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using DevContext.Cli;
using ModelContextProtocol.Client;

namespace DevContext.Tests;

[TestClass]
public sealed class CliIntegrationTests
{
    [TestMethod]
    public async Task Mcp_ListsTypedToolsAndReturnsStructuredRepoLensResults()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);
            await File.WriteAllTextAsync(Path.Combine(repository, "Tracked.cs"), "public sealed class Tracked;");
            var baseline = await RunCliAsync(["baseline", "--format", "json"], repository);
            Assert.AreEqual(0, baseline.ExitCode, baseline.StandardError);

            var serverErrors = new ConcurrentQueue<string>();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "RepoLens integration test",
                Command = "dotnet",
                Arguments = [typeof(DevContextApplication).Assembly.Location, "mcp"],
                WorkingDirectory = repository,
                ShutdownTimeout = TimeSpan.FromSeconds(1),
                StandardErrorLines = line => serverErrors.Enqueue(line)
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            CollectionAssert.AreEquivalent(
                new[] { "status", "affected", "explain", "context", "query", "refs", "verify" },
                tools.Select(tool => tool.Name).ToArray());
            Assert.IsTrue(tools.All(tool => tool.ProtocolTool.OutputSchema is not null));

            var status = await client.CallToolAsync("status", cancellationToken: timeout.Token);
            Assert.IsFalse(status.IsError ?? false, JsonSerializer.Serialize(status));
            Assert.IsNotNull(status.StructuredContent);
            Assert.AreEqual(
                DevContext.Core.SchemaVersions.Current,
                status.StructuredContent.Value.GetProperty("schemaVersion").GetInt32());

            ModelContextProtocol.Protocol.CallToolResult explanation;
            try
            {
                explanation = await client.CallToolAsync(
                    "explain",
                    new Dictionary<string, object?> { ["path"] = "Tracked.cs" },
                    cancellationToken: timeout.Token);
            }
            catch (Exception exception)
            {
                Assert.Fail($"{exception}{Environment.NewLine}{string.Join(Environment.NewLine, serverErrors)}");
                throw;
            }
            Assert.IsFalse(explanation.IsError ?? false, JsonSerializer.Serialize(explanation));
            Assert.IsNotNull(explanation.StructuredContent);
            Assert.IsTrue(explanation.StructuredContent.Value.GetProperty("isWithinRepository").GetBoolean());
            Assert.AreEqual(
                "Tracked.cs",
                explanation.StructuredContent.Value.GetProperty("normalizedPath").GetString());
        }
        finally
        {
            DeleteTemporaryDirectory(repository);
        }
    }

    [TestMethod]
    public async Task ReferenceReviewAndBaselineFromRef_ReportCleanCommittedChangesWithoutStoredState()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            await AssertGitAsync(repository, "init", "--quiet");
            await AssertGitAsync(repository, "config", "user.email", "dev-context@example.test");
            await AssertGitAsync(repository, "config", "user.name", "Dev Context Tests");
            await File.WriteAllTextAsync(Path.Combine(repository, "Tracked.cs"), "public class Before;");
            await AssertGitAsync(repository, "add", "--all");
            await AssertGitAsync(repository, "commit", "--quiet", "-m", "initial");
            var baseBranch = (await RunProcessAsync("git", ["branch", "--show-current"], repository))
                .StandardOutput.Trim();
            var baseCommit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repository))
                .StandardOutput.Trim();
            await AssertGitAsync(repository, "checkout", "--quiet", "-b", "feature/review");
            await File.WriteAllTextAsync(Path.Combine(repository, "Tracked.cs"), "public class After;");
            await AssertGitAsync(repository, "add", "--all");
            await AssertGitAsync(repository, "commit", "--quiet", "-m", "feature change");
            var featureCommit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repository))
                .StandardOutput.Trim();

            var review = await RunCliAsync(
                ["verify", "--against", baseBranch, "--format", "json"],
                repository);
            Assert.AreEqual(0, review.ExitCode, review.StandardError);
            using (var document = JsonDocument.Parse(review.StandardOutput))
            {
                Assert.AreEqual(baseCommit, document.RootElement.GetProperty("baseCommit").GetString());
                Assert.AreEqual(featureCommit, document.RootElement.GetProperty("headCommit").GetString());
                Assert.AreEqual("Tracked.cs", document.RootElement.GetProperty("changedFiles")[0].GetString());
                Assert.AreEqual(
                    "Committed",
                    document.RootElement.GetProperty("changes")[0].GetProperty("provenance").GetString());
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(repository, ".dev-context", "baseline")));

            var baseline = await RunCliAsync(
                ["baseline", "--from", baseBranch, "--format", "json"],
                repository);
            Assert.AreEqual(0, baseline.ExitCode, baseline.StandardError);
            using (var document = JsonDocument.Parse(baseline.StandardOutput))
            {
                var manifest = document.RootElement.GetProperty("manifest");
                Assert.AreEqual(baseBranch, manifest.GetProperty("diffBaseReference").GetString());
                Assert.AreEqual(baseCommit, manifest.GetProperty("headCommit").GetString());
                Assert.AreEqual(featureCommit, manifest.GetProperty("capturedHeadCommit").GetString());
            }

            var affected = await RunCliAsync(["affected", "--format", "json"], repository);
            Assert.AreEqual(0, affected.ExitCode, affected.StandardError);
            using (var document = JsonDocument.Parse(affected.StandardOutput))
            {
                Assert.AreEqual("Tracked.cs", document.RootElement.GetProperty("changedFiles")[0].GetString());
                Assert.AreEqual(
                    "Committed",
                    document.RootElement.GetProperty("changes")[0].GetProperty("provenance").GetString());
            }
        }
        finally
        {
            DeleteTemporaryDirectory(repository);
        }
    }

    [TestMethod]
    public async Task Query_DoesNotReturnLexicalEvidenceFromIgnoredFiles()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);
            await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "ignored/\n");
            Directory.CreateDirectory(Path.Combine(repository, "ignored"));
            await File.WriteAllTextAsync(
                Path.Combine(repository, "ignored", "secret.md"),
                "NebulaIgnoredEvidence appears only in this ignored file.");

            var query = await RunCliAsync(
                ["query", "NebulaIgnoredEvidence", "--format", "json", "--max-tokens", "512"],
                repository);
            Assert.AreEqual(3, query.ExitCode, query.StandardError);
            using var document = JsonDocument.Parse(query.StandardOutput);
            Assert.AreEqual(0, document.RootElement.GetProperty("blocks").GetArrayLength());
            Assert.IsTrue(document.RootElement.GetProperty("shouldAbstain").GetBoolean());
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task Init_CreatesValidatedConfigurationOnceWithoutCreatingABaseline()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);

            var first = await RunCliAsync(["init", "--format", "json"], repository);
            Assert.AreEqual(0, first.ExitCode, first.StandardError);
            var configPath = Path.Combine(repository, ".dev-context", "config.json");
            Assert.IsTrue(File.Exists(configPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(repository, ".dev-context", "baseline")));
            var originalConfig = await File.ReadAllTextAsync(configPath);
            using (var document = JsonDocument.Parse(first.StandardOutput))
            {
                Assert.IsTrue(document.RootElement.GetProperty("created").GetBoolean());
                Assert.IsTrue(document.RootElement.GetProperty("configuration")
                    .GetProperty("indexing")
                    .GetProperty("respectGitignore")
                    .GetBoolean());
            }

            var second = await RunCliAsync(["init", "--format", "json"], repository);
            Assert.AreEqual(0, second.ExitCode, second.StandardError);
            using (var document = JsonDocument.Parse(second.StandardOutput))
            {
                Assert.IsFalse(document.RootElement.GetProperty("created").GetBoolean());
            }

            Assert.AreEqual(originalConfig, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task BaselineStatusAndReset_WorkFromNestedRepositoryDirectory()
    {
        var repository = CreateTemporaryDirectory();
        var nested = Path.Combine(repository, "src", "nested");
        Directory.CreateDirectory(nested);

        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);

            var query = await RunCliAsync(
                ["query", "find calculator tests", "--max-tokens", "512"],
                nested);
            Assert.AreEqual(3, query.ExitCode, query.StandardError);
            StringAssert.Contains(query.StandardOutput, "No source evidence matched the query.");
            StringAssert.EndsWith(query.StandardOutput, "find calculator tests" + Environment.NewLine);
            Assert.IsFalse(Directory.Exists(Path.Combine(repository, ".dev-context", "baseline")));

            var baseline = await RunCliAsync(["baseline", "--format", "json"], nested);
            Assert.AreEqual(0, baseline.ExitCode, baseline.StandardError);
            using (var document = JsonDocument.Parse(baseline.StandardOutput))
            {
                Assert.AreEqual("Skipped", document.RootElement.GetProperty("build").GetProperty("state").GetString());
                Assert.AreEqual(0, document.RootElement.GetProperty("repository").GetProperty("projects").GetArrayLength());
            }

            var duplicate = await RunCliAsync(["baseline"], repository);
            Assert.AreEqual(2, duplicate.ExitCode);
            StringAssert.Contains(duplicate.StandardError, "A baseline already exists");

            var status = await RunCliAsync(["status", "--format", "json"], nested);
            Assert.AreEqual(0, status.ExitCode, status.StandardError);
            using (var document = JsonDocument.Parse(status.StandardOutput))
            {
                Assert.AreEqual(
                    DevContext.Core.SchemaVersions.Current,
                    document.RootElement.GetProperty("schemaVersion").GetInt32());
            }

            var reset = await RunCliAsync(["reset"], repository);
            Assert.AreEqual(0, reset.ExitCode, reset.StandardError);
            Assert.IsTrue(File.Exists(Path.Combine(repository, ".dev-context", "config.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(repository, ".dev-context", "baseline")));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [TestMethod]
    public async Task Schema_EmitsContractsWithoutRequiringAGitRepository()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var output = Path.Combine(directory, "tests.schema.json");
            var result = await RunCliAsync(["schema", "tests", "--output", output], directory);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            Assert.IsTrue(File.Exists(output));
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual(
                "https://json-schema.org/draft/2020-12/schema",
                document.RootElement.GetProperty("$schema").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task BenchmarkAcceptanceFailure_UsesDedicatedExitCode()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var git = await RunProcessAsync("git", ["init", "--quiet"], repository);
            Assert.AreEqual(0, git.ExitCode, git.StandardError);
            var corpus = Path.Combine(repository, "corpus.json");
            await File.WriteAllTextAsync(
                corpus,
                """
                [
                  {
                    "name": "missing evidence",
                    "query": "a symbol that does not exist",
                    "expectedFiles": ["missing.cs"],
                    "maxTokens": 512,
                    "maxResults": 4
                  }
                ]
                """);

            var result = await RunCliAsync(["benchmark", "corpus.json"], repository);

            Assert.AreEqual(4, result.ExitCode, result.StandardError);
            StringAssert.Contains(result.StandardOutput, "Result: FAILED");
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    private static Task<ProcessResult> RunCliAsync(IReadOnlyList<string> arguments, string workingDirectory) =>
        RunProcessAsync(
            "dotnet",
            new[] { typeof(DevContextApplication).Assembly.Location }.Concat(arguments).ToArray(),
            workingDirectory);

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
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task AssertGitAsync(string repository, params string[] arguments)
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

        Directory.Delete(path, true);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
