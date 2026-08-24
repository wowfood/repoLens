using DevContext.Configuration;
using DevContext.Core;

namespace DevContext.Services;

internal sealed class AffectedService(
    GitService gitService,
    RepositoryGraphService graphService,
    ContextStore store)
{
    public async Task<AffectedReport> CalculateAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var baseline = await store.ReadStatusAsync(repositoryRoot, cancellationToken);
        var (baselineSymbols, baselineDependencies) = await store.ReadIndexesAsync(
            repositoryRoot,
            cancellationToken);
        var currentGit = await gitService.CaptureAsync(repositoryRoot, cancellationToken);
        var changes = await gitService.ChangesSinceAsync(
            repositoryRoot,
            baseline.Git,
            currentGit,
            cancellationToken);
        var currentGraph = await graphService.BuildAsync(repositoryRoot, config, cancellationToken);
        return AffectedCalculator.Calculate(
            baseline,
            baselineSymbols,
            baselineDependencies,
            currentGraph,
            changes);
    }
}
