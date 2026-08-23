using DevContext.Core;

namespace DevContext.Services;

internal static class AffectedCalculator
{
    public static AffectedReport Calculate(
        StatusReport baseline,
        SymbolIndex baselineSymbols,
        DependencyIndex baselineDependencies,
        GitSnapshot currentGit,
        RepositoryGraph currentGraph)
    {
        var changedFiles = GitService.ChangedSince(baseline.Git, currentGit);
        var allProjects = currentGraph.Repository.Projects
            .Concat(baseline.Repository.Projects)
            .DistinctBy(project => project.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directlyAffected = changedFiles
            .SelectMany(file => ProjectOwnershipResolver.Explain(file, allProjects))
            .Select(project => project.ProjectPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedSymbols = baselineSymbols.Symbols
            .Concat(currentGraph.Symbols.Symbols)
            .Where(symbol => changedFiles.Contains(symbol.File, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .ToDictionary(symbol => symbol.Identity, StringComparer.Ordinal);
        var affectedSymbolIds = changedSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        var affectedProjects = new HashSet<string>(directlyAffected, StringComparer.OrdinalIgnoreCase);
        var symbolReferences = baselineDependencies.Symbols
            .Concat(currentGraph.Dependencies.Symbols)
            .Distinct()
            .ToArray();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var reference in symbolReferences)
            {
                if (affectedSymbolIds.Contains(reference.TargetSymbol)
                    && affectedSymbolIds.Add(reference.SourceSymbol))
                {
                    affectedProjects.Add(reference.SourceProject);
                    changed = true;
                }
            }
        }

        var projectDependencies = baselineDependencies.Projects
            .Concat(currentGraph.Dependencies.Projects)
            .Distinct()
            .ToArray();
        affectedProjects.UnionWith(ProjectOwnershipResolver.ExpandAffectedProjects(
            affectedProjects,
            projectDependencies));

        var allSymbols = baselineSymbols.Symbols
            .Concat(currentGraph.Symbols.Symbols)
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .ToArray();
        var affectedSymbols = allSymbols
            .Where(symbol => affectedSymbolIds.Contains(symbol.Identity))
            .Concat(changedSymbols.Values)
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .OrderBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ToArray();
        var testProjects = allProjects
            .Where(project => project.IsTestProject && affectedProjects.Contains(project.Path))
            .Select(project => project.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var testCases = allSymbols
            .Where(symbol => symbol.Kind == "test"
                             && affectedSymbolIds.Contains(symbol.Identity)
                             && testProjects.Contains(symbol.Project, StringComparer.OrdinalIgnoreCase))
            .Select(TestFullyQualifiedName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new AffectedReport
        {
            ChangedFiles = changedFiles,
            Projects = affectedProjects.Order(StringComparer.Ordinal).ToArray(),
            Symbols = affectedSymbols,
            ChangedSymbols = changedSymbols.Values
                .OrderBy(symbol => symbol.File, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.Line)
                .ToArray(),
            Tests = testProjects,
            TestCases = testCases
        };
    }

    private static string TestFullyQualifiedName(SymbolRecord symbol) =>
        string.Join('.', new[] { symbol.Namespace, symbol.ContainingType, symbol.Name }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

}
