# RepoLens feature plan

Prioritised functional improvements on the path to 1.0. The plan began at 0.7.0; this current-state
section is kept aligned with the latest verified implementation while the original problem and
design notes remain as the decision record.

## 1. Current state

| Aspect | State |
| --- | --- |
| Projects | `RepoLens` (CLI, net10.0, tool `dev-context`), `RepoLens.Api` (net8.0 library, shares source via `Compile Include`), `RepoLens.Tests` |
| Packages | CLI and API 0.11.0 |
| Build | Release build and package-consumer validation clean; `TreatWarningsAsErrors`, 0 warnings |
| Tests | 78 passing |
| Commands | `init`, `baseline`, `status`, `verify`, `affected`, `doctor`, `explain`, `context`, `query`, `refs`, `mcp`, `benchmark`, `report`, `trend`, `schema`, `clean`, `reset` |
| Configuration | v2 current; v1 is migrated with backward-compatible defaults on `init` or baseline save |
| Persisted schema | v10, readable window v5–v10; executable manifest fixtures cover every readable version |
| Engine | Solution-aware MSBuild evaluation + Roslyn semantic compilations; gitignore/exclude filtering, commit/ref-aware diffs, bounded parallel indexing, and per-project cache reuse; no LLM or embeddings |
| CI | `windows-latest`, `ubuntu-latest`, and `macos-latest`; restore, Release build, test, pack API + CLI, net8 consumer, smoke test, retrieval benchmark |
| Agent integration | Two repository Codex skills under `.agents/skills`, the installable/user-level `start-coding-task` workflow, and a stdio MCP server with seven typed tools |

Latest local release-gate evidence: 78/78 tests passing; retrieval benchmark 100% file recall,
93.8% file precision, and 10,559 approximate tokens; smoke-test context reduction 97.31%.
The fresh 0.11.0 task baseline built the repository graph in approximately 10.9 s on this workstation;
warm evidence queries complete in roughly 0.5–0.7 s.

The deterministic core and the plan's correctness, retrieval, and scale prerequisites are now in
place. All seventeen actionable roadmap items are implemented. Remaining work before 1.0 is release
validation and policy rather than a missing planned feature: observe the hosted three-OS matrix,
complete the manual Blazor/WPF/MAUI package matrix, freeze public record/property names, and publish
the deprecation policy.

---

## 2. Tier 1 — correctness and scope

These change what the tool reports, so they come first.

### 2.1 Commit-aware change detection

**Problem.** `GitService.ChangedSince` (`RepoLens/Services/GitService.cs:79`) diffs only
`git status --porcelain` entries. `GitSnapshot.HeadCommit` is captured
(`RepoLens/Core/Models.cs:349`) but never participates in any diff.

Consequence: a file that was clean at baseline, then edited **and committed**, appears in neither
snapshot and is invisible to `affected`, `verify`, and `query --changed`. This is not a corner
case — `.agents/skills/start-coding-task/SKILL.md` step 6 reviews `git diff --cached`, so the
intended workflow expects commits during a task.

**Design.** Change the baseline's notion of "changed" to *committed delta + working-tree delta*:

1. Persist the baseline HEAD (already stored) as the diff base.
2. Add `git diff --name-status <baselineHead>..HEAD` to `GitService` and union its paths with
   the existing porcelain comparison.
3. Record each changed path's provenance (`committed`, `working-tree`, `both`) so `verify` output
   can distinguish "you committed this" from "this is uncommitted".
4. Handle the rebase/reset case: if the baseline HEAD is no longer an ancestor of HEAD, report an
   explicit `BaselineDiverged` state rather than a silently wrong delta.

**Touches.** `GitService`, `GitSnapshot`/`GitFileState` (schema bump), `VerificationService:26`,
`AffectedService`, `EvidenceQueryService:38`, `OutputFormatter`.

**Acceptance.** Smoke-test step: baseline → edit → commit → `affected` lists the file and its
tests; `verify` reports it as a committed change. Add a divergence test.

### 2.2 Baseline against a Git ref

**Problem.** A baseline can only be "now". There is no way to ask "what changed on this branch
versus `origin/main`", which blocks the natural next use case: reviewing a PR or a branch in CI.

