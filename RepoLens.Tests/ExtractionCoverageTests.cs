using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

/// <summary>
/// A coverage contract nobody checks is worse than none: it reads as a guarantee while quietly
/// drifting from what the indexer does. These tests pin it to observed behaviour in both directions
/// that can be observed cheaply — nothing is emitted that the contract does not name, and a filter
/// naming a kind outside it is disclosed rather than answered with a confident empty result.
/// </summary>
[TestClass]
public sealed class ExtractionCoverageTests
{
    [TestMethod]
    public void Contract_IsSortedAndFreeOfDuplicates()
    {
        foreach (var (name, values) in new (string, IReadOnlyList<string>)[]
                 {
                     (nameof(ExtractionCoverage.DeclarationKinds), ExtractionCoverage.DeclarationKinds),
                     (nameof(ExtractionCoverage.RelationshipKinds), ExtractionCoverage.RelationshipKinds)
                 })
        {
            CollectionAssert.AreEqual(
                values.Order(StringComparer.Ordinal).ToArray(),
                values.ToArray(),
                $"{name} must stay sorted so additions are reviewable as a diff.");
            Assert.AreEqual(
                values.Count,
                values.Distinct(StringComparer.Ordinal).Count(),
                $"{name} contains a duplicate.");
        }

        Assert.IsNotEmpty(ExtractionCoverage.KnownLimits);
        Assert.AreEqual("roslyn-csharp/2", ExtractionCoverage.Identifier);
    }

    [TestMethod]
    public async Task Contract_NamesEveryKindTheIndexerEmits()
    {
        var root = TestHelpers.CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));

        // Deliberately broad rather than realistic: the point is to reach as many emitters as one
        // file can, so a kind added to the indexer without being added to the contract fails here.
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Everything.cs"),
            """
            using System;
            namespace Sample;

            [AttributeUsage(AttributeTargets.All)]
            public sealed class TaggedAttribute : Attribute { }

            public delegate void Signal(int value);

            public enum Mode { Fast, Slow }

            public interface IEngine { int Power { get; } void Start(); }

            public record struct Reading(int Value);

            [Tagged]
            public class Engine : IEngine
            {
                private int power = 1;
                public event Signal? Started;
                public int Power => power;
                public int this[int index] => index + power;
                public static Engine operator +(Engine left, Engine right) => left;
                public static explicit operator int(Engine engine) => engine.power;

                public Engine() { power = 2; }
                ~Engine() { }

                public virtual void Start()
                {
                    void Local() { power = 3; }
                    Local();
                    Started?.Invoke(power);
                }

                public Type Self() => typeof(Engine);
                public string PowerName() => nameof(Power);
                public Mode Preferred() => Mode.Fast;
                public Reading Read() => new(power);
            }

            public sealed class TurboEngine : Engine
            {
                public override void Start() => base.Start();
            }
            """);

        try
        {
            var project = TestHelpers.SampleProject("src/Everything.cs");
            var repository = new RepositoryIndex { Solution = "Sample.sln", Projects = [project] };

            var (symbols, dependencies) = await SymbolIndexer.BuildAsync(
                root,
                repository,
                CancellationToken.None);

            var emittedKinds = symbols.Symbols
                .Select(symbol => symbol.Kind)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var emittedRelationships = dependencies.Symbols
                .Select(reference => reference.Relationship)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.IsGreaterThan(
                10,
                emittedKinds.Length,
                "The fixture stopped exercising a useful range of declarations.");
            CollectionAssert.IsSubsetOf(
                emittedKinds,
                ExtractionCoverage.DeclarationKinds.ToArray(),
                "The indexer emitted a declaration kind the coverage contract does not name: "
                + string.Join(", ", emittedKinds.Except(ExtractionCoverage.DeclarationKinds, StringComparer.Ordinal)));
            CollectionAssert.IsSubsetOf(
                emittedRelationships,
                ExtractionCoverage.RelationshipKinds.ToArray(),
                "The indexer emitted a relationship the coverage contract does not name: "
                + string.Join(", ", emittedRelationships.Except(ExtractionCoverage.RelationshipKinds, StringComparer.Ordinal)));
        }
        finally
        {
            TempDirectory.Delete(root);
        }
    }

    [TestMethod]
    public void UnknownKindGap_NamesTheKindAndTheContract()
    {
        Assert.IsNull(ExtractionCoverage.UnknownKindGap([]));
        Assert.IsNull(ExtractionCoverage.UnknownKindGap(["method", "TEST"]));

        var gap = ExtractionCoverage.UnknownKindGap(["method", "macro"]);

        Assert.IsNotNull(gap);
        StringAssert.Contains(gap, "'macro'");
        StringAssert.Contains(gap, ExtractionCoverage.Identifier);
        Assert.DoesNotContain("'method'", gap);
    }

    [TestMethod]
    public async Task Query_DisclosesAKindFilterThatCouldNotHaveMatched()
    {
        var root = TestHelpers.CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Engine.cs"),
            "namespace Sample; public sealed class Engine { public void Start() { } }");

        IProcessRunner runner = new ProcessRunner();
        var git = await runner.RunAsync("git", ["init", "--quiet"], root, CancellationToken.None);
        Assert.AreEqual(ExecutionState.Succeeded, git.State, git.StandardError);

        try
        {
            var files = new RepositoryFileFilter(runner);
            var graph = new RepositoryGraphService(runner, new ProjectIndexer(runner, files), files);
            var evidence = new EvidenceQueryService(graph, new GitService(runner), new ContextStore(), files);

            var bundle = await evidence.BuildAsync(
                root,
                new DevContextConfig(),
                new EvidenceQueryOptions { Query = "start the engine", Kinds = ["macro"] },
                CancellationToken.None);

            // Without this the caller sees a clean empty result for a filter that never could have
            // matched, which is precisely a blind spot presented as an absence.
            Assert.IsTrue(
                bundle.AnalysisGaps.Any(gap => gap.Contains("'macro'", StringComparison.Ordinal)),
                string.Join(" | ", bundle.AnalysisGaps));
        }
        finally
        {
            TempDirectory.Delete(root);
        }
    }
}
