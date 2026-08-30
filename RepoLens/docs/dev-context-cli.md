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
dev-context init
dev-context baseline
dev-context status
```

For an existing feature branch or CI review, compare directly with another ref without creating a
baseline:

```powershell
dev-context verify --against origin/main
```

During implementation, inspect likely impact:

```powershell
dev-context affected
dev-context explain src/App/Components/Dashboard.razor
dev-context context change
dev-context query "change dashboard song refresh and its focused tests" --max-tokens 3000
dev-context refs "DashboardViewModel.RefreshAsync" --relation callers
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

### `init`

Writes a validated `.dev-context/config.json` without creating a baseline. If current configuration
already exists, it reports that fact and leaves the file unchanged. A readable v1 configuration is
migrated to v2 atomically; new fields retain their backward-compatible defaults. This makes
repository scope settings reviewable before the first capture.

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

When `solution` is configured, project discovery starts from projects explicitly included by
`.sln`, `.slnx`, or `.slnf` and then follows their transitive `ProjectReference` closure. Other
on-disk projects are not silently added. Without a configured solution, repository project
discovery remains the fallback.

`baseline --from <ref>` stores `merge-base(<ref>, HEAD)` as the Git diff base while capturing the
current branch's health and repository graph as the regression baseline. This is useful when work
already exists on a feature branch but later task commands should continue to report the whole
branch delta. Status shows both the merge base and the HEAD that was captured.

### `status`

Reads stored results only. It does not rebuild or retest. Text output is deliberately bounded so
it can be inserted into an agent context without dumping every diagnostic or file.

### `verify`

Rebuilds, retests, and reruns enabled analysis providers. It compares stable diagnostic and test
identities and reports:

- committed files changed between the baseline HEAD and current HEAD, plus working-tree files whose
  status or contents changed after the baseline;
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

Every changed file includes `committed`, `working tree`, or `committed + working tree` provenance.
Before reading a commit range, RepoLens verifies that the baseline HEAD is still an ancestor of the
current HEAD. A rebase, reset, or history rewrite produces `BaselineDiverged`, marks verification as
incomplete, and returns the usage/failure exit code instead of silently reporting a partial delta.

`verify --against <ref>` is a stateless branch-review mode. It calculates the merge base, analyzes
committed and working-tree changes, runs current build/analysis and affected-first tests, and
returns a typed `ReferenceReviewReport`. It does not read, create, or replace an immutable
baseline, which makes it suitable for a clean CI checkout. Because there is no historical health
snapshot, failures describe current verification health rather than baseline regressions.

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

Committed changes are included even when the working tree is clean. Text and JSON output expose
the same provenance and comparison state as `verify`.

### `doctor`

Checks SDK availability, NuGet configuration readability, the configured solution, effective
repository scope, evaluated project discovery, enabled optional analysis providers, and baseline availability. It does not
create a baseline. Warnings such as an inaccessible user `NuGet.Config` remain distinguishable
from failures that prevent repository evaluation. Repository scope reports whether `.gitignore`
was applied, the selected file count, and both built-in and configured exclude globs so unexpected
omissions or inclusions are visible.

Project discovery includes `.csproj`, `.fsproj`, and `.vbproj`. F# and Visual Basic projects use
evaluated MSBuild inputs for ownership, project dependencies, and test selection. Their
compilation-completeness records remain explicitly partial because the Roslyn C# declaration and
relationship index does not apply to those languages.

Doctor uses the repository graph cache by default and reports `HIT` or `MISS`; use `--no-cache`
only when diagnosing stale or environment-sensitive graph construction. `--explain-gaps` prints
every project's framework, source-load counts, top semantic diagnostic IDs with affected files,
and explicit gap messages. The same structured detail is always available in JSON under
`compilationCompleteness`.

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
Cobertura XML report and takes precedence over automatically collected coverage. When
`tests.collectCoverage` is enabled, baseline/verification test runs request the cross-platform
Coverlet collector and persist its reports for hotspot ranking. Test projects must reference a
compatible Coverlet collector. `--history-months`, `--max-hotspots`, and `--max-symbols` bound work
and output. The typed API and JSON context include an approximate token count; `report` prints
artifact character and token estimates and retains 20 default-directory reports unless `--retain`
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
`ceil(characters / 4)` estimate and has a minimum of 256. Evidence is always what gets cut to make
room: blocks are dropped, then the last one is truncated, then it is dropped too, and only then are
analysis gaps trimmed. One concrete gap always survives beside the notice that others were omitted,
so a bundle carrying nothing but disclosures may exceed the budget by that gap — a result may
understate the evidence found, never the analysis missed. `--graph-depth` accepts zero through
three; shallower queries use fewer graph-expanded results. Normal queries are stateless and do not
require or create a baseline; changed-only selection reads but never replaces the baseline.

