using DevContext.Core;

namespace DevContext.Services;

internal static class ProjectOwnershipResolver
{
    public static IReadOnlyList<ProjectOwnershipMatch> Explain(
        string repositoryRelativePath,
        IReadOnlyList<ProjectRecord> projects)
    {
        var normalizedPath = NormalizePath(repositoryRelativePath);
        var explicitOwners = projects
            .Select(project =>
            {
                var itemTypes = project.Items
                    .Where(item => item.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ItemType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var isProjectFile = normalizedPath.Equals(project.Path, StringComparison.OrdinalIgnoreCase);
                var isIndexedFile = project.SourceFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase)
                                    || project.ProjectFiles.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase);
                if (!isProjectFile && !isIndexedFile && itemTypes.Length == 0)
                {
                    return null;
                }

                var reason = isProjectFile
                    ? "project file"
                    : itemTypes.Length > 0
                        ? "evaluated MSBuild item"
                        : "indexed project input";
                return Match(project, reason, itemTypes);
            })
            .Where(match => match is not null)
            .Cast<ProjectOwnershipMatch>()
            .ToArray();
        if (explicitOwners.Length > 0)
        {
            return explicitOwners;
        }

        if (IsSharedProjectInput(normalizedPath))
        {
            var inputDirectory = DirectoryName(normalizedPath);
            var scopedProjects = projects
                .Where(project => IsWithinDirectory(project.Path, inputDirectory))
                .ToArray();
            var owners = scopedProjects.Length > 0 ? scopedProjects : projects;
            var reason = scopedProjects.Length > 0
                ? "shared repository input in project scope"
                : "shared repository input";
            return owners.Select(project => Match(project, reason, [])).ToArray();
        }

        var containingProjects = projects
            .Select(project => new { Project = project, Directory = DirectoryName(project.Path) })
            .Where(candidate => IsWithinDirectory(normalizedPath, candidate.Directory))
            .ToArray();
        if (containingProjects.Length == 0)
        {
            return [];
        }

        var closestDirectoryLength = containingProjects.Max(candidate => candidate.Directory.Length);
        return containingProjects
            .Where(candidate => candidate.Directory.Length == closestDirectoryLength)
            .Select(candidate => Match(candidate.Project, "nearest containing project", []))
            .ToArray();
    }

    public static IReadOnlySet<string> ExpandAffectedProjects(
        IEnumerable<string> directlyAffectedProjects,
        IEnumerable<ProjectDependency> dependencies)
    {
        var affected = directlyAffectedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allDependencies = dependencies.Distinct().ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var dependency in allDependencies)
            {
                if (affected.Contains(dependency.ReferencedProject) && affected.Add(dependency.Project))
                {
                    changed = true;
                }
            }
        }

        return affected;
    }

    public static bool IsSharedProjectInput(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        return fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static ProjectOwnershipMatch Match(
        ProjectRecord project,
        string reason,
        IReadOnlyList<string> itemTypes) =>
        new(project.Name, project.Path, reason, itemTypes);

    private static bool IsWithinDirectory(string path, string directory) =>
        directory.Length == 0
        || NormalizePath(path).StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

    private static string DirectoryName(string path) =>
        Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? string.Empty;
}
