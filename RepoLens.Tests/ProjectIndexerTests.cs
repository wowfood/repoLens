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
                new DevContextConfig { Solution = "BlazorApp.sln" },
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
            return;
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
                new DevContextConfig { Solution = "WpfApp.sln" },
                CancellationToken.None);
            var projectFiles = repository.Projects.Single().ProjectFiles.ToArray();

            var evaluationResult = runner.Results[0];
            if (evaluationResult is { State: ExecutionState.Failed } restrictedResult
                && restrictedResult.StandardError.Contains("Access to the path", StringComparison.OrdinalIgnoreCase))
            {
                return;
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
                new DevContextConfig { Solution = "App.sln" },
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
                new DevContextConfig { Solution = "App.sln" },
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
                new DevContextConfig { Solution = "App.sln" },
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
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            var output = outputs[Math.Min(Calls.Count - 1, outputs.Length - 1)];
            return Task.FromResult(new ProcessResult(
                ExecutionState.Succeeded,
                0,
                output,
                string.Empty,
                1,
                "dotnet msbuild"));
        }
    }

    private sealed class CapturingProcessRunner(IProcessRunner inner) : IProcessRunner
    {
        public List<ProcessResult> Results { get; } = [];

        public async Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(executable, arguments, workingDirectory, cancellationToken);
            Results.Add(result);
            return result;
        }
    }
}
