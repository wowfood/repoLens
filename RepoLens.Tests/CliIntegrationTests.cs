using System.Diagnostics;
using System.Text.Json;
using DevContext.Cli;

namespace DevContext.Tests;

[TestClass]
public sealed class CliIntegrationTests
{
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
            Assert.AreEqual(0, query.ExitCode, query.StandardError);
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
