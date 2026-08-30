# Working in this repository

`dev-context` is the source of repository facts here. It answers what exists, what depends on what,
and what broke, from MSBuild evaluation and Roslyn semantic compilation — deterministically, with no
LLM and no embeddings. Prefer it over grepping: a search tells you what matched, not what it missed.

The tool is available as a CLI and as an MCP server (`dev-context mcp`, configured in `.mcp.json`).
The commands below are the CLI form; the MCP tools have the same names.

## Before changing anything

```bash
dev-context init      # once per repository, if .dev-context/config.json is absent
dev-context baseline  # the comparison point for this task — do not replace it mid-task
dev-context status
```

The baseline is immutable for the life of a logical task. Pre-existing failures it records are not
your regressions, and `baseline --replace` discards the thing your work is measured against.

## Finding your way around

| You need | Use |
| --- | --- |
| Source relevant to a task or question | `dev-context query "<task>"` |
| An exact structural relationship | `dev-context refs "<symbol>" --relation callers` |
| Which projects own or are affected by a path | `dev-context explain <path>` |
| What changed since the baseline | `dev-context affected` |

Prefer `refs` whenever the question has an exact answer — who calls this, what implements this,
which tests cover this. Relations: `callers`, `callees`, `implementers`, `implementations`,
`overrides`, `subtypes`, `constructors-of`, `readers`, `writers`, `tests-covering`, `injected-into`.

## The rule that matters

**Exit `3`, or `shouldAbstain: true`, means do not assert.** Results carry a
`Sufficient`/`Partial`/`Insufficient` decision and the analysis gaps behind it. An empty `refs`
result proves absence only when the relevant compilation records are complete; otherwise it means
"not found in what could be analysed". Widen the search and say the answer was incomplete rather
than presenting an abstention as a finding.

Relationships are strong evidence, not proof. Reflection, dependency-injection wiring, Razor, XAML,
and generated code can reach past the graph.

## Before reporting done

```bash
dev-context clean    # only if cleanup.enabled is true in .dev-context/config.json
dev-context verify
```

Exit codes: `0` no regressions, `1` regressions found, `2` could not execute reliably (unavailable
command, timeout, or an artifact this version cannot read), `3` evidence insufficient — abstain,
`4` retrieval benchmark acceptance failure.

Never describe a skipped or failed check as passing. A run that could not execute is not a run that
found nothing.

## Developing RepoLens itself

```bash
dotnet build RepoLens.sln --configuration Release   # warnings are errors
dotnet test RepoLens.sln --configuration Release
dotnet format RepoLens.sln --verify-no-changes      # local-only check, not enforced in CI
pwsh -File scripts/run-smoke-test.ps1
pwsh -File scripts/run-benchmark-fixture.ps1        # the retrieval gate
```

`RepoLens/docs/README.md` is the documentation index. Retrieval changes must keep both evidence
corpora green — see `RepoLens/docs/evidence-benchmarks.md`, which also explains why benchmark
fixtures have to be restored.
