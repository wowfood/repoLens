using System.Reflection;
using System.Runtime.Loader;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DevContext.Services;

internal static class SymbolIndexer
{
    private const string SyntheticGlobalUsingsSuffix = ".RepoLens.GlobalUsings.g.cs";

    private sealed record CompilationSet(
        IReadOnlyDictionary<string, CSharpCompilation> Compilations,
        IReadOnlyList<CompilationCompletenessRecord> Completeness,
        IReadOnlyList<GeneratedSourceRecord> GeneratedSources);

    private sealed record DeclarationIndex(
        IReadOnlyList<SymbolRecord> Symbols,
        IReadOnlyDictionary<ISymbol, SymbolRecord> DeclaredSymbols);

    private sealed record GeneratorExecution(
        CSharpCompilation Compilation,
        int Discovered,
        bool Executed,
        IReadOnlyList<GeneratedSourceRecord> Sources,
        IReadOnlyList<string> Gaps);

    private static readonly SymbolDisplayFormat ShortNameFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat;

    /// <summary>
    /// Resolves a referenced <see cref="ISymbol"/> back to the declaration RepoLens indexed for it,
    /// across compilation boundaries.
    ///
    /// Each project is compiled separately and sees its project references through
    /// <see cref="Compilation.ToMetadataReference()"/>. A symbol reached that way is a different
    /// <see cref="ISymbol"/> instance from the one the declaring project's own compilation produced,
    /// and <see cref="SymbolEqualityComparer.Default"/> does not equate the two. Looking targets up
    /// by symbol identity alone therefore silently dropped every reference that crossed a project
    /// boundary — which, for a library, is every caller it has.
    ///
    /// The fallback key is the containing assembly name plus the normalized symbol's display string.
    /// Both halves are evaluated identically on either side of the boundary, so the declaring and the
    /// referencing compilation agree on it.
    /// </summary>
    internal sealed class DeclaredSymbolLookup
    {
        private readonly IReadOnlyDictionary<ISymbol, SymbolRecord> bySymbol;
        private readonly IReadOnlyDictionary<string, SymbolRecord> byAssemblyQualifiedName;
        private readonly IReadOnlySet<string> indexedAssemblies;

        private DeclaredSymbolLookup(
            IReadOnlyDictionary<ISymbol, SymbolRecord> bySymbol,
            IReadOnlyDictionary<string, SymbolRecord> byAssemblyQualifiedName,
            IReadOnlySet<string> indexedAssemblies)
        {
            this.bySymbol = bySymbol;
            this.byAssemblyQualifiedName = byAssemblyQualifiedName;
            this.indexedAssemblies = indexedAssemblies;
        }

        public static DeclaredSymbolLookup Empty { get; } = new(
            new Dictionary<ISymbol, SymbolRecord>(SymbolEqualityComparer.Default),
            new Dictionary<string, SymbolRecord>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        /// <param name="declarations">
        /// Keyed by the already-normalized declared symbol, in a deterministic order: where two
        /// projects declare the same assembly-qualified name — shared-source projects do this — the
        /// first wins, and the ordering of the input decides which that is.
        /// </param>
        public static DeclaredSymbolLookup Create(IEnumerable<IReadOnlyDictionary<ISymbol, SymbolRecord>> declarations)
        {
            var bySymbol = new Dictionary<ISymbol, SymbolRecord>(SymbolEqualityComparer.Default);
            var byName = new Dictionary<string, SymbolRecord>(StringComparer.Ordinal);
            var assemblies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in declarations)
            {
                foreach (var (symbol, record) in declaration)
                {
                    bySymbol.TryAdd(symbol, record);
                    var assembly = symbol.ContainingAssembly?.Name;
                    if (string.IsNullOrEmpty(assembly))
                    {
                        continue;
                    }

                    assemblies.Add(assembly);
                    byName.TryAdd(AssemblyQualifiedName(assembly, symbol), record);
                }
            }

            return new DeclaredSymbolLookup(bySymbol, byName, assemblies);
        }

        /// <summary>
        /// Every declaration RepoLens indexed, paired with the symbol that declared it. Used to walk
        /// declarations looking for the relationships that are only visible from the declaring side,
        /// such as overrides and explicit interface implementations.
        /// </summary>
        public IEnumerable<KeyValuePair<ISymbol, SymbolRecord>> Declarations => bySymbol;

        public bool TryResolve(ISymbol symbol, out SymbolRecord record)
        {
            var normalized = NormalizeSymbol(symbol);
            if (bySymbol.TryGetValue(normalized, out var direct))
            {
                record = direct;
                return true;
            }

            // Gated on the assembly set before the display string is rendered: the overwhelming
            // majority of unresolved targets are BCL and package symbols, and rendering a display
            // string for each of those would cost more than the edges are worth.
            var assembly = normalized.ContainingAssembly?.Name;
            if (assembly is null || !indexedAssemblies.Contains(assembly))
            {
                record = null!;
                return false;
            }

            return byAssemblyQualifiedName.TryGetValue(
                AssemblyQualifiedName(assembly, normalized),
                out record!);
        }

        private static string AssemblyQualifiedName(string assembly, ISymbol symbol) =>
            string.Concat(assembly, "|", symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    }

    public static async Task<(SymbolIndex Symbols, DependencyIndex Dependencies)> BuildAsync(
        string repositoryRoot,
        RepositoryIndex projects,
        CancellationToken cancellationToken) =>
        await BuildAsync(repositoryRoot, projects, new IndexingConfig(), cancellationToken);

    public static async Task<(SymbolIndex Symbols, DependencyIndex Dependencies)> BuildAsync(
        string repositoryRoot,
        RepositoryIndex projects,
        IndexingConfig indexing,
        CancellationToken cancellationToken) =>
        await BuildAsync(repositoryRoot, projects, indexing, null, cancellationToken);

