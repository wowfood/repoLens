using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace DevContext.Tests;

[TestClass]
public sealed class RepositoryAndSymbolTests
{
    [TestMethod]
    public void RepositoryLocator_FindsGitRootFromNestedDirectory()
    {
        var root = CreateTemporaryDirectory();
        var nested = Path.Combine(root, "src", "feature");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(nested);

        try
        {
            Assert.AreEqual(root, RepositoryLocator.FindRoot(nested));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_UsesRoslynForTypesRelationshipsAndTests()
    {
        var root = CreateTemporaryDirectory();
        var projectDirectory = Path.Combine(root, "Sample.Tests");
        Directory.CreateDirectory(projectDirectory);
        var sourcePath = Path.Combine(projectDirectory, "ExampleTests.cs");
        await File.WriteAllTextAsync(sourcePath,
            """
            namespace Sample;
            interface IMarker { }
            class BaseType { }
            class ExampleTests : BaseType, IMarker
            {
                [TestMethod]
                public void Works() { }
            }
            """);

        try
        {
            var repository = new RepositoryIndex
            {
                Solution = "Sample.sln",
                Projects =
                [
                    new ProjectRecord(
                        "Sample.Tests",
                        "Sample.Tests/Sample.Tests.csproj",
                        true,
                        ["net10.0"],
                        "enable",
                        "14.0",
                        new CompilerSettingsRecord("Library", true, null, null, "latest", null, false, false),
                        [],
                        [],
                        ["Sample.Tests/ExampleTests.cs"])
                ]
            };

            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);
            var type = symbols.Symbols.Single(symbol => symbol.Name == "ExampleTests");
            var test = symbols.Symbols.Single(symbol => symbol.Name == "Works");

            Assert.AreEqual("BaseType", type.BaseType);
            CollectionAssert.Contains(type.Interfaces.ToArray(), "IMarker");
            Assert.AreEqual("test", test.Kind);
            Assert.IsTrue(dependencies.Types.Any(dependency =>
                dependency.Symbol == type.Identity && dependency.RelatedType == "IMarker"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_UsesEvaluatedMetadataReferencesAndReportsCompleteCompilation()
    {
        var root = CreateTemporaryDirectory();
        var sourceDirectory = Path.Combine(root, "src");
        Directory.CreateDirectory(sourceDirectory);
        var externalAssembly = Path.Combine(root, "External.Contracts.dll");
        var externalCompilation = CSharpCompilation.Create(
            "External.Contracts",
            [CSharpSyntaxTree.ParseText("namespace External; public sealed class Marker { }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        await using (var stream = File.Create(externalAssembly))
        {
            var emitted = externalCompilation.Emit(stream);
            Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "Consumer.cs"),
            "namespace Sample; public sealed class Consumer { public External.Marker Create() => new(); }");

        try
        {
            var project = Project(
                "Sample",
                "src/Sample.csproj",
                false,
                [],
                ["src/Consumer.cs"]) with
            {
                MetadataReferences =
                [
                    new ResolvedReferenceRecord(typeof(object).Assembly.Location, "FrameworkFile", null, null, null),
                    new ResolvedReferenceRecord(externalAssembly, "ResolveAssemblyReference", "External.Contracts", "1.0.0", null)
                ],
                ReferenceResolutionState = ExecutionState.Succeeded
            };
            var repository = new RepositoryIndex
            {
                Solution = "Sample.sln",
                Projects = [project]
            };

            var (symbols, _) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);

            var completeness = symbols.CompilationCompleteness.Single();
            Assert.AreEqual(AnalysisCompletenessState.Complete, completeness.State);
            Assert.AreEqual(2, completeness.ResolvedMetadataReferences);
            Assert.AreEqual(0, completeness.FailedMetadataReferences);
            Assert.AreEqual(0, completeness.CompilationErrors);
            Assert.IsEmpty(completeness.Gaps);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_UsesEvaluatedGlobalUsingsWithoutGeneratedObjFiles()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Worker.cs"),
            "namespace Sample; public sealed class Worker { public Task RunAsync() => Task.CompletedTask; }");

        try
        {
            var project = Project(
                "Sample",
                "src/Sample.csproj",
                false,
                [],
                ["src/Worker.cs"]) with
            {
                GlobalUsings = [new GlobalUsingRecord("System.Threading.Tasks", false, null)],
                ReferenceResolutionState = ExecutionState.Succeeded
            };
            var repository = new RepositoryIndex
            {
                Solution = "Sample.sln",
                Projects = [project]
            };

            var (symbols, _) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);

            var completeness = symbols.CompilationCompleteness.Single();
            Assert.AreEqual(AnalysisCompletenessState.Complete, completeness.State);
            Assert.AreEqual(0, completeness.CompilationErrors);
            Assert.AreEqual(1, completeness.ExpectedSourceFiles);
            Assert.AreEqual(1, completeness.LoadedSourceFiles);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_ReportsGeneratedMarkupGapsForBlazorWpfAndMaui()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var project = Project("App", "src/App.csproj", false, [], []) with
            {
                Items =
                [
                    new ProjectItemRecord("RazorComponent", "src/Dashboard.razor"),
                    new ProjectItemRecord("Page", "src/MainWindow.xaml"),
                    new ProjectItemRecord("MauiXaml", "src/App.xaml")
                ],
                ReferenceResolutionState = ExecutionState.Succeeded
            };
            var repository = new RepositoryIndex
            {
                Solution = "App.sln",
                Projects = [project]
            };

            var (symbols, _) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);

            var completeness = symbols.CompilationCompleteness.Single();
            Assert.AreEqual(AnalysisCompletenessState.Partial, completeness.State);
            var gap = completeness.Gaps.Single();
            StringAssert.Contains(gap, "RazorComponent");
            StringAssert.Contains(gap, "Page");
            StringAssert.Contains(gap, "MauiXaml");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_ExecutesSourceGeneratorsAndIndexesGeneratedMembers()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Host.cs"),
            "namespace Sample; public sealed partial class Host { }");

        try
        {
            var project = Project("Sample", "src/Sample.csproj", false, [], ["src/Host.cs"]) with
            {
                AnalyzerReferences = [typeof(RepoLensTestSourceGenerator).Assembly.Location],
                ReferenceResolutionState = ExecutionState.Succeeded
            };
            var repository = new RepositoryIndex { Solution = "Sample.sln", Projects = [project] };

            var (symbols, _) = await SymbolIndexer.BuildAsync(
                root,
                repository,
                new DevContext.Configuration.IndexingConfig { ExecuteSourceGenerators = true },
                CancellationToken.None);

            Assert.IsTrue(symbols.Symbols.Any(symbol => symbol.Name == "GeneratedWidget"
                                                        && symbol.File.StartsWith("generated://", StringComparison.Ordinal)));
            Assert.IsTrue(symbols.Symbols.Any(symbol => symbol.Name == "Value"
                                                        && symbol.Kind == "property"
                                                        && symbol.File.StartsWith("generated://", StringComparison.Ordinal)));
            Assert.IsTrue(symbols.Symbols.Any(symbol => symbol.Name == "FirstIncrementalWidget"));
            Assert.IsTrue(symbols.Symbols.Any(symbol => symbol.Name == "SecondIncrementalWidget"));
            Assert.HasCount(3, symbols.GeneratedSources);
            var completeness = symbols.CompilationCompleteness.Single();
            Assert.IsTrue(completeness.SourceGeneratorsExecuted);
            Assert.IsGreaterThanOrEqualTo(3, completeness.SourceGeneratorsDiscovered);
            Assert.AreEqual(3, completeness.GeneratedSourceFiles);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_IndexesRichMemberAndUiRelationships()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "ViewModels.cs"),
            """
            using System;
            namespace Sample;
            public interface IWorker { string Title { get; } void Run(); }
            public abstract class WorkerBase { protected virtual void RunCore() { } }
            public sealed class SongCard { public string Text { get; set; } = ""; }
            public sealed class SongService { }
            public sealed class MainWindow : WorkerBase, IWorker
            {
                private string _title = "Songs";
                public MainWindow() { }
                public string Title { get => _title; set => _title = value; }
                public object SaveCommand { get; } = new();
                public event Action? Changed;
                public void Run() { Console.WriteLine(Title); Changed += OnTapped; Register(OnTapped); }
                protected override void RunCore() { }
                private static void Register(Action callback) { }
                private void OnTapped() { }
                private void Refresh() { }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Dashboard.razor"),
            "@inject SongService Songs\n<SongCard Text=\"@Title\" @onclick=\"Refresh\" />");
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "MainWindow.xaml"),
            """
            <Window x:Class="Sample.MainWindow" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <local:SongCard Text="{Binding Title}" Command="{Binding SaveCommand}" Tapped="OnTapped" />
            </Window>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "MobilePage.xaml"),
            """
            <ContentPage x:Class="Sample.MainWindow" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <local:SongCard Text="{Binding Title}" Tapped="OnTapped" />
            </ContentPage>
            """);

        try
        {
            var project = Project("Sample", "src/Sample.csproj", false, [], ["src/ViewModels.cs"]) with
            {
                Items =
                [
                    new ProjectItemRecord("RazorComponent", "src/Dashboard.razor"),
                    new ProjectItemRecord("Page", "src/MainWindow.xaml"),
                    new ProjectItemRecord("MauiXaml", "src/MobilePage.xaml")
                ],
                ReferenceResolutionState = ExecutionState.Succeeded
            };
            var repository = new RepositoryIndex { Solution = "Sample.sln", Projects = [project] };
            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);

            CollectionAssert.IsSubsetOf(
                new[] { "constructor", "property", "field", "event", "method" },
                symbols.Symbols.Select(symbol => symbol.Kind).Distinct().ToArray());
            Assert.HasCount(1, symbols.Symbols.Where(symbol => symbol.Kind == "razor-component"));
            Assert.HasCount(2, symbols.Symbols.Where(symbol => symbol.Kind == "xaml-view"));
            var relationships = dependencies.Symbols.Select(reference => reference.Relationship).Distinct().ToArray();
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    "member-read", "member-write", "event-subscription", "delegate-callback", "override",
                    "interface-implementation", "dependency-injection", "component-use", "markup-binding",
                    "markup-command", "markup-event", "markup-code-behind"
                },
                relationships);
            var markupBinding = dependencies.Symbols.First(reference =>
                reference.Relationship == "markup-binding"
                && reference.Confidence == EvidenceConfidence.ConventionHeuristic);
            Assert.AreEqual("markup-convention", markupBinding.Origin);
            Assert.IsGreaterThan(0, markupBinding.EvidenceLine.GetValueOrDefault());
            Assert.IsGreaterThan(0, markupBinding.EvidenceColumn.GetValueOrDefault());
            Assert.IsTrue(dependencies.Symbols.Any(reference => reference.Relationship == "component-use"
                                                               && reference.Confidence == EvidenceConfidence.SyntaxFallback));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_IndexesLocalFunctionsWithOperationEvidence()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Worker.cs"),
            """
            namespace Sample;
            public sealed class Worker
            {
                public void Run()
                {
                    void Local() { Helper(); }
                    Local();
                }

                private void Helper() { }
            }
            """);

