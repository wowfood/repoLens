using System.Diagnostics;
using System.Globalization;
using DevContext.Configuration;
using DevContext.Core;

namespace DevContext.Services;

internal sealed class EvidenceBenchmarkService(
    EvidenceQueryService evidence,
    RepositoryGraphService graph)
{
    public async Task<EvidenceBenchmarkReport> RunAsync(
        string repositoryRoot,
        DevContextConfig configuration,
        IReadOnlyList<EvidenceBenchmarkCase> cases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Count == 0)
        {
            throw new ArgumentException("At least one evidence benchmark case is required.", nameof(cases));
        }

        var results = new List<EvidenceBenchmarkCaseResult>(cases.Count);
        foreach (var benchmarkCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(benchmarkCase);
            var options = new EvidenceQueryOptions
            {
                Query = benchmarkCase.Query,
                MaxTokens = benchmarkCase.MaxTokens,
                MaxResults = benchmarkCase.MaxResults,
                GraphDepth = benchmarkCase.GraphDepth
            };

            // The cold run must not inherit a graph another case left in memory, or "cold" latency
            // measures a cache hit.
            graph.ClearMemoryCache();
            var stopwatch = Stopwatch.StartNew();
            var cold = await evidence.BuildAsync(repositoryRoot, configuration, options, cancellationToken);
            stopwatch.Stop();
            var coldMilliseconds = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            var warm = await evidence.BuildAsync(repositoryRoot, configuration, options, cancellationToken);
            stopwatch.Stop();
            var warmMilliseconds = stopwatch.ElapsedMilliseconds;

            // Comparing the warm run to the cold one only proves that one in-memory graph object
            // yields one bundle. Dropping the memory cache forces the graph to be rehydrated from
            // its persisted form, so this third run is the one that can actually catch ordering and
            // round-trip bugs.
            graph.ClearMemoryCache();
            var rebuilt = await evidence.BuildAsync(repositoryRoot, configuration, options, cancellationToken);

            var expectedFiles = benchmarkCase.ExpectedFiles
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retrievedFiles = cold.Blocks
                .Select(block => NormalizePath(block.File))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingFiles = expectedFiles.Except(retrievedFiles, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var unexpectedFiles = retrievedFiles.Except(expectedFiles, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missingRelationships = MissingRelationships(benchmarkCase, cold);
            var matched = expectedFiles.Count - missingFiles.Length;
            var recall = expectedFiles.Count == 0 ? 1d : matched / (double)expectedFiles.Count;
            var precision = retrievedFiles.Count == 0
                ? expectedFiles.Count == 0 ? 1d : 0d
                : matched / (double)retrievedFiles.Count;
            var determinismDetail = DeterminismDetail(cold, warm, rebuilt);
            var sufficiencyMatched = (benchmarkCase.ExpectedSufficiency is null
                                      || cold.Sufficiency == benchmarkCase.ExpectedSufficiency)
                                     && (benchmarkCase.ExpectAbstention is null
                                         || cold.ShouldAbstain == benchmarkCase.ExpectAbstention);

            var failureReasons = new List<string>();
            if (missingFiles.Length > 0)
            {
                failureReasons.Add($"Expected files were not retrieved: {string.Join(", ", missingFiles)}.");
            }

            if (missingRelationships.Length > 0)
            {
                failureReasons.Add(
                    $"Expected relationships were not retrieved: {string.Join("; ", missingRelationships)}.");
            }

            if (precision < benchmarkCase.MinPrecision)
            {
                failureReasons.Add(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Precision {0:P1} is below the required {1:P1}; unexpected files: {2}.",
                        precision,
                        benchmarkCase.MinPrecision,
                        unexpectedFiles.Length == 0 ? "none" : string.Join(", ", unexpectedFiles)));
            }

            var tokenCeiling = benchmarkCase.MaxApproximateTokens ?? benchmarkCase.MaxTokens;
            if (cold.ApproximateTokens > tokenCeiling)
            {
                failureReasons.Add(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Approximate tokens {0:N0} exceed the ceiling of {1:N0}.",
                        cold.ApproximateTokens,
                        tokenCeiling));
            }

            if (determinismDetail is not null)
            {
                failureReasons.Add(determinismDetail);
            }

            if (!sufficiencyMatched)
            {
                failureReasons.Add(
                    $"Expected evidence {benchmarkCase.ExpectedSufficiency?.ToString() ?? "any"} / abstain "
                    + $"{benchmarkCase.ExpectAbstention?.ToString() ?? "any"}, but observed "
                    + $"{cold.Sufficiency} / {cold.ShouldAbstain}.");
            }

            results.Add(new EvidenceBenchmarkCaseResult
            {
                Name = benchmarkCase.Name,
                FileRecall = recall,
                FilePrecision = precision,
                MissingFiles = missingFiles,
                UnexpectedFiles = unexpectedFiles,
                MissingRelationships = missingRelationships,
                ApproximateTokens = cold.ApproximateTokens,
                EvidenceBlocks = cold.Blocks.Count,
                ColdMilliseconds = coldMilliseconds,
                WarmMilliseconds = warmMilliseconds,
                Deterministic = determinismDetail is null,
                Sufficiency = cold.Sufficiency,
                ShouldAbstain = cold.ShouldAbstain,
                SufficiencyMatched = sufficiencyMatched,
                Passed = failureReasons.Count == 0 || benchmarkCase.Advisory,
                FailureReasons = failureReasons,
                Advisory = benchmarkCase.Advisory
            });
        }

        return new EvidenceBenchmarkReport
        {
            Cases = results,
            MeanFileRecall = results.Average(result => result.FileRecall),
            MeanFilePrecision = results.Average(result => result.FilePrecision),
            TotalApproximateTokens = results.Sum(result => result.ApproximateTokens),
            Passed = results.All(result => result.Passed),
            AdvisoryFailures = results.Count(result => result.Advisory && result.FailureReasons.Count > 0)
        };
    }

    private static string? DeterminismDetail(EvidenceBundle cold, EvidenceBundle warm, EvidenceBundle rebuilt)
    {
        if (!Equivalent(cold, warm))
        {
            return "The warm run produced a different bundle than the cold run over the same graph.";
        }

        return Equivalent(cold, rebuilt)
            ? null
            : "The bundle changed after the in-memory graph was dropped and rehydrated, so the "
              + "persisted graph does not round-trip deterministically.";
    }

    private static bool Equivalent(EvidenceBundle left, EvidenceBundle right) =>
        left.BundleId == right.BundleId
        && string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)
        && string.Equals(left.RepositoryInputHash, right.RepositoryInputHash, StringComparison.Ordinal)
        && left.Blocks.Select(block => block.Id).SequenceEqual(right.Blocks.Select(block => block.Id), StringComparer.Ordinal)
        && left.AnalysisGaps.SequenceEqual(right.AnalysisGaps, StringComparer.Ordinal);

    /// <summary>
    /// Resolves each expected relationship against the bundle. See
    /// <see cref="EvidenceBenchmarkCase.ExpectedRelationships"/> for the accepted forms.
    /// </summary>
    private static string[] MissingRelationships(EvidenceBenchmarkCase benchmarkCase, EvidenceBundle bundle)
    {
        if (benchmarkCase.ExpectedRelationships.Count == 0)
        {
            return [];
        }

        var fileByBlock = bundle.Blocks.ToDictionary(
            block => block.Id,
            block => NormalizePath(block.File),
            StringComparer.Ordinal);

        return benchmarkCase.ExpectedRelationships
            .Where(expected => !bundle.Relationships.Any(actual => Matches(expected, actual, fileByBlock)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Matches(
        string expected,
        EvidenceRelationship actual,
        IReadOnlyDictionary<string, string> fileByBlock)
    {
        var separator = expected.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return string.Equals(expected.Trim(), actual.Relationship, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(
                expected[..separator].Trim(),
                actual.Relationship,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var endpoints = expected[(separator + 1)..].Split("->", StringSplitOptions.TrimEntries);
        if (endpoints.Length != 2)
        {
            throw new ArgumentException(
                $"Expected relationship '{expected}' must be a relationship kind, optionally followed by "
                + "': <source file> -> <target file>'.",
                nameof(expected));
        }

        return fileByBlock.TryGetValue(actual.SourceBlock, out var sourceFile)
               && fileByBlock.TryGetValue(actual.TargetBlock, out var targetFile)
               && NormalizePath(endpoints[0]).Equals(sourceFile, StringComparison.OrdinalIgnoreCase)
               && NormalizePath(endpoints[1]).Equals(targetFile, StringComparison.OrdinalIgnoreCase);
    }

    private static void Validate(EvidenceBenchmarkCase benchmarkCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.Query);
        ArgumentNullException.ThrowIfNull(benchmarkCase.ExpectedFiles);
        if (benchmarkCase.MaxTokens < 256) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.MaxTokens));
        if (benchmarkCase.MaxResults < 1) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.MaxResults));
        if (benchmarkCase.GraphDepth is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.GraphDepth));
        if (benchmarkCase.MinPrecision is < 0d or > 1d) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.MinPrecision));
        if (benchmarkCase.MaxApproximateTokens is < 1) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.MaxApproximateTokens));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