**Design.** `dev-context baseline --from <ref>` and `dev-context verify --against <ref>`, where
the diff base is `git merge-base <ref> HEAD`. This reuses the 2.1 machinery: once the delta comes
from a commit range, the base commit is just a parameter. No baseline capture is required for the
ref case, which makes it usable from a clean CI checkout.

**Touches.** `CliArguments`, `DevContextApplication`, `DevContextApi` (new `ReviewAsync` or a
`DiffBase` option on the existing report methods), `GitService`.

**Acceptance.** In the smoke fixture: branch, commit two changes, `verify --against main` reports
exactly those files, their symbols, and their affected tests, from a clean working tree.

### 2.3 Honour ignore rules and add an exclude list

**Problem.** Excluded paths are hardcoded to `bin`, `obj`, `.git`, `.idea`, `.dev-context` in two
places — `RepositoryGraphService.IsGeneratedPath:217` and
`EvidenceQueryService.EnumerateCandidateFiles:710`. `.gitignore` is not consulted and there is no
configuration knob.

Observed on this repository: `artifacts/` is gitignored, yet **3,648 of 4,582 files** live under
it, and `artifacts/consumer/ApiConsumer.csproj` — a package-consumer test fixture produced by CI —
is indexed as a first-class project. It appears in `doctor`'s project list, in
`context architecture`'s `analyzedProjects`, and contributes a `Partial` completeness record that
reads as a real analysis gap in the repository under test.

**Design.**

1. Add `indexing.exclude` (glob list) and `indexing.respectGitignore` (default `true`) to
   `IndexingConfig` (`RepoLens/Configuration/DevContextConfig.cs:50`).
2. Resolve ignore state once per graph build via `git check-ignore --stdin -z` (deterministic,
   no reimplementation of gitignore semantics) and cache the result for the build.
3. Centralise path filtering into one `RepositoryFileFilter` used by the graph input hash, the
   project indexer, and the lexical evidence scan — the three places that currently disagree.
4. Report the effective exclude set in `doctor` so scope surprises are visible.

**Acceptance.** On this repository, `doctor` lists 3 projects; `context architecture` no longer
reports `artifacts/consumer` completeness; the graph input hash covers ~930 files rather than
4,582.

### 2.4 Scope discovery by solution, and support the other project types

**Problem.** `ProjectIndexer.BuildAsync:38` globs `**/*.csproj` and ignores `config.Solution`
entirely — the configured solution is used for build and test but not for discovery. Only
`.csproj` is discovered; `.fsproj` and `.vbproj` are invisible, and `.slnf` solution filters are
unsupported (`.slnx` is hashed as a repository input but never parsed).

**Design.**

- When `config.Solution` is set, enumerate projects from the solution (including transitive
  `ProjectReference` closure) and treat repository globbing as the fallback. Parse `.sln`,
  `.slnx`, and `.slnf`.
- Discover `.fsproj`/`.vbproj` for **ownership, project-reference, and test-selection** purposes.
  Be explicit that Roslyn C# semantic indexing does not apply to them, and emit a completeness gap
  rather than silently omitting the projects — the same honesty contract the Razor/XAML support
  already follows.

**Acceptance.** A fixture with a solution that excludes one on-disk project: that project is
absent from `doctor` and from `affected`. A fixture with an F# project referenced by a C# test
project: the F# project appears as an owner and propagates to the test project.

---

## 3. Tier 2 — retrieval and agent integration

This is where the product value is; Tier 1 makes it trustworthy.

### 3.1 Structural queries

**Problem.** `query` is lexical scoring plus undirected graph expansion
(`EvidenceQueryService.ScoreSymbols:279`, `ExpandThroughGraph:358`). The dependency index already
holds typed, direction-aware, semantically-resolved edges with exact use-site spans — but no
command exposes them directly. An agent cannot ask "who calls this" without hoping the lexical
scorer surfaces the callers.

**Design.** A `dev-context refs` command over `DependencyIndex.Symbols`, with the same token
budget, bundle ID, sufficiency decision, and abstention contract as `query`:

