using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

/// <summary>
/// The A3 contract: when a bundle will not fit its token budget, evidence is sacrificed and
/// disclosures are not. A tool whose thesis is "absence is not uncertainty" must never buy room for
/// an excerpt by deleting the sentence that says the analysis was incomplete.
///
/// This needs a repository that genuinely produces analysis gaps, which is why it carries its own
/// fixture rather than reusing the benchmark one: a project that fails to compile, so the gaps are
/// real records emitted by <see cref="SymbolIndexer"/> rather than strings injected by the test.
///
/// The fixture is deliberately a <em>single</em> project. Bundle gaps are scoped to the projects the
/// selected evidence came from, and the initial selection is itself bounded by the token budget, so
/// a multi-project fixture changes its gap set as the budget moves for a reason that has nothing to
/// do with this contract. With one project the gap set is constant and any shrinkage is the trade
/// this test exists to forbid.
/// </summary>
[TestClass]
public sealed class EvidenceBudgetTests
{
    /// <summary>
    /// Budgets from the validated minimum up to comfortable, so the sweep crosses every reduction
    /// stage: dropping blocks, truncating the last block, clearing it, and only then trimming gaps.
    /// </summary>
    private static readonly int[] Budgets = [256, 320, 420, 560, 750, 1000, 1400, 2000, 3000];

    private const string Query = "inventory reservation ledger stock";

    /// <summary>Mirrors the notice <c>EvidenceQueryService</c> emits when it has to trim gaps.</summary>
    private const string GapsOmittedNotice =
        "Some analysis gaps were omitted to fit the token budget; treat this result as incomplete.";

    [TestMethod]
    public async Task Budget_DropsEvidenceBeforeDisclosures()
    {
        await using var repository = await GappyRepository.CreateAsync();

        var generous = await repository.QueryAsync(Query, 4000);

        Assert.IsNotEmpty(generous.Blocks, "The fixture must retrieve evidence at a generous budget.");
        Assert.IsGreaterThan(
            1,
            generous.AnalysisGaps.Count,
            "The fixture must produce more than one gap, or the trimming path is never reached.");

        var sawBlocksDropped = false;
        var sawGapsTrimmed = false;
        foreach (var budget in Budgets)
        {
            var bundle = await repository.QueryAsync(Query, budget);

            if (bundle.Blocks.Count < generous.Blocks.Count)
            {
                sawBlocksDropped = true;
            }

            // The invariant. A gap may only go once there is no evidence left to give up.
            if (bundle.AnalysisGaps.Count < generous.AnalysisGaps.Count
                || bundle.AnalysisGaps.Contains(GapsOmittedNotice))
            {
                sawGapsTrimmed = true;
                Assert.IsEmpty(
                    bundle.Blocks,
                    $"At a {budget}-token budget a disclosure was dropped while {bundle.Blocks.Count} "
                    + "evidence block(s) were still being carried.");
            }

            // Overrunning the budget is only permitted once nothing but disclosures remain, and
            // only because the alternative is understating what the analysis missed.
            if (bundle.ApproximateTokens > budget)
            {
                Assert.IsEmpty(bundle.Blocks, $"A {budget}-token budget was exceeded while carrying evidence.");
            }
        }

        Assert.IsTrue(sawBlocksDropped, "No budget in the sweep was tight enough to drop a block.");
        Assert.IsTrue(sawGapsTrimmed, "No budget in the sweep was tight enough to reach the gap trimming.");
    }

