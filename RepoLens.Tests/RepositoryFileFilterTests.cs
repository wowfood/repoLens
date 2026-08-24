using DevContext.Configuration;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class RepositoryFileFilterTests
{
    [TestMethod]
    public async Task Inventory_HonorsGitignoreAndConfiguredGlobs()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runner = new ProcessRunner();
            var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
            Assert.AreEqual(DevContext.Core.ExecutionState.Succeeded, git.State, git.StandardError);

            await WriteFileAsync(Path.Combine(root, ".gitignore"), "artifacts/\n");
            await WriteFileAsync(Path.Combine(root, "src", "App.cs"), "public class App;");
            await WriteFileAsync(Path.Combine(root, "artifacts", "Generated.cs"), "public class Generated;");
            await WriteFileAsync(Path.Combine(root, "samples", "Sample.cs"), "public class Sample;");

            var inventory = await new RepositoryFileFilter(runner).GetFilesAsync(
                root,
                new IndexingConfig { Exclude = ["samples/**"] },
                CancellationToken.None);

            Assert.IsTrue(inventory.GitIgnoreApplied);
            CollectionAssert.Contains(inventory.RelativePaths.ToArray(), "src/App.cs");
            CollectionAssert.DoesNotContain(inventory.RelativePaths.ToArray(), "artifacts/Generated.cs");
            CollectionAssert.DoesNotContain(inventory.RelativePaths.ToArray(), "samples/Sample.cs");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Inventory_CanIncludeGitignoredFilesWhenConfigured()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runner = new ProcessRunner();
            var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
            Assert.AreEqual(DevContext.Core.ExecutionState.Succeeded, git.State, git.StandardError);
            await WriteFileAsync(Path.Combine(root, ".gitignore"), "artifacts/\n");
            await WriteFileAsync(Path.Combine(root, "artifacts", "Included.cs"), "public class Included;");

            var inventory = await new RepositoryFileFilter(runner).GetFilesAsync(
                root,
                new IndexingConfig { RespectGitignore = false },
                CancellationToken.None);

            Assert.IsFalse(inventory.GitIgnoreApplied);
            CollectionAssert.Contains(inventory.RelativePaths.ToArray(), "artifacts/Included.cs");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Configuration_RejectsExcludePatternsThatEscapeTheRepository()
    {
        var config = new DevContextConfig
        {
            Indexing = new IndexingConfig { Exclude = ["../outside/**"] }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Validate(config));
        StringAssert.Contains(exception.Message, "must not escape the repository");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(65)]
    public void Configuration_RejectsInvalidIndexingParallelism(int maxParallelism)
    {
        var config = new DevContextConfig
        {
            Indexing = new IndexingConfig { MaxParallelism = maxParallelism }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Validate(config));
        StringAssert.Contains(exception.Message, "indexing.maxParallelism must be between 1 and 64");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WriteFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }
}
