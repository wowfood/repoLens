using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

/// <summary>
/// The C2 leftover: <c>context</c> reported <c>ApproximateTokens</c> without enforcing anything, so a
/// repository-scope narrative rendered tens of thousands of tokens that no MCP client could use.
/// Bounding it means dropping detail, and dropping detail silently would be worse than not bounding
/// it at all — the result would read as a complete description of the repository.
/// </summary>
[TestClass]
public sealed class ContextNarrativeBoundTests
{
    [TestMethod]
    public async Task Context_IsUnboundedUnlessACeilingIsAsked()
    {
        await using var repository = await NarrativeRepository.CreateAsync();

        var report = await repository.ContextAsync(null);

        Assert.IsFalse(report.Truncated);
        Assert.IsEmpty(report.AnalysisGaps.Where(gap => gap.Contains("bounded", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Context_FitsTheCeilingAndSaysWhatItDropped()
    {
        await using var repository = await NarrativeRepository.CreateAsync();

        var full = await repository.ContextAsync(null);
        Assert.IsGreaterThan(
            1200,
            full.ApproximateTokens,
            "The fixture must render more than the ceiling below, or nothing is being bounded.");

        var bounded = await repository.ContextAsync(1200);

        Assert.IsLessThanOrEqualTo(1200, bounded.ApproximateTokens);
        Assert.IsTrue(bounded.Truncated);

        // The counters describe the analysis, not the rendering, so the report never understates the
        // scope of what it examined.
        Assert.HasCount(full.AnalyzedFiles.Count, bounded.AnalyzedFiles);
        Assert.HasCount(full.AnalyzedProjects.Count, bounded.AnalyzedProjects);

        var disclosure = bounded.AnalysisGaps
            .SingleOrDefault(gap => gap.Contains("bounded to fit", StringComparison.Ordinal));
        Assert.IsNotNull(disclosure, string.Join(" | ", bounded.AnalysisGaps));
        StringAssert.Contains(bounded.Markdown, disclosure);
        Assert.IsTrue(
            bounded.TypeDefinitions.Count < full.TypeDefinitions.Count
            || bounded.Symbols.Count < full.Symbols.Count
            || bounded.Hotspots.Count < full.Hotspots.Count,
            "Truncated was reported without anything actually being dropped.");
    }

    [TestMethod]
    public async Task Context_DropsDetailInOrderAndKeepsTheHeadroomItWasGiven()
    {
        await using var repository = await NarrativeRepository.CreateAsync();

        var roomy = await repository.ContextAsync(1600);
        var tight = await repository.ContextAsync(700);

        // Type definitions are the richest and least structural, so they go before symbols.
        Assert.IsLessThanOrEqualTo(roomy.TypeDefinitions.Count, tight.TypeDefinitions.Count);
        Assert.IsLessThanOrEqualTo(roomy.Symbols.Count, tight.Symbols.Count);

        // A binary search rather than repeated halving, so the caller gets the detail they paid for.
        Assert.IsGreaterThan(
            (int)(1600 * 0.75),
            roomy.ApproximateTokens,
            "More than a quarter of the requested budget was left unused.");
        Assert.IsLessThanOrEqualTo(1600, roomy.ApproximateTokens);
        Assert.IsLessThanOrEqualTo(700, tight.ApproximateTokens);
    }

    private sealed class NarrativeRepository : IAsyncDisposable
    {
        private readonly RepositoryIntelligenceService intelligence;
        private readonly DevContextConfig configuration = new();

        private NarrativeRepository(string root, RepositoryIntelligenceService intelligence)
        {
            Root = root;
            this.intelligence = intelligence;
        }

        public string Root { get; }

        public static async Task<NarrativeRepository> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"repolens-narrative-{Guid.NewGuid():N}");
            var source = Path.Combine(root, "src", "Sample");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(
                Path.Combine(source, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

            // Enough declarations that the rendered narrative comfortably exceeds the ceilings above.
            for (var file = 0; file < 12; file++)
            {
                var builder = new System.Text.StringBuilder();
                builder.AppendLine("namespace Sample;");
                for (var type = 0; type < 4; type++)
                {
                    builder.AppendLine($"public sealed class Widget{file:D2}{type}");
                    builder.AppendLine("{");
                    for (var member = 0; member < 5; member++)
                    {
                        builder.AppendLine($"    public int Measure{member}(int value) => value + {member};");
                    }

                    builder.AppendLine("}");
                }

                await File.WriteAllTextAsync(Path.Combine(source, $"Widgets{file:D2}.cs"), builder.ToString());
            }

            IProcessRunner runner = new ProcessRunner();
            var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
            Assert.AreEqual(ExecutionState.Succeeded, git.State, git.StandardError);

            var files = new RepositoryFileFilter(runner);
            var graph = new RepositoryGraphService(runner, new ProjectIndexer(runner, files), files);
            var store = new ContextStore();
            return new NarrativeRepository(
                root,
                new RepositoryIntelligenceService(runner, new GitService(runner), graph, store));
        }

        public Task<RepositoryContextReport> ContextAsync(int? maxTokens) =>
            intelligence.BuildAsync(
                Root,
                configuration,
                new RepositoryContextOptions
                {
                    Purpose = ContextPurpose.Architecture,
                    Scope = ContextScope.FullRepository,
                    MaxTokens = maxTokens
                },
                CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            TempDirectory.Delete(Root);
            return ValueTask.CompletedTask;
        }
    }
}
