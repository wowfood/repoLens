# Dev Context — Sequential Codex Prompts

These prompts are intended to be run sequentially in Codex on your desktop.

## Implementation status

Stage 1 is now feature-complete for its first release candidate. The CLI supports repository
discovery, versioned configuration and persistence, Git/build/test/analysis baselines, status and
delta verification, cached MSBuild/Roslyn semantic indexes, symbol-aware affected-test selection,
project ownership for C#, Razor, XAML, content, and shared MSBuild inputs, normalized
formatter/Qodana findings, configured cleanup, reset, text/JSON output, and regression exit codes.

The engine is also packaged as the net8-compatible `RepoLens.Api` 0.11.0 library. It exposes
preflight diagnostics, exact MSBuild ownership explanations, richer semantic dependency edges,
typed code metrics, source type/member definitions, deterministic hotspots,
full/changed/project/path scopes, purpose-specific change/architecture/build/risk contexts,
automatic or explicit Cobertura coverage, bounded Git history, Markdown reports with structured
trend history, published JSON Schema contracts, and deterministic,
token-budgeted source-evidence queries. Evaluated framework/package references now feed one
semantic compilation per target framework. Source generators can run for trusted repositories,
generated sources are retrievable evidence, and completeness records expose unresolved references,
compiler errors, and remaining markup-generated C# gaps instead of silently treating missing edges
as proof of absence. Razor and XAML add bounded, confidence-labelled component, code-behind,
binding, command, event, and data-context relationships.
Evidence bundles now make an explicit sufficient/partial/insufficient decision, tell consumers when
to abstain, and retain relationship origin, target framework, and exact use-site spans. C# calls,
construction, member access, callbacks, and local functions use Roslyn operation semantics.
The CLI delegates to this API, and an independent net8 package consumer is part of release
verification.

See [`dev-context-cli.md`](dev-context-cli.md) for current usage, configuration, storage schemas,
and known first-release limitations.

Stage 2 is implemented as the repository-local
[`dev-context-baseline`](../../.agents/skills/dev-context-baseline/SKILL.md) Codex skill. It
establishes immutable task context, preserves pre-existing conditions, uses affected-code data to
focus work, and verifies only task-introduced regressions. It is now the explicit lower-level
primitive used when baseline management is the whole request.

Stage 3 is implemented as the repository-local
[`start-coding-task`](../../.agents/skills/start-coding-task/SKILL.md) Codex skill. It is the primary
coding-task workflow and combines immutable baseline handling, focused reconnaissance, minimal
implementation, configured cleanup, delta verification, and final Git review.

Repository setup can now be made explicit with `dev-context init`. Project discovery, graph
hashing, and lexical evidence share one deterministic inventory that respects `.gitignore` by
default and supports repository-relative `indexing.exclude` globs.

Project evaluation and Roslyn indexing use bounded parallel work with a configurable ceiling.
Structural cache misses reuse unchanged per-project graph entries; changes invalidate the owning
project and its reverse dependents while independent projects stay warm.

The 0.11 CLI also exposes `status`, `affected`, `explain`, `context`, `query`, `refs`, and explicit
`verify` tools through a standards-compatible `dev-context mcp` stdio server. One API/graph session
is retained for the client process, with existing repository input hashes invalidating stale
in-memory graph data.

Release hardening now includes v1-to-v2 configuration migration, distinct evidence and benchmark
exit codes, v5–v11 persisted-schema fixtures, and Windows/Linux/macOS CI coverage. The `trend`
command compares retained diagnostic, test, churn, and hotspot-coverage metrics; `schema` emits
draft 2020-12 contracts without requiring a repository.

Change detection now unions commits made after the baseline with working-tree changes and records
their provenance. `verify --against <ref>` provides stateless merge-base review for CI, while
`baseline --from <ref>` carries an existing branch delta into the normal task workflow.

See [`testing.md`](testing.md) for the local verification matrix, isolated regression smoke test,
and raw-versus-compact context/token comparison procedure.

See [`agent-setup.md`](agent-setup.md) for connecting a coding agent: installing the CLI, the tracked
`.mcp.json`, the MCP tools and prompts, and the abstention contract an agent has to honour.

See [`evidence-benchmarks.md`](evidence-benchmarks.md) for the retrieval-recall and token-budget
contract used to evolve task-specific evidence selection without silently degrading it.

See [`release-readiness.md`](release-readiness.md) for the CI gates, schema/API compatibility
contract, trusted-repository boundary, and manual Blazor/WPF/MAUI release checks.

See [`stage-1-verification.md`](stage-1-verification.md) for the requirement-to-evidence checklist
used to assess the first release candidate.

See [`stage-2-verification.md`](stage-2-verification.md) for skill validation, discovery, and
user-wide installation instructions.

See [`stage-3-verification.md`](stage-3-verification.md) for the task-workflow checklist, invocation
hierarchy, validation evidence, and a manual acceptance test.

## Recommended order

### 1. `01-build-dev-context-cli.md`

Run this first in the repository where you want the `dev-context` tooling developed.

It asks Codex to build the deterministic local CLI responsible for:

- Git baselines
- builds
- tests
- Roslyn/static-analysis diagnostics
- baseline/current delta comparison
- structural repository indexing
- affected-code/test discovery
- optional cleanup
- optional Qodana support

Get this working and tested before proceeding.

### 2. `02-create-repository-baseline-skill.md`

Implemented as `$dev-context-baseline` under `.agents/skills/dev-context-baseline`.

It is the small baseline/orchestration skill that calls `dev-context` at the beginning and end of
coding tasks.

### 3. `03-create-start-coding-task-skill.md`

Implemented as `$start-coding-task` under `.agents/skills/start-coding-task`.

It is the higher-level coding workflow skill that combines baseline creation, reconnaissance,
implementation guidance, cleanup, verification, and final diff review.

## Intended day-to-day workflow

At the beginning of a new logical coding task:

```text
initialize configuration (once per repository)
      ↓
create baseline
      ↓
inspect baseline/status
      ↓
perform requested work
      ↓
run deterministic cleanup
      ↓
verify against baseline
      ↓
review final diff
      ↓
finish task
```

When moving to a genuinely new task/context, create a new baseline.

## Design principle

Keep Codex responsible for reasoning and code changes.

Keep `dev-context` responsible for deterministic facts such as:

- what already failed
- what currently fails
- what changed
- what projects reference each other
- what diagnostics exist
- which tests are likely affected

This prevents the LLM repeatedly rediscovering facts that normal developer tooling can establish more reliably.