```
dev-context refs "EvidenceQueryService.EvaluateSufficiency" --relation callers
dev-context refs "IProcessRunner" --relation implementers
dev-context refs "RepoLens/Services/GitService.cs" --relation tests-covering
```

Relations map onto existing edge kinds: `callers`, `callees`, `implementers`, `implementations`,
`overrides`, `subtypes`, `constructors-of`, `readers`, `writers`, `tests-covering`,
`injected-into`. Symbol resolution should accept a fully-qualified name, a bare name (with an
ambiguity list when several match), or `file:line`.

**Why it matters.** These are exact answers where `query` gives ranked guesses, and they cost far
fewer tokens because no scoring fallback is needed.

**Touches.** New `SymbolReferenceQueryService`, `CliArguments`, `DevContextApplication`,
`DevContextApi`, plus benchmark cases with `expectedRelationships`.

### 3.2 MCP server

**Status: implemented in 0.10.0.** `dev-context mcp` uses the official C# SDK's stdio transport,
advertises the seven planned tools with generated input/output schemas and structured results, and
keeps one `DevContextApi` graph session alive. Every graph access checks the existing repository
input hash before reusing memory; the integration suite negotiates with a real SDK client, lists
the tools, and calls typed `status` and `explain` results.

**Problem.** Every agent invocation is a fresh process: ~0.7 s warm, dominated by re-reading
4.3 MB of cache JSON (`.dev-context/cache/{projects,symbols,dependencies}.json`). Integration is
also Codex-specific — `.agents/skills/*/agents/openai.yaml`.

**Design.** A `dev-context mcp` stdio server exposing `status`, `affected`, `explain`, `context`,
`query`, `refs`, and `verify` as MCP tools, holding the graph in memory across calls and
invalidating on the existing input hash. This makes RepoLens usable from Claude Code, Cursor, and
anything else speaking MCP, and removes the per-call deserialization cost.

Return the typed JSON payloads that already exist — no new data model. Keep `verify` as an
explicit tool rather than something invoked implicitly, since it builds and runs tests.

**Touches.** New `RepoLens.Mcp` project (or a CLI subcommand), reusing `DevContextApi` unchanged.

### 3.3 Sharper analysis-gap reporting

**Problem.** Gap strings are shapeless. `RepoLens.Api`'s own compilation reports
`"The semantic compilation contains 12 error diagnostic(s)."` — the record already carries
`DiagnosticIds` (`RepoLens/Core/Models.cs:603`), but nothing surfaces which rules, in which files.
The tool's central promise is distinguishing absence from uncertainty, and here it reports
uncertainty without saying where.

**Design.** Include the top N diagnostic IDs with file and count in the gap text and in
`doctor`'s recommendation; add a `dev-context doctor --explain-gaps` mode that prints the
per-project diagnostic breakdown. Also: **RepoLens should be able to fully analyse itself** — the
12 errors on `RepoLens.Api` are a real signal (most likely the shared-source `Compile Include`
layout confusing reference resolution) and should be driven to zero as part of this work.

### 3.4 Coverage collection

**Status: implemented in 0.11.0.** Configuration v2 adds opt-in `tests.collectCoverage`; test
runs persist every produced Cobertura report independently of raw TRX retention, and context uses
the latest reports automatically unless `--coverage` supplies an explicit override. Missing
collector output remains visible through structured coverage state.

**Problem.** `--coverage` accepts an externally produced Cobertura file only; `TestService` never
collects coverage, so hotspot ranking's coverage input is usually absent in practice.

**Design.** `tests.collectCoverage` config that adds `--collect:"XPlat Code Coverage"` to the test
invocation and feeds the produced report into hotspot ranking automatically. Keep the external
`--coverage` path for CI systems that already produce one.

---

## 4. Tier 3 — performance and scale

Current timings are fine at 4 projects. These are the changes that keep them fine at 50.

### 4.1 Parallel indexing

`ProjectIndexer.BuildAsync:45` spawns one `dotnet msbuild` process per project, serially;
`SymbolIndexer`'s per-project passes are likewise serial. Both are embarrassingly parallel.
Parallelise with a bounded degree (`indexing.maxParallelism`, default `min(ProcessorCount, 8)`),
and keep every output sorted before persistence so determinism — which the benchmark gate
enforces — is unaffected.