`--changed` restricts seeds to declarations in files changed since the stored baseline.
`--exclude-tests`, `--project <name-or-path>`, and repeatable `--kind <kind,...>` filters further
constrain seeds; graph traversal may still add directly related declarations. When filters are
active, unconstrained lexical fallback is disabled so it cannot bypass the requested scope.
Committed changes after the baseline participate in this filter. If Git history diverged,
changed-only evidence is marked insufficient and tells consumers to abstain.

### `refs <symbol-or-file:line>`

Resolves one exact symbol and filters the typed, direction-aware dependency graph without lexical
ranking:

```powershell
dev-context refs "EvidenceQueryService.EvaluateSufficiency" --relation callers
dev-context refs "IProcessRunner" --relation implementers
dev-context refs "RepoLens/Services/GitService.cs:89" --relation tests-covering
```

Supported relations are `callers`, `callees`, `implementers`, `implementations`, `overrides`,
`subtypes`, `constructors-of`, `readers`, `writers`, `tests-covering`, and `injected-into`.
Fully-qualified names, unique bare names, and repository-relative `file:line` locations are
accepted. Ambiguity is returned as a candidate list rather than guessed away. Results retain edge
confidence, origin, target framework, and exact evidence spans; output is deterministic and bounded
by `--max-results` and `--max-tokens`.

An empty result is proof of absence only when the relevant compilation records are complete.
Otherwise the result is insufficient and requires abstention, preserving the same honesty contract
as `query`.

### `mcp`

Starts a local Model Context Protocol server over standard input/output, exposing the typed API
results as structured content through nine tools — `baseline`, `status`, `affected`, `query`,
`refs`, `explain`, `context`, `doctor`, and `verify` — plus the `coding-task` and `ground-question`
prompts.

`verify` is deliberately not invoked by any read-only tool. It can be slow and writes normal
build/test artifacts, so an MCP client or agent must choose it explicitly.

See [`agent-setup.md`](agent-setup.md) for client configuration, the tracked `.mcp.json`, payload
bounds, and the abstention contract an agent has to honour.

### `benchmark <corpus.json>`

Runs every JSON `EvidenceBenchmarkCase` twice and reports expected-file recall/precision,
missing relationships, sufficiency/abstention conformance, approximate tokens, cold/warm latency,
and deterministic output. It exits `0` only when every expected file/relationship and optional
evidence decision is satisfied within budget and both runs match; an acceptance failure exits `4`.

### `trend`

Reads the structured JSON sidecars retained beside default Markdown reports and compares
diagnostic count, failing-test count, hotspot churn, and average hotspot line coverage. Deltas are
computed only against an earlier report with the same purpose, scope, and target.

```powershell
dev-context report risk
dev-context trend --max-results 20
dev-context trend --format json
```

### `schema [document]`

Emits draft 2020-12 JSON Schema without requiring a Git repository. With no document operand the
command emits the complete persisted-document catalog; use a name such as `tests`, `configuration`,
or `verification` for one contract. `--output` also writes the emitted JSON to a file.

```powershell
dev-context schema
dev-context schema tests --output schemas/tests.schema.json
```

### `clean`

Runs only the command explicitly enabled in configuration. The command is launched directly,
without a shell, and the Git snapshot before and after it is compared so cleanup-created changes
remain visible.

### `reset`

Deletes `baseline/`, `current/`, `indexes/`, `cache/`, retained runs, `fingerprints.json`, and
`summary.md`. It retains `.dev-context/config.json` and never changes application source files.

## Configuration

Run `dev-context init` to create `.dev-context/config.json` explicitly. The first `baseline` also
creates it when necessary for backward compatibility. A typical file is:

```json
{
  "version": 2,
  "solution": "RepoLens.sln",
  "tests": {
    "enabled": true,
    "baselineMode": "all",
    "verifyMode": "affected-first",
    "collectCoverage": false
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
    "respectGitignore": true,
    "exclude": [],
    "maxParallelism": 8,
    "maxSourceFileBytes": 2097152,
    "maxEvidenceFileBytes": 524288,
    "maxEvidenceFilesScanned": 20000
  },
  "execution": {
    "processTimeoutSeconds": 900
  }
}
```

Raw build, test, and analysis logs are omitted by default. Enable `retainRawLogs` only when they
are required for investigation. Normalized machine-readable results are always persisted.
Coverage collection is opt-in because it adds instrumentation time and depends on the test
project's collector package. Coverage XML is retained even when raw logs and TRX files are removed.
Configuration v1 remains readable and is rewritten as v2 by `init` or the next baseline save;
unknown older or newer versions fail explicitly.

Repository discovery, graph hashing, project indexing, and lexical evidence all use the same file
inventory. With `respectGitignore` enabled, tracked files plus untracked, non-ignored files are
selected using Git's own ignore rules. `exclude` adds repository-relative globs such as
`["artifacts/**", "samples/**"]`. Built output and tool state under `bin`, `obj`, `.git`,
`.idea`, and `.dev-context` remain excluded regardless of these settings. Set
`respectGitignore` to `false` only when ignored source is intentionally part of the analysis.
`maxParallelism` bounds concurrent project evaluation and Roslyn indexing work from 1 through 64.
Its default is the smaller of the machine's processor count and 8, with a minimum of 1; the sample
shows the maximum default value. It is deliberately excluded from the graph cache key: it decides how
the work is scheduled, never what the resulting graph contains, so including it would give the same
repository a different cache entry on every machine.