    [TestMethod]
    public async Task MinimumBudget_KeepsAConcreteGapBesideTheOmissionNoticeAndAbstains()
    {
        await using var repository = await GappyRepository.CreateAsync();

        var generous = await repository.QueryAsync(Query, 4000);
        var squeezed = await repository.QueryAsync(Query, 256);

        Assert.IsEmpty(squeezed.Blocks, "Evidence should have been given up entirely at the floor budget.");
        Assert.IsTrue(squeezed.Truncated);
        Assert.IsTrue(
            squeezed.ShouldAbstain,
            "A bundle carrying no evidence at all cannot support a repository-backed conclusion.");

        // The notice replaces the gap it displaced; it never overwrites a survivor. Reporting only
        // "some gaps were omitted" would tell the caller that something is wrong and nothing about
        // what, which is the failure this whole contract exists to prevent.
        CollectionAssert.Contains(squeezed.AnalysisGaps.ToArray(), GapsOmittedNotice);
        var concrete = squeezed.AnalysisGaps.Where(gap => gap != GapsOmittedNotice).ToArray();
        Assert.IsNotEmpty(concrete, "Every concrete disclosure was traded away for tokens.");
        CollectionAssert.IsSubsetOf(concrete, generous.AnalysisGaps.ToArray());
    }

    /// <summary>
    /// A repository whose projects do not compile, so <c>CompilationCompleteness</c> reports gaps
    /// that the evidence bundle has to carry.
    /// </summary>
    private sealed class GappyRepository : IAsyncDisposable
    {
        private readonly EvidenceQueryService evidence;
        private readonly DevContextConfig configuration = new();

        private GappyRepository(string root, EvidenceQueryService evidence)
        {
            Root = root;
            this.evidence = evidence;
        }

        public string Root { get; }

        public static async Task<GappyRepository> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"repolens-budget-tests-{Guid.NewGuid():N}");
            var inventory = Path.Combine(root, "src", "Inventory");
            Directory.CreateDirectory(inventory);

            await File.WriteAllTextAsync(
                Path.Combine(inventory, "Inventory.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

            // The only deliberately broken file. MissingWarehouseClient exists nowhere, so the
            // compilation carries error diagnostics and the project is recorded as incomplete.
            await File.WriteAllTextAsync(
                Path.Combine(inventory, "StockLedger.cs"),
                """
                namespace Inventory;
                public sealed class StockLedger
                {
                    private readonly MissingWarehouseClient client = new();
                    public bool ReserveStock(string sku, int quantity) => client.Reserve(sku, quantity);
                    public bool ReleaseStock(string sku, int quantity) => client.Release(sku, quantity);
                }
                """);

            // Several more matches for the same query, so a generous budget has more than one block
            // to carry and a tight one has something to give up.
            await File.WriteAllTextAsync(
                Path.Combine(inventory, "ReservationPolicy.cs"),
                """
                namespace Inventory;
                public sealed class ReservationPolicy
                {
                    public int MaximumReservationQuantity { get; init; } = 100;
                    public bool AllowsReservation(int quantity) => quantity <= MaximumReservationQuantity;
                    public bool AllowsReservationOfStock(string sku, int quantity) =>
                        sku.Length > 0 && AllowsReservation(quantity);
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(inventory, "StockReservation.cs"),
                """
                namespace Inventory;
                public sealed class StockReservation
                {
                    public required string Sku { get; init; }
                    public required int ReservedQuantity { get; init; }
                    public string DescribeReservation() => $"{Sku} x{ReservedQuantity}";
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(inventory, "InventorySnapshot.cs"),
                """
                namespace Inventory;
                public sealed class InventorySnapshot
                {
                    public required IReadOnlyList<StockReservation> Reservations { get; init; }
                    public int TotalReservedStock() =>
                        Reservations.Sum(reservation => reservation.ReservedQuantity);
                }
                """);

            IProcessRunner runner = new ProcessRunner();
            var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
            Assert.AreEqual(ExecutionState.Succeeded, git.State, git.StandardError);

            var files = new RepositoryFileFilter(runner);
            var graph = new RepositoryGraphService(runner, new ProjectIndexer(runner, files), files);
            return new GappyRepository(
                root,
                new EvidenceQueryService(graph, new GitService(runner), new ContextStore(), files));
        }

        public Task<EvidenceBundle> QueryAsync(string query, int maxTokens) =>
            evidence.BuildAsync(
                Root,
                configuration,
                new EvidenceQueryOptions { Query = query, MaxTokens = maxTokens, MaxResults = 8 },
                CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            TempDirectory.Delete(Root);
            return ValueTask.CompletedTask;
        }
    }
}
