using System.Text;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed class SymbolReferenceQueryService(RepositoryGraphService graphService)
{
    private static readonly HashSet<string> CallRelationships = new(StringComparer.Ordinal)
    {
        "method-call", "delegate-callback", "markup-event"
    };

    /// <summary>
    /// Relations whose matches are declared by the resolved symbol itself, so only the declaring
    /// project can contain them. Every other relation is inbound and can be declared anywhere that
    /// references the declaring project.
    /// </summary>
    private static readonly HashSet<SymbolReferenceRelation> OutboundRelations =
    [
        SymbolReferenceRelation.Callees,
        SymbolReferenceRelation.Implementations
    ];

    public async Task<SymbolReferenceQueryReport> QueryAsync(
        string repositoryRoot,
        DevContextConfig configuration,
        SymbolReferenceQueryOptions options,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var graph = await graphService.BuildAsync(repositoryRoot, configuration, cancellationToken);
        var symbols = graph.Symbols.Symbols
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .ToArray();
        var candidates = Resolve(options.Target, symbols);
        var resolved = candidates.Count == 1 ? candidates[0] : null;
        var allAmbiguous = candidates.Count > 1 ? candidates : [];
        var ambiguous = allAmbiguous.Take(options.MaxResults).ToList();
        var matches = resolved is null
            ? []
            : FindMatches(resolved, options.Relation, symbols, graph.Dependencies);
        var truncated = matches.Count > options.MaxResults || allAmbiguous.Count > ambiguous.Count;
        var selectedMatches = matches.Take(options.MaxResults).ToList();
        var projects = selectedMatches
            .SelectMany(match => new[] { match.Source.Project, match.Target.Project })
            .Concat(ReferenceScopeProjects(resolved, options.Relation, graph.Dependencies.Projects))
            .Append(resolved?.Project)
            .Where(project => project is not null)
            .Select(project => project!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completeness = graph.Symbols.CompilationCompleteness
            .Where(record => projects.Count == 0 || projects.Contains(record.Project))
            .OrderBy(record => record.Project, StringComparer.Ordinal)
            .ThenBy(record => record.TargetFramework, StringComparer.Ordinal)
            .ToArray();
        var gaps = completeness
            .Where(record => record.State != AnalysisCompletenessState.Complete)
            .SelectMany(record => record.Gaps.Select(gap => $"{record.Project}: {gap}"))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
        var decision = Decide(resolved, allAmbiguous, selectedMatches, completeness, truncated);
        var reportId = CreateReportId(options, resolved, ambiguous, selectedMatches, decision);
        var markdown = Render(
            reportId,
            options,
            resolved,
            ambiguous,
            selectedMatches,
            gaps,
            decision,
            truncated);
        while (EstimateTokens(markdown) > options.MaxTokens && selectedMatches.Count > 0)
        {
            selectedMatches.RemoveAt(selectedMatches.Count - 1);
            truncated = true;
            decision = Decide(resolved, allAmbiguous, selectedMatches, completeness, truncated);
            reportId = CreateReportId(options, resolved, ambiguous, selectedMatches, decision);
            markdown = Render(
                reportId,
                options,
                resolved,
                ambiguous,
                selectedMatches,
                gaps,
                decision,
                truncated);
        }

        while (EstimateTokens(markdown) > options.MaxTokens && ambiguous.Count > 0)
        {
            ambiguous.RemoveAt(ambiguous.Count - 1);
            truncated = true;
            reportId = CreateReportId(options, resolved, ambiguous, selectedMatches, decision);
            markdown = Render(
                reportId,
                options,
                resolved,
                ambiguous,
                selectedMatches,
                gaps,
                decision,
                truncated);
        }

        while (EstimateTokens(markdown) > options.MaxTokens && gaps.Count > 0)
        {
            gaps.RemoveAt(gaps.Count - 1);
            truncated = true;
            reportId = CreateReportId(options, resolved, ambiguous, selectedMatches, decision);
            markdown = Render(
                reportId,
                options,
                resolved,
                ambiguous,
                selectedMatches,
                gaps,
                decision,
                truncated);
        }

        return new SymbolReferenceQueryReport
        {
            ReportId = reportId,
            Query = options.Target,
            Relation = options.Relation,
            ResolvedSymbol = resolved,
            AmbiguousSymbols = ambiguous,
            Matches = selectedMatches,
            CompilationCompleteness = completeness,
            AnalysisGaps = gaps,
            Sufficiency = decision.Sufficiency,
            ShouldAbstain = decision.ShouldAbstain,
            Truncated = truncated,
            ApproximateTokens = EstimateTokens(markdown),
            Markdown = markdown
        };
    }

    /// <summary>
    /// Returns every project whose semantic analysis must be complete before an empty result can be
    /// read as proof of absence. An inbound edge can be declared by any project that transitively
    /// references the declaring project, so the completeness of the declaring project alone says
    /// nothing about whether a caller exists in a project that failed to compile.
    /// </summary>
    internal static IReadOnlySet<string> ReferenceScopeProjects(
        SymbolRecord? resolved,
        SymbolReferenceRelation relation,
        IReadOnlyList<ProjectDependency> projectDependencies)
    {
        if (resolved is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return OutboundRelations.Contains(relation)
            ? new HashSet<string>([resolved.Project], StringComparer.OrdinalIgnoreCase)
            : ProjectOwnershipResolver.ExpandAffectedProjects([resolved.Project], projectDependencies);
    }

    private static IReadOnlyList<SymbolRecord> Resolve(
        string target,
        IReadOnlyList<SymbolRecord> symbols)
    {
        if (TryFileLine(target, out var file, out var line))
        {
            var atLocation = symbols
                .Where(symbol => symbol.File.Equals(file, StringComparison.OrdinalIgnoreCase)
                                 && symbol.Line <= line
                                 && (symbol.EndLine ?? symbol.Line) >= line)
                .OrderByDescending(symbol => symbol.Line)
                .ThenBy(symbol => (symbol.EndLine ?? symbol.Line) - symbol.Line)
                .ThenBy(symbol => symbol.Identity, StringComparer.Ordinal)
                .ToArray();
            return atLocation.Length == 0 ? [] : [atLocation[0]];
        }

        var exactSemantic = symbols
            .Where(symbol => string.Equals(symbol.SemanticName, target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ToArray();
        if (exactSemantic.Length > 0)
        {
            return exactSemantic;
        }

        var exactName = symbols
            .Where(symbol => symbol.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ToArray();
        if (exactName.Length > 0)
        {
            return exactName;
        }

        return symbols
            .Where(symbol => symbol.SemanticName?.EndsWith($".{target}", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ToArray();
    }

    private static IReadOnlyList<SymbolReferenceMatch> FindMatches(
        SymbolRecord resolved,
        SymbolReferenceRelation relation,
        IReadOnlyList<SymbolRecord> symbols,
        DependencyIndex dependencies)
    {
        var symbolMap = symbols.ToDictionary(symbol => symbol.Identity, StringComparer.Ordinal);
        var references = relation switch
        {
            SymbolReferenceRelation.Callers => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && CallRelationships.Contains(reference.Relationship)),
            SymbolReferenceRelation.Callees => dependencies.Symbols.Where(reference =>
                reference.SourceSymbol == resolved.Identity && CallRelationships.Contains(reference.Relationship)),
            SymbolReferenceRelation.Implementers => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && reference.Relationship == "interface-implementation"),
            SymbolReferenceRelation.Implementations => dependencies.Symbols.Where(reference =>
                reference.SourceSymbol == resolved.Identity && reference.Relationship == "interface-implementation"),
            SymbolReferenceRelation.Overrides => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && reference.Relationship == "override"),
            SymbolReferenceRelation.ConstructorsOf => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && reference.Relationship == "constructed-type"),
            SymbolReferenceRelation.Readers => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && reference.Relationship == "member-read"),
            SymbolReferenceRelation.Writers => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && reference.Relationship == "member-write"),
            SymbolReferenceRelation.TestsCovering => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity
                && symbolMap.GetValueOrDefault(reference.SourceSymbol)?.Kind == "test"),
            SymbolReferenceRelation.InjectedInto => dependencies.Symbols.Where(reference =>
                reference.TargetSymbol == resolved.Identity && reference.Relationship == "dependency-injection"),
            _ => []
        };
        var matches = references
            .Where(reference => symbolMap.ContainsKey(reference.SourceSymbol)
                                && symbolMap.ContainsKey(reference.TargetSymbol))
            .Select(reference => ToMatch(reference, symbolMap))
            .ToList();

        if (relation is SymbolReferenceRelation.Implementers or SymbolReferenceRelation.Subtypes)
        {
            var relationships = relation == SymbolReferenceRelation.Implementers
                ? new[] { "interface" }
                : new[] { "base-type", "interface" };
            matches.AddRange(dependencies.Types
                .Where(dependency => relationships.Contains(dependency.Relationship, StringComparer.Ordinal)
                                     && RelatedTypeMatches(resolved, dependency.RelatedType)
                                     && symbolMap.TryGetValue(dependency.Symbol, out _))
                .Select(dependency => new SymbolReferenceMatch
                {
                    Source = symbolMap[dependency.Symbol],
                    Target = resolved,
                    Relationship = dependency.Relationship,
                    Confidence = EvidenceConfidence.SemanticResolved,
                    Origin = "roslyn-symbol",
                    EvidenceFile = symbolMap[dependency.Symbol].File,
                    EvidenceLine = symbolMap[dependency.Symbol].Line,
                    EvidenceEndLine = symbolMap[dependency.Symbol].EndLine
                }));
        }

        return matches
            .DistinctBy(match =>
                $"{match.Source.Identity}\0{match.Target.Identity}\0{match.Relationship}",
                StringComparer.Ordinal)
            .OrderBy(match => match.Source.File, StringComparer.Ordinal)
            .ThenBy(match => match.Source.Line)
            .ThenBy(match => match.Target.File, StringComparer.Ordinal)
            .ThenBy(match => match.Target.Line)
            .ThenBy(match => match.Relationship, StringComparer.Ordinal)
            .ToArray();
    }

    private static SymbolReferenceMatch ToMatch(
        SymbolReference reference,
        IReadOnlyDictionary<string, SymbolRecord> symbols) =>
        new()
        {
            Source = symbols[reference.SourceSymbol],
            Target = symbols[reference.TargetSymbol],
            Relationship = reference.Relationship,
            Confidence = reference.Confidence,
            Origin = reference.Origin,
            TargetFramework = reference.TargetFramework,
            EvidenceFile = reference.EvidenceFile,
            EvidenceLine = reference.EvidenceLine,
            EvidenceColumn = reference.EvidenceColumn,
            EvidenceEndLine = reference.EvidenceEndLine,
            EvidenceEndColumn = reference.EvidenceEndColumn
        };

    private static bool RelatedTypeMatches(SymbolRecord resolved, string relatedType)
    {
        var normalized = relatedType.Replace("global::", string.Empty, StringComparison.Ordinal);
        return normalized.Equals(resolved.Name, StringComparison.Ordinal)
               || normalized.Equals(resolved.SemanticName, StringComparison.Ordinal)
               || normalized.EndsWith($".{resolved.Name}", StringComparison.Ordinal);
    }

    private static bool TryFileLine(string target, out string file, out int line)
    {
        var separator = target.LastIndexOf(':');
        if (separator > 0 && int.TryParse(target[(separator + 1)..], out line) && line > 0)
        {
            file = target[..separator].Replace('\\', '/');
            return true;
        }

        file = string.Empty;
        line = 0;
        return false;
    }

    private static ReferenceDecision Decide(
        SymbolRecord? resolved,
        IReadOnlyList<SymbolRecord> ambiguous,
        IReadOnlyList<SymbolReferenceMatch> matches,
        IReadOnlyList<CompilationCompletenessRecord> completeness,
        bool truncated)
    {
        if (resolved is null)
        {
            return new ReferenceDecision(
                EvidenceSufficiency.Insufficient,
                true,
                ambiguous.Count == 0 ? "No symbol matched the target." : "The target is ambiguous.");
        }

        var incomplete = completeness.Any(record => record.State != AnalysisCompletenessState.Complete);
        if (matches.Count == 0 && incomplete)
        {
            return new ReferenceDecision(
                EvidenceSufficiency.Insufficient,
                true,
                "No matching edge was found, but relevant semantic analysis is incomplete.");
        }

        if (truncated || incomplete)
        {
            return new ReferenceDecision(
                EvidenceSufficiency.Partial,
                false,
                truncated ? "The result was truncated by a bound." : "Relevant semantic analysis is incomplete.");
        }

        return new ReferenceDecision(
            EvidenceSufficiency.Sufficient,
            false,
            matches.Count == 0 ? "Complete analysis found no matching edges." : "Exact structural edges matched.");
    }

    private static string Render(
        string reportId,
        SymbolReferenceQueryOptions options,
        SymbolRecord? resolved,
        IReadOnlyList<SymbolRecord> ambiguous,
        IReadOnlyList<SymbolReferenceMatch> matches,
        IReadOnlyList<string> gaps,
        ReferenceDecision decision,
        bool truncated)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# RepoLens structural references");
        builder.AppendLine();
        builder.AppendLine($"- Report: `{reportId}`");
        builder.AppendLine($"- Target: `{options.Target}`");
        builder.AppendLine($"- Relation: {options.Relation}");
        builder.AppendLine($"- Sufficiency: {decision.Sufficiency}");
        builder.AppendLine($"- Abstain: {(decision.ShouldAbstain ? "yes" : "no")}");
        builder.AppendLine($"- Decision: {decision.Reason}");
        builder.AppendLine($"- Truncated: {(truncated ? "yes" : "no")}");
        if (resolved is not null)
        {
            builder.AppendLine(
                $"- Resolved: `{resolved.SemanticName ?? resolved.Name}` (`{resolved.File}:{resolved.Line}`)");
        }

        AppendSymbols(builder, "Ambiguous symbols", ambiguous);
        builder.AppendLine();
        builder.AppendLine("## Matches");
        if (matches.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var match in matches)
            {
                var evidence = match.EvidenceFile is null
                    ? string.Empty
                    : $"; `{match.EvidenceFile}:{match.EvidenceLine}`";
                builder.AppendLine();
                builder.AppendLine(
                    $"- `{match.Source.SemanticName ?? match.Source.Name}` --{match.Relationship}--> " +
                    $"`{match.Target.SemanticName ?? match.Target.Name}` " +
                    $"({match.Confidence}; {match.Origin}{evidence})");
            }
        }

        AppendText(builder, "Analysis gaps", gaps);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendSymbols(
        StringBuilder builder,
        string heading,
        IReadOnlyList<SymbolRecord> symbols)
    {
        builder.AppendLine();
        builder.AppendLine($"## {heading}");
        if (symbols.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("(none)");
            return;
        }

        foreach (var symbol in symbols)
        {
            builder.AppendLine();
            builder.AppendLine($"- `{symbol.SemanticName ?? symbol.Name}` (`{symbol.File}:{symbol.Line}`)");
        }
    }

    private static void AppendText(StringBuilder builder, string heading, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {heading}");
        if (values.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("(none)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine();
            builder.AppendLine($"- {value}");
        }
    }

    private static string CreateReportId(
        SymbolReferenceQueryOptions options,
        SymbolRecord? resolved,
        IReadOnlyList<SymbolRecord> ambiguous,
        IReadOnlyList<SymbolReferenceMatch> matches,
        ReferenceDecision decision) => Hashing.Text(string.Join(
        '\n',
        options.Target,
        options.Relation,
        resolved?.Identity,
        string.Join(',', ambiguous.Select(symbol => symbol.Identity)),
        string.Join(',', matches.Select(match =>
            $"{match.Source.Identity}>{match.Relationship}>{match.Target.Identity}")),
        decision.Sufficiency,
        decision.ShouldAbstain))[..16];

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4d);

    private static void Validate(SymbolReferenceQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Target);
        if (options.MaxResults < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxResults));
        if (options.MaxTokens < 256) throw new ArgumentOutOfRangeException(nameof(options.MaxTokens));
        if (EstimateTokens(options.Target) > options.MaxTokens - 128)
        {
            throw new ArgumentException(
                "The target itself is too large for the requested token budget.",
                nameof(options));
        }
    }

    private sealed record ReferenceDecision(
        EvidenceSufficiency Sufficiency,
        bool ShouldAbstain,
        string Reason);
}
