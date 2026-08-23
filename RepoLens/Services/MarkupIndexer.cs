using System.Text.RegularExpressions;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal static partial class MarkupIndexer
{
    internal sealed record Result(
        IReadOnlyList<SymbolRecord> Symbols,
        IReadOnlyList<SymbolReference> References);

    public static async Task<Result> BuildAsync(
        string repositoryRoot,
        RepositoryIndex repository,
        IReadOnlyList<SymbolRecord> codeSymbols,
        CancellationToken cancellationToken)
    {
        var symbols = new List<SymbolRecord>();
        var references = new HashSet<SymbolReference>();
        foreach (var project in repository.Projects)
        {
            foreach (var item in project.Items
                         .Where(item => item.ItemType is "RazorComponent" or "MauiXaml" or "Page" or "ApplicationDefinition")
                         .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveContainedPath(repositoryRoot, item.Path);
                if (!File.Exists(path))
                {
                    continue;
                }

                var text = await File.ReadAllTextAsync(path, cancellationToken);
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var isRazor = item.ItemType == "RazorComponent"
                              || Path.GetExtension(item.Path).Equals(".razor", StringComparison.OrdinalIgnoreCase);
                var kind = isRazor ? "razor-component" : "xaml-view";
                var classMatch = isRazor ? null : XamlClassPattern().Match(text);
                var semanticName = classMatch?.Success == true
                    ? classMatch.Groups[1].Value
                    : Path.GetFileNameWithoutExtension(item.Path);
                var source = new SymbolRecord(
                    Hashing.Text(string.Join('|', project.Path, kind, item.Path)),
                    kind,
                    Path.GetFileNameWithoutExtension(item.Path),
                    NamespaceOf(semanticName),
                    null,
                    project.Path,
                    item.Path.Replace('\\', '/'),
                    1,
                    null,
                    [])
                {
                    SemanticName = semanticName,
                    EndLine = Math.Max(1, lines.Length)
                };
                symbols.Add(source);

                if (isRazor)
                {
                    IndexRazor(text, source, project, codeSymbols, references);
                }
                else
                {
                    IndexXaml(text, source, project, codeSymbols, references);
                }
            }
        }

        return new Result(
            symbols.OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
                .ToArray(),
            references.OrderBy(reference => reference.SourceProject, StringComparer.Ordinal)
                .ThenBy(reference => reference.SourceSymbol, StringComparer.Ordinal)
                .ThenBy(reference => reference.TargetSymbol, StringComparer.Ordinal)
                .ThenBy(reference => reference.Relationship, StringComparer.Ordinal)
                .ToArray());
    }

    private static void IndexRazor(
        string text,
        SymbolRecord source,
        ProjectRecord project,
        IReadOnlyList<SymbolRecord> codeSymbols,
        ISet<SymbolReference> references)
    {
        foreach (Match match in RazorInjectPattern().Matches(text))
        {
            AddNamedReference(
                source,
                match.Groups[1].Value,
                "dependency-injection",
                project,
                codeSymbols,
                references,
                evidence: match,
                evidenceText: text);
        }

        foreach (Match match in RazorComponentPattern().Matches(text))
        {
            var name = match.Groups[1].Value.Split(':').Last();
            if (name.Length > 0 && char.IsUpper(name[0]))
            {
                AddNamedReference(
                    source,
                    name,
                    "component-use",
                    project,
                    codeSymbols,
                    references,
                    typesOnly: true,
                    evidence: match,
                    evidenceText: text);
            }
        }

        foreach (Match match in RazorEventPattern().Matches(text))
        {
            AddNamedReference(
                source,
                LastIdentifier(match.Groups[1].Value),
                "markup-event",
                project,
                codeSymbols,
                references,
                evidence: match,
                evidenceText: text);
        }

        foreach (Match match in RazorBindingPattern().Matches(text))
        {
            AddNamedReference(
                source,
                LastIdentifier(match.Groups[1].Value),
                "markup-binding",
                project,
                codeSymbols,
                references,
                evidence: match,
                evidenceText: text);
        }
    }

    private static void IndexXaml(
        string text,
        SymbolRecord source,
        ProjectRecord project,
        IReadOnlyList<SymbolRecord> codeSymbols,
        ISet<SymbolReference> references)
    {
        var classMatch = XamlClassPattern().Match(text);
        if (classMatch.Success)
        {
            AddNamedReference(
                source,
                classMatch.Groups[1].Value,
                "markup-code-behind",
                project,
                codeSymbols,
                references,
                typesOnly: true,
                evidence: classMatch,
                evidenceText: text);
        }

        foreach (Match match in XamlElementPattern().Matches(text))
        {
            var name = match.Groups[1].Value.Split(':').Last();
            AddNamedReference(
                source,
                name,
                "component-use",
                project,
                codeSymbols,
                references,
                typesOnly: true,
                evidence: match,
                evidenceText: text);
        }

        foreach (Match match in XamlBindingPattern().Matches(text))
        {
            var relationship = match.Groups[1].Value.Contains("Command", StringComparison.OrdinalIgnoreCase)
                ? "markup-command"
                : "markup-binding";
            AddNamedReference(
                source,
                LastIdentifier(match.Groups[2].Value),
                relationship,
                project,
                codeSymbols,
                references,
                evidence: match,
                evidenceText: text);
        }

        foreach (Match match in XamlDataTypePattern().Matches(text))
        {
            AddNamedReference(
                source,
                LastIdentifier(match.Groups[1].Value),
                "markup-data-context",
                project,
                codeSymbols,
                references,
                typesOnly: true,
                evidence: match,
                evidenceText: text);
        }

        foreach (Match match in XamlEventPattern().Matches(text))
        {
            var attribute = match.Groups[1].Value;
            if (attribute is "Class" or "Name" or "Title" or "Text" or "Content" or "Source"
                || attribute.EndsWith("Property", StringComparison.Ordinal))
            {
                continue;
            }

            AddNamedReference(
                source,
                match.Groups[2].Value,
                "markup-event",
                project,
                codeSymbols,
                references,
                evidence: match,
                evidenceText: text);
        }
    }

    private static void AddNamedReference(
        SymbolRecord source,
        string name,
        string relationship,
        ProjectRecord project,
        IReadOnlyList<SymbolRecord> codeSymbols,
        ISet<SymbolReference> references,
        bool typesOnly = false,
        Match? evidence = null,
        string? evidenceText = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var simpleName = LastIdentifier(name);
        var candidates = codeSymbols
            .Where(symbol => !typesOnly || symbol.Kind is "class" or "record" or "struct" or "interface" or "enum")
            .Where(symbol => symbol.Name.Equals(simpleName, StringComparison.Ordinal)
                             || symbol.SemanticName?.Equals(name, StringComparison.Ordinal) == true
                             || symbol.SemanticName?.EndsWith($".{name}", StringComparison.Ordinal) == true)
            .OrderByDescending(symbol => symbol.Project.Equals(project.Path, StringComparison.OrdinalIgnoreCase))
            .ThenBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .Take(3);
        foreach (var target in candidates)
        {
            if (source.Identity == target.Identity)
            {
                continue;
            }

            (int Line, int Column, int EndLine, int EndColumn)? span =
                evidence is null || evidenceText is null ? null : LocationOf(evidenceText, evidence);
            var confidence = relationship is "component-use" or "markup-code-behind"
                ? EvidenceConfidence.SyntaxFallback
                : EvidenceConfidence.ConventionHeuristic;
            references.Add(new SymbolReference(
                source.Identity,
                target.Identity,
                relationship,
                source.Project,
                target.Project)
            {
                Confidence = confidence,
                Origin = confidence == EvidenceConfidence.SyntaxFallback
                    ? "markup-syntax"
                    : "markup-convention",
                EvidenceFile = source.File,
                EvidenceLine = span?.Line ?? source.Line,
                EvidenceColumn = span?.Column,
                EvidenceEndLine = span?.EndLine ?? source.EndLine,
                EvidenceEndColumn = span?.EndColumn
            });
        }
    }

    private static (int Line, int Column, int EndLine, int EndColumn) LocationOf(string input, Match match)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < match.Index; index++)
        {
            if (input[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        var endLine = line;
        var endColumn = column;
        for (var index = match.Index; index < match.Index + match.Length; index++)
        {
            if (input[index] == '\n')
            {
                endLine++;
                endColumn = 1;
            }
            else
            {
                endColumn++;
            }
        }

        return (line, column, endLine, endColumn);
    }

    private static string LastIdentifier(string value) => value
        .Trim(' ', '{', '}', '"', '\'', '@')
        .Split(new[] { '.', ':', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault() ?? string.Empty;

    private static string? NamespaceOf(string semanticName)
    {
        var index = semanticName.LastIndexOf('.');
        return index > 0 ? semanticName[..index] : null;
    }

    private static string ResolveContainedPath(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Evaluated markup path escapes the repository: {relativePath}");
        }

        return fullPath;
    }

    [GeneratedRegex("@inject\\s+([A-Za-z_][A-Za-z0-9_\\.]*)\\s+[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex RazorInjectPattern();

    [GeneratedRegex("<([A-Za-z_][A-Za-z0-9_:.]*)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex RazorComponentPattern();

    [GeneratedRegex("@on[A-Za-z]+\\s*=\\s*\"(?:@?\\(?)([A-Za-z_][A-Za-z0-9_\\.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex RazorEventPattern();

    [GeneratedRegex("@bind(?:-[A-Za-z]+)?\\s*=\\s*\"@?([A-Za-z_][A-Za-z0-9_\\.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex RazorBindingPattern();

    [GeneratedRegex("x:Class\\s*=\\s*\"([A-Za-z_][A-Za-z0-9_\\.]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex XamlClassPattern();

    [GeneratedRegex("<([A-Za-z_][A-Za-z0-9_:.]*)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex XamlElementPattern();

    [GeneratedRegex("([A-Za-z_][A-Za-z0-9_.]*)(?:Property)?\\s*=\\s*\"\\{Binding(?:\\s+Path=)?\\s*([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex XamlBindingPattern();

    [GeneratedRegex("(?:x:DataType|DataType)\\s*=\\s*\"(?:\\{x:Type\\s+)?(?:[A-Za-z_][A-Za-z0-9_]*:)?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex XamlDataTypePattern();

    [GeneratedRegex("(?:[A-Za-z_][A-Za-z0-9_]*:)?([A-Z][A-Za-z0-9_]*)\\s*=\\s*\"([A-Za-z_][A-Za-z0-9_]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex XamlEventPattern();
}
