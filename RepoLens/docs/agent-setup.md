# Using RepoLens from a coding agent

RepoLens is meant to be reached by an agent, not only by a human at a terminal. There are two ways
in, and they are not alternatives: the MCP server is the vendor-neutral one, and skills are a
per-vendor convenience layer on top of it.

## 1. Install the CLI

```bash
dotnet pack RepoLens/RepoLens.csproj --configuration Release --output artifacts/packages
dotnet tool install --global --add-source ./artifacts/packages DevContext.Cli
```

`dev-context` must be on `PATH` for the MCP configuration below to launch it. Confirm with
`dev-context doctor` in the repository you want to analyze.

## 2. Connect the MCP server

`dev-context mcp` speaks the Model Context Protocol over stdio. This repository ships a tracked
[`.mcp.json`](../../.mcp.json):

```json
{
  "mcpServers": {
    "repolens": {
      "command": "dev-context",
      "args": ["mcp"]
    }
  }
}
```

Copy it into any repository you want an agent to analyze. The client must launch the process with
that repository as its working directory — RepoLens discovers the repository root from there.

Clients differ in where this file lives. Claude Code reads a project-level `.mcp.json`; other
clients keep an equivalent block in their own settings. The server itself is identical either way.

### What the client gets

Nine tools, each returning typed structured content:

| Tool | Purpose |
| --- | --- |
| `baseline` | Record the current state as the comparison point for a task. Writes to `.dev-context/`. |
| `status` | The stored baseline summary, without running anything. |
| `affected` | What changed since the baseline, and the declarations, projects, and tests it touches. |
| `query` | Ranked, token-bounded source evidence for a task or open question. |
| `refs` | Exact, direction-aware structural references for one symbol. |
| `explain` | Which evaluated projects own a path, and what depends on them. |
| `context` | Bounded change, architecture, build, or risk narrative. |
| `doctor` | Whether the SDK, Git, and configured providers are usable. |
| `verify` | Rebuild, run tests and analyzers, report regressions. Slow; writes build artifacts. |

`baseline` and `doctor` exist so the protocol is self-sufficient. `status`, `affected`, and `verify`
all fail without a baseline, and the remedy they name used to be a shell command — which an agent
speaking only MCP does not have.

`verify` is never invoked by a read-only tool. It is slow and writes normal build output, so the
client has to choose it deliberately.

Two prompts ship alongside the tools: `coding-task` runs the full baseline-to-verification workflow,
and `ground-question` answers a repository question from retrieved evidence. They carry the same
guidance as the skill files, so a client that supports prompts needs no vendor-specific setup.

The server sends instructions at initialization describing when to prefer `refs` over `query`, and
the abstention contract below.

### Payload bounds

`query` returns the token-bounded prompt plus the coordinates it was built from — not the excerpts
twice. `context` returns the rendered narrative and the decision-relevant records; pass
`includeSymbols: true` when you actually need the symbol list, which is otherwise the largest part of
the report.

## 3. The abstention contract

This is the part worth configuring an agent around.

Every `query` and `refs` result carries an evidence decision — `Sufficient`, `Partial`, or
`Insufficient` — an explicit `shouldAbstain` flag, and the analysis gaps behind it. The CLI signals
the same thing with exit code `3`.

**When a result abstains, it must not become a claim.** In particular, an empty `refs` result is
proof of absence only when the relevant compilation records are complete. Otherwise it means "not
found in what could be analysed", which is a weaker and different statement. The gaps name what was
missing: an unbuilt project, a target framework that was not indexed, markup-generated C# that was
unavailable.

Relationships are strong evidence, not proof. Reflection, dependency-injection wiring, Razor, XAML,
and generated code can reach past the graph.

## 4. Repository-level agent instructions

[`AGENTS.md`](../../AGENTS.md) at the repository root states the workflow and the abstention rule for
any agent, with `CLAUDE.md` pointing at it so the guidance cannot drift between vendors. Copy both
into repositories where you want the same policy.

Per-machine assistant directories (`.claude/`, `.codex/`, `.junie/`) stay git-ignored: they are
user settings, not repository policy.

## 5. Skills

Two repository-local skills wrap the same workflow for clients that prefer them:

- [`start-coding-task`](../../.agents/skills/start-coding-task/SKILL.md) — the full coding-task
  workflow.
- [`dev-context-baseline`](../../.agents/skills/dev-context-baseline/SKILL.md) — the lower-level
  primitive, for when baseline management is the whole request.

Prefer the MCP prompts when the client supports them; they reach every client that speaks MCP,
whereas a skill file has to be written once per vendor.

## Session behaviour

The MCP process holds one `DevContextApi` session and one graph in memory across calls. Graph-backed
tools recompute the deterministic repository input hash before reusing anything, so source, project,
solution, SDK, or configuration changes invalidate stale data. Concurrent read-only calls share a
single graph build rather than each triggering a full evaluation.

Protocol messages are the only content written to standard output; `--verbose` diagnostics stay on
standard error.
