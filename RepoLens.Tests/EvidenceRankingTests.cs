using DevContext.Core;
using DevContext.Services;

namespace DevContext.Tests;

/// <summary>
/// Pins the scoring function's behaviour directly, without going through a repository build.
///
/// Before these existed, the only way to observe a ranking change was to run a whole query and read
/// the output, so no change could be attributed to a particular weight. That is why the corpus could
/// say a case regressed but never say which knob did it.
/// </summary>
[TestClass]
public sealed class EvidenceRankingTests
{
    [TestMethod]
    public void ExactMatch_OutranksEveryPartialMatch()
    {
        var scored = Score(
            "OrderService",
            [Symbol("OrderService", "src/Ordering/OrderService.cs"),
             Symbol("OrderServiceFactory", "src/Ordering/OrderServiceFactory.cs")]);

        Assert.IsGreaterThan(
            scored["OrderServiceFactory"].Score,
            scored["OrderService"].Score,
            "An exact name match must beat a symbol that merely contains the query.");
    }

    [TestMethod]
    public void WordMatch_OutranksSubstringMatch()
    {
        // "Reservation" contains "reserve"? No — it contains "reserv". The point is the tiering:
        // a whole identifier word beats an incidental substring of a longer one.
        var scored = Score(
            "stock",
            [Symbol("StockLedger", "src/Inventory/StockLedger.cs"),
             Symbol("RestockingPolicy", "src/Inventory/RestockingPolicy.cs")]);

        Assert.IsGreaterThan(
            scored["RestockingPolicy"].Score,
            scored["StockLedger"].Score);
    }

    [TestMethod]
    public void Rarity_PrefersTheTermThatDiscriminates()
    {
        // "service" names half the repository; "ledger" names one thing. Both symbols match one
        // query term each, so only the rarity weighting can separate them.
        var symbols = new List<SymbolRecord> { Symbol("StockLedger", "src/Inventory/StockLedger.cs") };
        symbols.AddRange(Enumerable.Range(0, 20)
            .Select(index => Symbol($"Service{index}", $"src/Services/Service{index}.cs")));

        var scored = Score("service ledger", symbols);

        Assert.IsGreaterThan(
            scored["Service0"].Score,
            scored["StockLedger"].Score,
            "A term that matches one symbol must outweigh one that matches twenty.");
    }

    [TestMethod]
    public void ExecutableDeclarations_AreRankedAboveTheirContainingType()
    {
        var method = Symbol("ReserveStock", "src/Inventory/StockLedger.cs", kind: "method");
        var type = Symbol("ReserveStock", "src/Inventory/StockLedger.cs", kind: "class");
        var scored = EvidenceQueryService.ScoreSymbols([method, type], "reserve stock", ["reserve", "stock"], null);

        Assert.IsGreaterThan(
            scored[type.Identity].Score,
            scored[method.Identity].Score,
            "Methods are what gets edited, so they outrank the type that holds them.");
    }

    [TestMethod]
    public void RaisingAWeight_ReordersTheTwoSymbolsItSeparates()
    {
        // The property the whole weights table exists for: a named knob has an isolated, observable
        // effect, so a corpus movement can be attributed to it rather than guessed at.
        // Names chosen so neither symbol is an exact match for the query: an exact match scores
        // 1000 and would swamp whichever weight the test is trying to isolate.
        var namedForIt = Symbol("LedgerWriter", "src/Inventory/Unrelated.cs");
        var filedUnderIt = Symbol("Unrelated", "src/Inventory/Ledger.cs");
        var symbols = new[] { namedForIt, filedUnderIt };

        var byName = EvidenceQueryService.ScoreSymbols(
            symbols, "ledger", ["ledger"], null,
            EvidenceRankingWeights.Default with { SymbolNameWord = 180, FileNameWord = 10 });
        var byFile = EvidenceQueryService.ScoreSymbols(
            symbols, "ledger", ["ledger"], null,
            EvidenceRankingWeights.Default with { SymbolNameWord = 10, FileNameWord = 180 });

        Assert.IsGreaterThan(byName[filedUnderIt.Identity].Score, byName[namedForIt.Identity].Score);
        Assert.IsGreaterThan(byFile[namedForIt.Identity].Score, byFile[filedUnderIt.Identity].Score);
    }

    [TestMethod]
    public void ChangedOnly_RestrictsSeedsWithoutScoringThemDifferently()
    {
        var symbols = new List<SymbolRecord>
        {
            Symbol("Reserve", "src/Inventory/StockLedger.cs"),
            Symbol("Reserve", "src/Ordering/OrderService.cs")
        };
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/Inventory/StockLedger.cs"
        };

        var unfiltered = EvidenceQueryService.ScoreSymbols(symbols, "reserve", ["reserve"], null);
        var filtered = EvidenceQueryService.ScoreSymbols(symbols, "reserve", ["reserve"], changed);

        Assert.HasCount(2, unfiltered);
        Assert.HasCount(1, filtered);

        // --changed is a restriction on seeds, not a boost. Every surviving symbol is in a changed
        // file, so adding a constant to all of them reordered nothing and only put the same sentence
        // in every selection-reason list.
        var survivor = filtered.Values.Single();
        Assert.AreEqual(
            unfiltered.Values.Single(candidate =>
                candidate.Symbol.File == "src/Inventory/StockLedger.cs").Score,
            survivor.Score);
        Assert.IsFalse(
            survivor.Reasons.Any(reason => reason.Contains("changed since baseline", StringComparison.Ordinal)),
            string.Join(" | ", survivor.Reasons));
    }

    [TestMethod]
    public void GraphExpansion_RanksNearerRelationshipsHigher()
    {
        var seed = Symbol("PlaceOrder", "src/Ordering/OrderService.cs");
        var near = Symbol("Reserve", "src/Inventory/StockLedger.cs");
        var far = Symbol("Receive", "src/Inventory/Receiving.cs");
        var symbols = new[] { seed, near, far }.ToDictionary(
            symbol => symbol.Identity,
            StringComparer.Ordinal);
        var candidates = EvidenceQueryService.ScoreSymbols([seed], "placeorder", ["placeorder"], null);

        EvidenceQueryService.ExpandThroughGraph(
            candidates,
            symbols,
            [
                new SymbolReference(seed.Identity, near.Identity, "method-call", "a", "b"),
                new SymbolReference(near.Identity, far.Identity, "method-call", "b", "b")
            ],
            depth: 2);

        Assert.HasCount(3, candidates);
        Assert.IsGreaterThan(
            candidates[far.Identity].Score,
            candidates[near.Identity].Score,
            "A symbol one edge away must outrank one two edges away.");
        Assert.IsTrue(candidates[far.Identity].Reasons.Any(reason => reason.Contains("depth 2", StringComparison.Ordinal)));
    }

    private static Dictionary<string, EvidenceQueryService.Candidate> Score(
        string query,
        IReadOnlyCollection<SymbolRecord> symbols,
        EvidenceRankingWeights? weights = null)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = EvidenceQueryService.ScoreSymbols(symbols, query, terms, null, weights);
        return scored.Values.ToDictionary(candidate => candidate.Symbol.Name, StringComparer.Ordinal);
    }

    private static SymbolRecord Symbol(string name, string file, string kind = "class") => new(
        Identity: $"{file}|{name}|{kind}",
        Kind: kind,
        Name: name,
        Namespace: "Sample",
        ContainingType: null,
        Project: "src/Sample/Sample.csproj",
        File: file,
        Line: 1,
        BaseType: null,
        Interfaces: [])
    {
        SemanticName = $"Sample.{name}"
    };
}
