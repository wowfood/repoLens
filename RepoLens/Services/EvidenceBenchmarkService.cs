using System.Diagnostics;
using DevContext.Configuration;
using DevContext.Core;

namespace DevContext.Services;

internal sealed class EvidenceBenchmarkService(EvidenceQueryService evidence)
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

            var stopwatch = Stopwatch.StartNew();
            var cold = await evidence.BuildAsync(repositoryRoot, configuration, options, cancellationToken);
            stopwatch.Stop();
            var coldMilliseconds = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            var warm = await evidence.BuildAsync(repositoryRoot, configuration, options, cancellationToken);
            stopwatch.Stop();

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
            var retrievedRelationships = cold.Relationships
                .Select(relationship => relationship.Relationship)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingRelationships = benchmarkCase.ExpectedRelationships
                .Where(expected => !retrievedRelationships.Contains(expected))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var matched = expectedFiles.Count - missingFiles.Length;
            var recall = expectedFiles.Count == 0 ? 1d : matched / (double)expectedFiles.Count;
            var precision = retrievedFiles.Count == 0
                ? expectedFiles.Count == 0 ? 1d : 0d
                : matched / (double)retrievedFiles.Count;
            var deterministic = cold.BundleId == warm.BundleId && cold.Prompt == warm.Prompt;
            var sufficiencyMatched = (benchmarkCase.ExpectedSufficiency is null
                                      || cold.Sufficiency == benchmarkCase.ExpectedSufficiency)
                                     && (benchmarkCase.ExpectAbstention is null
                                         || cold.ShouldAbstain == benchmarkCase.ExpectAbstention);
            var passed = missingFiles.Length == 0
                         && missingRelationships.Length == 0
                         && cold.ApproximateTokens <= benchmarkCase.MaxTokens
                         && deterministic
                         && sufficiencyMatched;

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
                WarmMilliseconds = stopwatch.ElapsedMilliseconds,
                Deterministic = deterministic,
                Sufficiency = cold.Sufficiency,
                ShouldAbstain = cold.ShouldAbstain,
                SufficiencyMatched = sufficiencyMatched,
                Passed = passed
            });
        }

        return new EvidenceBenchmarkReport
        {
            Cases = results,
            MeanFileRecall = results.Average(result => result.FileRecall),
            MeanFilePrecision = results.Average(result => result.FilePrecision),
            TotalApproximateTokens = results.Sum(result => result.ApproximateTokens),
            Passed = results.All(result => result.Passed)
        };
    }

    private static void Validate(EvidenceBenchmarkCase benchmarkCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkCase.Query);
        ArgumentNullException.ThrowIfNull(benchmarkCase.ExpectedFiles);
        if (benchmarkCase.MaxTokens < 256) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.MaxTokens));
        if (benchmarkCase.MaxResults < 1) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.MaxResults));
        if (benchmarkCase.GraphDepth is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(benchmarkCase.GraphDepth));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
