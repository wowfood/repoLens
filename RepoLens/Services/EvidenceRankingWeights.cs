namespace DevContext.Services;

/// <summary>
/// The scoring constants used to rank evidence candidates.
///
/// These were previously integer literals scattered through <see cref="EvidenceQueryService"/> with
/// nothing naming them and no test isolating any one of them, so there was no way to reason about a
/// ranking change except by running the whole query and eyeballing the result. Naming them is the
/// prerequisite for the corpus in <c>RepoLens.Tests/Fixtures/BenchmarkRepo</c> being able to say
/// which knob moved a case.
///
/// Every value here is the one that was previously hard-coded. Changing one changes retrieval, so a
/// change belongs with a benchmark run that shows the effect.
/// </summary>
internal sealed record EvidenceRankingWeights
{
    public static EvidenceRankingWeights Default { get; } = new();

    /// <summary>The query is exactly this symbol's name or fully qualified name.</summary>
    public int ExactSymbolMatch { get; init; } = 1000;

    /// <summary>A query term is one of the words in the symbol's own name. Scaled by term rarity.</summary>
    public int SymbolNameWord { get; init; } = 180;

    /// <summary>A query term appears inside the symbol's name without being a word of it.</summary>
    public int SymbolNameSubstring { get; init; } = 45;

    /// <summary>A query term is a word of the fully qualified name but not of the short name.</summary>
    public int SemanticNameWord { get; init; } = 70;

    /// <summary>A query term is a word of the declaring file's name.</summary>
    public int FileNameWord { get; init; } = 55;




    /// <summary>Methods and tests are preferred over containing types: they are what gets edited.</summary>
    public int ExecutableDeclaration { get; init; } = 30;

    /// <summary>Ceiling on the score a symbol can reach through graph expansion alone.</summary>
    public int GraphScoreCeiling { get; init; } = 1000;

    /// <summary>Share of the anchor's score a related symbol inherits, as a divisor.</summary>
    public int GraphAnchorShareDivisor { get; init; } = 4;

    /// <summary>Ceiling on the inherited share, so a strong anchor cannot flood the result.</summary>
    public int GraphAnchorShareCeiling { get; init; } = 200;

    /// <summary>Subtracted per level of graph distance, so nearer relationships rank higher.</summary>
    public int GraphLevelPenalty { get; init; } = 15;

    /// <summary>
    /// How much each kind of structural edge contributes when graph expansion crosses it. An
    /// override or interface implementation is the strongest signal that two declarations are about
    /// the same thing; an unrecognized edge still counts, because the graph only records edges that
    /// exist.
    /// </summary>
    public int RelationshipWeight(string relationship) => relationship switch
    {
        "override" or "interface-implementation" => 130,
        "call" or "method-call" or "construct" or "constructs" or "constructed-type"
            or "member-read" or "member-write" => 115,
        "event-subscription" or "delegate-callback" => 105,
        "inheritance" or "interface" or "generic-type-argument" => 95,
        "dependency-injection" or "markup-binding" or "markup-event" or "component-use" => 85,
        _ => 70
    };
}
