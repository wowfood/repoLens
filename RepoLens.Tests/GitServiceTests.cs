using DevContext.Core;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class GitServiceTests
{
    [TestMethod]
    public async Task ChangesSince_CombinesCommittedAndWorkingTreeChangesWithProvenance()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runner = new DevContext.Infrastructure.ProcessRunner();
            await InitializeRepositoryAsync(runner, root);
            await File.WriteAllTextAsync(Path.Combine(root, "Both.cs"), "before");
            await File.WriteAllTextAsync(Path.Combine(root, "Committed.cs"), "before");
            await CommitAllAsync(runner, root, "initial");
            var service = new GitService(runner);
            var baseline = await service.CaptureAsync(root, CancellationToken.None);

            await File.WriteAllTextAsync(Path.Combine(root, "Both.cs"), "committed");
            await File.WriteAllTextAsync(Path.Combine(root, "Committed.cs"), "committed");
            await CommitAllAsync(runner, root, "committed change");
            await File.AppendAllTextAsync(Path.Combine(root, "Both.cs"), " and working");
            await File.WriteAllTextAsync(Path.Combine(root, "Working.cs"), "working");
            var current = await service.CaptureAsync(root, CancellationToken.None);

            var changes = await service.ChangesSinceAsync(root, baseline, current, CancellationToken.None);

            Assert.AreEqual(GitComparisonState.Comparable, changes.Comparison);
            Assert.AreEqual(
                GitChangeProvenance.Both,
                changes.Changes.Single(change => change.Path == "Both.cs").Provenance);
            Assert.AreEqual(
                GitChangeProvenance.Committed,
                changes.Changes.Single(change => change.Path == "Committed.cs").Provenance);
            Assert.AreEqual(
                GitChangeProvenance.WorkingTree,
                changes.Changes.Single(change => change.Path == "Working.cs").Provenance);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChangesSince_ReportsWhenBaselineHeadIsNoLongerAnAncestor()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runner = new DevContext.Infrastructure.ProcessRunner();
            await InitializeRepositoryAsync(runner, root);
            await File.WriteAllTextAsync(Path.Combine(root, "Value.txt"), "first");
            await CommitAllAsync(runner, root, "first");
            var firstCommit = (await RunGitAsync(runner, root, "rev-parse", "HEAD")).StandardOutput.Trim();
            await File.WriteAllTextAsync(Path.Combine(root, "Value.txt"), "baseline");
            await CommitAllAsync(runner, root, "baseline");
            var service = new GitService(runner);
            var baseline = await service.CaptureAsync(root, CancellationToken.None);

            await RunGitAsync(runner, root, "checkout", "--quiet", "--detach", firstCommit);
            await File.WriteAllTextAsync(Path.Combine(root, "Value.txt"), "rewritten");
            await CommitAllAsync(runner, root, "rewritten");
            var current = await service.CaptureAsync(root, CancellationToken.None);

            var changes = await service.ChangesSinceAsync(root, baseline, current, CancellationToken.None);

            Assert.AreEqual(GitComparisonState.BaselineDiverged, changes.Comparison);
            Assert.IsFalse(changes.IsComplete);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void ChangedSince_DetectsAddedRemovedAndFurtherModifiedFiles()
    {
        var baseline = Snapshot(
            new GitFileState("modified.cs", " ", "M", "old"),
            new GitFileState("removed.cs", "?", "?", "hash"));
        var current = Snapshot(
            new GitFileState("modified.cs", " ", "M", "new"),
            new GitFileState("added.cs", "?", "?", "hash"));

        var result = GitService.ChangedSince(baseline, current);

        CollectionAssert.AreEqual(
            new[] { "added.cs", "modified.cs", "removed.cs" },
            result.ToArray());
    }

    [TestMethod]
    public void ChangedSince_IgnoresUnchangedPreExistingModification()
    {
        var file = new GitFileState("existing.cs", " ", "M", "same");

        var result = GitService.ChangedSince(Snapshot(file), Snapshot(file));

        Assert.IsEmpty(result);
    }

    private static GitSnapshot Snapshot(params GitFileState[] files) => new()
    {
        Branch = "main",
        HeadCommit = "abc",
        Files = files
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }

    private static async Task InitializeRepositoryAsync(
        DevContext.Infrastructure.ProcessRunner runner,
        string root)
    {
        await RunGitAsync(runner, root, "init", "--quiet");
        await RunGitAsync(runner, root, "config", "user.email", "dev-context@example.test");
        await RunGitAsync(runner, root, "config", "user.name", "Dev Context Tests");
    }

    private static async Task CommitAllAsync(
        DevContext.Infrastructure.ProcessRunner runner,
        string root,
        string message)
    {
        await RunGitAsync(runner, root, "add", "--all");
        await RunGitAsync(runner, root, "commit", "--quiet", "-m", message);
    }

    private static async Task<DevContext.Infrastructure.ProcessResult> RunGitAsync(
        DevContext.Infrastructure.ProcessRunner runner,
        string root,
        params string[] arguments)
    {
        var result = await runner.RunAsync("git", arguments, root, CancellationToken.None);
        Assert.AreEqual(ExecutionState.Succeeded, result.State, result.StandardError);
        return result;
    }
}
