using System.Text;
using System.Text.RegularExpressions;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed partial class EvidenceQueryService(
    RepositoryGraphService graphService,
    GitService gitService,
    ContextStore contextStore,
    RepositoryFileFilter fileFilter)
{
    private const int ReservedPromptTokens = 550;
    private const int MaximumExcerptLines = 60;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "before", "could", "does", "from", "have", "into", "that", "the",
        "their", "this", "what", "when", "where", "which", "with", "would", "why"
    };

    public async Task<EvidenceBundle> BuildAsync(
        string repositoryRoot,
        DevContextConfig configuration,
        EvidenceQueryOptions options,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var graph = await graphService.BuildAsync(repositoryRoot, configuration, cancellationToken);
        IReadOnlyList<string> changedFiles = [];
        IReadOnlyList<GitFileChange> changes = [];
        var gitComparison = GitComparisonState.Comparable;
        var queryGaps = new List<string>();
        if (options.ChangedOnly)
        {
            if (contextStore.BaselineExists(repositoryRoot))
            {
                var baseline = await contextStore.ReadStatusAsync(repositoryRoot, cancellationToken);
                var current = await gitService.CaptureAsync(repositoryRoot, cancellationToken);
                var changeSet = await gitService.ChangesSinceAsync(
                    repositoryRoot,
                    baseline.Git,
                    current,
                    cancellationToken);
                changedFiles = changeSet.ChangedFiles;
                changes = changeSet.Changes;
                gitComparison = changeSet.Comparison;
                if (!changeSet.IsComplete)
                {
                    queryGaps.Add(
                        $"Changed-only Git comparison is incomplete: {changeSet.Comparison}. " +
                        "Recreate the baseline from an ancestor of the current HEAD.");
                }
            }
            else
            {
                queryGaps.Add("Changed-only selection requested, but no baseline exists.");
            }
        }

        var terms = QueryTerms(options.Query);
        var compoundTerms = CompoundIdentifierTerms(options.Query);
        var projectLookup = graph.Repository.Projects.ToDictionary(
            project => project.Path,
            StringComparer.OrdinalIgnoreCase);
        var symbols = graph.Symbols.Symbols
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .Where(symbol => options.IncludeTests
                             || !projectLookup.TryGetValue(symbol.Project, out var project)
                             || !project.IsTestProject)
            .Where(symbol => string.IsNullOrWhiteSpace(options.Project)
                             || symbol.Project.Contains(options.Project, StringComparison.OrdinalIgnoreCase)
                             || projectLookup.GetValueOrDefault(symbol.Project)?.Name.Contains(
                                 options.Project,
                                 StringComparison.OrdinalIgnoreCase) == true)
            .Where(symbol => options.Kinds.Count == 0
                             || options.Kinds.Contains(symbol.Kind, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(symbol => symbol.Identity, StringComparer.Ordinal);
        var candidates = ScoreSymbols(
            symbols.Values,
            options.Query,
            terms,
            options.ChangedOnly ? changedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase) : null);
        ExpandThroughGraph(candidates, symbols, graph.Dependencies.Symbols, options.GraphDepth);

        var blockBudget = Math.Max(64, options.MaxTokens - ReservedPromptTokens);
        var blocks = new List<EvidenceBlock>();
        var usedTokens = 0;
        var constrainedToIndexedSymbols = options.ChangedOnly
                                          || !options.IncludeTests
                                          || !string.IsNullOrWhiteSpace(options.Project)
                                          || options.Kinds.Count > 0;
        var semanticBlockLimit = constrainedToIndexedSymbols || options.MaxResults == 1
            ? options.MaxResults
            : options.MaxResults - 1;
        var selectionTruncated = candidates.Count > options.MaxResults;
        var orderedCandidates = candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Symbol.File, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Symbol.Line)
            .ThenBy(candidate => candidate.Symbol.Identity, StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in orderedCandidates.Take(options.MaxResults))
        {
            if (blocks.Count >= semanticBlockLimit)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = blockBudget - usedTokens;
            if (remaining < 32)
            {
                selectionTruncated = true;
                break;
            }

            var block = await CreateSymbolBlockAsync(
                repositoryRoot,
                candidate,
                graph.Symbols.GeneratedSources,
                remaining,
                cancellationToken);
            if (block is null)
            {
                continue;
            }

            if (blocks.Any(existing => Overlaps(existing, block)))
            {
                continue;
            }

            blocks.Add(block);
            usedTokens += block.ApproximateTokens;
        }

        if (!constrainedToIndexedSymbols && blocks.Count < options.MaxResults)
        {
            var remainingTokens = blockBudget - usedTokens;
            var lexicalBudget = compoundTerms.Count >= 2
                ? Math.Max(remainingTokens, Math.Min(128, blockBudget / 4))
                : remainingTokens;
            var lexical = await FindLexicalFileBlocksAsync(
                repositoryRoot,
                graph.Repository,
                terms,
                compoundTerms,
                blocks.Select(block => block.File).ToHashSet(StringComparer.OrdinalIgnoreCase),
                Math.Min(1, options.MaxResults - blocks.Count),
                lexicalBudget,
                configuration.Indexing,
                cancellationToken);
            if (lexical.Blocks.FirstOrDefault() is { } lexicalBlock)
            {
                var replacementTokens = 0;
                while (lexical.HighConfidence
                       && replacementTokens < lexicalBlock.ApproximateTokens
                       && FindDuplicateFileBlockIndex(blocks) is var duplicateIndex
                       && duplicateIndex >= 0)
                {
                    replacementTokens += blocks[duplicateIndex].ApproximateTokens;
                    usedTokens -= blocks[duplicateIndex].ApproximateTokens;
                    blocks.RemoveAt(duplicateIndex);
                }

                if ((lexical.HighConfidence || !lexicalBlock.Truncated)
                    && usedTokens + lexicalBlock.ApproximateTokens <= blockBudget)
                {
                    blocks.Add(lexicalBlock);
                    usedTokens += lexicalBlock.ApproximateTokens;
                }
            }

            selectionTruncated |= lexical.Truncated;
        }

        var relationships = BuildRelationships(blocks, graph.Dependencies.Symbols);
        var selectedProjects = blocks.Select(block => block.Project)
            .Where(project => project != "(unowned)")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completeness = graph.Symbols.CompilationCompleteness
            .Where(record => selectedProjects.Count == 0 || selectedProjects.Contains(record.Project))
            .OrderBy(record => record.Project, StringComparer.Ordinal)
            .ToArray();
        var gaps = completeness
            .Where(record => record.State != AnalysisCompletenessState.Complete)
            .SelectMany(record => record.Gaps.Select(gap => $"{record.Project}: {gap}"))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();
        gaps.InsertRange(0, queryGaps);
        var truncated = selectionTruncated || blocks.Any(block => block.Truncated);

        string bundleId;
        string prompt;
        EvidenceDecision decision;
        while (true)
        {
            var includedIds = blocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
            relationships = relationships
                .Where(relationship => includedIds.Contains(relationship.SourceBlock)
                                       && includedIds.Contains(relationship.TargetBlock))
                .ToArray();
            decision = EvaluateSufficiency(blocks, relationships, completeness, gaps, truncated);
            bundleId = CreateBundleId(
                graph.InputHash,
                options,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
            prompt = RenderPrompt(
                bundleId,
                options.Query,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
            if (EstimateTokens(prompt) <= options.MaxTokens || blocks.Count <= 1)
            {
                break;
            }

            blocks.RemoveAt(blocks.Count - 1);
            truncated = true;
        }

        while (EstimateTokens(prompt) > options.MaxTokens && blocks.Count == 1)
        {
            var excess = EstimateTokens(prompt) - options.MaxTokens;
            var reducedBudget = Math.Max(32, blocks[0].ApproximateTokens - excess - 8);
            if (reducedBudget >= blocks[0].ApproximateTokens)
            {
                break;
            }

            blocks[0] = TruncateBlock(blocks[0], reducedBudget);
            relationships = [];
            truncated = true;
            decision = EvaluateSufficiency(blocks, relationships, completeness, gaps, truncated);
            bundleId = CreateBundleId(
                graph.InputHash,
                options,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
            prompt = RenderPrompt(
                bundleId,
                options.Query,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
        }

        while (EstimateTokens(prompt) > options.MaxTokens && gaps.Count > 0)
        {
            gaps.RemoveAt(gaps.Count - 1);
            truncated = true;
            decision = EvaluateSufficiency(blocks, relationships, completeness, gaps, truncated);
            bundleId = CreateBundleId(
                graph.InputHash,
                options,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
            prompt = RenderPrompt(
                bundleId,
                options.Query,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
        }

        if (EstimateTokens(prompt) > options.MaxTokens && blocks.Count > 0)
        {
            blocks.Clear();
            relationships = [];
            truncated = true;
            decision = EvaluateSufficiency(blocks, relationships, completeness, gaps, truncated);
            bundleId = CreateBundleId(
                graph.InputHash,
                options,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
            prompt = RenderPrompt(
                bundleId,
                options.Query,
                blocks,
                relationships,
                gaps,
                truncated,
                decision);
        }

        return new EvidenceBundle
        {
            BundleId = bundleId,
            RepositoryInputHash = graph.InputHash,
            Query = options.Query,
            Blocks = blocks,
            Relationships = relationships,
            CompilationCompleteness = completeness,
            AnalysisGaps = gaps,
            Sufficiency = decision.Sufficiency,
            ShouldAbstain = decision.ShouldAbstain,
            SufficiencyReasons = decision.Reasons,
            ChangedFiles = changedFiles,
            Changes = changes,
            GitComparison = gitComparison,
            Truncated = truncated,
            ApproximateTokens = EstimateTokens(prompt),
            Prompt = prompt
        };
    }

    private static Dictionary<string, Candidate> ScoreSymbols(
        IEnumerable<SymbolRecord> symbols,
        string query,
        IReadOnlyList<string> terms,
        ISet<string>? changedFiles)
    {
        var symbolArray = symbols.ToArray();
        var documentFrequency = terms.ToDictionary(
            term => term,
            term => symbolArray.Count(symbol => SearchWords(symbol).Contains(term, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var symbol in symbolArray)
        {
            if (changedFiles is not null && !changedFiles.Contains(symbol.File))
            {
                continue;
            }

            var reasons = new List<string>();
            var score = 0;
            if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.SemanticName, query, StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
                reasons.Add("exact symbol match");
            }

            foreach (var term in terms)
            {
                var rarity = 1d + Math.Log(
                    1d + symbolArray.Length / (double)(1 + documentFrequency.GetValueOrDefault(term)));
                var nameWords = IdentifierWords(symbol.Name);
                var semanticWords = IdentifierWords(symbol.SemanticName ?? string.Empty);
                var fileWords = IdentifierWords(Path.GetFileNameWithoutExtension(symbol.File));
                if (nameWords.Contains(term, StringComparer.OrdinalIgnoreCase))
                {
                    score += (int)Math.Round(180 * rarity);
                    reasons.Add($"symbol word matches '{term}'");
                }
                else if (symbol.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += (int)Math.Round(45 * rarity);
                    reasons.Add($"symbol name contains '{term}'");
                }
                else if (semanticWords.Contains(term, StringComparer.OrdinalIgnoreCase))
                {
                    score += (int)Math.Round(70 * rarity);
                    reasons.Add($"semantic name matches word '{term}'");
                }

                if (fileWords.Contains(term, StringComparer.OrdinalIgnoreCase))
                {
                    score += (int)Math.Round(55 * rarity);
                    reasons.Add($"file name matches '{term}'");
                }
            }

            if (changedFiles?.Contains(symbol.File) == true)
            {
                score += 250;
                reasons.Add("declaration changed since baseline");
            }

            if (score > 0)
            {
                if (symbol.Kind is "method" or "test")
                {
                    score += 30;
                    reasons.Add("focused executable declaration");
                }

                result[symbol.Identity] = new Candidate(symbol, score, reasons.Distinct().ToArray());
            }
        }

        return result;
    }

    private static void ExpandThroughGraph(
        IDictionary<string, Candidate> candidates,
        IReadOnlyDictionary<string, SymbolRecord> symbols,
        IReadOnlyList<SymbolReference> references,
        int depth)
    {
        var frontier = candidates.Keys.ToHashSet(StringComparer.Ordinal);
        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var relatedCandidates = new Dictionary<
                string,
                (SymbolRecord Symbol, int Score, List<string> Reasons)>(StringComparer.Ordinal);
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                CollectRelated(reference.SourceSymbol, reference.TargetSymbol, reference.Relationship, "uses");
                CollectRelated(reference.TargetSymbol, reference.SourceSymbol, reference.Relationship, "used by");
            }

            foreach (var (identity, related) in relatedCandidates.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                candidates[identity] = new Candidate(
                    related.Symbol,
                    Math.Min(1000, related.Score),
                    related.Reasons.Distinct(StringComparer.Ordinal).ToArray());
                next.Add(identity);
            }

            frontier = next;

            void CollectRelated(string anchor, string related, string relationship, string direction)
            {
                if (!frontier.Contains(anchor) || candidates.ContainsKey(related)
                                               || !symbols.TryGetValue(related, out var symbol))
                {
                    return;
                }

                var anchorScore = candidates[anchor].Score;
                var contribution = Math.Max(
                    1,
                    RelationshipWeight(relationship) + Math.Min(200, anchorScore / 4) - level * 15);
                if (!relatedCandidates.TryGetValue(related, out var accumulated))
                {
                    accumulated = (symbol, 0, []);
                }

                accumulated.Reasons.Add($"graph depth {level}: {direction} '{relationship}'");
                relatedCandidates[related] = (
                    symbol,
                    accumulated.Score + contribution,
                    accumulated.Reasons);
            }
        }
    }

    private static async Task<EvidenceBlock?> CreateSymbolBlockAsync(
        string repositoryRoot,
        Candidate candidate,
        IReadOnlyList<GeneratedSourceRecord> generatedSources,
        int remainingTokens,
        CancellationToken cancellationToken)
    {
        string[] lines;
        if (candidate.Symbol.File.StartsWith("generated://", StringComparison.Ordinal))
        {
            var generated = generatedSources.FirstOrDefault(source =>
                source.File.Equals(candidate.Symbol.File, StringComparison.Ordinal));
            if (generated is null)
            {
                return null;
            }

            lines = generated.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        }
        else
        {
            var fullPath = ResolveRepositoryPath(repositoryRoot, candidate.Symbol.File);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        }
        if (lines.Length == 0)
        {
            return null;
        }

        var start = Math.Clamp(candidate.Symbol.Line, 1, lines.Length);
        var declaredEnd = candidate.Symbol.EndLine ?? start + 12;
        var end = Math.Clamp(declaredEnd, start, Math.Min(lines.Length, start + MaximumExcerptLines - 1));
        var excerptLines = lines[(start - 1)..end].ToList();
        var text = string.Join(Environment.NewLine, excerptLines);
        var truncated = declaredEnd > end;
        var maximumCharacters = Math.Max(32, (remainingTokens - 16) * 4);
        while (text.Length > maximumCharacters && excerptLines.Count > 1)
        {
            excerptLines.RemoveAt(excerptLines.Count - 1);
            end--;
            text = string.Join(Environment.NewLine, excerptLines);
            truncated = true;
        }

        if (text.Length > maximumCharacters)
        {
            text = text[..maximumCharacters].TrimEnd();
            truncated = true;
        }

        var contentHash = Hashing.Text(text);
        var id = $"e-{Hashing.Text($"{candidate.Symbol.File}|{start}|{end}|{contentHash}")[..12]}";
        return new EvidenceBlock
        {
            Id = id,
            Kind = candidate.Symbol.Kind,
            Project = candidate.Symbol.Project,
            File = candidate.Symbol.File,
            StartLine = start,
            EndLine = end,
            ContentHash = contentHash,
            Text = text,
            SymbolIdentity = candidate.Symbol.Identity,
            SemanticName = candidate.Symbol.SemanticName ?? candidate.Symbol.Name,
            SelectionReasons = candidate.Reasons,
            ApproximateTokens = EstimateTokens(text) + 16,
            Truncated = truncated
        };
    }

    private async Task<LexicalSelection> FindLexicalFileBlocksAsync(
        string repositoryRoot,
        RepositoryIndex repository,
        IReadOnlyList<string> terms,
        IReadOnlySet<string> compoundTerms,
        ISet<string> excludedFiles,
        int limit,
        int tokenBudget,
        IndexingConfig indexing,
        CancellationToken cancellationToken)
    {
        if (limit < 1 || tokenBudget < 32 || terms.Count == 0)
        {
            return new LexicalSelection([], false, false);
        }

        var inventory = await fileFilter.GetFilesAsync(repositoryRoot, indexing, cancellationToken);
        var matches = new List<FileMatch>();
        foreach (var path in EnumerateCandidateFiles(repositoryRoot, inventory)
                     .Take(indexing.MaxEvidenceFilesScanned))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            if (excludedFiles.Contains(relative) || new FileInfo(path).Length > indexing.MaxEvidenceFileBytes)
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var score = 0;
            var firstLine = 0;
            var bestLineScore = 0;
            var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var termCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < lines.Length; index++)
            {
                var lineScore = 0;
                foreach (var term in terms)
                {
                    if (!lines[index].Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var weight = term.Length >= 10 ? 40 : 10;
                    var count = termCounts.GetValueOrDefault(term);
                    if (count < 3)
                    {
                        score += weight;
                        termCounts[term] = count + 1;
                    }

                    lineScore += weight;
                    matchedTerms.Add(term);
                }

                if (lineScore > bestLineScore)
                {
                    bestLineScore = lineScore;
                    firstLine = index + 1;
                }
            }

            if (score > 0)
            {
                score += matchedTerms.Count * 15;
                matches.Add(new FileMatch(relative, lines, firstLine, score, matchedTerms.Order().ToArray()));
            }
        }

        var result = new List<EvidenceBlock>();
        var usedTokens = 0;
        var highConfidence = false;
        var orderedMatches = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.File, StringComparer.Ordinal)
            .ToArray();
        var truncated = orderedMatches.Length > limit;
        foreach (var match in orderedMatches.Take(limit))
        {
            var start = Math.Max(1, match.FirstLine - 4);
            var end = Math.Min(match.Lines.Length, match.FirstLine + 5);
            var excerptLines = match.Lines[(start - 1)..end].ToList();
            var text = string.Join(Environment.NewLine, excerptLines);
            var blockTokens = EstimateTokens(text) + 16;
            var remaining = tokenBudget - usedTokens;
            var excerptTruncated = false;
            while (blockTokens > remaining && excerptLines.Count > 1)
            {
                excerptLines.RemoveAt(excerptLines.Count - 1);
                end--;
                text = string.Join(Environment.NewLine, excerptLines);
                blockTokens = EstimateTokens(text) + 16;
                excerptTruncated = true;
            }

            if (blockTokens > remaining && remaining >= 32)
            {
                text = text[..Math.Min(text.Length, Math.Max(32, (remaining - 16) * 4))].TrimEnd();
                blockTokens = EstimateTokens(text) + 16;
                excerptTruncated = true;
            }

            if (remaining < 32 || blockTokens > remaining)
            {
                truncated = true;
                break;
            }

            var project = ProjectOwnershipResolver.Explain(match.File, repository.Projects)
                .Select(owner => owner.ProjectPath)
                .FirstOrDefault() ?? "(unowned)";
            var isTestProject = repository.Projects.FirstOrDefault(candidate =>
                candidate.Path.Equals(project, StringComparison.OrdinalIgnoreCase))?.IsTestProject == true;
            var contentHash = Hashing.Text(text);
            result.Add(new EvidenceBlock
            {
                Id = $"e-{Hashing.Text($"{match.File}|{start}|{end}|{contentHash}")[..12]}",
                Kind = "lexical-excerpt",
                Project = project,
                File = match.File,
                StartLine = start,
                EndLine = end,
                ContentHash = contentHash,
                Text = text,
                SelectionReasons = [$"text matches: {string.Join(", ", match.Terms)}"],
                ApproximateTokens = blockTokens,
                Truncated = excerptTruncated
            });
            highConfidence |= match.Terms.Count(compoundTerms.Contains) >= 2
                              && Path.GetExtension(match.File).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                              && !isTestProject;
            usedTokens += blockTokens;
        }

        return new LexicalSelection(result, truncated, highConfidence);
    }

    private static IReadOnlyList<EvidenceRelationship> BuildRelationships(
        IReadOnlyList<EvidenceBlock> blocks,
        IReadOnlyList<SymbolReference> references)
    {
        var blockBySymbol = blocks
            .Where(block => block.SymbolIdentity is not null)
            .ToDictionary(block => block.SymbolIdentity!, block => block.Id, StringComparer.Ordinal);
        return references
            .Where(reference => blockBySymbol.ContainsKey(reference.SourceSymbol)
                                && blockBySymbol.ContainsKey(reference.TargetSymbol))
            .Select(reference => new EvidenceRelationship(
                blockBySymbol[reference.SourceSymbol],
                blockBySymbol[reference.TargetSymbol],
                reference.Relationship,
                reference.Confidence)
            {
                Origin = reference.Origin,
                TargetFramework = reference.TargetFramework,
                EvidenceFile = reference.EvidenceFile,
                EvidenceLine = reference.EvidenceLine,
                EvidenceColumn = reference.EvidenceColumn,
                EvidenceEndLine = reference.EvidenceEndLine,
                EvidenceEndColumn = reference.EvidenceEndColumn
            })
            .Distinct()
            .OrderBy(relationship => relationship.SourceBlock, StringComparer.Ordinal)
            .ThenBy(relationship => relationship.TargetBlock, StringComparer.Ordinal)
            .ThenBy(relationship => relationship.Relationship, StringComparer.Ordinal)
            .Take(40)
            .ToArray();
    }

    private static string RenderPrompt(
        string bundleId,
        string query,
        IReadOnlyList<EvidenceBlock> blocks,
        IReadOnlyList<EvidenceRelationship> relationships,
        IReadOnlyList<string> gaps,
        bool truncated,
        EvidenceDecision decision)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# RepoLens evidence");
        builder.AppendLine();
        builder.AppendLine($"- Bundle: `{bundleId}`");
        builder.AppendLine($"- Evidence blocks: {blocks.Count}");
        builder.AppendLine($"- Truncated by budget or result bound: {(truncated ? "yes" : "no")}");
        builder.AppendLine($"- Evidence sufficiency: {decision.Sufficiency}");
        builder.AppendLine($"- Abstain from repository-backed conclusions: {(decision.ShouldAbstain ? "yes" : "no")}");
        if (decision.Reasons.Count > 0)
        {
            builder.AppendLine($"- Decision reasons: {string.Join("; ", decision.Reasons)}");
        }
        builder.AppendLine();
        builder.AppendLine("## Source evidence");
        if (blocks.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No source evidence matched the query.");
        }

        foreach (var block in blocks)
        {
            builder.AppendLine();
            builder.AppendLine($"### [{block.Id}] `{block.File}:{block.StartLine}-{block.EndLine}`");
            builder.AppendLine($"Selected because: {string.Join("; ", block.SelectionReasons)}.");
            if (block.Truncated)
            {
                builder.AppendLine("Excerpt truncated: yes.");
            }
            builder.AppendLine($"```{FenceLanguage(block.File)}");
            builder.AppendLine(block.Text);
            builder.AppendLine("```");
        }

        if (relationships.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Evidence relationships");
            builder.AppendLine();
            foreach (var relationship in relationships)
            {
                builder.AppendLine($"- [{relationship.SourceBlock}] --{relationship.Relationship}--> " +
                                   $"[{relationship.TargetBlock}] ({relationship.Confidence}; " +
                                   $"{relationship.Origin}{FormatEvidenceLocation(relationship)})");
            }
        }

        if (gaps.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Analysis gaps");
            builder.AppendLine();
            foreach (var gap in gaps)
            {
                builder.AppendLine($"- {gap}");
            }
        }

        if (decision.ShouldAbstain)
        {
            builder.AppendLine();
            builder.AppendLine("## Evidence decision");
            builder.AppendLine();
            builder.AppendLine(
                "Repository evidence is insufficient. Do not infer an implementation answer or proof of absence; report the evidence gap.");
        }

        builder.AppendLine();
        builder.AppendLine("## Task");
        builder.AppendLine();
        builder.AppendLine(query);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static EvidenceBlock TruncateBlock(EvidenceBlock block, int tokenBudget)
    {
        var maximumCharacters = Math.Max(32, (tokenBudget - 16) * 4);
        if (block.Text.Length <= maximumCharacters)
        {
            return block;
        }

        var text = block.Text[..maximumCharacters].TrimEnd();
        var endLine = Math.Min(block.EndLine, block.StartLine + text.Count(character => character == '\n'));
        var contentHash = Hashing.Text(text);
        return block with
        {
            Id = $"e-{Hashing.Text($"{block.File}|{block.StartLine}|{endLine}|{contentHash}")[..12]}",
            EndLine = endLine,
            ContentHash = contentHash,
            Text = text,
            ApproximateTokens = EstimateTokens(text) + 16,
            Truncated = true
        };
    }

    private static IReadOnlyList<string> QueryTerms(string query) => WordPattern()
        .Matches(query)
        .Select(match => match.Value)
        .SelectMany(value => CamelCasePattern().Matches(value).Select(match => match.Value).Append(value))
        .Select(value => value.ToLowerInvariant())
        .Where(value => value.Length >= 2 && !StopWords.Contains(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlySet<string> CompoundIdentifierTerms(string query) => WordPattern()
        .Matches(query)
        .Where(match => CamelCasePattern().Matches(match.Value).Count > 1)
        .Select(match => match.Value.ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int FindDuplicateFileBlockIndex(IReadOnlyList<EvidenceBlock> blocks)
    {
        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            if (blocks.Count(block => block.File.Equals(
                    blocks[index].File,
                    StringComparison.OrdinalIgnoreCase)) > 1)
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(
        string repositoryRoot,
        RepositoryFileInventory inventory) =>
        inventory.RelativePaths
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is
                ".cs" or ".fs" or ".vb" or ".razor" or ".xaml" or ".md" or ".json"
                or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets")
            .Select(path => RepositoryFileFilter.ToFullPath(repositoryRoot, path));

    private static string ResolveRepositoryPath(string repositoryRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Evidence file path must be repository-relative: {relativePath}");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var containedPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(containedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Evidence file path escapes the repository: {relativePath}");
        }

        return fullPath;
    }

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4d);

    private static string CreateBundleId(
        string repositoryInputHash,
        EvidenceQueryOptions options,
        IReadOnlyList<EvidenceBlock> blocks,
        IReadOnlyList<EvidenceRelationship> relationships,
        IReadOnlyList<string> gaps,
        bool truncated,
        EvidenceDecision decision) => Hashing.Text(string.Join(
        '|',
        repositoryInputHash,
        options.Query,
        options.MaxTokens,
        options.MaxResults,
        options.GraphDepth,
        options.ChangedOnly,
        options.IncludeTests,
        options.Project,
        string.Join(',', options.Kinds.Order(StringComparer.OrdinalIgnoreCase)),
        truncated,
        string.Join(',', blocks.Select(block => block.Id)),
        string.Join(',', relationships.Select(relationship =>
            $"{relationship.SourceBlock}>{relationship.Relationship}>{relationship.TargetBlock}>" +
            $"{relationship.Confidence}>{relationship.Origin}>{relationship.TargetFramework}>" +
            $"{relationship.EvidenceFile}:{relationship.EvidenceLine}:{relationship.EvidenceColumn}:" +
            $"{relationship.EvidenceEndLine}:{relationship.EvidenceEndColumn}")),
        string.Join(',', gaps),
        decision.Sufficiency,
        decision.ShouldAbstain,
        string.Join(',', decision.Reasons)))[..16];

    private static EvidenceDecision EvaluateSufficiency(
        IReadOnlyList<EvidenceBlock> blocks,
        IReadOnlyList<EvidenceRelationship> relationships,
        IReadOnlyList<CompilationCompletenessRecord> completeness,
        IReadOnlyList<string> gaps,
        bool truncated)
    {
        if (blocks.Count == 0)
        {
            return new EvidenceDecision(
                EvidenceSufficiency.Insufficient,
                true,
                ["No repository source evidence matched the query."]);
        }

        if (gaps.Any(gap => gap.StartsWith(
                "Changed-only Git comparison is incomplete:",
                StringComparison.Ordinal)))
        {
            return new EvidenceDecision(
                EvidenceSufficiency.Insufficient,
                true,
                ["The baseline commit cannot be compared safely with the current HEAD."]);
        }

        var reasons = new List<string>();
        if (truncated)
        {
            reasons.Add("The result or token budget truncated the evidence set.");
        }

        if (gaps.Count > 0 || completeness.Any(record => record.State != AnalysisCompletenessState.Complete))
        {
            reasons.Add("One or more selected project analyses are incomplete.");
        }

        if (blocks.All(block => block.Kind == "lexical-excerpt"))
        {
            reasons.Add("Only lexical evidence matched; no declared symbol was selected.");
        }

        if (relationships.Count > 0
            && relationships.All(relationship => relationship.Confidence != EvidenceConfidence.SemanticResolved))
        {
            reasons.Add("All selected relationships are syntax or convention fallbacks.");
        }

        return reasons.Count == 0
            ? new EvidenceDecision(EvidenceSufficiency.Sufficient, false, [])
            : new EvidenceDecision(EvidenceSufficiency.Partial, false, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string FormatEvidenceLocation(EvidenceRelationship relationship)
    {
        if (relationship.EvidenceFile is null || relationship.EvidenceLine is null)
        {
            return string.Empty;
        }

        var column = relationship.EvidenceColumn is null ? string.Empty : $":{relationship.EvidenceColumn}";
        var framework = relationship.TargetFramework is null ? string.Empty : $"; {relationship.TargetFramework}";
        return $"; {relationship.EvidenceFile}:{relationship.EvidenceLine}{column}{framework}";
    }

    private static IReadOnlyList<string> SearchWords(SymbolRecord symbol) =>
        IdentifierWords(string.Join(' ', symbol.Name, symbol.SemanticName, Path.GetFileNameWithoutExtension(symbol.File)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> IdentifierWords(string value) => WordPattern()
        .Matches(value)
        .Select(match => match.Value)
        .SelectMany(word => CamelCasePattern().Matches(word).Select(match => match.Value).Append(word))
        .Select(word => word.ToLowerInvariant())
        .Where(word => word.Length >= 2 && !StopWords.Contains(word))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static int RelationshipWeight(string relationship) => relationship switch
    {
        "override" or "interface-implementation" => 130,
        "call" or "method-call" or "construct" or "constructs" or "constructed-type"
            or "member-read" or "member-write" => 115,
        "event-subscription" or "delegate-callback" => 105,
        "inheritance" or "interface" or "generic-type-argument" => 95,
        "dependency-injection" or "markup-binding" or "markup-event" or "component-use" => 85,
        _ => 70
    };

    private static bool Overlaps(EvidenceBlock left, EvidenceBlock right) =>
        left.File.Equals(right.File, StringComparison.OrdinalIgnoreCase)
        && left.StartLine <= right.EndLine
        && right.StartLine <= left.EndLine;

    private static string FenceLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".razor" => "razor",
        ".xaml" => "xml",
        ".json" => "json",
        ".md" => "markdown",
        ".csproj" or ".props" or ".targets" => "xml",
        _ => "csharp"
    };

    private static void Validate(EvidenceQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Query);
        if (options.MaxTokens < 256) throw new ArgumentOutOfRangeException(nameof(options.MaxTokens));
        if (EstimateTokens(options.Query) > options.MaxTokens - 128)
        {
            throw new ArgumentException("The query itself is too large for the requested token budget.", nameof(options));
        }
        if (options.MaxResults < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxResults));
        if (options.GraphDepth is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(options.GraphDepth));
    }

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_./-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCasePattern();

    private sealed record Candidate(SymbolRecord Symbol, int Score, IReadOnlyList<string> Reasons);

    private sealed record EvidenceDecision(
        EvidenceSufficiency Sufficiency,
        bool ShouldAbstain,
        IReadOnlyList<string> Reasons);

    private sealed record FileMatch(
        string File,
        string[] Lines,
        int FirstLine,
        int Score,
        IReadOnlyList<string> Terms);

    private sealed record LexicalSelection(
        IReadOnlyList<EvidenceBlock> Blocks,
        bool Truncated,
        bool HighConfidence);
}