### 4.2 Per-project cache granularity

`ComputeInputHashAsync:59` hashes the content of every `.cs` file in the repository into a single
key. One keystroke invalidates the whole graph: full MSBuild evaluation, full Roslyn compilation,
full source-generator execution. On this repository that is the 13.8 s cold path versus 0.74 s
warm; the cost scales with repository size while the edit does not.

Move to a per-project input hash with a project-level cache, so an edit re-indexes only the owning
project and its dependents. The reverse project-reference closure needed to decide "and its
dependents" already exists in `ProjectOwnershipResolver.ExpandAffectedProjects`.

### 4.3 Stop forcing `doctor` cold

`DevContextApi.DoctorAsync:223` builds the graph with `Cache = new CacheConfig { Enabled = false }`,
which costs a measured 14.4 s on every run. Use the cache by default and add
`doctor --no-cache` for the diagnostic case where a stale cache is the suspect.

---

## 5. Tier 4 — ergonomics and release hardening

- **`dev-context init` (implemented).** Writes validated configuration without creating or
  replacing a baseline and makes scope, parallelism, and coverage knobs discoverable.
- **Config migration (implemented in 0.11.0).** `ConfigLoader` originally hard-rejected any `version != 1`
  (`DevContextConfig.cs:87`). It now reads v1, applies backward-compatible defaults, and writes v2
  on `init` or the next baseline save; unknown versions fail explicitly.
- **Finer exit codes (implemented in 0.11.0).** `query` previously exited `0` even when the bundle was `Insufficient` with
  `ShouldAbstain = true`. Evidence abstention now exits `3`, benchmark acceptance failure exits
  `4`, and regression/usage semantics remain `1`/`2`.
- **Cross-platform CI (implemented in 0.11.0).** The workflow was `windows-latest` only, while the API package targets
  "net8.0 or later". The full build/test/package/consumer/smoke/benchmark gate now runs on Windows,
  Ubuntu, and macOS, and the smoke harness no longer passes Windows-only paths.
- **Baseline history and trends (implemented in 0.11.0).** Reports were timestamped and retained (default 20) but nothing
  compared them. Versioned JSON sidecars and `dev-context trend` now expose diagnostics, failing
  tests, hotspot churn, and coverage deltas within comparable purpose/scope/target series.
- **Published JSON schemas (implemented in 0.11.0).** `dev-context schema` now emits draft 2020-12 contracts for the
  persisted documents so non-.NET consumers can validate current artifacts. Executable manifest
  fixtures exercise every readable persisted version from v5 through v10.

---

## 6. Suggested sequencing

| Order | Work | Rationale |
| --- | --- | --- |
| 1 | 2.3 exclude/ignore, 2.1 commit-aware diff | Both change reported facts; everything downstream inherits the fix. Independent of each other. |
| 2 | 2.2 baseline against a ref | Builds directly on 2.1's commit-range diff. |
| 3 | 3.3 gap reporting, 4.3 `doctor` cache | Small, self-contained; make the next stages diagnosable. |
| 4 | 3.1 structural queries | Highest retrieval value; needs no schema change. |
| 5 | 2.4 solution scoping and other project types | Schema bump; pairs naturally with 4.1/4.2. |
| 6 | 4.1, 4.2 parallel and per-project caching | Pure performance; determinism is guarded by the benchmark gate. |
| 7 | 3.2 MCP server | Best done once the tool surface (`refs`) has settled. |
| 8 | Tier 4 | Release hardening ahead of 1.0. |

Items 2.1, 2.2, 2.4, and 3.1 add persisted fields and so need a schema bump to v9, with the
readable window extended and — per `release-readiness.md` — a migration fixture per readable
version.

## 7. Explicitly out of scope

Consistent with the existing design principle that RepoLens supplies deterministic facts and the
agent supplies reasoning:

- LLM-based or embedding-based retrieval. Lexical plus semantic-graph selection is auditable and
  reproducible; the benchmark gate depends on that reproducibility.
- Interprocedural data-flow analysis. Already documented as out of bounds
  (`dev-context-cli.md:338`) and a poor fit for the deterministic-graph contract.
- Automated fix application. RepoLens reports; the agent edits.
