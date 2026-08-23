# `dev-context` CLI

`dev-context` captures deterministic facts about a .NET repository at the beginning of a
logical coding task, then reports only changes and regressions introduced after that baseline.

The initial implementation targets .NET 10 and uses Git, MSBuild, Roslyn syntax trees, normal
`dotnet build` output, and TRX test results. It does not use an LLM or vector embeddings to
construct repository metadata.

## Build and run

From the repository root:

```powershell
dotnet build RepoLens.sln
dotnet RepoLens/bin/Debug/net10.0/dev-context.dll --help
```

During development, the equivalent command is:

```powershell
dotnet run --project RepoLens/RepoLens.csproj -- status
```

The project is configured as a .NET tool named `dev-context`. To produce a local tool package:

```powershell
dotnet pack RepoLens/RepoLens.csproj --configuration Release
```

For a repeatable behavioral test, use the isolated smoke harness documented in
[`testing.md`](testing.md).

## Workflow

Create the immutable baseline before changing source:

```powershell
dev-context baseline
dev-context status
```

During implementation, inspect likely impact:

```powershell
dev-context affected
dev-context explain src/App/Components/Dashboard.razor
dev-context context change
dev-context query "change dashboard song refresh and its focused tests" --max-tokens 3000
dev-context benchmark RepoLens/benchmarks/evidence-corpus.json
```

At the end of the task:

```powershell
dev-context clean
dev-context verify
```

`baseline` refuses to overwrite an existing baseline. Use `baseline --replace` only when an
explicitly new logical task begins. Use `reset` to remove generated state without deleting the
repository configuration.

Every significant command accepts `--format text` (the default) or `--format json`. Use
`--verbose` to write repository and stage timing details to standard error without corrupting
JSON written to standard output.

## Commands

### `baseline`

Captures:

- branch, HEAD, porcelain status, and content hashes for existing changed files;
- evaluated target frameworks, compiler settings, package references, project references, and
  compile items through MSBuild;
- build state and normalized compiler/Roslyn diagnostics;
- individual structured test outcomes from TRX;
- optional `dotnet format` and Qodana provider states;
- cached Roslyn semantic indexes for declared types and methods;
- evaluated project-file ownership for C#, Razor, XAML, content, resources, linked files, and
  repository-local MSBuild imports;
- project, type, method-call, construction, dependency-injection, and test-to-production
  relationships; and
- UTC timestamps and per-stage durations.

The pending baseline is written separately and moved into place only after capture succeeds.
Source cleanup is never invoked by this command.

### `status`

Reads stored results only. It does not rebuild or retest. Text output is deliberately bounded so
it can be inserted into an agent context without dumping every diagnostic or file.

### `verify`

Rebuilds, retests, and reruns enabled analysis providers. It compares stable diagnostic and test
identities and reports:

- working-tree files whose status or contents changed after the baseline;
- declarations changed within those files;
- new and resolved diagnostics;
- new, existing, and resolved failing tests;
- current build and provider states; and
- whether the delta is a regression.

A build/provider transition from success to failure, a new warning/error, or a newly failing test
is currently considered a regression.

The configured verification modes are:

- `all`: run every discovered test project;
- `affected-first`: run symbol/project-selected tests first, stop immediately on a targeted
  failure, and otherwise confirm with the full suite;
- `affected-only`: run selected tests without a full-suite confirmation; and
- `none`: skip tests explicitly.

Targeted/incomplete runs never report a pre-existing failing test as resolved unless that exact
test was executed and passed.

### `affected`

Maps files changed after the baseline to containing projects and declared symbols. Explicitly
evaluated MSBuild items take precedence, including Blazor `RazorComponent`, MAUI `MauiXaml` and
resource items, WPF `Page`/`ApplicationDefinition`, normal content/resources, and linked files.
Unclassified files fall back to the nearest containing project. Shared solution, props, targets,
SDK, NuGet, analyzer-configuration, and ruleset inputs map to applicable projects in their
directory scope; repository-local imports map to every project that imports them.

The affected calculation then walks semantic symbol references and reverse project-reference
relationships to include downstream production and test projects. Symbols from both the baseline
and current indexes are considered, so removed declarations can still be reported. Likely test
cases are emitted as fully qualified names suitable for VSTest filters. Razor and XAML changes
currently receive project-level ownership and dependency propagation rather than fabricated C#
declaration or symbol results.