`execution.processTimeoutSeconds` bounds every external command — build, test, Git, formatter, and
analyzer — from 1 through 86400. A command that overruns it is terminated and reported as `TimedOut`,
which is distinct from `Failed`: a failed command reached a verdict, a terminated one reached none, so
`verify` reports an execution failure and exits `2` rather than concluding that nothing regressed. The
default of 900 seconds is meant to bound a hang, not to police slowness; raise it for a repository
whose test suite legitimately runs longer.

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
    project-entries/
      <project-path-hash>.json
  cache.lock
  fingerprints.json
  reports/
    <timestamp>-<purpose>.md
    <timestamp>-<purpose>.trend.json
  runs/
    <run-id>/coverage/*.cobertura.xml
  summary.md
```

`cache.lock` exists only while a process is swapping the cache directory into place, and is deleted
when that process releases it.

`fingerprints.json` records `(path, size, modified, content hash)` for every repository input. It sits
beside the cache rather than inside it because the cache directory is swapped wholesale on publish.

Every stored document has a schema version. Schema v10 adds persisted coverage provenance and
structured retained-report trend points. Schema v9 adds commit-aware change provenance, an
explicit Git comparison state, solution-scoped multi-language ownership, and structured semantic
diagnostic summaries. Schema v8 adds evidence sufficiency/abstention,
relationship origin/framework provenance, exact evidence spans, and local-function symbols.
Schema v7 adds per-target compilation/reference
records, generated-source provenance, richer member/markup relationships, and filtered evidence.
Readers reject schemas outside their declared compatibility window (currently v5-v11). Schema v11 adds the timed-out execution state. Schema v6 adds resolved metadata-reference provenance,
semantic-compilation completeness, source end lines, and evidence bundles. Schema v5 added rich
source type/member definitions; schema v4 introduced evaluated MSBuild item provenance and richer
semantic type relationships.
Arrays are sorted before persistence wherever their source order is not meaningful, using keys that
do not depend on the machine: resolved references order by file name rather than by absolute path,
because the SDK lives in a different place on each platform. Diagnostic identities intentionally
exclude line/column numbers so unrelated line movement does not manufacture a new diagnostic.

Stored artifacts under `.dev-context/` are written without indentation, since only the tool reads
them back; indentation cost a quarter of every persisted index. `config.json` and the `.trend.json`
beside each report stay indented, because a person opens those.

The graph cache key includes the schema, SDK version, configuration, filtered repository file inventory,
solution/project/build files, and C# source contents. The inventory invalidates ownership when
project items are added, removed, or renamed without unnecessarily hashing large content assets.
Git-ignored paths, configured excludes, generated output, Git internals, IDE state, and
`.dev-context/` are excluded. On an exact cache miss, matching per-project entries are reused.
A changed project and its reverse project-reference dependents are rebuilt; independent projects
remain cached. `doctor` reports the reuse/rebuild counts.

Those content hashes are read from `fingerprints.json` when a file's size and modification time both
match the recorded ones, and computed from the file otherwise, in parallel. The cache key is still the
content hash, so two runs over identical content still agree and nothing about determinism changes;
what changes is that a warm call re-reads only what was edited instead of the whole repository. On a
5,000-file repository that took a warm `explain` from 2.4 s to 1.3 s. The shortcut is only taken when
`cache.enabled` is true — the switch that already trades freshness for speed — and it is wrong only
for an edit that preserves a file's exact byte length *and* lands inside the filesystem's timestamp
granularity. Deleting the file, or running with the cache disabled, forces a full re-read; it is an
optimization that can never change an answer, only how long it takes to produce one.

Concurrent callers of one process — overlapping MCP requests, most obviously — share a single graph
build rather than each running a full evaluation. Between processes, the cache directory is published
under a `.dev-context/cache.lock` file: the previous directory is renamed aside and replaced, and a
run that loses the race skips writing the cache rather than failing. The cache only ever makes a
command faster, so it must never be what makes one fail.

## Exit codes

- `0`: command completed and verification found no regressions.
- `1`: verification completed and found regressions.
- `2`: usage, configuration, repository, or external-command failure, including a command terminated
  for exceeding `execution.processTimeoutSeconds` and a persisted artifact outside the readable
  schema window.
- `3`: `query` or `refs` completed but evidence was insufficient and the result requires abstention.
- `4`: the retrieval benchmark completed but failed an acceptance criterion.

An unavailable command, a timed-out command, a command execution failure, analyzer findings, and a
repository build/test failure remain separate machine-readable states. In particular, configured analysis
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
