using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DevContext.Core;
using DevContext.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevContext.Services;

internal sealed class RepositoryIntelligenceService(
    IProcessRunner processRunner,
    GitService gitService,
    RepositoryGraphService graphService,
    ContextStore store)
{
    public async Task<RepositoryContextReport> BuildAsync(
        string repositoryRoot,
        DevContext.Configuration.DevContextConfig configuration,
        RepositoryContextOptions options,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var graph = await graphService.BuildAsync(repositoryRoot, configuration, cancellationToken);
        var currentGit = await gitService.CaptureAsync(repositoryRoot, cancellationToken);
        StatusReport? baseline = null;
        if (store.BaselineExists(repositoryRoot))
        {
            baseline = await store.ReadStatusAsync(repositoryRoot, cancellationToken);
        }

        var changes = baseline is null
            ? new GitChangeSet(
                null,
                currentGit.HeadCommit,
                GitComparisonState.Comparable,
                currentGit.Files
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .Select(file => new GitFileChange(file.Path, GitChangeProvenance.WorkingTree))
                    .ToArray())
            : await gitService.ChangesSinceAsync(
                repositoryRoot,
                baseline.Git,
                currentGit,
                cancellationToken);
        var changedFiles = changes.ChangedFiles;
        var scope = options.Scope == ContextScope.Automatic
            ? options.Purpose == ContextPurpose.Change
                ? ContextScope.ChangedFiles
                : ContextScope.FullRepository
            : options.Scope;
        var selection = ResolveScope(repositoryRoot, graph, scope, options.Target, changedFiles);
        var diagnostics = (baseline?.Build.Diagnostics ?? [])
            .Concat(baseline?.Analysis.Diagnostics ?? [])
            .Where(diagnostic => selection.Files.Count == 0
                                 || diagnostic.File is null
                                 || selection.Files.Contains(NormalizeDiagnosticPath(repositoryRoot, diagnostic.File)))
            .DistinctBy(diagnostic => diagnostic.Identity, StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(diagnostic => diagnostic.Line)
            .ToArray();
        var failingTests = baseline?.Tests.Outcomes
            .Where(outcome => TestService.IsFailed(outcome.Outcome))
            .OrderBy(outcome => outcome.Name, StringComparer.Ordinal)
            .ToArray() ?? [];
        var coveragePaths = !string.IsNullOrWhiteSpace(options.CoberturaPath)
            ? new[] { options.CoberturaPath }
            : baseline is null
                ? []
                : (await store.ReadLatestTestsAsync(repositoryRoot, cancellationToken)).CoverageFiles;
        var coverage = ReadCobertura(repositoryRoot, coveragePaths);
        var churn = await ReadGitHistoryAsync(
            repositoryRoot,
            options.GitHistoryMonths,
            selection.Files,
            cancellationToken);
        var (types, methods) = await BuildCodeMetricsAsync(
            repositoryRoot,
            graph,
            selection,
            cancellationToken);
        var hotspots = await BuildHotspotsAsync(
            repositoryRoot,
            graph,
            selection,
            diagnostics,
            coverage,
            churn,
            options.MaxHotspots,
            cancellationToken);
        var symbols = SelectSymbols(graph, selection, hotspots, options).ToArray();
        var typeDefinitions = SelectTypeDefinitions(graph, selection, hotspots, options).ToArray();
        var compilationCompleteness = graph.Symbols.CompilationCompleteness
            .Where(record => selection.Projects.Contains(record.Project))
            .OrderBy(record => record.Project, StringComparer.Ordinal)
            .ToArray();
        var dependencies = graph.Dependencies.Projects
            .Where(dependency => selection.Projects.Contains(dependency.Project)
                                 || selection.Projects.Contains(dependency.ReferencedProject))
            .OrderBy(dependency => dependency.Project, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.ReferencedProject, StringComparer.Ordinal)
            .ToArray();

        var draft = new RepositoryContextReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            RepositoryRoot = repositoryRoot,
            Branch = currentGit.Branch,
            HeadCommit = currentGit.HeadCommit,
            Purpose = options.Purpose,
            Scope = scope,
            Target = options.Target,
            AnalyzedProjects = selection.Projects.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            AnalyzedFiles = selection.Files.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ChangedFiles = changedFiles,
            Changes = changes.Changes,
            GitComparison = changes.Comparison,
            Diagnostics = diagnostics,
            FailingTests = failingTests,
            ProjectDependencies = dependencies,
            Symbols = symbols,
            TypeDefinitions = typeDefinitions,
            CompilationCompleteness = compilationCompleteness,
            Types = types,
            Methods = methods,
            Hotspots = hotspots,
            Markdown = string.Empty,
            ApproximateTokens = 0
        };
        var markdown = RenderMarkdown(draft);
        return draft with
        {
            Markdown = markdown,
            ApproximateTokens = EstimateTokens(markdown)
        };
    }

    public async Task<RepositoryReportArtifact> SaveAsync(
        string repositoryRoot,
        RepositoryContextReport report,
        string? outputPath,
        int retain,
        CancellationToken cancellationToken)
    {
        if (retain < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retain), "Retention must be at least one report.");
        }

        var reportsRoot = ContextPaths.Reports(repositoryRoot);
        var resolvedPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(
                reportsRoot,
                $"{report.GeneratedAtUtc:yyyyMMddHHmmss}-{report.Purpose.ToString().ToLowerInvariant()}.md")
            : Path.GetFullPath(Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(repositoryRoot, outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);
        await File.WriteAllTextAsync(resolvedPath, report.Markdown, cancellationToken);
        var trendPath = Path.ChangeExtension(resolvedPath, ".trend.json");
        var coverageValues = report.Hotspots
            .Where(hotspot => hotspot.LineCoveragePercent is not null)
            .Select(hotspot => hotspot.LineCoveragePercent!.Value)
            .ToArray();
        await JsonFile.WriteAsync(
            trendPath,
            new RepositoryTrendPoint
            {
                GeneratedAtUtc = report.GeneratedAtUtc,
                ReportPath = RelativeOrAbsolutePath(repositoryRoot, resolvedPath),
                Purpose = report.Purpose,
                Scope = report.Scope,
                Target = report.Target,
                DiagnosticCount = report.Diagnostics.Count,
                FailingTestCount = report.FailingTests.Count,
                HotspotCount = report.Hotspots.Count,
                HotspotChurn = report.Hotspots.Sum(hotspot => hotspot.Churn),
                HotspotsWithCoverage = coverageValues.Length,
                AverageLineCoveragePercent = coverageValues.Length == 0 ? null : coverageValues.Average()
            },
            cancellationToken);

        if (Path.GetFullPath(Path.GetDirectoryName(resolvedPath)!)
            .Equals(Path.GetFullPath(reportsRoot), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var stale in Directory.EnumerateFiles(reportsRoot, "*.md")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(retain))
            {
                File.Delete(stale);
                var staleTrend = Path.ChangeExtension(stale, ".trend.json");
                if (File.Exists(staleTrend))
                {
                    File.Delete(staleTrend);
                }
            }
        }

        return new RepositoryReportArtifact(
            resolvedPath,
            report.Markdown.Length,
            report.ApproximateTokens);
    }

    public async Task<RepositoryTrendReport> TrendAsync(
        string repositoryRoot,
        int maxPoints,
        CancellationToken cancellationToken)
    {
        if (maxPoints < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPoints), "The trend point bound must be positive.");
        }

        var reportsRoot = ContextPaths.Reports(repositoryRoot);
        if (!Directory.Exists(reportsRoot))
        {
            return new RepositoryTrendReport
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Points = []
            };
        }

        var snapshots = new List<RepositoryTrendPoint>();
        foreach (var path in Directory.EnumerateFiles(reportsRoot, "*.trend.json")
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await JsonFile.ReadAsync<RepositoryTrendPoint>(path, cancellationToken);
            SchemaVersions.EnsureReadable(snapshot.SchemaVersion, Path.GetFileName(path));
            snapshots.Add(snapshot);
        }

        var ordered = snapshots
            .OrderBy(point => point.GeneratedAtUtc)
            .ThenBy(point => point.ReportPath, StringComparer.Ordinal)
            .ToArray();
        var points = new List<RepositoryTrendPoint>(ordered.Length);
        var previousBySeries = new Dictionary<
            (ContextPurpose Purpose, ContextScope Scope, string? Target),
            RepositoryTrendPoint>();
        foreach (var point in ordered)
        {
            var series = (point.Purpose, point.Scope, point.Target);
            previousBySeries.TryGetValue(series, out var previous);
            points.Add(point with
            {
                DiagnosticDelta = previous is null ? null : point.DiagnosticCount - previous.DiagnosticCount,
                FailingTestDelta = previous is null ? null : point.FailingTestCount - previous.FailingTestCount,
                HotspotChurnDelta = previous is null ? null : point.HotspotChurn - previous.HotspotChurn,
                AverageLineCoverageDelta = previous?.AverageLineCoveragePercent is null
                                           || point.AverageLineCoveragePercent is null
                    ? null
                    : point.AverageLineCoveragePercent - previous.AverageLineCoveragePercent
            });
            previousBySeries[series] = point;
        }

        return new RepositoryTrendReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Points = points.TakeLast(maxPoints).ToArray()
        };
    }

    private static string RelativeOrAbsolutePath(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() == ".."
            ? Path.GetFullPath(path)
            : relative;
    }

    public static string RenderMarkdown(RepositoryContextReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# RepoLens {report.Purpose} context");
        builder.AppendLine();
        builder.AppendLine($"- Scope: {report.Scope}" +
                           (string.IsNullOrWhiteSpace(report.Target) ? string.Empty : $" (`{report.Target}`)"));
        builder.AppendLine($"- Projects: {report.AnalyzedProjects.Count}");
        builder.AppendLine($"- Files: {report.AnalyzedFiles.Count}");
        builder.AppendLine($"- Changed files: {report.ChangedFiles.Count}");
        builder.AppendLine($"- Git comparison: {report.GitComparison}");
        builder.AppendLine($"- Baseline diagnostics in scope: {report.Diagnostics.Count}");
        builder.AppendLine($"- Existing failing tests: {report.FailingTests.Count}");
        builder.AppendLine($"- Semantic compilations: " +
                           $"{report.CompilationCompleteness.Count(record => record.State == AnalysisCompletenessState.Complete)} complete / " +
                           $"{report.CompilationCompleteness.Count(record => record.State == AnalysisCompletenessState.Partial)} partial / " +
                           $"{report.CompilationCompleteness.Count(record => record.State == AnalysisCompletenessState.Failed)} failed");

        AppendList(builder, "Analysis gaps", report.CompilationCompleteness
            .Where(record => record.State != AnalysisCompletenessState.Complete)
            .SelectMany(record => record.Gaps.Select(gap => $"`{record.Project}`: {gap}")));

        if (report.GitComparison != GitComparisonState.Comparable)
        {
            AppendList(
                builder,
                "Git comparison gaps",
                [$"Baseline comparison is {report.GitComparison}; committed changes may be incomplete."]);
        }

        if (report.Purpose == ContextPurpose.Change)
        {
            AppendList(builder, "Changed files", report.Changes.Count == 0
                ? report.ChangedFiles
                : report.Changes.Select(change => $"`{change.Path}` ({ChangeLabel(change.Provenance)})"));
            AppendList(builder, "Affected projects", report.AnalyzedProjects);
            AppendSymbols(builder, report.Symbols);
            AppendTypeDefinitions(builder, report.TypeDefinitions);
        }

        if (report.Purpose is ContextPurpose.Architecture or ContextPurpose.Change)
        {
            AppendList(builder, "Project dependencies", report.ProjectDependencies.Select(dependency =>
                $"`{dependency.Project}` -> `{dependency.ReferencedProject}`"));
            if (report.Purpose == ContextPurpose.Architecture)
            {
                AppendSymbols(builder, report.Symbols);
                AppendTypeDefinitions(builder, report.TypeDefinitions);
            }
        }

        if (report.Purpose is ContextPurpose.Build or ContextPurpose.Change)
        {
            AppendList(builder, "Diagnostics", report.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Severity} {diagnostic.Rule}: {diagnostic.Message}" +
                (diagnostic.File is null ? string.Empty : $" (`{diagnostic.File}:{diagnostic.Line}`)")));
            AppendList(builder, "Failing tests", report.FailingTests.Select(test =>
                string.IsNullOrWhiteSpace(test.ErrorMessage)
                    ? test.Name
                    : $"{test.Name}: {test.ErrorMessage}"));
        }

        if (report.Purpose is ContextPurpose.Risk or ContextPurpose.Architecture or ContextPurpose.Change)
        {
            builder.AppendLine();
            builder.AppendLine("## Hotspots");
            if (report.Hotspots.Count == 0)
            {
                builder.AppendLine();
                builder.AppendLine("(none in scope)");
            }
            else
            {
                foreach (var hotspot in report.Hotspots)
                {
                    builder.AppendLine();
                    builder.AppendLine($"{hotspot.Rank}. `{hotspot.Path}` ({hotspot.Project})");
                    builder.AppendLine($"   - LOC {hotspot.LinesOfCode}; max complexity " +
                                       $"{hotspot.MaximumCyclomaticComplexity}; dependencies " +
                                       $"{hotspot.OutgoingDependencyCount} out/{hotspot.IncomingDependencyCount} in; " +
                                       $"diagnostics {hotspot.DiagnosticCount}; commits {hotspot.CommitCount}; churn {hotspot.Churn}" +
                                       (hotspot.LineCoveragePercent is null
                                           ? string.Empty
                                           : $"; coverage {hotspot.LineCoveragePercent:F1}%"));
                    builder.AppendLine($"   - Selected because: {string.Join("; ", hotspot.SelectionReasons)}");
                }
            }
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static ScopeSelection ResolveScope(
        string repositoryRoot,
        RepositoryGraph graph,
        ContextScope scope,
        string? target,
        IReadOnlyList<string> changedFiles)
    {
        var allProjects = graph.Repository.Projects;
        if (scope == ContextScope.FullRepository)
        {
            return new ScopeSelection(
                allProjects.Select(project => project.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
                allProjects.SelectMany(project => project.SourceFiles).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        if (scope == ContextScope.ChangedFiles)
        {
            var owners = changedFiles
                .SelectMany(file => ProjectOwnershipResolver.Explain(file, allProjects))
                .Select(owner => owner.ProjectPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var affected = ProjectOwnershipResolver.ExpandAffectedProjects(owners, graph.Dependencies.Projects);
            return new ScopeSelection(
                affected.ToHashSet(StringComparer.OrdinalIgnoreCase),
                changedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException($"Scope {scope} requires a target.", nameof(target));
        }

        if (scope == ContextScope.Project)
        {
            var normalizedTarget = NormalizeTarget(repositoryRoot, target);
            var matches = allProjects.Where(project =>
                    project.Name.Equals(target, StringComparison.OrdinalIgnoreCase)
                    || project.Path.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"No project matches '{target}'.");
            }

            return new ScopeSelection(
                matches.Select(project => project.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
                matches.SelectMany(project => project.SourceFiles).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        var normalizedPath = NormalizeTarget(repositoryRoot, target);
        var prefix = normalizedPath.TrimEnd('/') + "/";
        var files = allProjects.SelectMany(project => project.SourceFiles)
            .Where(file => file.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
                           || file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0 && File.Exists(Path.Combine(repositoryRoot, normalizedPath)))
        {
            files.Add(normalizedPath);
        }

        var projects = files
            .SelectMany(file => ProjectOwnershipResolver.Explain(file, allProjects))
            .Select(owner => owner.ProjectPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ScopeSelection(projects, files);
    }

    private static async Task<IReadOnlyList<FileHotspot>> BuildHotspotsAsync(
        string repositoryRoot,
        RepositoryGraph graph,
        ScopeSelection selection,
        IReadOnlyList<DiagnosticRecord> diagnostics,
        IReadOnlyDictionary<string, double> coverage,
        IReadOnlyDictionary<string, GitHistoryMetric> history,
        int limit,
        CancellationToken cancellationToken)
    {
        var symbolsByIdentity = graph.Symbols.Symbols.ToDictionary(symbol => symbol.Identity, StringComparer.Ordinal);
        var sourceFiles = selection.Files
            .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = new List<FileHotspot>(sourceFiles.Length);
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var source = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var root = await CSharpSyntaxTree.ParseText(source, path: fullPath)
                .GetRootAsync(cancellationToken);
            var complexities = root.DescendantNodes()
                .OfType<BaseMethodDeclarationSyntax>()
                .Select(CyclomaticComplexity)
                .ToArray();
            var fileSymbols = graph.Symbols.Symbols
                .Where(symbol => symbol.File.Equals(file, StringComparison.OrdinalIgnoreCase))
                .Select(symbol => symbol.Identity)
                .ToHashSet(StringComparer.Ordinal);
            var outgoing = graph.Dependencies.Symbols
                .Where(reference => fileSymbols.Contains(reference.SourceSymbol))
                .Select(reference => reference.TargetSymbol)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var incoming = graph.Dependencies.Symbols
                .Where(reference => fileSymbols.Contains(reference.TargetSymbol))
                .Select(reference => reference.SourceSymbol)
                .Where(symbolsByIdentity.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var diagnosticCount = diagnostics.Count(diagnostic => diagnostic.File is not null
                                                                  && NormalizeDiagnosticPath(repositoryRoot, diagnostic.File)
                                                                      .Equals(file, StringComparison.OrdinalIgnoreCase));
            history.TryGetValue(file, out var git);
            var lineCoverage = TryFindCoverage(coverage, file);
            var maxComplexity = complexities.DefaultIfEmpty(0).Max();
            var lines = source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Count(line => !string.IsNullOrWhiteSpace(line));
            var reasons = new List<string>();
            if (diagnosticCount > 0) reasons.Add($"{diagnosticCount} baseline diagnostic(s)");
            if (maxComplexity >= 10) reasons.Add($"maximum cyclomatic complexity {maxComplexity}");
            if (outgoing + incoming > 0) reasons.Add($"{outgoing} outgoing and {incoming} incoming dependencies");
            if (lineCoverage is < 80) reasons.Add($"line coverage {lineCoverage:F1}%");
            if (git?.Churn > 0) reasons.Add($"recent churn {git.Churn} across {git.CommitCount} commit(s)");
            if (reasons.Count == 0) reasons.Add("highest remaining deterministic rank in scope");

            var project = graph.Repository.Projects
                .FirstOrDefault(candidate => candidate.SourceFiles.Contains(file, StringComparer.OrdinalIgnoreCase))?.Path
                ?? "(unowned)";
            candidates.Add(new FileHotspot
            {
                Rank = 0,
                Path = file,
                Project = project,
                LinesOfCode = lines,
                MaximumCyclomaticComplexity = maxComplexity,
                OutgoingDependencyCount = outgoing,
                IncomingDependencyCount = incoming,
                DiagnosticCount = diagnosticCount,
                CommitCount = git?.CommitCount ?? 0,
                ContributorCount = git?.Contributors.Count ?? 0,
                Churn = git?.Churn ?? 0,
                LastModifiedUtc = git?.LastModifiedUtc,
                LineCoveragePercent = lineCoverage,
                SelectionReasons = reasons
            });
        }

        return candidates
            .OrderByDescending(candidate => candidate.DiagnosticCount)
            .ThenByDescending(candidate => candidate.MaximumCyclomaticComplexity)
            .ThenByDescending(candidate => candidate.IncomingDependencyCount + candidate.OutgoingDependencyCount)
            .ThenBy(candidate => candidate.LineCoveragePercent ?? double.MaxValue)
            .ThenByDescending(candidate => candidate.Churn)
            .ThenByDescending(candidate => candidate.LinesOfCode)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Take(limit)
            .Select((candidate, index) => candidate with { Rank = index + 1 })
            .ToArray();
    }

    private static async Task<(IReadOnlyList<CodeTypeMetric> Types, IReadOnlyList<CodeMethodMetric> Methods)>
        BuildCodeMetricsAsync(
            string repositoryRoot,
            RepositoryGraph graph,
            ScopeSelection selection,
            CancellationToken cancellationToken)
    {
        var symbolByIdentity = graph.Symbols.Symbols.ToDictionary(symbol => symbol.Identity, StringComparer.Ordinal);
        var typeDrafts = new List<TypeMetricDraft>();
        var methods = new List<CodeMethodMetric>();
        var methodOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var publicMethods = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in selection.Files
                     .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var source = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var root = await CSharpSyntaxTree.ParseText(source, path: fullPath)
                .GetRootAsync(cancellationToken);
            var fileSymbols = graph.Symbols.Symbols
                .Where(symbol => symbol.File.Equals(file, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var typeSymbolsByLine = fileSymbols
                .Where(symbol => symbol.Kind is "class" or "record" or "struct" or "interface" or "enum")
                .ToDictionary(symbol => symbol.Line);

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var line = LineOf(declaration);
                if (!typeSymbolsByLine.TryGetValue(line, out var symbol))
                {
                    continue;
                }

                typeDrafts.Add(new TypeMetricDraft(
                    symbol,
                    LinesOfCode(declaration),
                    declaration is TypeDeclarationSyntax typeDeclaration
                        ? typeDeclaration.Members.OfType<ConstructorDeclarationSyntax>().Count()
                        : 0));
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var line = LineOf(method);
                var symbol = fileSymbols.FirstOrDefault(candidate =>
                    candidate.Line == line
                    && candidate.Name.Equals(method.Identifier.Text, StringComparison.Ordinal)
                    && candidate.Kind is "method" or "test");
                var ownerDeclaration = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if (symbol is null || ownerDeclaration is null
                                   || !typeSymbolsByLine.TryGetValue(LineOf(ownerDeclaration), out var owner))
                {
                    continue;
                }

                var containingType = owner.SemanticName
                                     ?? string.Join('.', new[] { owner.Namespace, owner.ContainingType, owner.Name }
                                         .Where(part => !string.IsNullOrWhiteSpace(part)));
                methods.Add(new CodeMethodMetric
                {
                    SymbolIdentity = symbol.Identity,
                    Name = symbol.Name,
                    FullName = symbol.SemanticName ?? $"{containingType}.{symbol.Name}",
                    ContainingType = containingType,
                    ReturnType = method.ReturnType.ToString(),
                    Project = symbol.Project,
                    File = symbol.File,
                    Line = symbol.Line,
                    LinesOfCode = LinesOfCode(method),
                    ParameterCount = method.ParameterList.Parameters.Count,
                    CyclomaticComplexity = CyclomaticComplexity(method),
                    IsAsync = method.Modifiers.Any(SyntaxKind.AsyncKeyword)
                });
                methodOwners[symbol.Identity] = owner.Identity;
                if (method.Modifiers.Any(SyntaxKind.PublicKeyword))
                {
                    publicMethods.Add(symbol.Identity);
                }
            }
        }

        var orderedMethods = methods
            .OrderBy(method => method.File, StringComparer.Ordinal)
            .ThenBy(method => method.Line)
            .ToArray();
        var types = typeDrafts.Select(draft =>
            {
                var ownedMethods = orderedMethods
                    .Where(method => methodOwners.TryGetValue(method.SymbolIdentity, out var owner)
                                     && owner == draft.Symbol.Identity)
                    .ToArray();
                var sourceSymbols = ownedMethods.Select(method => method.SymbolIdentity)
                    .Append(draft.Symbol.Identity)
                    .ToHashSet(StringComparer.Ordinal);
                var dependencies = graph.Dependencies.Symbols
                    .Where(reference => sourceSymbols.Contains(reference.SourceSymbol)
                                        && symbolByIdentity.TryGetValue(reference.TargetSymbol, out var target)
                                        && target.Kind is "class" or "record" or "struct" or "interface" or "enum")
                    .Select(reference =>
                    {
                        var target = symbolByIdentity[reference.TargetSymbol];
                        return new CodeDependencyMetric(
                            target.Identity,
                            target.SemanticName ?? target.Name,
                            reference.Relationship);
                    })
                    .Distinct()
                    .OrderBy(dependency => dependency.TargetName, StringComparer.Ordinal)
                    .ThenBy(dependency => dependency.Relationship, StringComparer.Ordinal)
                    .ToArray();
                return new CodeTypeMetric
                {
                    SymbolIdentity = draft.Symbol.Identity,
                    Kind = draft.Symbol.Kind,
                    Name = draft.Symbol.Name,
                    FullName = draft.Symbol.SemanticName ?? draft.Symbol.Name,
                    Project = draft.Symbol.Project,
                    File = draft.Symbol.File,
                    Line = draft.Symbol.Line,
                    LinesOfCode = draft.LinesOfCode,
                    MethodCount = ownedMethods.Length,
                    PublicMethodCount = ownedMethods.Count(method => publicMethods.Contains(method.SymbolIdentity)),
                    ConstructorCount = draft.ConstructorCount,
                    DependencyCount = dependencies.Select(dependency => dependency.TargetSymbol)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    AverageMethodComplexity = ownedMethods.Length == 0
                        ? 0
                        : Math.Round(ownedMethods.Average(method => method.CyclomaticComplexity), 2),
                    MaximumMethodComplexity = ownedMethods.Select(method => method.CyclomaticComplexity).DefaultIfEmpty(0).Max(),
                    BaseType = draft.Symbol.BaseType,
                    Interfaces = draft.Symbol.Interfaces,
                    Dependencies = dependencies
                };
            })
            .OrderBy(type => type.File, StringComparer.Ordinal)
            .ThenBy(type => type.Line)
            .ToArray();

        return (types, orderedMethods);
    }

    private async Task<IReadOnlyDictionary<string, GitHistoryMetric>> ReadGitHistoryAsync(
        string repositoryRoot,
        int historyMonths,
        IReadOnlySet<string> scopedFiles,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["log", $"--since={historyMonths} months ago", "--numstat", "--format=@@%x09%H%x09%aN%x09%aI", "--", "."],
            repositoryRoot,
            cancellationToken);
        if (result.State != ExecutionState.Succeeded)
        {
            return new Dictionary<string, GitHistoryMetric>();
        }

        var metrics = new Dictionary<string, GitHistoryMetric>(StringComparer.OrdinalIgnoreCase);
        string? commit = null;
        string? contributor = null;
        DateTimeOffset? committedAt = null;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("@@\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t');
                commit = parts.ElementAtOrDefault(1);
                contributor = parts.ElementAtOrDefault(2);
                committedAt = DateTimeOffset.TryParse(
                    parts.ElementAtOrDefault(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedDate)
                    ? parsedDate
                    : null;
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 3 || commit is null)
            {
                continue;
            }

            var file = fields[^1].Replace('\\', '/');
            if (!scopedFiles.Contains(file))
            {
                continue;
            }

            if (!metrics.TryGetValue(file, out var metric))
            {
                metric = new GitHistoryMetric();
                metrics[file] = metric;
            }

            metric.Commits.Add(commit);
            if (!string.IsNullOrWhiteSpace(contributor)) metric.Contributors.Add(contributor);
            if (committedAt is not null && (metric.LastModifiedUtc is null || committedAt > metric.LastModifiedUtc))
            {
                metric.LastModifiedUtc = committedAt;
            }
            if (long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var added))
            {
                metric.Churn += added;
            }

            if (long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var deleted))
            {
                metric.Churn += deleted;
            }
        }

        return metrics;
    }

    internal static IReadOnlyDictionary<string, double> ReadCobertura(
        string repositoryRoot,
        IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return new Dictionary<string, double>();
        }

        var lineHits = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
        var fallbackRates = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Cobertura coverage file was not found.", fullPath);
            }

            foreach (var element in XDocument.Load(fullPath).Descendants()
                         .Where(element => element.Name.LocalName == "class"))
            {
                var file = element.Attribute("filename")?.Value?.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                var lines = element.Descendants()
                    .Where(child => child.Name.LocalName == "line")
                    .Select(child => new
                    {
                        Number = int.TryParse(
                            child.Attribute("number")?.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var number) ? number : (int?)null,
                        Hits = long.TryParse(
                            child.Attribute("hits")?.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var hits) ? hits : (long?)null
                    })
                    .Where(line => line.Number is not null && line.Hits is not null)
                    .ToArray();
                if (lines.Length > 0)
                {
                    if (!lineHits.TryGetValue(file, out var hitsByLine))
                    {
                        hitsByLine = new Dictionary<int, long>();
                        lineHits[file] = hitsByLine;
                    }

                    foreach (var line in lines)
                    {
                        hitsByLine[line.Number!.Value] = Math.Max(
                            hitsByLine.GetValueOrDefault(line.Number.Value),
                            line.Hits!.Value);
                    }
                }
                else if (double.TryParse(
                             element.Attribute("line-rate")?.Value,
                             NumberStyles.Float,
                             CultureInfo.InvariantCulture,
                             out var rate))
                {
                    if (!fallbackRates.TryGetValue(file, out var rates))
                    {
                        rates = [];
                        fallbackRates[file] = rates;
                    }

                    rates.Add(Math.Clamp(rate * 100d, 0d, 100d));
                }
            }
        }

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, hitsByLine) in lineHits)
        {
            result[file] = hitsByLine.Count == 0
                ? 0d
                : hitsByLine.Count(pair => pair.Value > 0) * 100d / hitsByLine.Count;
        }

        foreach (var (file, rates) in fallbackRates.Where(pair => !result.ContainsKey(pair.Key)))
        {
            result[file] = rates.Average();
        }

        return result;
    }

    private static double? TryFindCoverage(IReadOnlyDictionary<string, double> coverage, string file)
    {
        if (coverage.TryGetValue(file, out var exact))
        {
            return exact;
        }

        return coverage
            .Where(pair => file.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase)
                           || pair.Key.EndsWith(file, StringComparison.OrdinalIgnoreCase))
            .Select(pair => (double?)pair.Value)
            .FirstOrDefault();
    }

    private static IEnumerable<SymbolRecord> SelectSymbols(
        RepositoryGraph graph,
        ScopeSelection selection,
        IReadOnlyList<FileHotspot> hotspots,
        RepositoryContextOptions options)
    {
        var files = options.Purpose == ContextPurpose.Risk
            ? hotspots.Select(hotspot => hotspot.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : selection.Files;
        return graph.Symbols.Symbols
            .Where(symbol => files.Count == 0
                             ? selection.Projects.Contains(symbol.Project)
                             : files.Contains(symbol.File))
            .Where(symbol => options.Purpose != ContextPurpose.Architecture
                             || symbol.Kind is "class" or "record" or "struct" or "interface" or "enum")
            .OrderBy(symbol => symbol.File, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Line)
            .Take(options.MaxSymbols);
    }

    private static IEnumerable<TypeDefinitionRecord> SelectTypeDefinitions(
        RepositoryGraph graph,
        ScopeSelection selection,
        IReadOnlyList<FileHotspot> hotspots,
        RepositoryContextOptions options)
    {
        var files = options.Purpose == ContextPurpose.Risk
            ? hotspots.Select(hotspot => hotspot.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : selection.Files;
        return graph.Symbols.TypeDefinitions
            .Where(definition => files.Count == 0
                ? selection.Projects.Contains(definition.Project)
                : definition.Declarations.Any(declaration => files.Contains(declaration.File)))
            .OrderBy(definition => definition.Declarations[0].File, StringComparer.Ordinal)
            .ThenBy(definition => definition.Declarations[0].Line)
            .ThenBy(definition => definition.SymbolIdentity, StringComparer.Ordinal)
            .Take(options.MaxSymbols);
    }

    private static int CyclomaticComplexity(BaseMethodDeclarationSyntax method) =>
        1 + method.DescendantNodes().Count(node => node is
            IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
            or DoStatementSyntax or CaseSwitchLabelSyntax or CatchClauseSyntax or ConditionalExpressionSyntax
            or SwitchExpressionArmSyntax
            || node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.LogicalAndExpression)
            || node is BinaryExpressionSyntax logicalOr && logicalOr.IsKind(SyntaxKind.LogicalOrExpression)
            || node is BinaryExpressionSyntax coalesce && coalesce.IsKind(SyntaxKind.CoalesceExpression));

    private static int LinesOfCode(SyntaxNode node)
    {
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string NormalizeTarget(string repositoryRoot, string target)
    {
        if (!Path.IsPathRooted(target))
        {
            return ProjectOwnershipResolver.NormalizePath(target);
        }

        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(target));
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The target must be within the repository.", nameof(target));
        }

        return ProjectOwnershipResolver.NormalizePath(relative);
    }

    private static string NormalizeDiagnosticPath(string repositoryRoot, string path) =>
        Path.IsPathRooted(path)
            ? ProjectOwnershipResolver.NormalizePath(Path.GetRelativePath(repositoryRoot, path))
            : ProjectOwnershipResolver.NormalizePath(path);

    private static void AppendList(StringBuilder builder, string heading, IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        builder.AppendLine();
        builder.AppendLine($"## {heading}");
        builder.AppendLine();
        if (items.Length == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- {item}");
        }
    }

    private static void AppendSymbols(StringBuilder builder, IReadOnlyList<SymbolRecord> symbols) =>
        AppendList(builder, "Declarations", symbols.Select(symbol =>
            $"{symbol.Kind} `{symbol.SemanticName ?? symbol.Name}` (`{symbol.File}:{symbol.Line}`)"));

    private static void AppendTypeDefinitions(
        StringBuilder builder,
        IReadOnlyList<TypeDefinitionRecord> definitions)
    {
        if (definitions.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Type details");
        foreach (var definition in definitions)
        {
            builder.AppendLine();
            var modifiers = definition.Modifiers.Count == 0
                ? string.Empty
                : $" {string.Join(' ', definition.Modifiers)}";
            builder.AppendLine($"- {definition.Accessibility}{modifiers} {definition.Kind} `{definition.FullName}`");
            var relationships = new[] { definition.BaseType }
                .Where(type => type is not null)
                .Cast<string>()
                .Concat(definition.Interfaces)
                .ToArray();
            if (relationships.Length > 0)
            {
                builder.AppendLine($"  - Base/interfaces: {string.Join(", ", relationships.Select(type => $"`{type}`"))}");
            }

            if (definition.TypeParameters.Count > 0)
            {
                builder.AppendLine($"  - Type parameters: {string.Join(", ", definition.TypeParameters.Select(FormatTypeParameter))}");
            }

            if (definition.Attributes.Count > 0)
            {
                builder.AppendLine($"  - Attributes: {string.Join(", ", definition.Attributes.Select(attribute => $"`{attribute.TypeName}`"))}");
            }

            if (definition.Members.Count > 0)
            {
                builder.AppendLine($"  - Members: {string.Join("; ", definition.Members.Select(FormatMember))}");
            }
        }
    }

    private static string FormatMember(MemberDefinitionRecord member)
    {
        var modifiers = member.Modifiers.Count == 0
            ? string.Empty
            : $" {string.Join(' ', member.Modifiers)}";
        var parameters = member.Parameters.Count == 0
            ? member.Kind is "method" or "constructor" or "destructor" or "operator" or "conversion-operator"
                ? "()"
                : string.Empty
            : $"({string.Join(", ", member.Parameters.Select(parameter =>
                $"{(string.IsNullOrEmpty(parameter.RefKind) ? string.Empty : $"{parameter.RefKind} ")}{parameter.TypeName} {parameter.Name}"))})";
        var declaredType = member.DeclaredType is null ? string.Empty : $"{member.DeclaredType} ";
        var accessors = member.Accessors.Count == 0
            ? string.Empty
            : $" {{ {string.Join("; ", member.Accessors)}; }}";
        return $"`{member.Accessibility}{modifiers} {declaredType}{member.Name}{parameters}{accessors}`";
    }

    private static string FormatTypeParameter(TypeParameterDefinitionRecord parameter) =>
        parameter.Constraints.Count == 0
            ? $"`{parameter.Name}`"
            : $"`{parameter.Name} : {string.Join(", ", parameter.Constraints)}`";

    private static string ChangeLabel(GitChangeProvenance provenance) => provenance switch
    {
        GitChangeProvenance.Committed => "committed",
        GitChangeProvenance.WorkingTree => "working tree",
        GitChangeProvenance.Both => "committed + working tree",
        _ => provenance.ToString()
    };

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4d);

    private static void Validate(RepositoryContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxHotspots < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxHotspots));
        if (options.MaxSymbols < 1) throw new ArgumentOutOfRangeException(nameof(options.MaxSymbols));
        if (options.GitHistoryMonths < 1) throw new ArgumentOutOfRangeException(nameof(options.GitHistoryMonths));
    }

    private sealed record ScopeSelection(HashSet<string> Projects, HashSet<string> Files);

    private sealed class GitHistoryMetric
    {
        public HashSet<string> Commits { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Contributors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CommitCount => Commits.Count;
        public long Churn { get; set; }
        public DateTimeOffset? LastModifiedUtc { get; set; }
    }

    private sealed record TypeMetricDraft(SymbolRecord Symbol, int LinesOfCode, int ConstructorCount);
}