    internal static async Task<(SymbolIndex Symbols, DependencyIndex Dependencies)> BuildAsync(
        string repositoryRoot,
        RepositoryIndex projects,
        IndexingConfig indexing,
        IReadOnlySet<string>? projectsToIndex,
        CancellationToken cancellationToken)
    {
        var projectMap = projects.Projects.ToDictionary(project => project.Path, StringComparer.OrdinalIgnoreCase);
        var outputProjects = (projectsToIndex ?? projectMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Where(projectMap.ContainsKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var analysisProjects = new RepositoryIndex
        {
            Solution = projects.Solution,
            Projects = ExpandReferencedProjects(outputProjects, projectMap)
                .Select(path => projectMap[path])
                .OrderBy(project => project.Path, StringComparer.Ordinal)
                .ToArray()
        };
        var outputRepository = new RepositoryIndex
        {
            Solution = projects.Solution,
            Projects = projects.Projects
                .Where(project => outputProjects.Contains(project.Path))
                .OrderBy(project => project.Path, StringComparer.Ordinal)
                .ToArray()
        };
        var analysisProjectMap = analysisProjects.Projects.ToDictionary(
            project => project.Path,
            StringComparer.OrdinalIgnoreCase);
        var syntaxTrees = await LoadSyntaxTreesAsync(
            repositoryRoot,
            analysisProjects,
            indexing,
            cancellationToken);
        var compilationSet = BuildCompilations(
            repositoryRoot,
            analysisProjects,
            analysisProjectMap,
            syntaxTrees,
            indexing);
        var compilations = compilationSet.Compilations;
        var declarationIndexes = await ParallelWork.SelectAsync(
            analysisProjects.Projects,
            indexing.MaxParallelism,
            async (project, token) => await IndexDeclarationsAsync(
                repositoryRoot,
                project,
                compilations,
                token),
            cancellationToken);
        var symbols = declarationIndexes.SelectMany(index => index.Symbols).ToList();

        // analysisProjects is ordered by path, and ParallelWork.SelectAsync preserves input order,
        // so the "first declaration wins" tie-break inside the lookup is stable across runs.
        var declaredSymbols = DeclaredSymbolLookup.Create(
            declarationIndexes.Select(index => index.DeclaredSymbols));

        IReadOnlyList<SymbolReference> references = await IndexReferencesAsync(
            repositoryRoot,
            analysisProjects,
            compilations,
            declaredSymbols,
            indexing.MaxParallelism,
            cancellationToken);
        references = references
            .Where(reference => outputProjects.Contains(reference.SourceProject))
            .ToArray();
        var markup = await MarkupIndexer.BuildAsync(
            repositoryRoot,
            outputRepository,
            symbols,
            cancellationToken);
        symbols = symbols.Where(symbol => outputProjects.Contains(symbol.Project))
            .Concat(markup.Symbols)
            .ToList();
        references = references.Concat(markup.References)
            .Distinct()
            .OrderBy(reference => reference.SourceProject, StringComparer.Ordinal)
            .ThenBy(reference => reference.SourceSymbol, StringComparer.Ordinal)
            .ThenBy(reference => reference.TargetSymbol, StringComparer.Ordinal)
            .ThenBy(reference => reference.Relationship, StringComparer.Ordinal)
            .ToArray();
        // Partial types and partial members declared across several files share one identity, so only
        // one record can survive. Deduplicating before ordering left that choice to whichever project
        // task happened to append first, and the survivor was as likely to be the bodyless half --
        // "where is Compute implemented?" answered with the signature. Order first, and put the
        // declaration site that carries a body ahead of the one that does not.
        //
        // Hand-written source outranks a body, though, and that ordering is not cosmetic: a
        // [GeneratedRegex] method is a partial whose implementation is a generated DFA, so preferring
        // the body alone moved every regex in this repository from the file that declares the pattern
        // to a machine-written file nobody can edit. The benchmark caught it as a total recall
        // collapse on two cases. The useful answer is the code a person wrote.
        var orderedSymbols = symbols
            .OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => IsGeneratedPath(symbol.File))
            .ThenByDescending(symbol => symbol.IsPartialImplementation)
            .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ThenBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .DistinctBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .OrderBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .ThenBy(symbol => symbol.Identity, StringComparer.Ordinal)
            .ToArray();
        var typeDefinitions = await TypeDefinitionIndexer.BuildAsync(
            repositoryRoot,
            outputRepository,
            compilations,
            orderedSymbols,
            indexing.MaxParallelism,
            cancellationToken);
        var projectDependencies = outputRepository.Projects
            .SelectMany(project => project.ProjectReferences.Select(reference =>
                new ProjectDependency(project.Path, reference)))
            .OrderBy(dependency => dependency.Project, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.ReferencedProject, StringComparer.Ordinal)
            .ToArray();
        var typeDependencies = orderedSymbols
            .Where(symbol => symbol.Kind is "class" or "record" or "struct" or "interface")
            .SelectMany(symbol =>
                (symbol.BaseType is null
                    ? Enumerable.Empty<TypeDependency>()
                    : [new TypeDependency(symbol.Identity, symbol.BaseType, "base-type")])
                .Concat(symbol.Interfaces.Select(name =>
                    new TypeDependency(symbol.Identity, name, "interface"))))
            .OrderBy(dependency => dependency.Symbol, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.RelatedType, StringComparer.Ordinal)
            .ToArray();

        return (
            new SymbolIndex
            {
                Symbols = orderedSymbols,
                TypeDefinitions = typeDefinitions,
                CompilationCompleteness = compilationSet.Completeness
                    .Where(record => outputProjects.Contains(record.Project))
                    .ToArray(),
                GeneratedSources = compilationSet.GeneratedSources
                    .Where(source => outputProjects.Contains(source.Project))
                    .ToArray()
            },
            new DependencyIndex
            {
                Projects = projectDependencies,
                Types = typeDependencies,
                Symbols = references
            });
    }

    private static IReadOnlySet<string> ExpandReferencedProjects(
        IReadOnlySet<string> outputProjects,
        IReadOnlyDictionary<string, ProjectRecord> projectMap)
    {
        var result = outputProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(outputProjects.Order(StringComparer.Ordinal));
        while (pending.TryDequeue(out var projectPath))
        {
            if (!projectMap.TryGetValue(projectPath, out var project))
            {
                continue;
            }

            foreach (var reference in project.ProjectReferences.Order(StringComparer.Ordinal))
            {
                if (projectMap.ContainsKey(reference) && result.Add(reference))
                {
                    pending.Enqueue(reference);
                }
            }
        }

        return result;
    }

    private static async Task<DeclarationIndex> IndexDeclarationsAsync(
        string repositoryRoot,
        ProjectRecord project,
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        CancellationToken cancellationToken)
    {
        var symbols = new List<SymbolRecord>();
        var declaredSymbols = new Dictionary<ISymbol, SymbolRecord>(SymbolEqualityComparer.Default);
        if (!compilations.TryGetValue(project.Path, out var compilation))
        {
            return new DeclarationIndex(symbols, declaredSymbols);
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await tree.GetRootAsync(cancellationToken);
            var model = compilation.GetSemanticModel(tree, true);
            var file = NormalizeRelative(repositoryRoot, tree.FilePath);
            IndexTypes(root, model, project, file, symbols, declaredSymbols);
            IndexMembers(root, model, project, file, symbols, declaredSymbols);
        }

        return new DeclarationIndex(symbols, declaredSymbols);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>>> LoadSyntaxTreesAsync(
        string repositoryRoot,
        RepositoryIndex projects,
        IndexingConfig indexing,
        CancellationToken cancellationToken)
    {
        var projectTrees = await ParallelWork.SelectAsync(
            projects.Projects,
            indexing.MaxParallelism,
            async (project, token) => await LoadProjectSyntaxTreesAsync(
                repositoryRoot,
                project,
                indexing,
                token),
            cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<SyntaxTree>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < projects.Projects.Count; index++)
        {
            result[projects.Projects[index].Path] = projectTrees[index];
        }

        return result;
    }

    private static async Task<IReadOnlyList<SyntaxTree>> LoadProjectSyntaxTreesAsync(
        string repositoryRoot,
        ProjectRecord project,
        IndexingConfig indexing,
        CancellationToken cancellationToken)
    {
        var trees = new List<SyntaxTree>();
        if (!IsCSharpProject(project))
        {
            return trees;
        }

        var parseOptions = CreateParseOptions(project);
        foreach (var relativeFile in project.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                relativeFile.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > indexing.MaxSourceFileBytes)
            {
                continue;
            }

            var source = await File.ReadAllTextAsync(fullPath, cancellationToken);
            trees.Add(CSharpSyntaxTree.ParseText(
                source,
                parseOptions,
                fullPath,
                cancellationToken: cancellationToken));
        }

        if (project.GlobalUsings.Count > 0)
        {
            var globalUsingSource = string.Join(
                Environment.NewLine,
                project.GlobalUsings.Select(RenderGlobalUsing));
            var syntheticPath = Path.Combine(
                repositoryRoot,
                ContextPaths.DirectoryName,
                "synthetic",
                $"{project.AssemblyName ?? project.Name}{SyntheticGlobalUsingsSuffix}");
            trees.Add(CSharpSyntaxTree.ParseText(
                globalUsingSource,
                parseOptions,
                syntheticPath,
                cancellationToken: cancellationToken));
        }

        return trees;
    }

    private static CompilationSet BuildCompilations(
        string repositoryRoot,
        RepositoryIndex projects,
        IReadOnlyDictionary<string, ProjectRecord> projectMap,
        IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> syntaxTrees,
        IndexingConfig indexing)
    {
        var allCompilations = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);
        var primaryCompilations = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);
        var building = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var platformReferences = GetPlatformReferences();
        var referenceCounts = new Dictionary<string, (int Loaded, int Failed)>(StringComparer.OrdinalIgnoreCase);
        var generatorExecutions = new Dictionary<string, GeneratorExecution>(StringComparer.OrdinalIgnoreCase);

