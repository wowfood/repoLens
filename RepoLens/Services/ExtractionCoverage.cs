namespace DevContext.Services;

/// <summary>
/// What the semantic extractor sees, stated as data rather than as prose in a design document.
///
/// RepoLens sells a negative answer: "no callers exist" is supposed to mean something. That claim is
/// only as good as the set of constructs the extractor can actually observe, and until this type
/// existed that set lived in nobody's head and no file. A blind spot was therefore indistinguishable
/// from an absence -- which is the one confusion the product exists to prevent.
///
/// The rule for editing this file: a limit is removed from <see cref="KnownLimits"/> only when a test
/// proves the construct now produces a symbol or an edge, and <see cref="Version"/> is bumped
/// whenever either list changes, so a stored report can be read against the contract that produced
/// it.
/// </summary>
public static class ExtractionCoverage
{
    public const string Name = "roslyn-csharp";

    /// <summary>
    /// Bumped whenever <see cref="DeclarationKinds"/>, <see cref="RelationshipKinds"/> or
    /// <see cref="KnownLimits"/> change. Version 2 added top-level statements, delegates, and the
    /// typeof/nameof/attribute edges, and made partial declarations resolve to their implementation.
    /// </summary>
    public const int Version = 2;

    public static string Identifier => $"{Name}/{Version}";

    /// <summary>
    /// Declaration kinds the indexer emits, and therefore the only values a <c>--kind</c> filter can
    /// usefully name.
    /// </summary>
    public static IReadOnlyList<string> DeclarationKinds { get; } =
    [
        "class", "constructor", "conversion-operator", "delegate", "destructor", "entry-point",
        "enum", "enum-member", "event", "field", "indexer", "interface", "local-function", "member",
        "method", "operator", "property", "razor-component", "record", "struct", "test", "xaml-view"
    ];

    /// <summary>Relationship kinds the indexer emits between two indexed declarations.</summary>
    public static IReadOnlyList<string> RelationshipKinds { get; } =
    [
        "attribute", "component-use", "constructed-type", "constructor-parameter", "constructs",
        "delegate-callback", "dependency-injection", "event-subscription", "event-type", "field-type",
        "generic-type-argument", "interface-implementation", "markup-binding", "markup-code-behind",
        "markup-command", "markup-data-context", "markup-event", "member-read", "member-write",
        "method-call", "nameof-reference", "override", "parameter-type", "property-type",
        "return-type", "typeof-reference"
    ];

    /// <summary>
    /// Edges recorded between type declarations rather than between symbols. They live in a separate
    /// index keyed by display string rather than by symbol identity, which is why graph expansion
    /// cannot traverse them and asking about an interface does not pull in its implementations.
    /// </summary>
    public static IReadOnlyList<string> TypeRelationshipKinds { get; } = ["base-type", "interface"];

    /// <summary>
    /// Constructs that are still invisible, each phrased as what a caller would wrongly conclude.
    /// These are surfaced by <c>doctor</c> so an operator can read them before trusting an empty
    /// result, rather than discovering them from a wrong answer.
    /// </summary>
    public static IReadOnlyList<string> KnownLimits { get; } =
    [
        "Only one target framework per project is indexed, so a declaration that exists solely under "
        + "another framework is reported absent rather than unknown. The chosen framework is named in "
        + "each project's completeness record.",

        "Reflection, string-keyed service location, and configuration-driven wiring produce no edges, "
        + "so a type reached only that way appears unused.",

        "Source generators are executed only when indexing.executeSourceGenerators is enabled; "
        + "otherwise generated declarations and the edges into them are absent.",

        "Partial types and partial members share one identity, so a symbol declared across several "
        + "files resolves to a single declaration site -- the one carrying the implementation.",

        "Preprocessor-excluded code is not analysed under the branches that were not taken, so a "
        + "reference inside an inactive #if is invisible.",

        "F# and Visual Basic projects are indexed for ownership, references, and test propagation "
        + "only; no declarations or relationships are extracted from them.",

        "Base-type and interface edges are recorded between type declarations keyed by display "
        + "string, not by symbol identity, so graph expansion cannot traverse them and a query about "
        + "an interface does not reach its implementations."
    ];

    /// <summary>
    /// The gap to disclose when a query filters on declaration kinds the extractor never emits.
    /// Without it the query returns an honest-looking empty result for a filter that could not have
    /// matched anything.
    /// </summary>
    public static string? UnknownKindGap(IReadOnlyList<string> requestedKinds)
    {
        var unknown = requestedKinds
            .Where(kind => !DeclarationKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return unknown.Length == 0
            ? null
            : $"Coverage contract {Identifier} indexes no declaration kind named "
              + $"{string.Join(", ", unknown.Select(kind => $"'{kind}'"))}; that filter matched nothing "
              + "because the kind does not exist, not because the repository has none. Indexed kinds: "
              + $"{string.Join(", ", DeclarationKinds)}.";
    }
}