### `doctor`

Checks SDK availability, NuGet configuration readability, the configured solution, evaluated
project discovery, enabled optional analysis providers, and baseline availability. It does not
create a baseline. Warnings such as an inaccessible user `NuGet.Config` remain distinguishable
from failures that prevent repository evaluation.

### `explain <path>`

Explains exactly which evaluated projects own a path, why they own it, the MSBuild item types that
matched, and the reverse project-reference closure affected by it. It does not require a baseline.

### `context` and `report`

Render bounded `change`, `architecture`, `build`, or `risk` context from the shared API engine:

```powershell
dev-context context risk --scope project --target RepoLens.Api --max-hotspots 10
dev-context context change --format json
dev-context report architecture --output reports/architecture.md
```

Scopes are `automatic`, `full`, `changed`, `project`, and `path`. Optional `--coverage` accepts a
Cobertura XML report. `--history-months`, `--max-hotspots`, and `--max-symbols` bound work and
output. The typed API and JSON context include an approximate token count; `report` prints artifact
character and token estimates and retains 20 default-directory reports unless `--retain`
changes the bound.

Architecture and change Markdown include compact type details. JSON additionally exposes the full
`typeDefinitions` model: accessibility, modifiers, generic constraints, attributes, inheritance,
interfaces, partial declaration locations, and declared member signatures with parameters,
nullability, accessors, and stable identities.

### `query <task>`

Retrieves task-specific source evidence through lexical symbol scoring and bounded traversal of
semantic relationships:

```powershell
dev-context query "change dashboard song refresh and its focused tests" `
  --max-tokens 3000 --max-results 20 --graph-depth 1
dev-context query "where is dashboard refresh tested?" --format json
dev-context query "what changed in dashboard?" --changed --exclude-tests --project App
```

Text output is prompt-ready Markdown with repository-relative source coordinates, exact excerpts,
selection reasons, evidence relationships, a sufficient/partial/insufficient decision, an explicit
abstention flag, and known analysis gaps. JSON returns the typed `EvidenceBundle`, including content
hashes, stable symbol identities, confidence labels, relationship origins and exact use-site spans,
compilation completeness, and the same rendered prompt. `--max-tokens` uses the conservative
`ceil(characters / 4)` estimate and has a minimum of 256. `--graph-depth` accepts zero through
three; shallower queries use fewer graph-expanded results. Normal queries are stateless and do not
require or create a baseline; changed-only selection reads but never replaces the baseline.

`--changed` restricts seeds to declarations in files changed since the stored baseline.
`--exclude-tests`, `--project <name-or-path>`, and repeatable `--kind <kind,...>` filters further
constrain seeds; graph traversal may still add directly related declarations. When filters are
active, unconstrained lexical fallback is disabled so it cannot bypass the requested scope.

### `benchmark <corpus.json>`

Runs every JSON `EvidenceBenchmarkCase` twice and reports expected-file recall/precision,
missing relationships, sufficiency/abstention conformance, approximate tokens, cold/warm latency,
and deterministic output. It exits `0` only when every expected file/relationship and optional
evidence decision is satisfied within budget and both runs match; an acceptance failure exits `1`.

### `clean`

Runs only the command explicitly enabled in configuration. The command is launched directly,
without a shell, and the Git snapshot before and after it is compared so cleanup-created changes
remain visible.

### `reset`

Deletes `baseline/`, `current/`, `indexes/`, `cache/`, retained runs, and `summary.md`. It retains
`.dev-context/config.json` and never changes application source files.

## Configuration

The first `baseline` creates `.dev-context/config.json` if it does not exist. A typical file is:

```json
{
  "version": 1,
  "solution": "RepoLens.sln",
  "tests": {
    "enabled": true,
    "baselineMode": "all",
    "verifyMode": "affected-first"
  },
  "analysis": {
    "roslyn": true,
    "dotnetFormat": false,
    "qodana": false,
    "qodanaCommand": "qodana",
    "failOnNewWarnings": true
  },
  "cleanup": {
    "command": "dotnet format",
    "enabled": false
  },
  "storage": {
    "retainRawLogs": false
  },
  "cache": {
    "enabled": true
  },
  "indexing": {
    "executeSourceGenerators": true,
    "maxSourceFileBytes": 2097152,
    "maxEvidenceFileBytes": 524288,
    "maxEvidenceFilesScanned": 20000
  }
}
```

Raw build, test, and analysis logs are omitted by default. Enable `retainRawLogs` only when they
are required for investigation. Normalized machine-readable results are always persisted.

## Generated layout

```text
.dev-context/
  config.json
  baseline/
    manifest.json
    git.json
    build.json
    tests.json
    analysis.json
  current/
    manifest.json
    git.json
    build.json
    tests.json
    analysis.json
    verification.json
  indexes/
    projects.json
    symbols.json
    dependencies.json
  cache/
    manifest.json
    projects.json
    symbols.json
    dependencies.json
  reports/
    <timestamp>-<purpose>.md
  summary.md
