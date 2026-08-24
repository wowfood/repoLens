using DevContext.Configuration;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class RepositoryGraphCacheTests
{
    [TestMethod]
    public async Task InputHash_IgnoresGitignoredFilesButTracksIncludedSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-cache-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ProcessRunner();
            var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
            Assert.AreEqual(DevContext.Core.ExecutionState.Succeeded, git.State, git.StandardError);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "artifacts/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class First;");
            Directory.CreateDirectory(Path.Combine(root, "artifacts"));
            var ignored = Path.Combine(root, "artifacts", "generated.cs");
            await File.WriteAllTextAsync(ignored, "public class Generated;");

            var service = new RepositoryGraphService(runner, new ProjectIndexer(runner));
            var config = new DevContextConfig();
            var original = await service.ComputeInputHashAsync(root, config, CancellationToken.None);
            await File.WriteAllTextAsync(ignored, "public class ChangedGenerated;");
            var afterIgnoredChange = await service.ComputeInputHashAsync(root, config, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Second;");
            var afterSourceChange = await service.ComputeInputHashAsync(root, config, CancellationToken.None);

            Assert.AreEqual(original, afterIgnoredChange);
            Assert.AreNotEqual(original, afterSourceChange);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Cache_HitsForIdenticalInputsAndInvalidatesForSourceChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var source = Path.Combine(root, "Sample.cs");
        await File.WriteAllTextAsync(source, "namespace Sample; public class First { }");

        try
        {
            var runner = new ProcessRunner();
            var service = new RepositoryGraphService(runner, new ProjectIndexer(runner));
            var config = new DevContextConfig { Cache = new CacheConfig { Enabled = true } };

            var first = await service.BuildAsync(root, config, CancellationToken.None);
            Directory.Delete(DevContext.Infrastructure.ContextPaths.Cache(root), true);
            var second = await service.BuildAsync(root, config, CancellationToken.None);
            Assert.IsFalse(
                Directory.Exists(DevContext.Infrastructure.ContextPaths.Cache(root)),
                "An in-memory hit should not need to recreate or deserialize the disk cache.");
            await File.WriteAllTextAsync(source, "namespace Sample; public class Second { }");
            var changed = await service.BuildAsync(root, config, CancellationToken.None);

            Assert.IsFalse(first.CacheHit);
            Assert.IsTrue(second.CacheHit);
            Assert.AreEqual(first.InputHash, second.InputHash);
            Assert.IsFalse(changed.CacheHit);
            Assert.AreNotEqual(first.InputHash, changed.InputHash);
            Assert.IsTrue(changed.Symbols.Symbols.Any(symbol => symbol.Name == "Second"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ProjectCache_ReusesIndependentProjectsAndInvalidatesDependents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-project-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await WriteProjectAsync(root, "Core", [],
                "namespace Sample; public sealed class CoreService { public void Run() { } }");
            await WriteProjectAsync(root, "App", ["Core"],
                "namespace Sample; public sealed class AppService { public void Execute() { new CoreService().Run(); } }");
            await WriteProjectAsync(root, "Independent", [],
                "namespace Sample; public sealed class IndependentV1 { }");

            var runner = new CountingProcessRunner(new ProcessRunner());
            var service = new RepositoryGraphService(runner, new ProjectIndexer(runner));
            var config = new DevContextConfig
            {
                Cache = new CacheConfig { Enabled = true },
                Indexing = new IndexingConfig
                {
                    ExecuteSourceGenerators = false,
                    MaxParallelism = 2
                }
            };

            var cold = await service.BuildAsync(root, config, CancellationToken.None);
            var coldEvaluationCalls = runner.TakeMsBuildCallCount();
            var warm = await service.BuildAsync(root, config, CancellationToken.None);
            var warmEvaluationCalls = runner.TakeMsBuildCallCount();
            await File.WriteAllTextAsync(
                Path.Combine(root, "Independent", "Independent.cs"),
                "namespace Sample; public sealed class IndependentV2 { }");
            var independentChange = await service.BuildAsync(root, config, CancellationToken.None);
            var independentEvaluationCalls = runner.TakeMsBuildCallCount();
            await File.WriteAllTextAsync(
                Path.Combine(root, "App", "App.cs"),
                "namespace Sample; public sealed class AppService { public void ExecuteChanged() { new CoreService().Run(); } }");
            var leafChange = await service.BuildAsync(root, config, CancellationToken.None);
            var leafEvaluationCalls = runner.TakeMsBuildCallCount();
            await File.WriteAllTextAsync(
                Path.Combine(root, "Core", "Core.cs"),
                "namespace Sample; public sealed class CoreService { public void Run() { } public void RunChanged() { } }");
            var dependencyChange = await service.BuildAsync(root, config, CancellationToken.None);
            var dependencyEvaluationCalls = runner.TakeMsBuildCallCount();
            var coreProjectPath = Path.Combine(root, "Core", "Core.csproj");
            var coreProject = await File.ReadAllTextAsync(coreProjectPath);
            await File.WriteAllTextAsync(
                coreProjectPath,
                coreProject.Replace(
                    "</PropertyGroup>",
                    "<Nullable>enable</Nullable></PropertyGroup>",
                    StringComparison.Ordinal));
            var structuralDependencyChange = await service.BuildAsync(root, config, CancellationToken.None);
            var structuralEvaluationCalls = runner.TakeMsBuildCallCount();
            File.Delete(coreProjectPath);
            var removedDependency = await service.BuildAsync(root, config, CancellationToken.None);
            var removedDependencyEvaluationCalls = runner.TakeMsBuildCallCount();

            Assert.AreEqual(0, cold.ProjectCacheHits);
            Assert.AreEqual(3, cold.ProjectCacheMisses);
            Assert.IsGreaterThan(0, coldEvaluationCalls);
            Assert.IsTrue(warm.CacheHit);
            Assert.AreEqual(3, warm.ProjectCacheHits);
            Assert.AreEqual(0, warmEvaluationCalls);
            Assert.AreEqual(2, independentChange.ProjectCacheHits);
            Assert.AreEqual(1, independentChange.ProjectCacheMisses);
            Assert.AreEqual(0, independentEvaluationCalls);
            Assert.IsTrue(independentChange.Symbols.Symbols.Any(symbol => symbol.Name == "IndependentV2"));
            Assert.AreEqual(2, leafChange.ProjectCacheHits);
            Assert.AreEqual(1, leafChange.ProjectCacheMisses);
            Assert.AreEqual(0, leafEvaluationCalls);
            var run = leafChange.Symbols.Symbols.Single(symbol => symbol.Name == "Run");
            Assert.IsTrue(leafChange.Dependencies.Symbols.Any(reference =>
                reference.SourceProject.EndsWith("App/App.csproj", StringComparison.Ordinal)
                && reference.TargetSymbol == run.Identity
                && reference.Relationship == "method-call"));
            Assert.AreEqual(1, dependencyChange.ProjectCacheHits);
            Assert.AreEqual(2, dependencyChange.ProjectCacheMisses);
            Assert.AreEqual(0, dependencyEvaluationCalls);
            Assert.IsTrue(dependencyChange.Symbols.Symbols.Any(symbol => symbol.Name == "RunChanged"));
            Assert.IsTrue(dependencyChange.Symbols.Symbols.Any(symbol => symbol.Name == "IndependentV2"));
            Assert.AreEqual(1, structuralDependencyChange.ProjectCacheHits);
            Assert.AreEqual(2, structuralDependencyChange.ProjectCacheMisses);
            Assert.IsGreaterThan(0, structuralEvaluationCalls);
            Assert.IsLessThan(coldEvaluationCalls, structuralEvaluationCalls);
            Assert.AreEqual(1, removedDependency.ProjectCacheHits);
            Assert.AreEqual(1, removedDependency.ProjectCacheMisses);
            Assert.IsGreaterThan(0, removedDependencyEvaluationCalls);
            Assert.IsFalse(removedDependency.Repository.Projects.Any(project =>
                project.Path.EndsWith("Core/Core.csproj", StringComparison.Ordinal)));
            Assert.AreEqual(
                DevContext.Core.AnalysisCompletenessState.Partial,
                removedDependency.Symbols.CompilationCompleteness.Single(record =>
                    record.Project.EndsWith("App/App.csproj", StringComparison.Ordinal)).State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task WriteProjectAsync(
        string root,
        string name,
        IReadOnlyList<string> references,
        string source)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var projectReferences = string.Join(
            string.Empty,
            references.Select(reference =>
                $"<ProjectReference Include=\"..\\{reference}\\{reference}.csproj\" />"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, $"{name}.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            $"<ItemGroup>{projectReferences}</ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.cs"), source);
    }

    private sealed class CountingProcessRunner(IProcessRunner inner) : IProcessRunner
    {
        private int msBuildCallCount;

        public int TakeMsBuildCallCount() => Interlocked.Exchange(ref msBuildCallCount, 0);

        public async Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            if (executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && arguments.Count > 0
                && arguments[0].Equals("msbuild", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref msBuildCallCount);
            }

            return await inner.RunAsync(executable, arguments, workingDirectory, cancellationToken);
        }
    }
}