        CSharpCompilation Build(ProjectRecord project, string framework)
        {
            var key = CompilationKey(project.Path, framework);
            if (allCompilations.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (!building.Add(key))
            {
                return CreateCompilation(project, syntaxTrees[project.Path], platformReferences);
            }

            var references = new List<MetadataReference>();
            var failedReferences = 0;
            var loadedMetadataReferences = 0;
            var analysis = TargetAnalysis(project, framework);
            var evaluatedReferences = analysis.MetadataReferences
                .Where(reference => !string.Equals(
                    reference.Source,
                    "ProjectReference",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (evaluatedReferences.Length == 0)
            {
                references.AddRange(platformReferences);
                loadedMetadataReferences = platformReferences.Count;
            }
            else
            {
                foreach (var reference in evaluatedReferences)
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(reference.Path));
                        loadedMetadataReferences++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
                    {
                        failedReferences++;
                    }
                }
            }

            foreach (var referencePath in project.ProjectReferences)
            {
                if (projectMap.TryGetValue(referencePath, out var referencedProject)
                    && IsCSharpProject(referencedProject))
                {
                    var referencedFramework = referencedProject.TargetFrameworks.Contains(
                        framework,
                        StringComparer.OrdinalIgnoreCase)
                        ? framework
                        : referencedProject.TargetFrameworks.FirstOrDefault() ?? string.Empty;
                    references.Add(Build(referencedProject, referencedFramework).ToMetadataReference());
                }
            }

            var compilation = CreateCompilation(project, syntaxTrees[project.Path], references);
            var generatorExecution = ExecuteGenerators(compilation, project, framework, analysis, indexing);
            compilation = generatorExecution.Compilation;
            referenceCounts[key] = (loadedMetadataReferences, failedReferences);
            generatorExecutions[key] = generatorExecution;
            building.Remove(key);
            allCompilations[key] = compilation;
            return compilation;
        }

        foreach (var project in projects.Projects)
        {
            if (!IsCSharpProject(project))
            {
                continue;
            }

            var frameworks = FrameworksOf(project);
            foreach (var framework in frameworks)
            {
                var compilation = Build(project, framework);
                primaryCompilations.TryAdd(project.Path, compilation);
            }
        }

        var completeness = projects.Projects
            .SelectMany(project => IsCSharpProject(project)
                ? FrameworksOf(project).Select(framework =>
            {
                var key = CompilationKey(project.Path, framework);
                return BuildCompleteness(
                    repositoryRoot,
                    project,
                    framework,
                    FrameworksOf(project)[0],
                    TargetAnalysis(project, framework),
                    allCompilations[key],
                    syntaxTrees[project.Path].Count(tree =>
                        !tree.FilePath.EndsWith(SyntheticGlobalUsingsSuffix, StringComparison.Ordinal)),
                    referenceCounts.GetValueOrDefault(key),
                    generatorExecutions[key]);
            })
                : BuildNonCSharpCompleteness(project))
            .OrderBy(record => record.Project, StringComparer.Ordinal)
            .ThenBy(record => record.TargetFramework, StringComparer.Ordinal)
            .ToArray();
        return new CompilationSet(
            primaryCompilations,
            completeness,
            generatorExecutions.Values.SelectMany(execution => execution.Sources)
                .DistinctBy(source => source.Id, StringComparer.Ordinal)
                .OrderBy(source => source.Project, StringComparer.Ordinal)
                .ThenBy(source => source.TargetFramework, StringComparer.Ordinal)
                .ThenBy(source => source.File, StringComparer.Ordinal)
                .ToArray());
    }

    private static IReadOnlyList<CompilationCompletenessRecord> BuildNonCSharpCompleteness(
        ProjectRecord project)
    {
        var language = Path.GetExtension(project.Path).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            ? "F#"
            : "Visual Basic";
        return FrameworksOf(project).Select(framework => new CompilationCompletenessRecord
        {
            Project = project.Path,
            TargetFrameworks = project.TargetFrameworks,
            TargetFramework = framework.Length == 0 ? null : framework,
            State = AnalysisCompletenessState.Partial,
            ReferenceResolutionState = project.ReferenceResolutionState,
            ExpectedSourceFiles = project.SourceFiles.Count,
            LoadedSourceFiles = 0,
            ResolvedMetadataReferences = project.MetadataReferences.Count,
            FailedMetadataReferences = 0,
            AnalyzerReferences = project.AnalyzerReferences.Count,
            GeneratedSourcesIncluded = false,
            SourceGeneratorsExecuted = false,
            SourceGeneratorsDiscovered = 0,
            GeneratedSourceFiles = 0,
            CompilationErrors = 0,
            DiagnosticIds = [],
            Gaps =
            [
                $"{language} project ownership, references, and test propagation are indexed; " +
                "Roslyn C# semantic declarations and relationships do not apply to this project."
            ]
        }).ToArray();
    }

    private static bool IsCSharpProject(ProjectRecord project) =>
        Path.GetExtension(project.Path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    private static CompilationCompletenessRecord BuildCompleteness(
        string repositoryRoot,
        ProjectRecord project,
        string framework,
        string indexedFramework,
        TargetFrameworkAnalysisRecord analysis,
        CSharpCompilation compilation,
        int loadedSourceFiles,
        (int Loaded, int Failed) referenceCounts,
        GeneratorExecution generatorExecution)
    {
        var diagnostics = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        var gaps = new List<string>();
        if (!framework.Equals(indexedFramework, StringComparison.OrdinalIgnoreCase))
        {
            gaps.Add(
                $"Declarations and relationships were indexed from target framework '{indexedFramework}' only; "
                + $"symbols that exist solely under '{framework}' are absent from the graph.");
        }

        if (analysis.ReferenceResolutionState != ExecutionState.Succeeded)
        {
            gaps.Add(analysis.ReferenceResolutionDetail is null
                ? $"Evaluated metadata reference resolution was {analysis.ReferenceResolutionState}."
                : $"Evaluated metadata reference resolution was {analysis.ReferenceResolutionState}: {analysis.ReferenceResolutionDetail}");
        }

        if (loadedSourceFiles != project.SourceFiles.Count)
        {
            gaps.Add($"Loaded {loadedSourceFiles} of {project.SourceFiles.Count} evaluated source files.");
        }

        if (referenceCounts.Failed > 0)
        {
            gaps.Add($"Failed to load {referenceCounts.Failed} evaluated metadata reference(s).");
        }

        gaps.AddRange(generatorExecution.Gaps);

        var generatedMarkupItems = project.Items
            .Where(item => item.ItemType is "RazorComponent" or "MauiXaml" or "Page" or "ApplicationDefinition")
            .Select(item => item.ItemType)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (generatedMarkupItems.Length > 0)
        {
            gaps.Add(
                $"Compiled/generated C# for evaluated {string.Join(", ", generatedMarkupItems)} item(s) is unavailable; markup relationships use explicit syntax or conventions.");
        }

        if (diagnostics.Length > 0)
        {
            var topDiagnostics = BuildDiagnosticSummaries(repositoryRoot, diagnostics)
                .Take(5)
                .Select(summary =>
                    $"{summary.Id} x{summary.Count}" +
                    (summary.Files.Count == 0 ? string.Empty : $" in {string.Join(", ", summary.Files.Take(3))}"));
            gaps.Add(
                $"The semantic compilation contains {diagnostics.Length} error diagnostic(s): " +
                string.Join("; ", topDiagnostics) + ".");
        }

        var state = loadedSourceFiles == 0 && project.SourceFiles.Count > 0
            ? AnalysisCompletenessState.Failed
            : gaps.Count == 0
                ? AnalysisCompletenessState.Complete
                : AnalysisCompletenessState.Partial;
        return new CompilationCompletenessRecord
        {
            Project = project.Path,
            TargetFrameworks = project.TargetFrameworks,
            TargetFramework = framework.Length == 0 ? null : framework,
            State = state,
            ReferenceResolutionState = analysis.ReferenceResolutionState,
            ExpectedSourceFiles = project.SourceFiles.Count,
            LoadedSourceFiles = loadedSourceFiles,
            ResolvedMetadataReferences = referenceCounts.Loaded,
            FailedMetadataReferences = referenceCounts.Failed,
            AnalyzerReferences = analysis.AnalyzerReferences.Count,
            GeneratedSourcesIncluded = generatorExecution.Executed,
            SourceGeneratorsExecuted = generatorExecution.Executed,
            SourceGeneratorsDiscovered = generatorExecution.Discovered,
            GeneratedSourceFiles = generatorExecution.Sources.Count,
            CompilationErrors = diagnostics.Length,
            DiagnosticIds = diagnostics.Select(diagnostic => diagnostic.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            DiagnosticSummaries = BuildDiagnosticSummaries(repositoryRoot, diagnostics),
            Gaps = gaps
        };
    }

    private static IReadOnlyList<CompilationDiagnosticSummary> BuildDiagnosticSummaries(
        string repositoryRoot,
        IEnumerable<Diagnostic> diagnostics) => diagnostics
        .GroupBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
        .Select(group => new CompilationDiagnosticSummary(
            group.Key,
            group.Count(),
            group.Select(diagnostic => DiagnosticFile(repositoryRoot, diagnostic))
                .Where(path => path is not null)
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .Take(10)
                .ToArray()))
        .OrderByDescending(summary => summary.Count)
        .ThenBy(summary => summary.Id, StringComparer.Ordinal)
        .ToArray();

    private static string? DiagnosticFile(string repositoryRoot, Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource)
        {
            return null;
        }

        var path = diagnostic.Location.GetLineSpan().Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.IsPathRooted(path) ? NormalizeRelative(repositoryRoot, path) : path.Replace('\\', '/');
    }

    private static IReadOnlyList<string> FrameworksOf(ProjectRecord project) =>
        project.TargetFrameworks.Count == 0 ? [string.Empty] : project.TargetFrameworks;

    private static TargetFrameworkAnalysisRecord TargetAnalysis(ProjectRecord project, string framework) =>
        project.TargetFrameworkAnalyses.FirstOrDefault(analysis =>
            analysis.TargetFramework.Equals(framework, StringComparison.OrdinalIgnoreCase))
        ?? new TargetFrameworkAnalysisRecord
        {
            TargetFramework = framework,
            MetadataReferences = project.MetadataReferences,
            AnalyzerReferences = project.AnalyzerReferences,
            GlobalUsings = project.GlobalUsings,
            ReferenceResolutionState = project.ReferenceResolutionState,
            ReferenceResolutionDetail = project.ReferenceResolutionDetail
        };

    private static string CompilationKey(string project, string framework) => $"{project}\0{framework}";

    private static GeneratorExecution ExecuteGenerators(
        CSharpCompilation compilation,
        ProjectRecord project,
        string framework,
        TargetFrameworkAnalysisRecord analysis,
        IndexingConfig indexing)
    {
        if (!indexing.ExecuteSourceGenerators || analysis.AnalyzerReferences.Count == 0)
        {
            return new GeneratorExecution(
                compilation,
                0,
                false,
                [],
                analysis.AnalyzerReferences.Count == 0
                    ? []
                    : ["Analyzer assemblies were discovered but source-generator execution is disabled."]);
        }

        var loader = new GeneratorAssemblyLoader();
        var generators = new List<ISourceGenerator>();
        var gaps = new List<string>();
        foreach (var path in analysis.AnalyzerReferences)
        {
            loader.AddDependencyLocation(path);
        }

        foreach (var path in analysis.AnalyzerReferences)
        {
            try
            {
                var reference = new AnalyzerFileReference(path, loader);
                generators.AddRange(reference.GetGenerators(LanguageNames.CSharp));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or BadImageFormatException
                                               or FileLoadException
                                               or ReflectionTypeLoadException
                                               or TypeLoadException)
            {
                gaps.Add($"Could not inspect analyzer '{Path.GetFileName(path)}' for generators: {exception.Message}");
            }
        }

        if (generators.Count == 0)
        {
            return new GeneratorExecution(compilation, 0, true, [], gaps);
        }

        try
        {
            var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
                               ?? CSharpParseOptions.Default;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators.ToArray(),
                parseOptions: parseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out var outputCompilation,
                out var generatorDiagnostics);
            foreach (var diagnostic in generatorDiagnostics.Where(diagnostic =>
                         diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error))
            {
                gaps.Add(
                    $"Source generator {diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id}: " +
                    CompactDiagnosticMessage(diagnostic.GetMessage()));
            }

            var generatedTrees = outputCompilation.SyntaxTrees
                .Except(compilation.SyntaxTrees)
                .ToArray();
            var sources = new List<GeneratedSourceRecord>(generatedTrees.Length);
            var rebasedTrees = new List<SyntaxTree>(generatedTrees.Length);
            foreach (var tree in generatedTrees)
            {
                var text = tree.GetText().ToString();
                var hintName = Path.GetFileName(tree.FilePath);
                if (hintName.Length == 0)
                {
                    hintName = $"generated-{sources.Count + 1}.g.cs";
                }

                var file = $"generated://{project.Path}/{DisplayFramework(framework)}/{hintName}";
                var contentHash = Hashing.Text(text);
                sources.Add(new GeneratedSourceRecord(
                    Hashing.Text($"{project.Path}|{framework}|{hintName}|{contentHash}"),
                    project.Path,
                    framework,
                    file,
                    contentHash,
                    text,
                    text.Count(character => character == '\n') + 1));
                rebasedTrees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, file));
            }

