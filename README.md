# RepoLens

Deterministic repository baselines, affected-code discovery, and regression verification for
.NET coding tasks — built so an AI coding agent (or a human) gets facts instead of guesses about
what changed, what depends on what, and what broke.

RepoLens ships as two packages built from the same source: `DevContext.Cli` (the `dev-context`
.NET tool) and `RepoLens.Api` (a net8-compatible library for embedding the same engine
elsewhere). The engine is solution-aware MSBuild evaluation plus Roslyn semantic compilation —
no LLM calls, no embeddings, fully reproducible output.

## What it does

- Captures a baseline of a repository's git state, build, tests, and structural graph.
- Reports exactly what changed since that baseline — committed and working-tree, or against any
  git ref — and which projects, symbols, and tests are affected.
- Answers structural questions directly (`refs ... --relation callers`) instead of only ranked
  lexical search.
- Runs as a stdio MCP server (`dev-context mcp`) so agents can hold one graph session across
  calls instead of re-parsing the repository on every invocation.

See [`RepoLens/docs/README.md`](RepoLens/docs/README.md) for the full documentation index,
including the command reference, API docs, testing procedure, retrieval benchmarks, and release
readiness criteria.

## Quickstart

Requires the .NET 8 and .NET 10 SDKs.

```bash
git clone https://github.com/wowfood/repoLens.git
cd repoLens
dotnet build RepoLens.sln --configuration Release

# Pack and install the CLI as a local global tool
dotnet pack RepoLens/RepoLens.csproj --configuration Release --output artifacts/packages
dotnet tool install --global --add-source ./artifacts/packages DevContext.Cli

# In the repository you want to analyze:
dev-context init
dev-context baseline
dev-context status
```

Full command reference: [`RepoLens/docs/dev-context-cli.md`](RepoLens/docs/dev-context-cli.md).

## Status

Currently 0.11.0, pre-1.0. The functional roadmap is complete; remaining work before 1.0 is
release validation and API-stability policy. See [`plan.md`](plan.md) for the roadmap and
[`RepoLens/docs/release-readiness.md`](RepoLens/docs/release-readiness.md) for the release gate
and compatibility contract.

## License

MIT — see [`LICENSE`](LICENSE).
