using System.Text.Json;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class ProjectIndexerTests
{
    [TestMethod]
    public async Task ProjectIndexer_UsesSolutionScopeAndTransitiveNonCSharpReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-solution-scope-{Guid.NewGuid():N}");
        try
        {
            await WriteFileAsync(
                Path.Combine(root, "Scope.sln"),
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App.Tests\", \"tests\\App.Tests\\App.Tests.csproj\", \"{11111111-1111-1111-1111-111111111111}\"");
            await WriteFileAsync(
                Path.Combine(root, "tests", "App.Tests", "App.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"../../src/Library/Library.fsproj\" /></ItemGroup></Project>");
            await WriteFileAsync(
                Path.Combine(root, "tests", "App.Tests", "LibraryTests.cs"),
                "public class LibraryTests;");
            await WriteFileAsync(
                Path.Combine(root, "src", "Library", "Library.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup></Project>");
            await WriteFileAsync(
                Path.Combine(root, "src", "Library", "Library.fs"),
                "module Library");
            await WriteFileAsync(
                Path.Combine(root, "excluded", "Excluded.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var repository = await new ProjectIndexer(new StubProcessRunner("{}")).BuildAsync(
                root,
                new DevContextConfig { Solution = "Scope.sln" },
                CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { "src/Library/Library.fsproj", "tests/App.Tests/App.Tests.csproj" },
                repository.Projects.Select(project => project.Path).ToArray());
            Assert.IsFalse(repository.Projects.Any(project => project.Path == "excluded/Excluded.csproj"));
            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(
                root,
                repository,
                new IndexingConfig { ExecuteSourceGenerators = false },
                CancellationToken.None);
            var fsharpCompleteness = symbols.CompilationCompleteness.Single(record =>
                record.Project == "src/Library/Library.fsproj");
            Assert.AreEqual(AnalysisCompletenessState.Partial, fsharpCompleteness.State);
            StringAssert.Contains(fsharpCompleteness.Gaps.Single(), "F# project ownership");
            var owner = ProjectOwnershipResolver.Explain("src/Library/Library.fs", repository.Projects).Single();
            Assert.AreEqual("src/Library/Library.fsproj", owner.ProjectPath);

            var graph = new RepositoryGraph(repository, symbols, dependencies, "hash", false);
            var affected = AffectedCalculator.Calculate(
                graph,
                new GitChangeSet(
                    "base",
                    "head",
                    GitComparisonState.Comparable,
                    [new GitFileChange("src/Library/Library.fs", GitChangeProvenance.Committed)]));
            CollectionAssert.Contains(affected.Projects.ToArray(), "src/Library/Library.fsproj");
            CollectionAssert.Contains(affected.Projects.ToArray(), "tests/App.Tests/App.Tests.csproj");
            CollectionAssert.Contains(affected.Tests.ToArray(), "tests/App.Tests/App.Tests.csproj");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_SupportsSlnxAndSlnfScope()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-solution-formats-{Guid.NewGuid():N}");
        try
        {
            await WriteFileAsync(
                Path.Combine(root, "src", "App", "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await WriteFileAsync(
                Path.Combine(root, "excluded", "Excluded.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await WriteFileAsync(
                Path.Combine(root, "Scope.slnx"),
                "<Solution><Folder Name=\"/src/\"><Project Path=\"src/App/App.csproj\" /></Folder></Solution>");
            await WriteFileAsync(
                Path.Combine(root, "Scope.slnf"),
                "{\"solution\":{\"path\":\"Scope.sln\",\"projects\":[\"src/App/App.csproj\"]}}");
            await WriteFileAsync(
                Path.Combine(root, "Scope.sln"),
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"src\\App\\App.csproj\", \"{11111111-1111-1111-1111-111111111111}\"");

            foreach (var solution in new[] { "Scope.slnx", "Scope.slnf" })
            {
                var repository = await new ProjectIndexer(new StubProcessRunner("{}")).BuildAsync(
                    root,
                    new DevContextConfig { Solution = solution },
                    CancellationToken.None);

                Assert.HasCount(1, repository.Projects, solution);
                Assert.AreEqual("src/App/App.csproj", repository.Projects.Single().Path, solution);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_AppliesConfiguredProjectExcludes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-project-exclude-{Guid.NewGuid():N}");
        try
        {
            await WriteFileAsync(Path.Combine(root, "src", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await WriteFileAsync(Path.Combine(root, "artifacts", "Consumer.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var evaluation = JsonSerializer.Serialize(new
            {
                Properties = new Dictionary<string, string> { ["AssemblyName"] = "App" },
                Items = new Dictionary<string, object>()
            });
            var runner = new StubProcessRunner(evaluation, "{}");

            var repository = await new ProjectIndexer(runner).BuildAsync(
                root,
                new DevContextConfig
                {
                    Indexing = new IndexingConfig
                    {
                        RespectGitignore = false,
                        Exclude = ["artifacts/**"]
                    }
                },
                CancellationToken.None);

            Assert.HasCount(1, repository.Projects);
            Assert.AreEqual("src/App.csproj", repository.Projects.Single().Path);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_BoundsParallelProjectEvaluation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-project-parallelism-{Guid.NewGuid():N}");
        try
        {
            for (var index = 0; index < 4; index++)
            {
                await WriteFileAsync(
                    Path.Combine(root, $"Project{index}", $"Project{index}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            }

            var runner = new DelayedProcessRunner();
            var repository = await new ProjectIndexer(runner).BuildAsync(
                root,
                new DevContextConfig
                {
                    Indexing = new IndexingConfig
                    {
                        ExecuteSourceGenerators = false,
                        MaxParallelism = 2
                    }
                },
                CancellationToken.None);

            Assert.HasCount(4, repository.Projects);
            Assert.AreEqual(2, runner.MaxConcurrency);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_EvaluatesBlazorRazorComponents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-blazor-{Guid.NewGuid():N}");
        var componentPath = Path.Combine(root, "Components", "Counter.razor");

        try
        {
            await WriteFileAsync(
                Path.Combine(root, "BlazorApp.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await WriteFileAsync(componentPath, "<p>Counter</p>");

            var repository = await new ProjectIndexer(new ProcessRunner()).BuildAsync(
                root,
                new DevContextConfig(),
                CancellationToken.None);

            CollectionAssert.Contains(
                repository.Projects.Single().ProjectFiles.ToArray(),
                "Components/Counter.razor");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_EvaluatesWpfApplicationAndPageItemsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Inconclusive, not a silent return. WPF evaluation needs the Windows desktop SDK, so
            // this cannot run on the Linux and macOS legs — but returning quietly made those legs
            // report a full pass for a test that did nothing, which is the exact failure mode this
            // project refuses to ship in its own output.
            Assert.Inconclusive("WPF project evaluation requires the Windows desktop SDK.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"dev-context-wpf-{Guid.NewGuid():N}");

        try
        {
            await WriteFileAsync(
                Path.Combine(root, "WpfApp.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
            await WriteFileAsync(
                Path.Combine(root, "App.xaml"),
                "<Application xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");
            await WriteFileAsync(
                Path.Combine(root, "Views", "MainWindow.xaml"),
                "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");

            var runner = new CapturingProcessRunner(new ProcessRunner());
            var repository = await new ProjectIndexer(runner).BuildAsync(
                root,
                new DevContextConfig(),
                CancellationToken.None);
            var projectFiles = repository.Projects.Single().ProjectFiles.ToArray();

            var evaluationResult = runner.Results[0];
            if (evaluationResult is { State: ExecutionState.Failed } restrictedResult
                && restrictedResult.StandardError.Contains("Access to the path", StringComparison.OrdinalIgnoreCase))
            {
                // A sandbox that denies the SDK a path it needs is an environment limitation rather
                // than a product failure, but it is still a check that did not happen.
                Assert.Inconclusive(
                    $"MSBuild evaluation was denied filesystem access: {restrictedResult.StandardError}");
            }

            Assert.AreEqual(
                ExecutionState.Succeeded,
                evaluationResult.State,
                evaluationResult.StandardError);
            var projectFileList = string.Join(", ", projectFiles);
            CollectionAssert.Contains(projectFiles, "App.xaml", $"Evaluated project files: {projectFileList}");
            CollectionAssert.Contains(
                projectFiles,
                "Views/MainWindow.xaml",
                $"Evaluated project files: {projectFileList}");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_CapturesBlazorMauiWpfItemsAndRepositoryImports()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-projects-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(projectDirectory);

        var projectPath = Path.Combine(projectDirectory, "App.csproj");
        var importPath = Path.Combine(root, "Directory.Build.props");
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Compile"] = Path.Combine(projectDirectory, "App.xaml.cs"),
            ["RazorComponent"] = Path.Combine(projectDirectory, "Components", "Dashboard.razor"),
            ["MauiXaml"] = Path.Combine(projectDirectory, "App.xaml"),
            ["Page"] = Path.Combine(projectDirectory, "Views", "MainWindow.xaml"),
            ["ApplicationDefinition"] = Path.Combine(projectDirectory, "WpfApp.xaml"),
            ["Content"] = Path.Combine(projectDirectory, "wwwroot", "site.css"),
            ["EmbeddedResource"] = Path.Combine(projectDirectory, "Resources", "Labels.resx"),
            ["MauiImage"] = Path.Combine(projectDirectory, "Resources", "Images", "icon.svg")
        };

        try
        {
            await WriteFileAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await WriteFileAsync(importPath, "<Project />");
            foreach (var file in files.Values)
            {
                await WriteFileAsync(file, string.Empty);
            }

            var output = JsonSerializer.Serialize(new
            {
                Properties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net10.0",
                    ["Nullable"] = "enable",
                    ["LangVersion"] = "14.0",
                    ["AssemblyName"] = "App",
                    ["MSBuildAllProjects"] = importPath
                },
                Items = files.ToDictionary(
                    pair => pair.Key,
                    pair => new[]
                    {
                        new Dictionary<string, string>
                        {
                            ["Identity"] = Path.GetRelativePath(projectDirectory, pair.Value),
                            ["FullPath"] = pair.Value
                        }
                    },
                    StringComparer.Ordinal)
            });
            var runner = new StubProcessRunner(output);

            var repository = await new ProjectIndexer(runner).BuildAsync(
                root,
                new DevContextConfig(),
                CancellationToken.None);

            var project = repository.Projects.Single();
            var itemArgument = runner.Calls[0]
                .Single(argument => argument.StartsWith("-getItem:", StringComparison.Ordinal));
            foreach (var itemName in files.Keys)
            {
                StringAssert.Contains(itemArgument, itemName);
            }

            var expectedFiles = files.Values
                .Append(importPath)
                .Append(projectPath)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .ToArray();
            CollectionAssert.IsSubsetOf(expectedFiles, project.ProjectFiles.ToArray());
            CollectionAssert.AreEqual(new[] { "src/App/App.xaml.cs" }, project.SourceFiles.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_CapturesResolvedMetadataAndAnalyzerReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-references-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(root, "App.csproj");
        var metadataPath = typeof(object).Assembly.Location;
        var analyzerPath = typeof(ProjectIndexerTests).Assembly.Location;

        try
        {
            await WriteFileAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var evaluation = JsonSerializer.Serialize(new
            {
                Properties = new Dictionary<string, string>
                {
                    ["TargetFramework"] = "net10.0",
                    ["AssemblyName"] = "App"
                },
                Items = new Dictionary<string, object>
                {
                    ["Using"] = new[]
                    {
                        new Dictionary<string, string>
                        {
                            ["Identity"] = "System.Threading.Tasks"
                        }
                    }
                }
            });
            var resolvedReferences = JsonSerializer.Serialize(new
            {
                Items = new Dictionary<string, object>
                {
                    ["ReferencePath"] = new[]
                    {
                        new Dictionary<string, string>
                        {
                            ["Identity"] = metadataPath,
                            ["FullPath"] = metadataPath,
                            ["ReferenceSourceTarget"] = "ResolveAssemblyReference",
                            ["NuGetPackageId"] = "System.Runtime",
                            ["NuGetPackageVersion"] = "10.0.0",
                            ["FrameworkReferenceName"] = "Microsoft.NETCore.App"
                        }
                    },
                    ["Analyzer"] = new[]
                    {
                        new Dictionary<string, string>
                        {
                            ["Identity"] = analyzerPath,
                            ["FullPath"] = analyzerPath
                        }
                    }
                }
            });
            var runner = new StubProcessRunner(evaluation, resolvedReferences);

            var repository = await new ProjectIndexer(runner).BuildAsync(
                root,
                new DevContextConfig(),
                CancellationToken.None);

            var project = repository.Projects.Single();
            Assert.AreEqual(ExecutionState.Succeeded, project.ReferenceResolutionState);
            var reference = project.MetadataReferences.Single();
            Assert.AreEqual(metadataPath, reference.Path);
            Assert.AreEqual("System.Runtime", reference.PackageName);
            Assert.AreEqual("10.0.0", reference.PackageVersion);
            Assert.AreEqual("Microsoft.NETCore.App", reference.FrameworkReference);
            CollectionAssert.AreEqual(new[] { analyzerPath }, project.AnalyzerReferences.ToArray());
            Assert.AreEqual("System.Threading.Tasks", project.GlobalUsings.Single().Name);
            Assert.HasCount(2, runner.Calls);
            CollectionAssert.Contains(runner.Calls[1].ToArray(), "-target:ResolveReferences");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectIndexer_ResolvesReferencesIndependentlyForEveryTargetFramework()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-multitarget-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(root, "App.csproj");
        try
        {
            await WriteFileAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var evaluation = JsonSerializer.Serialize(new
            {
                Properties = new Dictionary<string, string>
                {
                    ["TargetFrameworks"] = "net8.0;net10.0",
                    ["AssemblyName"] = "App"
                },
                Items = new Dictionary<string, object>()
            });
            var net8 = ResolvedReferenceOutput(typeof(object).Assembly.Location, "Net8.Package");
            var net10 = ResolvedReferenceOutput(typeof(string).Assembly.Location, "Net10.Package");
            var runner = new StubProcessRunner(evaluation, net8, net10);

            var repository = await new ProjectIndexer(runner).BuildAsync(
                root,
                new DevContextConfig(),
                CancellationToken.None);

            var project = repository.Projects.Single();
            Assert.HasCount(2, project.TargetFrameworkAnalyses);
            Assert.AreEqual("Net8.Package", project.TargetFrameworkAnalyses[0].MetadataReferences.Single().PackageName);
            Assert.AreEqual("Net10.Package", project.TargetFrameworkAnalyses[1].MetadataReferences.Single().PackageName);
            Assert.IsTrue(runner.Calls.Any(call => call.Contains("-property:TargetFramework=net8.0")));
            Assert.IsTrue(runner.Calls.Any(call => call.Contains("-property:TargetFramework=net10.0")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string ResolvedReferenceOutput(string path, string package) => JsonSerializer.Serialize(new
    {
        Items = new Dictionary<string, object>
        {
            ["ReferencePath"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["Identity"] = path,
                    ["FullPath"] = path,
                    ["NuGetPackageId"] = package
                }
            }
        }
    });

    private static async Task WriteFileAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    private sealed class StubProcessRunner(params string[] outputs) : IProcessRunner
    {
        private readonly object gate = new();

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            string output;
            lock (gate)
            {
                Calls.Add(arguments);
                output = outputs[Math.Min(Calls.Count - 1, outputs.Length - 1)];
            }

            return Task.FromResult(new ProcessResult(
                ExecutionState.Succeeded,
                0,
                output,
                string.Empty,
                1,
                "dotnet msbuild"));
        }
    }

    private sealed class DelayedProcessRunner : IProcessRunner
    {
        private int active;
        private int maxConcurrency;

        public int MaxConcurrency => Volatile.Read(ref maxConcurrency);

        public async Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var concurrency = Interlocked.Increment(ref active);
            UpdateMaximum(concurrency);
            try
            {
                await Task.Delay(75, cancellationToken);
                return new ProcessResult(
                    ExecutionState.Failed,
                    1,
                    string.Empty,
                    "deliberate fallback",
                    75,
                    "dotnet msbuild");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            var current = Volatile.Read(ref maxConcurrency);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref maxConcurrency, candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class CapturingProcessRunner(IProcessRunner inner) : IProcessRunner
    {
        private readonly object gate = new();

        public List<ProcessResult> Results { get; } = [];

        public async Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(executable, arguments, workingDirectory, cancellationToken);
            lock (gate)
            {
                Results.Add(result);
            }

            return result;
        }
    }
}