            var rebasedCompilation = (CSharpCompilation)outputCompilation
                .RemoveSyntaxTrees(generatedTrees)
                .AddSyntaxTrees(rebasedTrees);
            return new GeneratorExecution(
                rebasedCompilation,
                generators.Count,
                true,
                sources,
                gaps);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or ArgumentException
                                           or FileLoadException
                                           or ReflectionTypeLoadException
                                           or TypeLoadException)
        {
            gaps.Add($"Source-generator execution failed: {exception.Message}");
            return new GeneratorExecution(compilation, generators.Count, false, [], gaps);
        }
    }

    private static string DisplayFramework(string framework) =>
        framework.Length == 0 ? "default" : framework.Replace('/', '_').Replace('\\', '_');

    private static string CompactDiagnosticMessage(string message)
    {
        var compact = string.Join(
            ' ',
            message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 500 ? compact : compact[..497] + "...";
    }

    private sealed class GeneratorAssemblyLoader : IAnalyzerAssemblyLoader
    {
        private readonly Dictionary<string, string> _dependencyPaths =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly GeneratorLoadContext _loadContext;

        public GeneratorAssemblyLoader()
        {
            _loadContext = new GeneratorLoadContext(_dependencyPaths);
        }

        public void AddDependencyLocation(string fullPath)
        {
            var name = Path.GetFileNameWithoutExtension(fullPath);
            _dependencyPaths[name] = Path.GetFullPath(fullPath);
        }

        public Assembly LoadFromPath(string fullPath)
        {
            var resolvedPath = Path.GetFullPath(fullPath);
            var assemblyName = AssemblyName.GetAssemblyName(resolvedPath);
            var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            return loaded ?? _loadContext.LoadFromAssemblyPath(resolvedPath);
        }

        private sealed class GeneratorLoadContext(
            IReadOnlyDictionary<string, string> dependencyPaths) : AssemblyLoadContext
        {
            protected override Assembly? Load(AssemblyName assemblyName)
            {
                var shared = Default.Assemblies.FirstOrDefault(assembly =>
                    AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
                if (shared is not null)
                {
                    return shared;
                }

                return dependencyPaths.TryGetValue(assemblyName.Name ?? string.Empty, out var path)
                    ? LoadFromAssemblyPath(path)
                    : null;
            }
        }
    }

    private static CSharpCompilation CreateCompilation(
        ProjectRecord project,
        IEnumerable<SyntaxTree> trees,
        IEnumerable<MetadataReference> references)
    {
        var syntaxTrees = trees.ToArray();
        var nullable = project.Nullable?.Contains("enable", StringComparison.OrdinalIgnoreCase) == true
            ? NullableContextOptions.Enable
            : NullableContextOptions.Disable;
        var hasTopLevelStatements = syntaxTrees.Any(tree =>
            tree.GetRoot() is CompilationUnitSyntax root
            && root.Members.Any(member => member is GlobalStatementSyntax));
        var outputKind = (project.CompilerSettings.OutputType?.ToLowerInvariant(), hasTopLevelStatements) switch
        {
            ("exe", true) => OutputKind.ConsoleApplication,
            ("winexe", true) => OutputKind.WindowsApplication,
            ("module", _) => OutputKind.NetModule,
            _ => OutputKind.DynamicallyLinkedLibrary
        };
        var options = new CSharpCompilationOptions(
            outputKind,
            optimizationLevel: project.CompilerSettings.Optimize
                ? OptimizationLevel.Release
                : OptimizationLevel.Debug,
            allowUnsafe: project.CompilerSettings.AllowUnsafe,
            nullableContextOptions: nullable);
        return CSharpCompilation.Create(
            project.AssemblyName ?? project.Name,
            syntaxTrees,
            references,
            options);
    }

    private static CSharpParseOptions CreateParseOptions(ProjectRecord project)
    {
        var languageVersion = LanguageVersion.Latest;
        if (!string.IsNullOrWhiteSpace(project.LanguageVersion)
            && LanguageVersionFacts.TryParse(project.LanguageVersion, out var parsedLanguageVersion))
        {
            languageVersion = parsedLanguageVersion;
        }

        var symbols = project.CompilerSettings.DefineConstants?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        return new CSharpParseOptions(languageVersion, preprocessorSymbols: symbols);
    }

    private static string RenderGlobalUsing(GlobalUsingRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.Alias))
        {
            return $"global using {item.Alias} = {item.Name};";
        }

        return item.IsStatic
            ? $"global using static {item.Name};"
            : $"global using {item.Name};";
    }

    private static IReadOnlyList<MetadataReference> GetPlatformReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            return [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        }

        return trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static void IndexTypes(
        SyntaxNode root,
        SemanticModel model,
        ProjectRecord project,
        string file,
        ICollection<SymbolRecord> symbols,
        IDictionary<ISymbol, SymbolRecord> declaredSymbols)
    {
        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var kind = TypeKind(declaration);
            var semanticSymbol = model.GetDeclaredSymbol(declaration);
            var namespaceName = semanticSymbol?.ContainingNamespace.IsGlobalNamespace == false
                ? semanticSymbol.ContainingNamespace.ToDisplayString()
                : NamespaceOf(declaration);
            var containingType = semanticSymbol?.ContainingType?.Name
                                 ?? declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                                     .FirstOrDefault()?.Identifier.Text;
            var semanticName = semanticSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                               ?? string.Join('.', new[] { namespaceName, containingType, declaration.Identifier.Text }
                                   .Where(part => !string.IsNullOrWhiteSpace(part)));
            var baseType = semanticSymbol?.BaseType is { SpecialType: not SpecialType.System_Object } declaredBase
                ? declaredBase.ToDisplayString(ShortNameFormat)
                : null;
            var interfaces = semanticSymbol?.Interfaces
                .Select(item => item.ToDisplayString(ShortNameFormat))
                .Order(StringComparer.Ordinal)
                .ToArray()
                ?? SyntaxInterfaces(declaration, kind);
            var identity = Hashing.Text(string.Join('|', project.Path, kind, semanticName));
            var record = new SymbolRecord(
                identity,
                kind,
                declaration.Identifier.Text,
                namespaceName,
                containingType,
                project.Path,
                file,
                LineOf(declaration),
                baseType,
                interfaces)
            {
                SemanticName = semanticName,
                EndLine = EndLineOf(declaration),
                IsPartialImplementation = declaration is TypeDeclarationSyntax { Members.Count: > 0 }
            };
            symbols.Add(record);
            if (semanticSymbol is not null)
            {
                declaredSymbols[NormalizeSymbol(semanticSymbol)] = record;
            }
        }

        // Delegates are types but not BaseTypeDeclarationSyntax, so the loop above never sees them
        // and they are also not among the member kinds IndexMembers yields. Without this they are
        // absent from the graph entirely, and a callback contract is exactly the kind of indirection
        // an agent asks about.
        foreach (var declaration in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            var semanticSymbol = model.GetDeclaredSymbol(declaration);
            var namespaceName = semanticSymbol?.ContainingNamespace.IsGlobalNamespace == false
                ? semanticSymbol.ContainingNamespace.ToDisplayString()
                : NamespaceOf(declaration);
            var containingType = semanticSymbol?.ContainingType?.Name
                                 ?? declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                                     .FirstOrDefault()?.Identifier.Text;
            var semanticName = semanticSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                               ?? string.Join('.', new[] { namespaceName, containingType, declaration.Identifier.Text }
                                   .Where(part => !string.IsNullOrWhiteSpace(part)));
            var record = new SymbolRecord(
                Hashing.Text(string.Join('|', project.Path, "delegate", semanticName)),
                "delegate",
                declaration.Identifier.Text,
                namespaceName,
                containingType,
                project.Path,
                file,
                LineOf(declaration),
                null,
                [])
            {
                SemanticName = semanticName,
                EndLine = EndLineOf(declaration)
            };
            symbols.Add(record);
            if (semanticSymbol is not null)
            {
                declaredSymbols[NormalizeSymbol(semanticSymbol)] = record;
            }
        }
    }

    /// <summary>
    /// The entry point a file of top-level statements compiles into. Roslyn synthesizes it, so it is
    /// <c>IsImplicitlyDeclared</c> and <see cref="IndexMembers"/> skips it — which left every
    /// statement in a <c>Program.cs</c> with no containing symbol, so the composition root of a
    /// modern application was structurally invisible and none of its wiring produced an edge.
    /// </summary>
    private static IMethodSymbol? TopLevelEntryPoint(SyntaxNode root, SemanticModel model) =>
        root is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any()
            ? model.GetDeclaredSymbol(unit)
            : null;

    private static void IndexMembers(
        SyntaxNode root,
        SemanticModel model,
        ProjectRecord project,
        string file,
        ICollection<SymbolRecord> symbols,
        IDictionary<ISymbol, SymbolRecord> declaredSymbols)
    {
        foreach (var declaration in MemberDeclarations(root)
                     .Concat(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()))
        {
            var semanticSymbol = model.GetDeclaredSymbol(declaration);
            if (semanticSymbol is null || semanticSymbol.IsImplicitlyDeclared)
            {
                continue;
            }

            var containingType = semanticSymbol!.ContainingType?.Name
                                 ?? declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                                     .FirstOrDefault()?.Identifier.Text;
            var namespaceName = semanticSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
                ? containingNamespace.ToDisplayString()
                : NamespaceOf(declaration);
            var memberKind = declaration is LocalFunctionStatementSyntax
                ? "local-function"
                : MemberKind(semanticSymbol);
            var kind = project.IsTestProject
                       && declaration is MethodDeclarationSyntax method
                       && IsTestMethod(method)
                ? "test"
                : memberKind;
            var semanticName = semanticSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var identity = Hashing.Text(string.Join('|', project.Path, "member", memberKind, semanticName));
            var record = new SymbolRecord(
                identity,
                kind,
                MemberName(semanticSymbol),
                namespaceName,
                containingType,
                project.Path,
                file,
                LineOf(declaration),
                null,
                [])
            {
                SemanticName = semanticName,
                EndLine = EndLineOf(declaration),
                IsPartialImplementation = HasBody(declaration)
            };
            symbols.Add(record);
            declaredSymbols[NormalizeSymbol(semanticSymbol)] = record;
        }

        if (TopLevelEntryPoint(root, model) is { } entryPoint)
        {
            var statements = ((CompilationUnitSyntax)root).Members.OfType<GlobalStatementSyntax>().ToArray();
            var semanticName = entryPoint.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

            // Named "Main" rather than the synthesized "<Main>$": the angle-bracket form matches
            // nothing an agent would type, and the concept a caller is looking for is the entry point.
            var record = new SymbolRecord(
                Hashing.Text(string.Join('|', project.Path, "member", "entry-point", semanticName)),
                "entry-point",
                "Main",
                entryPoint.ContainingNamespace is { IsGlobalNamespace: false } entryNamespace
                    ? entryNamespace.ToDisplayString()
                    : NamespaceOf(root),
                entryPoint.ContainingType?.Name,
                project.Path,
                file,
                LineOf(statements[0]),
                null,
                [])
            {
                SemanticName = semanticName,
                EndLine = EndLineOf(statements[^1])
            };
            symbols.Add(record);
            declaredSymbols[NormalizeSymbol(entryPoint)] = record;
        }
    }

    private static IEnumerable<SyntaxNode> MemberDeclarations(SyntaxNode root)
    {
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            switch (member)
            {
                case MethodDeclarationSyntax
                    or ConstructorDeclarationSyntax
                    or DestructorDeclarationSyntax
                    or OperatorDeclarationSyntax
                    or ConversionOperatorDeclarationSyntax
                    or PropertyDeclarationSyntax
                    or IndexerDeclarationSyntax
                    or EventDeclarationSyntax:
                    yield return member;
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return variable;
                    }
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return variable;
                    }
                    break;
            }
        }

        foreach (var member in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>())
        {
            yield return member;
        }
    }

    private static async Task<IReadOnlyList<SymbolReference>> IndexReferencesAsync(
        string repositoryRoot,
        RepositoryIndex projects,
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        DeclaredSymbolLookup declaredSymbols,
        int maxParallelism,
        CancellationToken cancellationToken)
    {
        var projectReferences = await ParallelWork.SelectAsync(
            projects.Projects,
            maxParallelism,
            async (project, token) => await IndexProjectReferencesAsync(
                project,
                compilations,
                declaredSymbols,
                token),
            cancellationToken);
        var references = projectReferences.SelectMany(project => project).ToHashSet();
        AddOverrideAndInterfaceReferences(declaredSymbols, references);

        return references
            .OrderBy(reference => reference.SourceProject, StringComparer.Ordinal)
            .ThenBy(reference => reference.SourceSymbol, StringComparer.Ordinal)
            .ThenBy(reference => reference.TargetSymbol, StringComparer.Ordinal)
            .ThenBy(reference => reference.Relationship, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<SymbolReference>> IndexProjectReferencesAsync(
        ProjectRecord project,
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        DeclaredSymbolLookup declaredSymbols,
        CancellationToken cancellationToken)
    {
        var references = new HashSet<SymbolReference>();
        if (!compilations.TryGetValue(project.Path, out var compilation))
        {
            return [];
        }

        var targetFramework = FrameworksOf(project).FirstOrDefault();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await tree.GetRootAsync(cancellationToken);
            var model = compilation.GetSemanticModel(tree, true);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var source = FindContainingSymbol(invocation, model, declaredSymbols);
                var operation = model.GetOperation(invocation, cancellationToken) as IInvocationOperation;
                var target = operation?.TargetMethod
                             ?? model.GetSymbolInfo(invocation, cancellationToken).Symbol;
                AddReference(
                    source,
                    target,
                    "method-call",
                    declaredSymbols,
                    references,
                    evidence: invocation,
                    origin: operation is null ? "roslyn-semantic" : "roslyn-operation",
                    targetFramework: targetFramework);
                AddDependencyInjectionReferences(
                    invocation,
                    operation,
                    source,
                    model,
                    declaredSymbols,
                    references,
                    cancellationToken,
                    targetFramework);
            }

            foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                var source = FindContainingSymbol(creation, model, declaredSymbols);
                var operation = model.GetOperation(creation, cancellationToken) as IObjectCreationOperation;
                AddReference(
                    source,
                    operation?.Constructor ?? model.GetSymbolInfo(creation, cancellationToken).Symbol,
                    "constructs",
                    declaredSymbols,
                    references,
                    evidence: creation,
                    origin: operation is null ? "roslyn-semantic" : "roslyn-operation",
                    targetFramework: targetFramework);
                AddTypeReferences(
                    source,
                    operation?.Type ?? model.GetTypeInfo(creation, cancellationToken).Type,
                    "constructed-type",
                    declaredSymbols,
                    references,
                    evidence: creation,
                    origin: operation is null ? "roslyn-semantic" : "roslyn-operation",
                    targetFramework: targetFramework);
            }

            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (IsInvocationTarget(name) || IsDeclarationName(name))
                {
                    continue;
                }

                var operation = model.GetOperation(name, cancellationToken);
                var target = ReferencedMember(operation)
                             ?? model.GetSymbolInfo(name, cancellationToken).Symbol;
                if (target is not (IPropertySymbol or IFieldSymbol or IEventSymbol))
                {
                    continue;
                }

                var relationships = MemberAccessRelationships(operation, name, target);
                foreach (var relationship in relationships)
                {
                    AddReference(
                        FindContainingSymbol(name, model, declaredSymbols),
                        target,
                        relationship,
                        declaredSymbols,
                        references,
                        evidence: name,
                        origin: operation is null ? "roslyn-semantic" : "roslyn-operation",
                        targetFramework: targetFramework);
                }
            }

            foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
            {
                var operation = model.GetOperation(argument, cancellationToken) as IArgumentOperation;
                var target = FindMethodReference(operation?.Value)
                             ?? model.GetSymbolInfo(argument.Expression, cancellationToken).Symbol;
                if (target is IMethodSymbol)
                {
                    AddReference(
                        FindContainingSymbol(argument, model, declaredSymbols),
                        target,
                        "delegate-callback",
                        declaredSymbols,
                        references,
                        evidence: argument.Expression,
                        origin: operation is null ? "roslyn-semantic" : "roslyn-operation",
                        targetFramework: targetFramework);
                }
            }

            // typeof, nameof and attributes are how a lot of real wiring is written -- DI
            // registrations by type token, [MemberNotNull(nameof(Field))], serializer and test
            // attributes -- and none of it produced an edge. A rename that misses one of these is
            // exactly the kind of break a structural index is supposed to be able to answer for.
            foreach (var typeOf in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
            {
                AddTypeReferences(
                    FindContainingSymbol(typeOf, model, declaredSymbols),
                    model.GetTypeInfo(typeOf.Type, cancellationToken).Type,
                    "typeof-reference",
                    declaredSymbols,
                    references,
                    evidence: typeOf.Type,
                    origin: "roslyn-semantic",
                    targetFramework: targetFramework);
            }

            foreach (var nameOf in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (nameOf is not
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" },
                        ArgumentList.Arguments: [{ } onlyArgument]
                    })
                {
                    continue;
                }

                // A real method called "nameof" would resolve; the contextual keyword does not.
                if (model.GetSymbolInfo(nameOf, cancellationToken).Symbol is not null)
                {
                    continue;
                }

                var source = FindContainingSymbol(nameOf, model, declaredSymbols);
                var named = model.GetSymbolInfo(onlyArgument.Expression, cancellationToken).Symbol;
                if (named is ITypeSymbol namedType)
                {
                    AddTypeReferences(
                        source,
                        namedType,
                        "nameof-reference",
                        declaredSymbols,
                        references,
                        evidence: onlyArgument.Expression,
                        origin: "roslyn-semantic",
                        targetFramework: targetFramework);
                }
                else
                {
                    AddReference(
                        source,
                        named,
                        "nameof-reference",
                        declaredSymbols,
                        references,
                        evidence: onlyArgument.Expression,
                        origin: "roslyn-semantic",
                        targetFramework: targetFramework);
                }
            }

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                AddTypeReferences(
                    FindContainingSymbol(attribute, model, declaredSymbols)
                    ?? FindContainingTypeSymbol(attribute, model, declaredSymbols),
                    model.GetSymbolInfo(attribute, cancellationToken).Symbol?.ContainingType,
                    "attribute",
                    declaredSymbols,
                    references,
                    evidence: attribute.Name,
                    origin: "roslyn-semantic",
                    targetFramework: targetFramework);
            }

            foreach (var genericName in root.DescendantNodes().OfType<GenericNameSyntax>())
            {
                var source = FindContainingSymbol(genericName, model, declaredSymbols);
                foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
                {
                    AddTypeReferences(
                        source,
                        model.GetTypeInfo(typeArgument, cancellationToken).Type,
                        "generic-type-argument",
                        declaredSymbols,
                        references);
                }
            }

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                AddTypeReferences(
                    FindContainingSymbol(field, model, declaredSymbols),
                    model.GetTypeInfo(field.Declaration.Type, cancellationToken).Type,
                    "field-type",
                    declaredSymbols,
                    references);
            }

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var propertyType = model.GetTypeInfo(property.Type, cancellationToken).Type;
                AddTypeReferences(
                    FindContainingSymbol(property, model, declaredSymbols),
                    propertyType,
                    "property-type",
                    declaredSymbols,
                    references);
                AddTypeReferences(
                    FindContainingTypeSymbol(property, model, declaredSymbols),
                    propertyType,
                    "property-type",
                    declaredSymbols,
                    references);
            }

            foreach (var eventDeclaration in root.DescendantNodes().OfType<EventDeclarationSyntax>())
            {
                var eventType = model.GetTypeInfo(eventDeclaration.Type, cancellationToken).Type;
                AddTypeReferences(
                    FindContainingSymbol(eventDeclaration, model, declaredSymbols),
                    eventType,
                    "event-type",
                    declaredSymbols,
                    references);
                AddTypeReferences(
                    FindContainingTypeSymbol(eventDeclaration, model, declaredSymbols),
                    eventType,
                    "event-type",
                    declaredSymbols,
                    references);
            }

            foreach (var eventField in root.DescendantNodes().OfType<EventFieldDeclarationSyntax>())
            {
                AddTypeReferences(
                    FindContainingSymbol(eventField, model, declaredSymbols),
                    model.GetTypeInfo(eventField.Declaration.Type, cancellationToken).Type,
                    "event-type",
                    declaredSymbols,
                    references);
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var source = FindContainingSymbol(method.ReturnType, model, declaredSymbols);
                AddTypeReferences(
                    source,
                    model.GetTypeInfo(method.ReturnType, cancellationToken).Type,
                    "return-type",
                    declaredSymbols,
                    references);
                foreach (var parameter in method.ParameterList.Parameters)
                {
                    AddTypeReferences(
                        source,
                        parameter.Type is null
                            ? null
                            : model.GetTypeInfo(parameter.Type, cancellationToken).Type,
                        "parameter-type",
                        declaredSymbols,
                        references);
                }
            }

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (typeDeclaration.ParameterList is null)
                {
                    continue;
                }

                var primarySource = FindContainingTypeSymbol(typeDeclaration, model, declaredSymbols);
                foreach (var parameter in typeDeclaration.ParameterList.Parameters)
                {
                    AddTypeReferences(
                        primarySource,
                        parameter.Type is null
                            ? null
                            : model.GetTypeInfo(parameter.Type, cancellationToken).Type,
                        "constructor-parameter",
                        declaredSymbols,
                        references);
                }
            }

            foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            {
                var source = FindContainingSymbol(constructor, model, declaredSymbols);
                var containingTypeSource = FindContainingTypeSymbol(constructor, model, declaredSymbols);
                foreach (var parameter in constructor.ParameterList.Parameters)
                {
                    var parameterType = parameter.Type is null
                        ? null
                        : model.GetTypeInfo(parameter.Type, cancellationToken).Type;
                    AddTypeReferences(
                        source,
                        parameterType,
                        "constructor-parameter",
                        declaredSymbols,
                        references);
                    AddTypeReferences(
                        containingTypeSource,
                        parameterType,
                        "constructor-parameter",
                        declaredSymbols,
                        references);
                }
            }
        }

        return references.ToArray();
    }

    private static SymbolRecord? FindContainingSymbol(
        SyntaxNode node,
        SemanticModel model,
        DeclaredSymbolLookup declaredSymbols)
    {
        foreach (var declaration in node.AncestorsAndSelf())
        {
            if (declaration is not (MethodDeclarationSyntax
                or LocalFunctionStatementSyntax
                or ConstructorDeclarationSyntax
                or DestructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax
                or EventDeclarationSyntax
                or VariableDeclaratorSyntax
                or EnumMemberDeclarationSyntax))
            {
                continue;
            }

            if (model.GetDeclaredSymbol(declaration) is { } memberSymbol
                && declaredSymbols.TryResolve(memberSymbol, out var memberRecord))
            {
                return memberRecord;
            }
        }

        var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (type is not null
            && model.GetDeclaredSymbol(type) is { } typeSymbol
            && declaredSymbols.TryResolve(typeSymbol, out var typeRecord))
        {
            return typeRecord;
        }

        // Top-level statements have neither a member nor a type declaration above them, so without
        // this every reference in a Program.cs -- the DI registrations, the pipeline construction --
        // was attributed to nothing and discarded.
        if (node.Ancestors().OfType<GlobalStatementSyntax>().Any()
            && TopLevelEntryPoint(node.SyntaxTree.GetRoot(), model) is { } entryPoint
            && declaredSymbols.TryResolve(entryPoint, out var entryRecord))
        {
            return entryRecord;
        }

        return null;
    }

    private static SymbolRecord? FindContainingTypeSymbol(
        SyntaxNode node,
        SemanticModel model,
        DeclaredSymbolLookup declaredSymbols)
    {
        var type = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return type is not null
               && model.GetDeclaredSymbol(type) is { } typeSymbol
               && declaredSymbols.TryResolve(typeSymbol, out var typeRecord)
            ? typeRecord
            : null;
    }

    private static void AddReference(
        SymbolRecord? source,
        ISymbol? target,
        string relationship,
        DeclaredSymbolLookup declaredSymbols,
        ISet<SymbolReference> references,
        EvidenceConfidence confidence = EvidenceConfidence.SemanticResolved,
        SyntaxNode? evidence = null,
        string origin = "roslyn-symbol",
        string? targetFramework = null)
    {
        if (source is null || target is null
            || !declaredSymbols.TryResolve(target, out var targetRecord)
            || source.Identity == targetRecord.Identity)
        {
            return;
        }

        var lineSpan = evidence?.GetLocation().GetLineSpan();
        references.Add(new SymbolReference(
            source.Identity,
            targetRecord.Identity,
            relationship,
            source.Project,
            targetRecord.Project)
        {
            Confidence = confidence,
            Origin = origin,
            TargetFramework = targetFramework,
            EvidenceFile = source.File,
            EvidenceLine = lineSpan is null ? source.Line : lineSpan.Value.StartLinePosition.Line + 1,
            EvidenceColumn = lineSpan is null ? null : lineSpan.Value.StartLinePosition.Character + 1,
            EvidenceEndLine = lineSpan is null ? source.EndLine : lineSpan.Value.EndLinePosition.Line + 1,
            EvidenceEndColumn = lineSpan is null ? null : lineSpan.Value.EndLinePosition.Character + 1
        });
    }

    private static void AddTypeReferences(
        SymbolRecord? source,
        ITypeSymbol? target,
        string relationship,
        DeclaredSymbolLookup declaredSymbols,
        ISet<SymbolReference> references,
        EvidenceConfidence confidence = EvidenceConfidence.SemanticResolved,
        SyntaxNode? evidence = null,
        string origin = "roslyn-symbol",
        string? targetFramework = null)
    {
        if (target is null)
        {
            return;
        }

        switch (target)
        {
            case IArrayTypeSymbol array:
                AddTypeReferences(
                    source,
                    array.ElementType,
                    relationship,
                    declaredSymbols,
                    references,
                    confidence,
                    evidence,
                    origin,
                    targetFramework);
                return;
            case IPointerTypeSymbol pointer:
                AddTypeReferences(
                    source,
                    pointer.PointedAtType,
                    relationship,
                    declaredSymbols,
                    references,
                    confidence,
                    evidence,
                    origin,
                    targetFramework);
                return;
            case INamedTypeSymbol named:
                AddReference(
                    source,
                    named,
                    relationship,
                    declaredSymbols,
                    references,
                    confidence,
                    evidence,
                    origin,
                    targetFramework);
                foreach (var typeArgument in named.TypeArguments)
                {
                    AddTypeReferences(
                        source,
                        typeArgument,
                        relationship,
                        declaredSymbols,
                        references,
                        confidence,
                        evidence,
                        origin,
                        targetFramework);
                }

                return;
        }

        AddReference(
            source,
            target,
            relationship,
            declaredSymbols,
            references,
            confidence,
            evidence,
            origin,
            targetFramework);
    }

    private static void AddDependencyInjectionReferences(
        InvocationExpressionSyntax invocation,
        IInvocationOperation? operation,
        SymbolRecord? source,
        SemanticModel model,
        DeclaredSymbolLookup declaredSymbols,
        ISet<SymbolReference> references,
        CancellationToken cancellationToken,
        string? targetFramework)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            GenericNameSyntax generic => generic,
            _ => null
        };
        if (name is null || name.Identifier.Text is not ("AddSingleton" or "AddScoped" or "AddTransient"))
        {
            return;
        }

        var semanticTypeArguments = operation?.TargetMethod.TypeArguments ?? [];
        if (semanticTypeArguments.Length > 0)
        {
            foreach (var typeArgument in semanticTypeArguments)
            {
                AddTypeReferences(
                    source,
                    typeArgument,
                    "dependency-injection",
                    declaredSymbols,
                    references,
                    EvidenceConfidence.ConventionHeuristic,
                    invocation,
                    "roslyn-operation",
                    targetFramework);
            }

            return;
        }

        foreach (var typeArgument in name is GenericNameSyntax genericName
                     ? genericName.TypeArgumentList.Arguments
                     : [])
        {
            AddTypeReferences(
                source,
                model.GetTypeInfo(typeArgument, cancellationToken).Type,
                "dependency-injection",
                declaredSymbols,
                references,
                EvidenceConfidence.ConventionHeuristic,
                typeArgument,
                "roslyn-semantic",
                targetFramework);
        }
    }

    private static void AddOverrideAndInterfaceReferences(
        DeclaredSymbolLookup declaredSymbols,
        ISet<SymbolReference> references)
    {
        foreach (var pair in declaredSymbols.Declarations)
        {
            var source = pair.Value;
            switch (pair.Key)
            {
                case IMethodSymbol method:
                    AddReference(source, method.OverriddenMethod, "override", declaredSymbols, references);
                    foreach (var implementation in method.ExplicitInterfaceImplementations)
                    {
                        AddReference(source, implementation, "interface-implementation", declaredSymbols, references);
                    }
                    break;
                case IPropertySymbol property:
                    AddReference(source, property.OverriddenProperty, "override", declaredSymbols, references);
                    foreach (var implementation in property.ExplicitInterfaceImplementations)
                    {
                        AddReference(source, implementation, "interface-implementation", declaredSymbols, references);
                    }
                    break;
                case IEventSymbol @event:
                    AddReference(source, @event.OverriddenEvent, "override", declaredSymbols, references);
                    foreach (var implementation in @event.ExplicitInterfaceImplementations)
                    {
                        AddReference(source, implementation, "interface-implementation", declaredSymbols, references);
                    }
                    break;
            }

            var containingType = pair.Key.ContainingType;
            if (containingType is null)
            {
                continue;
            }

            foreach (var @interface in containingType.AllInterfaces)
            {
                foreach (var interfaceMember in @interface.GetMembers())
                {
                    if (containingType.FindImplementationForInterfaceMember(interfaceMember) is { } implementation
                        && SymbolEqualityComparer.Default.Equals(
                            NormalizeSymbol(implementation),
                            NormalizeSymbol(pair.Key)))
                    {
                        AddReference(
                            source,
                            interfaceMember,
                            "interface-implementation",
                            declaredSymbols,
                            references);
                    }
                }
            }
        }
    }

    private static ISymbol? ReferencedMember(IOperation? operation) => operation switch
    {
        IPropertyReferenceOperation property => property.Property,
        IFieldReferenceOperation field => field.Field,
        IEventReferenceOperation @event => @event.Event,
        _ => null
    };

    private static IReadOnlyList<string> MemberAccessRelationships(
        IOperation? operation,
        SimpleNameSyntax name,
        ISymbol target)
    {
        var assignment = name.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.Left.FullSpan.Contains(name.Span));
        if (target is IEventSymbol
            && (operation?.Parent is IEventAssignmentOperation
                || assignment?.Kind() is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression))
        {
            return ["event-subscription"];
        }

        if (assignment is not null)
        {
            return assignment.Kind() is SyntaxKind.AddAssignmentExpression
                or SyntaxKind.SubtractAssignmentExpression
                or SyntaxKind.MultiplyAssignmentExpression
                or SyntaxKind.DivideAssignmentExpression
                or SyntaxKind.ModuloAssignmentExpression
                or SyntaxKind.AndAssignmentExpression
                or SyntaxKind.ExclusiveOrAssignmentExpression
                or SyntaxKind.OrAssignmentExpression
                or SyntaxKind.LeftShiftAssignmentExpression
                or SyntaxKind.RightShiftAssignmentExpression
                or SyntaxKind.UnsignedRightShiftAssignmentExpression
                or SyntaxKind.CoalesceAssignmentExpression
                ? ["member-read", "member-write"]
                : ["member-write"];
        }

        if (name.AncestorsAndSelf().Any(ancestor =>
                ancestor is PrefixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression
                }
                or PostfixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression
                }))
        {
            return ["member-read", "member-write"];
        }

        if (name.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault() is { } argument)
        {
            return argument.RefKindKeyword.Kind() switch
            {
                SyntaxKind.OutKeyword => ["member-write"],
                SyntaxKind.RefKeyword => ["member-read", "member-write"],
                _ => ["member-read"]
            };
        }

        return ["member-read"];
    }

    private static IMethodSymbol? FindMethodReference(IOperation? operation)
    {
        return operation switch
        {
            IMethodReferenceOperation methodReference => methodReference.Method,
            IDelegateCreationOperation delegateCreation => FindMethodReference(delegateCreation.Target),
            IConversionOperation { Type.TypeKind: Microsoft.CodeAnalysis.TypeKind.Delegate } conversion =>
                FindMethodReference(conversion.Operand),
            _ => null
        };
    }

    private static bool IsInvocationTarget(SimpleNameSyntax name) =>
        name.Parent is InvocationExpressionSyntax { Expression: var direct } && ReferenceEquals(direct, name)
        || name.Parent is MemberAccessExpressionSyntax { Name: var memberName } member
           && ReferenceEquals(memberName, name)
           && member.Parent is InvocationExpressionSyntax
        || name.Parent is MemberBindingExpressionSyntax { Name: var bindingName } binding
           && ReferenceEquals(bindingName, name)
           && binding.Parent is InvocationExpressionSyntax;

    private static bool IsDeclarationName(SimpleNameSyntax name) =>
        name.Parent is VariableDeclaratorSyntax
            or MethodDeclarationSyntax
            or PropertyDeclarationSyntax
            or EventDeclarationSyntax
            or TypeDeclarationSyntax;

    private static ISymbol NormalizeSymbol(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition,
        INamedTypeSymbol type => type.OriginalDefinition,
        _ => symbol.OriginalDefinition
    };

    private static string MemberKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.Destructor } => "destructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => "operator",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "conversion-operator",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol { ContainingType.TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } => "enum-member",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "member"
    };

    private static string MemberName(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => ".ctor",
        IMethodSymbol { MethodKind: MethodKind.StaticConstructor } => ".cctor",
        IMethodSymbol { MethodKind: MethodKind.Destructor } method => $"~{method.ContainingType.Name}",
        IPropertySymbol { IsIndexer: true } => "this[]",
        _ => symbol.Name
    };

    private static string TypeKind(BaseTypeDeclarationSyntax declaration) => declaration.Kind() switch
    {
        SyntaxKind.ClassDeclaration => "class",
        SyntaxKind.RecordDeclaration or SyntaxKind.RecordStructDeclaration => "record",
        SyntaxKind.StructDeclaration => "struct",
        SyntaxKind.InterfaceDeclaration => "interface",
        SyntaxKind.EnumDeclaration => "enum",
        _ => "type"
    };

    private static IReadOnlyList<string> SyntaxInterfaces(
        BaseTypeDeclarationSyntax declaration,
        string kind)
    {
        var baseTypes = declaration is TypeDeclarationSyntax typeDeclaration
            ? typeDeclaration.BaseList?.Types.Select(type => type.Type.ToString()).ToArray() ?? []
            : [];
        return kind switch
        {
            "interface" or "struct" => baseTypes,
            "class" or "record" when baseTypes.Length > 1 => baseTypes[1..],
            _ => []
        };
    }

    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes)
            .Select(attribute => attribute.Name.ToString())
            .Any(name => name.EndsWith("Test", StringComparison.Ordinal)
                         || name.EndsWith("TestMethod", StringComparison.Ordinal)
                         || name.EndsWith("Fact", StringComparison.Ordinal)
                         || name.EndsWith("Theory", StringComparison.Ordinal));

    private static string? NamespaceOf(SyntaxNode node) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    /// <summary>
    /// Whether a declaration carries an implementation, which is what makes it the useful half of a
    /// partial pair. A bodyless declaration is a signature, and pointing a caller at it is a worse
    /// answer than pointing them at the code.
    /// </summary>
    private static bool HasBody(SyntaxNode declaration) => declaration switch
    {
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        PropertyDeclarationSyntax property =>
            property.ExpressionBody is not null
            || property.AccessorList?.Accessors.Any(accessor =>
                accessor.Body is not null || accessor.ExpressionBody is not null) == true,
        IndexerDeclarationSyntax indexer =>
            indexer.ExpressionBody is not null
            || indexer.AccessorList?.Accessors.Any(accessor =>
                accessor.Body is not null || accessor.ExpressionBody is not null) == true,
        EventDeclarationSyntax eventDeclaration =>
            eventDeclaration.AccessorList?.Accessors.Any(accessor => accessor.Body is not null) == true,
        LocalFunctionStatementSyntax local => local.Body is not null || local.ExpressionBody is not null,
        VariableDeclaratorSyntax variable => variable.Initializer is not null,
        _ => false
    };

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static bool IsGeneratedPath(string path) =>
        path.StartsWith("generated://", StringComparison.Ordinal);

    private static string NormalizeRelative(string repositoryRoot, string path) =>
        IsGeneratedPath(path)
            ? path
            : Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
}