```

Every stored document has a schema version. Schema v8 adds evidence sufficiency/abstention,
relationship origin/framework provenance, exact evidence spans, and local-function symbols.
Schema v7 adds per-target compilation/reference
records, generated-source provenance, richer member/markup relationships, and filtered evidence.
Readers reject schemas outside their declared compatibility window (currently v5-v8). Schema v6 adds resolved metadata-reference provenance,
semantic-compilation completeness, source end lines, and evidence bundles. Schema v5 added rich
source type/member definitions; schema v4 introduced evaluated MSBuild item provenance and richer
semantic type relationships.
Arrays are sorted before persistence wherever their source order is not meaningful. Diagnostic
identities intentionally exclude line/column numbers so unrelated line movement does not
manufacture a new diagnostic.

The graph cache key includes the schema, SDK version, configuration, repository file inventory,
solution/project/build files, and C# source contents. The inventory invalidates ownership when
project items are added, removed, or renamed without unnecessarily hashing large content assets.
Generated output, Git internals, IDE state, and `.dev-context/` are excluded. Cache entries are
replaced atomically and invalidated whenever a relevant input changes.

## Exit codes

- `0`: command completed and verification found no regressions.
- `1`: verification completed and found regressions.
- `2`: usage, configuration, repository, or external-command failure.

An unavailable command, a command execution failure, analyzer findings, and a repository
build/test failure remain separate machine-readable states. In particular, configured analysis
that cannot execute makes `verify` return `2`, while successfully produced findings participate in
the normal baseline delta and return `1` only when they are regressions.

## Current first-release boundaries

- Semantic compilations use evaluated framework/package metadata assemblies, global `Using` items,
  and source project-to-project references independently for every target framework. The compact
  declaration/reference view uses the first evaluated target; completeness remains per target.
- MAUI, Blazor, and WPF project items participate in project ownership and downstream project/test
  selection. Razor and XAML contribute markup declarations plus confidence-labelled component,
  code-behind, binding, command, event, and data-context relationships. Ordinary C# view models,
  components, pages, controls, and code-behind receive normal rich type/member indexing.
- Rich type definitions cover explicit source constructors, methods, properties/indexers, fields,
  events, and enum members. Local functions are emitted as graph symbols and participate in
  operation-resolved call relationships, but are not class-diagram members. Compiler-synthesized
  members and markup structure are not emitted as declared members.
- Analyzer assemblies are inspected and source generators execute by default. Generated sources
  are indexed and retrievable. This loads third-party code in-process: analyze only trusted
  repositories/dependencies, or disable `indexing.executeSourceGenerators`. Markup-generated C#
  outside analyzer execution remains an explicit gap.
- Symbol relationships cover declared types and members, inheritance/interfaces, overrides,
  interface implementation, calls, construction, member reads/writes, event subscription,
  delegate callbacks, generic type arguments, common dependency-injection registrations, and
  signature types. Calls, construction, member access, and callbacks use Roslyn operation semantics;
  arbitrary interprocedural data flow remains outside the deterministic graph.
- The cache covers structural repository indexes. Builds, tests, and enabled analyzers still run
  during verification because correctness and current external-tool state take precedence.
- Qodana is optional. When enabled, SARIF results are normalized; environments without the Qodana
  executable report the provider as `Unavailable` without affecting normal disabled operation.
