# RepoLens API

`RepoLens.Api` exposes the deterministic engine used by the `dev-context` CLI as a normal .NET 8
library and NuGet package. Applications can capture repository structure and health, query
MSBuild ownership, build bounded repository contexts, or use the same immutable baseline,
affected-code, cleanup, and verification lifecycle as the CLI.

```powershell
dotnet add package RepoLens.Api --version 0.7.0
```

```csharp
using DevContext;

var api = await DevContextApi.OpenAsync(repositoryPath, cancellationToken: cancellationToken);
var snapshot = await api.CaptureAsync(cancellationToken);

Console.WriteLine($"Projects: {snapshot.Repository.Projects.Count}");
Console.WriteLine($"Build: {snapshot.Build.State}");

foreach (var type in snapshot.Symbols.TypeDefinitions)
{
    Console.WriteLine($"{type.Accessibility} {type.Kind} {type.FullName}");
    foreach (var member in type.Members)
    {
        Console.WriteLine($"  {member.Accessibility} {member.Kind} {member.Name}");
    }
}
```

For task-delta workflows:

```csharp
var baseline = await api.BaselineAsync(cancellationToken: cancellationToken);
var affected = await api.AffectedAsync(cancellationToken);
var verification = await api.VerifyAsync(cancellationToken);
```

`BaselineAsync` refuses to replace existing task state unless `replace: true` is supplied
explicitly. `CaptureAsync` is stateless and does not create or replace a baseline.

## Diagnostics and project ownership

Neither operation requires a baseline:

```csharp
var doctor = await api.DoctorAsync(cancellationToken);
var ownership = await api.ExplainAsync(
    "src/App/Components/Dashboard.razor",
    cancellationToken);

foreach (var owner in ownership.Owners)
{
    Console.WriteLine($"{owner.ProjectPath}: {string.Join(", ", owner.ItemTypes)}");
}
```

`DoctorAsync` checks the SDK, NuGet configuration, configured solution, evaluated project graph,
semantic-compilation completeness, and enabled optional providers. Ownership uses evaluated
MSBuild item types, so Blazor
`RazorComponent`, WPF `Page`/`ApplicationDefinition`, and MAUI item types are explained without
extension guessing.

## Token-bounded source evidence

Use `QueryAsync` when an agent or another application needs source relevant to a concrete task
without receiving a broad architecture dump:

```csharp
var evidence = await api.QueryAsync(new EvidenceQueryOptions
{
    Query = "change dashboard song refresh and its focused tests",
    MaxTokens = 3000,
    MaxResults = 20,
    GraphDepth = 1,
    ChangedOnly = false,
    IncludeTests = true,
    Project = null,
    Kinds = []
}, cancellationToken);

Console.WriteLine(evidence.Prompt);
Console.WriteLine($"Evidence tokens: {evidence.ApproximateTokens:N0}");
Console.WriteLine($"Sufficiency: {evidence.Sufficiency}; abstain: {evidence.ShouldAbstain}");
```

Each `EvidenceBlock` contains repository-relative source coordinates, an exact excerpt, a content
hash, stable symbol identity when available, and observable selection reasons. Relationships state
whether an edge was semantically resolved or inferred by a named convention and retain their
origin, target framework, and exact evidence span. `Sufficiency`, `ShouldAbstain`, and
`SufficiencyReasons` distinguish supported, partial, and missing repository evidence. `AnalysisGaps` and
`CompilationCompleteness` make unresolved references, compiler errors, multi-target limitations,
and generated-source omissions explicit. The rendered prompt is deterministic, ends with the task,
and is bounded by `MaxTokens` using the documented four-characters-per-token approximation. Use
the JSON form when consumers need typed provenance rather than prompt-ready Markdown.

Use `ChangedOnly`, `Project`, `Kinds`, and `IncludeTests` to constrain seed declarations. Graph
expansion can still include directly related declarations so the result retains dependency context.
Changed-only queries require a baseline and report the exact changed-file set in the bundle.

Run a repeatable retrieval corpus without reimplementing measurement code:

```csharp
var benchmark = await api.BenchmarkAsync(
[
    new EvidenceBenchmarkCase
    {
        Name = "dashboard refresh",
        Query = "refresh dashboard songs",
        ExpectedFiles = ["src/App/DashboardViewModel.cs"],
        ExpectedRelationships = ["method-call"],
        MaxTokens = 1400
    }
], cancellationToken);
```

`EvidenceBenchmarkReport` records file recall/precision, missing expectations, token use,
cold/warm latency, and deterministic repeated output. `DevContextApi.Contract` exposes the package
version and persisted-schema compatibility window before a consumer reads stored artifacts.
Benchmark cases can set `ExpectedSufficiency` and `ExpectAbstention` so no-evidence behavior is a
measured contract rather than an untested fallback.

## Purpose-specific context and hotspots

```csharp
var context = await api.ContextAsync(new RepositoryContextOptions
{
    Purpose = ContextPurpose.Risk,
    Scope = ContextScope.Project,
    Target = "MyApplication",
    CoberturaPath = "coverage/coverage.cobertura.xml",
    MaxHotspots = 10,
    MaxSymbols = 200,
    GitHistoryMonths = 12
}, cancellationToken);

Console.WriteLine(context.Markdown);
Console.WriteLine($"Approximate tokens: {context.ApproximateTokens:N0}");
```

Purposes are `Change`, `Architecture`, `Build`, and `Risk`. Scopes are automatic, full repository,
changed files, project, and path. The report exposes typed projects, files, symbols, scoped type
definitions, semantic-compilation completeness, type/method metrics, semantic dependency edges,
diagnostics, failing tests, Git
churn, optional Cobertura coverage, and transparently ranked file hotspots. Hotspots are ordered
lexicographically by observable metrics rather than an opaque weighted score, and every result
includes selection reasons.

`TypeDefinitions` is the class-diagram-oriented source model. Each type includes its stable symbol
identity, kind, accessibility, modifiers, generic constraints, attributes, base type, interfaces,
and all source declaration locations. Its declared members include constructors, methods,
properties and indexers, fields, events, and enum members, with stable identities, signatures,
accessors, parameter passing/defaults, nullability, generic constraints, and attributes. Partial
type declarations are merged deterministically. Existing `Symbols` remains available as the
smaller declaration/reference index for compatibility and affected-code calculations.

Save an explicitly requested Markdown artifact with bounded default history:

```csharp
var artifact = await api.SaveReportAsync(context, retain: 20, cancellationToken: cancellationToken);
```

Default reports are written beneath `.dev-context/reports`. Passing an explicit output path does
not apply default-directory retention.

## Trust and current boundaries

- Source generators from evaluated analyzer assemblies execute in the RepoLens process by default.
  Only analyze repositories and restored dependencies you trust. Set
  `Indexing.ExecuteSourceGenerators = false` to disable execution; completeness then records the gap.
- MAUI, Blazor, and WPF project items participate in ownership and affected-project selection.
  Razor/XAML files are represented as markup declarations and receive confidence-labelled links to
  resolvable source types/members. Framework-generated backing C# that is not an analyzer output
  remains an explicit completeness gap.
- Type definitions represent explicit source declarations. Compiler-synthesized members and local
  functions are not emitted as declared members.
- Semantic compilations use evaluated framework/package metadata references, global `Using` items,
  and source project references independently for every target framework. The compact symbol graph
  currently uses the first evaluated target as its primary view while completeness is per target.