        try
        {
            var project = Project("Sample", "src/Sample.csproj", false, [], ["src/Worker.cs"]) with
            {
                TargetFrameworks = ["net8.0"],
                ReferenceResolutionState = ExecutionState.Succeeded
            };
            var repository = new RepositoryIndex { Solution = "Sample.sln", Projects = [project] };

            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(
                root,
                repository,
                CancellationToken.None);

            var run = symbols.Symbols.Single(symbol => symbol.Name == "Run");
            var local = symbols.Symbols.Single(symbol => symbol.Kind == "local-function" && symbol.Name == "Local");
            var helper = symbols.Symbols.Single(symbol => symbol.Name == "Helper");
            var localCall = dependencies.Symbols.Single(reference =>
                reference.SourceSymbol == run.Identity
                && reference.TargetSymbol == local.Identity
                && reference.Relationship == "method-call");
            var helperCall = dependencies.Symbols.Single(reference =>
                reference.SourceSymbol == local.Identity
                && reference.TargetSymbol == helper.Identity
                && reference.Relationship == "method-call");

            Assert.AreEqual("roslyn-operation", localCall.Origin);
            Assert.AreEqual("net8.0", localCall.TargetFramework);
            Assert.AreEqual("src/Worker.cs", localCall.EvidenceFile);
            Assert.AreEqual(7, localCall.EvidenceLine);
            Assert.IsGreaterThan(0, localCall.EvidenceColumn.GetValueOrDefault());
            Assert.AreEqual("roslyn-operation", helperCall.Origin);
            Assert.AreEqual(6, helperCall.EvidenceLine);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void DeclaredSymbolLookup_ResolvesASymbolReachedThroughADifferentCompilation()
    {
        // Each project is compiled separately and sees its references through a metadata reference,
        // so the ISymbol a referencing compilation hands back is a different instance from the one
        // the declaring compilation produced. SymbolEqualityComparer.Default does not equate the two,
        // and resolving targets by symbol identity alone therefore dropped every reference that
        // crossed a project boundary. Two independently created compilations reproduce that
        // inequality deterministically, without depending on when Roslyn chooses to retarget.
        const string source = """
            namespace Ordering;
            public sealed class OrderService
            {
                public void Place(string sku) { }
            }
            """;
        var declaring = CSharpCompilation.Create(
            "Ordering",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var referencing = CSharpCompilation.Create(
            "Ordering",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var declared = declaring.GetTypeByMetadataName("Ordering.OrderService")!;
        var seenFromElsewhere = referencing.GetTypeByMetadataName("Ordering.OrderService")!;
        Assert.IsFalse(
            SymbolEqualityComparer.Default.Equals(declared, seenFromElsewhere),
            "The two compilations must produce distinct symbols for this test to mean anything.");

        var record = new SymbolRecord(
            "identity",
            "class",
            "OrderService",
            "Ordering",
            null,
            "src/Ordering/Ordering.csproj",
            "src/Ordering/OrderService.cs",
            1,
            null,
            [])
        {
            SemanticName = "Ordering.OrderService"
        };
        var lookup = SymbolIndexer.DeclaredSymbolLookup.Create(
        [
            new Dictionary<ISymbol, SymbolRecord>(SymbolEqualityComparer.Default) { [declared] = record }
        ]);

        Assert.IsTrue(lookup.TryResolve(declared, out var direct));
        Assert.AreSame(record, direct);
        Assert.IsTrue(
            lookup.TryResolve(seenFromElsewhere, out var acrossCompilations),
            "A symbol reached through a project reference must resolve to the declaring project's record.");
        Assert.AreSame(record, acrossCompilations);

        var unrelated = referencing.GetTypeByMetadataName("System.String");
        Assert.IsFalse(
            lookup.TryResolve(unrelated!, out _),
            "A symbol from an assembly RepoLens did not index must not resolve to anything.");
    }

    [TestMethod]
    public void ReferenceQuery_ScopesAbsenceProofToEveryProjectThatCouldHoldAnInboundEdge()
    {
        // Core -> Domain -> App: an inbound edge to a Core symbol can be declared by Domain or App,
        // so the completeness of Core alone can never prove that no caller exists.
        ProjectDependency[] dependencies =
        [
            new("src/Domain/Domain.csproj", "src/Core/Core.csproj"),
            new("src/App/App.csproj", "src/Domain/Domain.csproj"),
            new("src/Unrelated/Unrelated.csproj", "src/Other/Other.csproj")
        ];
        var symbol = new SymbolRecord(
            "identity",
            "method",
            "Handle",
            "Core",
            "Handler",
            "src/Core/Core.csproj",
            "src/Core/Handler.cs",
            10,
            null,
            []);

        var inbound = SymbolReferenceQueryService.ReferenceScopeProjects(
            symbol,
            SymbolReferenceRelation.Callers,
            dependencies);
        CollectionAssert.AreEquivalent(
            new[] { "src/Core/Core.csproj", "src/Domain/Domain.csproj", "src/App/App.csproj" },
            inbound.ToArray());

        // Callees are declared by the resolved symbol itself, so only its own project matters.
        var outbound = SymbolReferenceQueryService.ReferenceScopeProjects(
            symbol,
            SymbolReferenceRelation.Callees,
            dependencies);
        CollectionAssert.AreEquivalent(new[] { "src/Core/Core.csproj" }, outbound.ToArray());

        Assert.IsEmpty(SymbolReferenceQueryService.ReferenceScopeProjects(
            null,
            SymbolReferenceRelation.Callers,
            dependencies));
    }

    [TestMethod]
    public async Task SymbolIndexer_IndexesTargetTypedNewAndPrimaryConstructorParameters()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "App.cs"),
            """
            namespace Sample;

            public class Dependency
            {
            }

            // A primary constructor declares its parameters on the type, not in a
            // ConstructorDeclarationSyntax, so this dependency used to be invisible.
            public class Service(Dependency dependency)
            {
                public Dependency Dependency { get; } = dependency;
            }

            public class Factory
            {
                // Target-typed `new()` is an ImplicitObjectCreationExpressionSyntax, which the
                // narrower ObjectCreationExpressionSyntax walk used to skip entirely.
                public Dependency Create()
                {
                    Dependency created = new();
                    return created;
                }
            }
            """);
        try
        {
            var project = Project("Sample", "src/Sample.csproj", false, [], ["src/App.cs"]);
            var repository = new RepositoryIndex { Solution = "Sample.sln", Projects = [project] };

            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);
            var byIdentity = symbols.Symbols.ToDictionary(symbol => symbol.Identity, StringComparer.Ordinal);

            bool Edge(string relationship, string source, string target) =>
                dependencies.Symbols.Any(reference =>
                    reference.Relationship == relationship
                    && byIdentity.TryGetValue(reference.SourceSymbol, out var from)
                    && byIdentity.TryGetValue(reference.TargetSymbol, out var to)
                    && from.Name == source
                    && to.Name == target);

            Assert.IsTrue(
                Edge("constructor-parameter", "Service", "Dependency"),
                "A primary constructor parameter must produce a constructor-parameter edge.");
            Assert.IsTrue(
                Edge("constructed-type", "Create", "Dependency"),
                "Target-typed new() must produce a constructed-type edge.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_ReportsCompilationCompletenessPerTargetFramework()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "App.cs"), "namespace Sample; public class App { }");
        try
        {
            var project = Project("Sample", "src/Sample.csproj", false, [], ["src/App.cs"]) with
            {
                TargetFrameworkAnalyses =
                [
                    new TargetFrameworkAnalysisRecord
                    {
                        TargetFramework = "net8.0",
                        ReferenceResolutionState = ExecutionState.Succeeded
                    },
                    new TargetFrameworkAnalysisRecord
                    {
                        TargetFramework = "net10.0",
                        ReferenceResolutionState = ExecutionState.Succeeded
                    }
                ]
            };
            project = project with { TargetFrameworks = ["net8.0", "net10.0"] };
            var repository = new RepositoryIndex { Solution = "Sample.sln", Projects = [project] };

            var (symbols, _) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { "net10.0", "net8.0" },
                symbols.CompilationCompleteness.Select(record => record.TargetFramework).ToArray());

            // Declarations and relationships are indexed from the first evaluated target framework
            // only, so every other target must say so rather than claim a complete analysis.
            var indexed = symbols.CompilationCompleteness.Single(record => record.TargetFramework == "net8.0");
            var notIndexed = symbols.CompilationCompleteness.Single(record => record.TargetFramework == "net10.0");
            Assert.AreEqual(AnalysisCompletenessState.Complete, indexed.State);
            Assert.IsEmpty(indexed.Gaps);
            Assert.AreEqual(AnalysisCompletenessState.Partial, notIndexed.State);
            Assert.IsTrue(
                notIndexed.Gaps.Any(gap =>
                    gap.Contains("indexed from target framework 'net8.0' only", StringComparison.Ordinal)),
                string.Join(" | ", notIndexed.Gaps));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SymbolIndexer_CapturesRichTypeAndMemberDefinitionsAcrossPartialDeclarations()
    {
        var root = CreateTemporaryDirectory();
        var projectDirectory = Path.Combine(root, "src");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "Repository.Core.cs"),
            """
            using System;
            using System.Diagnostics.CodeAnalysis;
            namespace Sample;

            public interface IMarker { }
            public abstract class BaseType { }
            public sealed record Result(string Name, int Count);

            [Obsolete("legacy")]
            public sealed partial class Repository<T> : BaseType, IMarker
                where T : class, new()
            {
                [MaybeNull]
                private readonly T? _value;

                public required T Item { get; init; }
                public T? Cached { get; private set; }
                public event Action<T>? Changed;

                public Repository([DisallowNull] T value) => _value = value;

                public T this[int index]
                {
                    get => _value!;
                    set { }
                }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "Repository.Queries.cs"),
            """
            using System.Threading.Tasks;
            namespace Sample;

            public sealed partial class Repository<T>
            {
                public bool TryGet(ref int count, out T? value)
                {
                    value = default;
                    return false;
                }

                public async Task<T?> GetAsync<TArg>(string? name = "default")
                    where TArg : struct
                {
                    await Task.Yield();
                    return default;
                }
            }

            public sealed class Service(Repository<string> repository)
            {
                public Repository<string> Repository => repository;
            }
            """);

        try
        {
            var repository = new RepositoryIndex
            {
                Solution = "Sample.sln",
                Projects =
                [
                    Project(
                        "Sample",
                        "src/Sample.csproj",
                        false,
                        [],
                        ["src/Repository.Core.cs", "src/Repository.Queries.cs"])
                ]
            };

            var (symbols, _) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);
            var definition = symbols.TypeDefinitions.Single(item => item.Name == "Repository");

            Assert.AreEqual("class", definition.Kind);
            Assert.AreEqual("public", definition.Accessibility);
            CollectionAssert.IsSubsetOf(new[] { "sealed", "partial" }, definition.Modifiers.ToArray());
            Assert.HasCount(2, definition.Declarations);
            Assert.IsNotNull(definition.BaseType);
            StringAssert.EndsWith(definition.BaseType, "BaseType");
            Assert.IsTrue(definition.Interfaces.Any(item => item.EndsWith("IMarker", StringComparison.Ordinal)));
            Assert.IsTrue(definition.Attributes.Any(attribute =>
                attribute.TypeName.EndsWith("ObsoleteAttribute", StringComparison.Ordinal)
                && attribute.Arguments.SequenceEqual(["\"legacy\""])));

            var typeParameter = definition.TypeParameters.Single();
            Assert.AreEqual("T", typeParameter.Name);
            CollectionAssert.AreEqual(new[] { "class", "new()" }, typeParameter.Constraints.ToArray());

            var field = definition.Members.Single(member => member.Name == "_value");
            Assert.AreEqual("field", field.Kind);
            Assert.AreEqual("private", field.Accessibility);
            CollectionAssert.Contains(field.Modifiers.ToArray(), "readonly");
            Assert.AreEqual("annotated", field.Nullability);
            Assert.IsTrue(field.Attributes.Any(attribute =>
                attribute.TypeName.EndsWith("MaybeNullAttribute", StringComparison.Ordinal)));

            var property = definition.Members.Single(member => member.Name == "Item");
            CollectionAssert.Contains(property.Modifiers.ToArray(), "required");
            CollectionAssert.AreEqual(new[] { "get", "init" }, property.Accessors.ToArray());
            CollectionAssert.AreEqual(
                new[] { "get", "private set" },
                definition.Members.Single(member => member.Name == "Cached").Accessors.ToArray());

            var constructor = definition.Members.Single(member => member.Kind == "constructor");
            var constructorParameter = constructor.Parameters.Single();
            Assert.IsTrue(constructorParameter.Attributes.Any(attribute =>
                attribute.TypeName.EndsWith("DisallowNullAttribute", StringComparison.Ordinal)));

            var indexer = definition.Members.Single(member => member.Kind == "indexer");
            Assert.AreEqual("this[]", indexer.Name);
            Assert.AreEqual("index", indexer.Parameters.Single().Name);
            CollectionAssert.AreEqual(new[] { "get", "set" }, indexer.Accessors.ToArray());

            var tryGet = definition.Members.Single(member => member.Name == "TryGet");
            CollectionAssert.AreEqual(
                new[] { "ref", "out" },
                tryGet.Parameters.Select(parameter => parameter.RefKind).ToArray());

            var getAsync = definition.Members.Single(member => member.Name == "GetAsync");
            CollectionAssert.Contains(getAsync.Modifiers.ToArray(), "async");
            Assert.IsNotNull(getAsync.DeclaredType);
            StringAssert.Contains(getAsync.DeclaredType, "T?");
            Assert.AreEqual("not-annotated", getAsync.Nullability);
            var optionalParameter = getAsync.Parameters.Single();
            Assert.IsTrue(optionalParameter.IsOptional);
            Assert.AreEqual("\"default\"", optionalParameter.DefaultValue);
            CollectionAssert.AreEqual(
                new[] { "struct" },
                getAsync.TypeParameters.Single().Constraints.ToArray());

            Assert.AreEqual(
                definition.Members.Count,
                definition.Members.Select(member => member.Identity).Distinct(StringComparer.Ordinal).Count());

            var (rebuilt, _) = await SymbolIndexer.BuildAsync(root, repository, CancellationToken.None);
            CollectionAssert.AreEqual(
                definition.Members.Select(member => member.Identity).ToArray(),
                rebuilt.TypeDefinitions.Single(item => item.Name == "Repository")
                    .Members.Select(member => member.Identity).ToArray());

            var positionalRecord = symbols.TypeDefinitions.Single(item => item.Name == "Result");
            CollectionAssert.IsSubsetOf(
                new[] { "Name", "Count" },
                positionalRecord.Members
                    .Where(member => member.Kind == "property")
                    .Select(member => member.Name)
                    .ToArray());
            CollectionAssert.AreEqual(
                new[] { "Name", "Count" },
                positionalRecord.Members.Single(member => member.Kind == "constructor")
                    .Parameters.Select(parameter => parameter.Name).ToArray());

            var primaryConstructorType = symbols.TypeDefinitions.Single(item => item.Name == "Service");
            CollectionAssert.AreEqual(
                new[] { "repository" },
                primaryConstructorType.Members.Single(member => member.Kind == "constructor")
                    .Parameters.Select(parameter => parameter.Name).ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SemanticReferences_ConnectChangedProductionMethodsToFocusedTests()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Calculator.cs"),
            "namespace Sample; public static class Calculator { public static int Add(int a, int b) => a + b; }");
        await File.WriteAllTextAsync(
            Path.Combine(root, "tests", "CalculatorTests.cs"),
            "namespace Sample.Tests; class CalculatorTests { [TestMethod] public void AddWorks() { _ = Calculator.Add(2, 2); } }");

        try
        {
            var production = Project(
                "Sample",
                "src/Sample.csproj",
                false,
                [],
                ["src/Calculator.cs"]);
            var tests = Project(
                "Sample.Tests",
                "tests/Sample.Tests.csproj",
                true,
                ["src/Sample.csproj"],
                ["tests/CalculatorTests.cs"]);
            var repository = new RepositoryIndex
            {
                Solution = "Sample.sln",
                Projects = [production, tests]
            };
            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(
                root,
                repository,
                CancellationToken.None);
            var add = symbols.Symbols.Single(symbol => symbol.Name == "Add");
            var addTest = symbols.Symbols.Single(symbol => symbol.Name == "AddWorks");

            Assert.IsTrue(dependencies.Symbols.Any(reference =>
                reference.SourceSymbol == addTest.Identity
                && reference.TargetSymbol == add.Identity
                && reference.Relationship == "method-call"));

            var baseline = CreateStatus(repository, new GitFileState("src/Calculator.cs", " ", "M", "old"));
            var currentGraph = new RepositoryGraph(
                repository,
                symbols,
                dependencies,
                "hash",
                false);
            var affected = AffectedCalculator.Calculate(
                baseline,
                symbols,
                dependencies,
                new GitSnapshot
                {
                    Branch = "main",
                    HeadCommit = "abc",
                    Files = [new GitFileState("src/Calculator.cs", " ", "M", "new")]
                },
                currentGraph);

            CollectionAssert.Contains(affected.Tests.ToArray(), "tests/Sample.Tests.csproj");
            CollectionAssert.Contains(affected.TestCases.ToArray(), "Sample.Tests.CalculatorTests.AddWorks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SemanticReferences_IncludeMemberAndSignatureTypeRelationships()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Consumer.cs"),
            """
            using System;
            using System.Threading.Tasks;
            namespace Sample;
            public sealed class Dependency { }
            public sealed class Consumer
            {
                private readonly Dependency _field;
                public Dependency Property { get; }
                public event Action<Dependency>? Changed;
                public Consumer(Dependency dependency) => _field = dependency;
                public Task<Dependency> LoadAsync(Dependency dependency) => Task.FromResult(dependency);
            }
            """);

        try
        {
            var repository = new RepositoryIndex
            {
                Solution = "Sample.sln",
                Projects = [Project("Sample", "src/Sample.csproj", false, [], ["src/Consumer.cs"])]
            };
            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(
                root,
                repository,
                CancellationToken.None);
            var dependency = symbols.Symbols.Single(symbol => symbol.Name == "Dependency");
            var consumer = symbols.Symbols.Single(symbol => symbol.Name == "Consumer");
            var load = symbols.Symbols.Single(symbol => symbol.Name == "LoadAsync");

            var consumerRelationships = dependencies.Symbols
                .Where(reference => reference.SourceSymbol == consumer.Identity
                                    && reference.TargetSymbol == dependency.Identity)
                .Select(reference => reference.Relationship)
                .ToArray();
            CollectionAssert.IsSubsetOf(
                new[] { "field-type", "property-type", "event-type", "constructor-parameter" },
                consumerRelationships);
            var methodRelationships = dependencies.Symbols
                .Where(reference => reference.SourceSymbol == load.Identity
                                    && reference.TargetSymbol == dependency.Identity)
                .Select(reference => reference.Relationship)
                .ToArray();
            CollectionAssert.IsSubsetOf(
                new[] { "parameter-type", "return-type" },
                methodRelationships);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ProjectOwnership_ReportsEvaluatedBlazorItemType()
    {
        const string path = "src/App/Components/Dashboard.razor";
        var project = Project("App", "src/App/App.csproj", false, [], ["src/App/App.cs"])
            with
        {
            ProjectFiles = [path],
            Items = [new ProjectItemRecord("RazorComponent", path)]
        };

        var owner = ProjectOwnershipResolver.Explain(path, [project]).Single();

        Assert.AreEqual("evaluated MSBuild item", owner.Reason);
        CollectionAssert.AreEqual(new[] { "RazorComponent" }, owner.ItemTypes.ToArray());
    }

    [TestMethod]
    [DataRow("src/App/Components/Dashboard.razor")]
    [DataRow("src/App/App.xaml")]
    [DataRow("src/App/Views/MainWindow.xaml")]
    [DataRow("src/App/wwwroot/site.css")]
    public void AffectedCalculator_MapsBlazorMauiWpfAndContentItemsToProjects(string changedFile)
    {
        var application = Project(
            "App",
            "src/App/App.csproj",
            false,
            [],
            ["src/App/App.xaml.cs"],
            [changedFile]);
        var tests = Project(
            "App.Tests",
            "tests/App.Tests/App.Tests.csproj",
            true,
            [application.Path],
            ["tests/App.Tests/AppTests.cs"]);
        var repository = new RepositoryIndex
        {
            Solution = "App.sln",
            Projects = [application, tests]
        };

        var affected = CalculateAffected(repository, changedFile);

        CollectionAssert.Contains(affected.Projects.ToArray(), application.Path);
        CollectionAssert.Contains(affected.Projects.ToArray(), tests.Path);
        CollectionAssert.Contains(affected.Tests.ToArray(), tests.Path);
        Assert.HasCount(0, affected.ChangedSymbols);
    }

    [TestMethod]
    public void AffectedCalculator_MapsLinkedProjectItemsOutsideProjectDirectory()
    {
        const string changedFile = "shared/Theme.xaml";
        var application = Project(
            "Desktop",
            "src/Desktop/Desktop.csproj",
            false,
            [],
            ["src/Desktop/App.xaml.cs"],
            [changedFile]);
        var repository = new RepositoryIndex
        {
            Solution = "Desktop.sln",
            Projects = [application]
        };

        var affected = CalculateAffected(repository, changedFile);

        CollectionAssert.AreEqual(new[] { application.Path }, affected.Projects.ToArray());
    }

    [TestMethod]
    public void AffectedCalculator_UsesNearestContainingProjectForUnclassifiedFiles()
    {
        const string changedFile = "src/Features/Player/Views/PlayerWindow.xaml";
        var shell = Project("Shell", "src/Shell.csproj", false, [], ["src/Shell.cs"]);
        var player = Project(
            "Player",
            "src/Features/Player/Player.csproj",
            false,
            [],
            ["src/Features/Player/Player.cs"]);
        var repository = new RepositoryIndex
        {
            Solution = "Desktop.sln",
            Projects = [shell, player]
        };

        var affected = CalculateAffected(repository, changedFile);

        CollectionAssert.AreEqual(new[] { player.Path }, affected.Projects.ToArray());
    }

    [TestMethod]
    public void AffectedCalculator_MapsSharedBuildInputsWithinTheirDirectoryScope()
    {
        const string changedFile = "src/Directory.Build.props";
        var core = Project("Core", "src/Core/Core.csproj", false, [], ["src/Core/Core.cs"]);
        var desktop = Project("Desktop", "src/Desktop/Desktop.csproj", false, [], ["src/Desktop/App.cs"]);
        var unrelatedTests = Project(
            "Other.Tests",
            "tests/Other.Tests/Other.Tests.csproj",
            true,
            [],
            ["tests/Other.Tests/OtherTests.cs"]);
        var repository = new RepositoryIndex
        {
            Solution = "Desktop.sln",
            Projects = [core, desktop, unrelatedTests]
        };

        var affected = CalculateAffected(repository, changedFile);

        CollectionAssert.AreEquivalent(new[] { core.Path, desktop.Path }, affected.Projects.ToArray());
        Assert.HasCount(0, affected.Tests);
    }

    [TestMethod]
    public void AffectedCalculator_PropagatesSharedInputsToDownstreamTestProjects()
    {
        const string changedFile = "eng/Common.props";
        var core = Project(
            "Core",
            "src/Core/Core.csproj",
            false,
            [],
            ["src/Core/Core.cs"],
            [changedFile]);
        var tests = Project(
            "Core.Tests",
            "tests/Core.Tests/Core.Tests.csproj",
            true,
            [core.Path],
            ["tests/Core.Tests/CoreTests.cs"]);
        var repository = new RepositoryIndex
        {
            Solution = "Core.sln",
            Projects = [core, tests]
        };

        var affected = CalculateAffected(repository, changedFile);

        CollectionAssert.Contains(affected.Projects.ToArray(), core.Path);
        CollectionAssert.Contains(affected.Projects.ToArray(), tests.Path);
        CollectionAssert.AreEqual(new[] { tests.Path }, affected.Tests.ToArray());
    }

    private static ProjectRecord Project(
        string name,
        string path,
        bool isTest,
        IReadOnlyList<string> references,
        IReadOnlyList<string> sources,
        IReadOnlyList<string>? projectFiles = null)
    {
        return new ProjectRecord(
            name,
            path,
            isTest,
            ["net10.0"],
            "enable",
            "14.0",
            new CompilerSettingsRecord("Library", true, null, null, "latest", null, false, false),
            [],
            references,
            sources)
        {
            AssemblyName = name,
            ProjectFiles = projectFiles ?? sources
        };
    }

    private static AffectedReport CalculateAffected(RepositoryIndex repository, string changedFile)
    {
        var baselineSymbols = new SymbolIndex { Symbols = [] };
        var dependencies = new DependencyIndex
        {
            Projects = repository.Projects
                .SelectMany(project => project.ProjectReferences.Select(reference =>
                    new ProjectDependency(project.Path, reference)))
                .ToArray(),
            Types = []
        };
        var baseline = CreateStatus(
            repository,
            new GitFileState(changedFile, " ", "M", "before"));
        var currentGraph = new RepositoryGraph(
            repository,
            baselineSymbols,
            dependencies,
            "hash",
            false);

        return AffectedCalculator.Calculate(
            baseline,
            baselineSymbols,
            dependencies,
            new GitSnapshot
            {
                Branch = "main",
                HeadCommit = "abc",
                Files = [new GitFileState(changedFile, " ", "M", "after")]
            },
            currentGraph);
    }

    private static StatusReport CreateStatus(RepositoryIndex repository, GitFileState file) => new()
    {
        Manifest = new BaselineManifest
        {
            BaselineId = "baseline",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RepositoryRoot = "C:/repo",
            Branch = "main",
            HeadCommit = "abc",
            WorkingTreeDirty = true,
            SdkVersion = "10.0.204",
            Timings = []
        },
        Git = new GitSnapshot { Branch = "main", HeadCommit = "abc", Files = [file] },
        Build = new BuildSnapshot
        {
            State = ExecutionState.Succeeded,
            ExitCode = 0,
            DurationMilliseconds = 1,
            Command = "dotnet build",
            Diagnostics = []
        },
        Tests = new TestSnapshot
        {
            State = ExecutionState.Succeeded,
            Total = 0,
            Passed = 0,
            Failed = 0,
            Skipped = 0,
            DurationMilliseconds = 1,
            Outcomes = []
        },
        Analysis = new AnalysisSnapshot
        {
            Diagnostics = [],
            DotnetFormat = new ProviderResult(ExecutionState.Skipped, 0, null),
            Qodana = new ProviderResult(ExecutionState.Skipped, 0, null)
        },
        Repository = repository
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev-context-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

#pragma warning disable RS1041, RS1042, RS1036
[Generator]
public sealed class RepoLensTestSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context) => context.AddSource(
        "GeneratedWidget.g.cs",
        SourceText.From(
            "namespace Sample; public sealed class GeneratedWidget { public string Value { get; } = \"ok\"; }",
            Encoding.UTF8));
}

[Generator]
public sealed class RepoLensFirstIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterPostInitializationOutput(output => output.AddSource(
            "FirstIncrementalWidget.g.cs",
            "namespace Generated; public sealed class FirstIncrementalWidget { }"));
}

[Generator]
public sealed class RepoLensSecondIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterPostInitializationOutput(output => output.AddSource(
            "SecondIncrementalWidget.g.cs",
            "namespace Generated; public sealed class SecondIncrementalWidget { }"));
}
#pragma warning restore RS1041, RS1042, RS1036
