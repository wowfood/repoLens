using DevContext.Cli;

namespace DevContext.Tests;

[TestClass]
public sealed class CliArgumentsTests
{
    [TestMethod]
    public void Parse_AcceptsCommandAndOptionsInEitherOrder()
    {
        var result = CliArguments.Parse(["--format", "json", "baseline", "--replace"]);

        Assert.AreEqual("baseline", result.Command);
        Assert.AreEqual("json", result.Format);
        Assert.IsTrue(result.Replace);
    }

    [TestMethod]
    public void Parse_RejectsUnknownOption()
    {
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["baseline", "--mystery"]));
    }

    [TestMethod]
    public void Parse_RejectsUnsupportedFormat()
    {
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["status", "--format", "yaml"]));
    }

    [TestMethod]
    public void Parse_PreservesExplainPathOperand()
    {
        var result = CliArguments.Parse(["explain", "src/App/Components/Dashboard.razor", "--format", "json"]);

        Assert.AreEqual("explain", result.Command);
        Assert.AreEqual("src/App/Components/Dashboard.razor", result.Target);
        Assert.AreEqual("json", result.Format);
    }

    [TestMethod]
    public void Parse_RejectsMoreThanOneOperand()
    {
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["explain", "first.cs", "second.cs"]));
    }

    [TestMethod]
    public void Parse_AcceptsEvidenceQueryBudgetOptions()
    {
        var result = CliArguments.Parse(
            ["query", "refresh dashboard songs", "--max-tokens", "1200", "--max-results", "6", "--graph-depth", "2"]);

        Assert.AreEqual("query", result.Command);
        Assert.AreEqual("refresh dashboard songs", result.Target);
        Assert.AreEqual(1200, result.MaxTokens);
        Assert.AreEqual(6, result.MaxResults);
        Assert.AreEqual(2, result.GraphDepth);
    }

    [TestMethod]
    public void Parse_RejectsInvalidEvidenceQueryBudgets()
    {
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["query", "task", "--max-tokens", "255"]));
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["query", "task", "--graph-depth", "4"]));
    }

    [TestMethod]
    public void Parse_AcceptsEvidenceFilters()
    {
        var result = CliArguments.Parse(
            ["query", "task", "--changed", "--exclude-tests", "--project", "Desktop", "--kind", "class,method", "--kind", "razor-component"]);

        Assert.IsTrue(result.ChangedOnly);
        Assert.IsFalse(result.IncludeTests);
        Assert.AreEqual("Desktop", result.ProjectFilter);
        CollectionAssert.AreEqual(new[] { "class", "method", "razor-component" }, result.Kinds.ToArray());
    }
}
