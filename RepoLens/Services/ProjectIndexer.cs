using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record ProjectIndexBuildResult(
    RepositoryIndex Repository,
    IReadOnlySet<string> EvaluatedProjects,
    IReadOnlySet<string> ReusedProjects);

internal sealed class ProjectIndexer
{
    private readonly IProcessRunner processRunner;
    private readonly RepositoryFileFilter fileFilter;

    public ProjectIndexer(IProcessRunner processRunner, RepositoryFileFilter? fileFilter = null)
    {
        this.processRunner = processRunner;
        this.fileFilter = fileFilter ?? new RepositoryFileFilter(processRunner);
    }

    private static readonly string[] ProjectItemNames =
    [
        "Compile",
        "Content",
        "None",
        "EmbeddedResource",
        "AdditionalFiles",
        "AnalyzerConfigFiles",
        "EditorConfigFiles",
        "RazorComponent",
        "Page",
        "ApplicationDefinition",
        "Resource",
        "SplashScreen",
        "MauiXaml",
        "MauiCss",
        "MauiImage",
        "MauiFont",
        "MauiAsset",
        "MauiSplashScreen",
        "MauiIcon"
    ];

    public async Task<RepositoryIndex> BuildAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var inventory = await fileFilter.GetFilesAsync(repositoryRoot, config.Indexing, cancellationToken);
        return await BuildAsync(repositoryRoot, config, inventory, cancellationToken);
    }

    internal async Task<RepositoryIndex> BuildAsync(
        string repositoryRoot,
        DevContextConfig config,
        RepositoryFileInventory inventory,
        CancellationToken cancellationToken) =>
        (await BuildAsync(
            repositoryRoot,
            config,
            inventory,
            new Dictionary<string, ProjectRecord>(StringComparer.OrdinalIgnoreCase),
            cancellationToken)).Repository;

    internal async Task<ProjectIndexBuildResult> BuildAsync(
        string repositoryRoot,
        DevContextConfig config,
        RepositoryFileInventory inventory,
        IReadOnlyDictionary<string, ProjectRecord> reusableProjects,
        CancellationToken cancellationToken)
    {
        var availableProjects = inventory.RelativePaths
            .Where(IsSupportedProjectPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedProjects = SelectConfiguredProjects(repositoryRoot, config.Solution, availableProjects);
        var pending = selectedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, ProjectRecord>(StringComparer.OrdinalIgnoreCase);
        var evaluatedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reusedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var wave = pending
                .Where(path => availableProjects.Contains(path) && !processed.Contains(path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            pending.Clear();
            if (wave.Length == 0)
            {
                break;
            }

            var projectsToEvaluate = wave
                .Where(path => !reusableProjects.ContainsKey(path))
                .ToArray();
            var evaluated = await ParallelWork.SelectAsync(
                projectsToEvaluate,
                config.Indexing.MaxParallelism,
                async (relativeProject, token) => await ReadProjectAsync(
                    repositoryRoot,
                    RepositoryFileFilter.ToFullPath(repositoryRoot, relativeProject),
                    token),
                cancellationToken);
            var evaluatedByPath = projectsToEvaluate
                .Select((path, index) => (path, Project: evaluated[index]))
                .ToDictionary(item => item.path, item => item.Project, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < wave.Length; index++)
            {
                var relativeProject = wave[index];
                var project = reusableProjects.TryGetValue(relativeProject, out var reusable)
                    ? reusable
                    : evaluatedByPath[relativeProject];
                if (reusable is null)
                {
                    evaluatedProjects.Add(relativeProject);
                }
                else
                {
                    reusedProjectPaths.Add(relativeProject);
                }

                processed.Add(relativeProject);
                records[relativeProject] = project;
                foreach (var reference in project.ProjectReferences
                             .Where(availableProjects.Contains)
                             .Where(reference => !processed.Contains(reference)))
                {
                    pending.Add(reference);
                }
            }
        }

        return new ProjectIndexBuildResult(
            new RepositoryIndex
            {
                Solution = config.Solution,
                Projects = records.Values.OrderBy(project => project.Path, StringComparer.Ordinal).ToArray()
            },
            evaluatedProjects,
            reusedProjectPaths);
    }

    private static IReadOnlyList<string> SelectConfiguredProjects(
        string repositoryRoot,
        string? configuredSolution,
        IReadOnlySet<string> availableProjects)
    {
        if (string.IsNullOrWhiteSpace(configuredSolution))
        {
            return availableProjects.Order(StringComparer.Ordinal).ToArray();
        }

        var solutionPath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            configuredSolution.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(solutionPath))
        {
            throw new InvalidOperationException($"Configured solution does not exist: {configuredSolution}");
        }

        var projects = Path.GetExtension(solutionPath).ToLowerInvariant() switch
        {
            ".sln" => ReadSlnProjects(repositoryRoot, solutionPath),
            ".slnx" => ReadSlnxProjects(repositoryRoot, solutionPath),
            ".slnf" => ReadSlnfProjects(repositoryRoot, solutionPath),
            _ => throw new InvalidOperationException(
                $"Configured solution must be a .sln, .slnx, or .slnf file: {configuredSolution}")
        };
        return projects
            .Where(availableProjects.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadSlnProjects(string repositoryRoot, string solutionPath) =>
        File.ReadLines(solutionPath)
            .Select(line => Regex.Match(
                line,
                "^Project\\(\"[^\"]+\"\\) = \"[^\"]*\", \"([^\"]+)\",",
                RegexOptions.CultureInvariant))
            .Where(match => match.Success)
            .Select(match => ResolveSolutionProject(repositoryRoot, solutionPath, match.Groups[1].Value))
            .Where(IsSupportedProjectPath)
            .ToArray();

    private static IReadOnlyList<string> ReadSlnxProjects(string repositoryRoot, string solutionPath) =>
        XDocument.Load(solutionPath, LoadOptions.None)
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolveSolutionProject(repositoryRoot, solutionPath, path!))
            .Where(IsSupportedProjectPath)
            .ToArray();

    private static IReadOnlyList<string> ReadSlnfProjects(string repositoryRoot, string solutionPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(solutionPath));
        if (!document.RootElement.TryGetProperty("solution", out var solution)
            || !solution.TryGetProperty("path", out var referencedSolutionElement)
            || referencedSolutionElement.ValueKind != JsonValueKind.String
            || !solution.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Solution filter must contain solution.path and a solution.projects array: {solutionPath}");
        }

        var referencedSolution = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(solutionPath)!,
            referencedSolutionElement.GetString()!.Replace('\\', Path.DirectorySeparatorChar)));
        if (!File.Exists(referencedSolution))
        {
            throw new InvalidOperationException(
                $"Solution filter references a missing solution: {referencedSolutionElement.GetString()}");
        }

        return projects.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolveSolutionProject(repositoryRoot, referencedSolution, path!))
            .Where(IsSupportedProjectPath)
            .ToArray();
    }

    private static string ResolveSolutionProject(
        string repositoryRoot,
        string solutionPath,
        string projectPath) => NormalizeRelative(
        repositoryRoot,
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(solutionPath)!,
            projectPath.Replace('\\', Path.DirectorySeparatorChar))));

    private static bool IsSupportedProjectPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    private async Task<ProjectRecord> ReadProjectAsync(
        string repositoryRoot,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "dotnet",
            [
                "msbuild",
                projectPath,
                "-nologo",
                "-getProperty:TargetFramework,TargetFrameworks,Nullable,LangVersion,IsTestProject,AssemblyName,OutputType,TreatWarningsAsErrors,WarningsAsErrors,NoWarn,AnalysisLevel,DefineConstants,AllowUnsafe,Optimize,MSBuildAllProjects",
                $"-getItem:ProjectReference,PackageReference,Using,{string.Join(',', ProjectItemNames)}"
            ],
            repositoryRoot,
            cancellationToken);

        if (result.State == ExecutionState.Succeeded && TryParseEvaluation(
                result.StandardOutput,
                repositoryRoot,
                projectPath,
                out var evaluated))
        {
            return await ResolveReferencesAsync(
                repositoryRoot,
                projectPath,
                evaluated,
                cancellationToken);
        }

        return ReadProjectFileFallback(repositoryRoot, projectPath);
    }

    private async Task<ProjectRecord> ResolveReferencesAsync(
        string repositoryRoot,
        string projectPath,
        ProjectRecord project,
        CancellationToken cancellationToken)
    {
        var frameworks = project.TargetFrameworks.Count == 0 ? [string.Empty] : project.TargetFrameworks;
        var analyses = new List<TargetFrameworkAnalysisRecord>(frameworks.Count);
        foreach (var framework in frameworks)
        {
            var arguments = new List<string>
            {
                "msbuild",
                projectPath,
                "-nologo",
                "-target:ResolveReferences"
            };
            if (framework.Length > 0)
            {
                arguments.Add($"-property:TargetFramework={framework}");
            }

            arguments.Add("-getItem:ReferencePath,Analyzer,Using");
            var result = await processRunner.RunAsync(
                "dotnet",
                arguments,
                repositoryRoot,
                cancellationToken);
            if (result.State == ExecutionState.Succeeded
                && TryParseResolvedReferences(
                    result.StandardOutput,
                    out var references,
                    out var analyzers,
                    out var globalUsings))
            {
                analyses.Add(new TargetFrameworkAnalysisRecord
                {
                    TargetFramework = framework,
                    MetadataReferences = references,
                    AnalyzerReferences = analyzers,
                    GlobalUsings = globalUsings,
                    ReferenceResolutionState = ExecutionState.Succeeded
                });
                continue;
            }

            analyses.Add(new TargetFrameworkAnalysisRecord
            {
                TargetFramework = framework,
                ReferenceResolutionState = result.State == ExecutionState.Succeeded
                    ? ExecutionState.Failed
                    : result.State,
                ReferenceResolutionDetail = NormalizeDetail(repositoryRoot, FirstDetail(result))
            });
        }

        var primary = analyses.FirstOrDefault(analysis =>
                          analysis.ReferenceResolutionState == ExecutionState.Succeeded)
                      ?? analyses[0];
        var failed = analyses.FirstOrDefault(analysis =>
            analysis.ReferenceResolutionState != ExecutionState.Succeeded);
        return project with
        {
            MetadataReferences = primary.MetadataReferences,
            AnalyzerReferences = analyses.SelectMany(analysis => analysis.AnalyzerReferences)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            GlobalUsings = project.GlobalUsings.Concat(primary.GlobalUsings)
                .Distinct()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Alias, StringComparer.Ordinal)
                .ToArray(),
            TargetFrameworkAnalyses = analyses,
            ReferenceResolutionState = failed?.ReferenceResolutionState ?? ExecutionState.Succeeded,
            ReferenceResolutionDetail = failed is null
                ? null
                : $"{DisplayFramework(failed.TargetFramework)}: {failed.ReferenceResolutionDetail ?? failed.ReferenceResolutionState.ToString()}"
        };
    }

    private static bool TryParseResolvedReferences(
        string output,
        out IReadOnlyList<ResolvedReferenceRecord> references,
        out IReadOnlyList<string> analyzers,
        out IReadOnlyList<GlobalUsingRecord> globalUsings)
    {
        references = [];
        analyzers = [];
        globalUsings = [];
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            var items = document.RootElement.GetProperty("Items");
            references = ReadItems(items, "ReferencePath")
                .Select(item => new ResolvedReferenceRecord(
                    GetItemValue(item, "FullPath") ?? GetItemValue(item, "Identity") ?? string.Empty,
                    GetItemValue(item, "ReferenceSourceTarget"),
                    GetItemValue(item, "NuGetPackageId"),
                    GetItemValue(item, "NuGetPackageVersion"),
                    GetItemValue(item, "FrameworkReferenceName")))
                .Where(reference => reference.Path.Length > 0 && File.Exists(reference.Path))
                .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
                // File name before full path. Ordering by the absolute path alone is
                // deterministic on one machine but not across machines: the SDK lives under
                // /usr/share on Linux, /usr/local on macOS and Program Files on Windows, so
                // the same reference set comes out in a different order on each. The file
                // name identifies the reference wherever it was resolved from.
                .OrderBy(
                    reference => Path.GetFileName(reference.Path),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            analyzers = ReadItems(items, "Analyzer")
                .Select(item => GetItemValue(item, "FullPath") ?? GetItemValue(item, "Identity"))
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            globalUsings = ReadItems(items, "Using")
                .Select(item => new GlobalUsingRecord(
                    GetItemValue(item, "Identity") ?? string.Empty,
                    IsTrue(GetItemValue(item, "Static")),
                    NullIfEmpty(GetItemValue(item, "Alias"))))
                .Where(item => item.Name.Length > 0)
                .Distinct()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Alias, StringComparer.Ordinal)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static string DisplayFramework(string framework) =>
        framework.Length == 0 ? "default target" : framework;

    private static bool TryParseEvaluation(
        string output,
        string repositoryRoot,
        string projectPath,
        out ProjectRecord record)
    {
        record = null!;
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            var root = document.RootElement;
            var properties = root.GetProperty("Properties");
            var items = root.GetProperty("Items");

            var targetFramework = GetProperty(properties, "TargetFramework");
            var targetFrameworks = GetProperty(properties, "TargetFrameworks");
            var frameworks = string.IsNullOrWhiteSpace(targetFrameworks)
                ? SplitFrameworks(targetFramework)
                : SplitFrameworks(targetFrameworks);

            var packages = ReadItems(items, "PackageReference")
                .Select(item => new PackageReferenceRecord(
                    GetItemValue(item, "Identity") ?? string.Empty,
                    GetItemValue(item, "Version")))
                .Where(package => package.Name.Length > 0)
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var globalUsings = ReadItems(items, "Using")
                .Select(item => new GlobalUsingRecord(
                    GetItemValue(item, "Identity") ?? string.Empty,
                    IsTrue(GetItemValue(item, "Static")),
                    NullIfEmpty(GetItemValue(item, "Alias"))))
                .Where(item => item.Name.Length > 0)
                .Distinct()
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Alias, StringComparer.Ordinal)
                .ToArray();

            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var references = ReadItems(items, "ProjectReference")
                .Select(item => GetItemValue(item, "FullPath") ?? GetItemValue(item, "Identity"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.IsPathRooted(path!) ? path! : Path.GetFullPath(Path.Combine(projectDirectory, path!)))
                .Select(path => NormalizeRelative(repositoryRoot, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var projectItems = ProjectItemNames
                .SelectMany(name => ReadItems(items, name)
                    .Select(item => new { ItemType = name, Path = ResolveItemPath(item, projectDirectory) }))
                .Where(item => IsRepositoryFile(repositoryRoot, item.Path))
                .Select(item => new ProjectItemRecord(
                    item.ItemType,
                    NormalizeRelative(repositoryRoot, item.Path!)))
                .Concat(ReadMsBuildImports(properties, projectDirectory)
                    .Where(path => IsRepositoryFile(repositoryRoot, path))
                    .Select(path => new ProjectItemRecord(
                        "MSBuildImport",
                        NormalizeRelative(repositoryRoot, path))))
                .Append(new ProjectItemRecord(
                    "Project",
                    NormalizeRelative(repositoryRoot, projectPath)))
                .DistinctBy(
                    item => $"{item.ItemType}\0{item.Path}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.ItemType, StringComparer.Ordinal)
                .ToArray();
            var sourceFiles = projectItems
                .Where(item => item.ItemType == "Compile")
                .Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var projectFiles = projectItems
                .Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var name = Path.GetFileNameWithoutExtension(projectPath);
            var isTest = string.Equals(GetProperty(properties, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                         || packages.Any(package => package.Name == "Microsoft.NET.Test.Sdk")
                         || LooksLikeTestProject(name);

            record = new ProjectRecord(
                name,
                NormalizeRelative(repositoryRoot, projectPath),
                isTest,
                frameworks,
                NullIfEmpty(GetProperty(properties, "Nullable")),
                NullIfEmpty(GetProperty(properties, "LangVersion")),
                new CompilerSettingsRecord(
                    NullIfEmpty(GetProperty(properties, "OutputType")),
                    IsTrue(GetProperty(properties, "TreatWarningsAsErrors")),
                    NullIfEmpty(GetProperty(properties, "WarningsAsErrors")),
                    NullIfEmpty(GetProperty(properties, "NoWarn")),
                    NullIfEmpty(GetProperty(properties, "AnalysisLevel")),
                    NullIfEmpty(GetProperty(properties, "DefineConstants")),
                    IsTrue(GetProperty(properties, "AllowUnsafe")),
                    IsTrue(GetProperty(properties, "Optimize"))),
                packages,
                references,
                sourceFiles)
            {
                AssemblyName = NullIfEmpty(GetProperty(properties, "AssemblyName")) ?? name,
                ProjectFiles = projectFiles,
                Items = projectItems,
                GlobalUsings = globalUsings
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static ProjectRecord ReadProjectFileFallback(string repositoryRoot, string projectPath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.None);
        var values = document.Descendants()
            .Where(element => element.Name.LocalName is
                "TargetFramework" or "TargetFrameworks" or "Nullable" or "LangVersion" or "IsTestProject" or "AssemblyName"
                or "OutputType" or "TreatWarningsAsErrors" or "WarningsAsErrors" or "NoWarn"
                or "AnalysisLevel" or "DefineConstants" or "AllowUnsafe" or "Optimize")
            .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var packages = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => new PackageReferenceRecord(
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? string.Empty,
                element.Attribute("Version")?.Value ??
                element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value))
            .Where(package => package.Name.Length > 0)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var globalUsings = document.Descendants()
            .Where(element => element.Name.LocalName == "Using")
            .Select(element => new GlobalUsingRecord(
                element.Attribute("Include")?.Value ?? string.Empty,
                IsTrue(element.Attribute("Static")?.Value),
                NullIfEmpty(element.Attribute("Alias")?.Value)))
            .Where(item => item.Name.Length > 0)
            .Distinct()
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Alias, StringComparer.Ordinal)
            .ToArray();
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var references = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeRelative(repositoryRoot, Path.GetFullPath(Path.Combine(projectDirectory, path!))))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourceExtension = Path.GetExtension(projectPath).ToLowerInvariant() switch
        {
            ".fsproj" => "*.fs",
            ".vbproj" => "*.vb",
            _ => "*.cs"
        };
        var sourceItems = Directory.EnumerateFiles(projectDirectory, sourceExtension, SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Where(path => IsRepositoryFile(repositoryRoot, path))
            .Select(path => new ProjectItemRecord("Compile", NormalizeRelative(repositoryRoot, path)))
            .ToArray();
        var projectItems = sourceItems
            .Concat(ReadExplicitProjectItems(document, projectDirectory)
                .Where(item => IsRepositoryFile(repositoryRoot, item.Path))
                .Select(item => new ProjectItemRecord(
                    item.ItemType,
                    NormalizeRelative(repositoryRoot, item.Path))))
            .Concat(ReadExplicitImports(document, projectDirectory)
                .Where(path => IsRepositoryFile(repositoryRoot, path))
                .Select(path => new ProjectItemRecord(
                    "MSBuildImport",
                    NormalizeRelative(repositoryRoot, path))))
            .Append(new ProjectItemRecord(
                "Project",
                NormalizeRelative(repositoryRoot, projectPath)))
            .DistinctBy(
                item => $"{item.ItemType}\0{item.Path}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ItemType, StringComparer.Ordinal)
            .ToArray();
        var sources = projectItems
            .Where(item => item.ItemType == "Compile")
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var projectFiles = projectItems
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var name = Path.GetFileNameWithoutExtension(projectPath);

        values.TryGetValue("TargetFrameworks", out var targetFrameworks);
        values.TryGetValue("TargetFramework", out var targetFramework);
        values.TryGetValue("Nullable", out var nullable);
        values.TryGetValue("LangVersion", out var languageVersion);
        values.TryGetValue("IsTestProject", out var isTestProject);
        values.TryGetValue("AssemblyName", out var assemblyName);
        values.TryGetValue("OutputType", out var outputType);
        values.TryGetValue("TreatWarningsAsErrors", out var treatWarningsAsErrors);
        values.TryGetValue("WarningsAsErrors", out var warningsAsErrors);
        values.TryGetValue("NoWarn", out var noWarn);
        values.TryGetValue("AnalysisLevel", out var analysisLevel);
        values.TryGetValue("DefineConstants", out var defineConstants);
        values.TryGetValue("AllowUnsafe", out var allowUnsafe);
        values.TryGetValue("Optimize", out var optimize);

        return new ProjectRecord(
            name,
            NormalizeRelative(repositoryRoot, projectPath),
            string.Equals(isTestProject, "true", StringComparison.OrdinalIgnoreCase)
            || packages.Any(package => package.Name == "Microsoft.NET.Test.Sdk")
            || LooksLikeTestProject(name),
            SplitFrameworks(string.IsNullOrWhiteSpace(targetFrameworks) ? targetFramework : targetFrameworks),
            NullIfEmpty(nullable),
            NullIfEmpty(languageVersion),
            new CompilerSettingsRecord(
                NullIfEmpty(outputType),
                IsTrue(treatWarningsAsErrors),
                NullIfEmpty(warningsAsErrors),
                NullIfEmpty(noWarn),
                NullIfEmpty(analysisLevel),
                NullIfEmpty(defineConstants),
                IsTrue(allowUnsafe),
                IsTrue(optimize)),
            packages,
            references,
            sources)
        {
            AssemblyName = NullIfEmpty(assemblyName) ?? name,
            ProjectFiles = projectFiles,
            Items = projectItems,
            GlobalUsings = globalUsings
        };
    }

    private static IEnumerable<(string ItemType, string Path)> ReadExplicitProjectItems(
        XDocument document,
        string projectDirectory) =>
        document.Descendants()
            .Where(element => ProjectItemNames.Contains(element.Name.LocalName, StringComparer.Ordinal))
            .SelectMany(element =>
                (element.Attribute("Include")?.Value ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsLiteralMsBuildPath)
                    .Select(path => (element.Name.LocalName, ResolvePath(projectDirectory, path))));

    private static IEnumerable<string> ReadExplicitImports(
        XDocument document,
        string projectDirectory) =>
        document.Descendants()
            .Where(element => element.Name.LocalName == "Import")
            .Select(element => element.Attribute("Project")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value) && IsLiteralMsBuildPath(value!))
            .Select(path => ResolvePath(projectDirectory, path!));

    private static IEnumerable<string> ReadMsBuildImports(
        JsonElement properties,
        string projectDirectory)
    {
        var value = GetProperty(properties, "MSBuildAllProjects");
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsLiteralMsBuildPath)
                .Select(path => ResolvePath(projectDirectory, path));
    }

    private static string? ResolveItemPath(JsonElement item, string projectDirectory)
    {
        var path = GetItemValue(item, "FullPath") ?? GetItemValue(item, "Identity");
        return string.IsNullOrWhiteSpace(path) || !IsLiteralMsBuildPath(path)
            ? null
            : ResolvePath(projectDirectory, path);
    }

    private static string ResolvePath(string projectDirectory, string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(projectDirectory, path));

    private static bool IsLiteralMsBuildPath(string path) =>
        !path.Contains("$(", StringComparison.Ordinal)
        && path.IndexOfAny(['*', '?', '%']) < 0;

    private static bool IsRepositoryFile(string repositoryRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || IsGeneratedPath(path))
        {
            return false;
        }

        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> ReadItems(JsonElement items, string name)
    {
        if (!items.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray().ToArray();
    }

    private static string? GetItemValue(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string? GetProperty(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static IReadOnlyList<string> SplitFrameworks(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.Ordinal)
                .ToArray();

    private static bool LooksLikeTestProject(string name) =>
        name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or ContextPaths.DirectoryName);

    private static string NormalizeRelative(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path)).Replace('\\', '/');

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FirstDetail(ProcessResult result) =>
        new[] { result.StandardError, result.StandardOutput }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .FirstOrDefault();

    private static string? NormalizeDetail(string repositoryRoot, string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(repositoryRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return detail
            .Replace(normalizedRoot, ".", StringComparison.OrdinalIgnoreCase)
            .Replace(normalizedRoot.Replace('\\', '/'), ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
