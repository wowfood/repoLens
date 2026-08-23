namespace DevContext.Infrastructure;

internal static class RepositoryLocator
{
    public static string FindRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate a Git repository from '{Path.GetFullPath(startDirectory)}'.");
    }
}
