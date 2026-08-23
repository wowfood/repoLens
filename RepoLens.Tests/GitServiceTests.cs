using DevContext.Core;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class GitServiceTests
{
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
}
