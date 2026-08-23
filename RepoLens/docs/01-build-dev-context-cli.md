# Dev Context CLI Implementation Prompt

Create a local developer-context CLI application for .NET repositories.

The purpose of the application is to perform deterministic repository inspection before an AI coding agent begins work, persist that information as a baseline, and later compare the modified repository against that baseline.

The application must reduce the amount of repository discovery, test execution interpretation, static-analysis interpretation, and repeated codebase searching that an LLM needs to perform.

## Core principles

1. Deterministic tools should perform deterministic analysis.
2. The LLM should consume concise summaries and deltas rather than raw command output wherever possible.
3. A baseline must remain immutable for the lifetime of a coding task.
4. Existing warnings and failing tests must be distinguishable from regressions introduced during the task.
5. Commands should be usable by humans, Codex/Claude-style coding agents, CI pipelines, and scripts.
6. Do not require an LLM to construct repository metadata.
7. Prefer Roslyn/MSBuild/compiler information over regex-based source parsing.
8. Avoid modifying source files during baseline generation.
9. All stored data must be reproducible and safe to delete.
10. The tool must work when invoked from anywhere underneath a repository by locating the repository root.

## CLI name

Use:

```bash
dev-context
```

Implement the following commands.

### `dev-context baseline`

Create a fresh baseline of the current repository.

It should collect, where applicable:

- repository root
- current branch
- HEAD commit SHA
- current Git status
- existing modified/untracked files
- solution/project files
- target frameworks
- SDK version
- package references
- project-to-project references
- compiler settings
- nullable settings
- language version
- build result
- compiler diagnostics
- static-analysis diagnostics
- unit/integration test results
- existing failing tests
- symbol/dependency information
- test-project relationships
- timestamps and command durations

Store raw machine-readable results separately from a concise summary.

A baseline must have a unique identifier.

If a baseline already exists, require an explicit `--replace` option before overwriting it.

Do not format, clean up, fix, or otherwise modify application source code while generating a baseline.

### `dev-context status`

Return a concise summary suitable for insertion directly into an LLM context.

Example structure:

```text
Repository
  Branch: feature/foo
  Baseline: a4d9...
  HEAD at baseline: 22fe...
  Working tree was dirty at baseline: yes

Solution
  Projects: 17
  Production: 12
  Tests: 5
  Framework: net10.0

Baseline health
  Build: PASS
  Tests: 1,284 total / 1,279 passed / 5 failed
  Static analysis: 0 errors / 17 warnings

Existing failures
  FooTests.Should...
  BarTests.Should...

Existing modified files
  src/Foo.cs
```

Keep this deliberately compact.

### `dev-context verify`

Compare the current repository against the stored baseline.

Report deltas rather than simply reporting current totals.

Include:

- files changed since baseline
- symbols changed where practical
- new compiler errors
- resolved compiler errors
- new analyzer warnings
- resolved analyzer warnings
- new failing tests
- tests that were already failing
- previously failing tests that now pass
- build status
- formatter/cleanup status if configured

The primary output should make regressions obvious.

Return an appropriate non-zero process exit code when regressions exist.

### `dev-context affected`

Given the changes since baseline, determine the likely affected projects, source files, symbols, and tests.

Prefer deterministic relationships.

Use information including:

- project references
- type references
- inheritance
- interface implementations
- method references where practical
- test-project references
- naming relationships only as a fallback

Output both JSON and a concise human-readable form.

This command should eventually allow an agent to run targeted tests before running the entire test suite.

### `dev-context clean`

Perform only explicitly configured deterministic source cleanup.

Examples:

- `dotnet format`
- safe Roslyn fixes
- repository-configured formatting commands

Cleanup must never execute during `baseline`.

After cleanup, report exactly which files were modified.

Do not hide cleanup-generated modifications from Git.

### `dev-context reset`

Delete generated baseline/context information.

This must not modify repository source files.

## Persistence

Use a repository-local directory:

```text
.dev-context/
```

Recommended structure:

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
    git.json
    build.json
    tests.json
    analysis.json
  indexes/
    projects.json
    symbols.json
    dependencies.json
  summary.md
```

Design the schemas deliberately rather than simply serialising internal implementation classes.

Include a schema/version field so formats can evolve.

Do not store enormous raw logs indefinitely. If raw logs are retained, make this configurable.

## Static analysis

Support an extensible provider model.

Initially support:

1. compiler/Roslyn diagnostics through normal .NET build tooling
2. `dotnet format` verification where applicable
3. optional Qodana integration when installed/configured

Do not require Qodana for normal operation.

Normalise diagnostics into a common representation containing approximately:

```json
{
  "tool": "roslyn",
  "severity": "warning",
  "rule": "CA2007",
  "file": "src/Foo.cs",
  "line": 47,
  "column": 12,
  "message": "..."
}
```

Generate stable identities for diagnostics so baseline/current results can be compared reliably.

## Tests

Support `dotnet test`.

Do not rely solely on console text parsing if structured test result formats are available.

Persist individual test outcomes.

Normalise test identity so failures can be compared between baseline and current runs.

Distinguish:

- existing failure
- new failure
- resolved failure

Provide configuration for repositories where the full test suite is prohibitively expensive.

## Repository graph

Do not use an LLM to generate the graph.

Build a deterministic repository representation from MSBuild and Roslyn where feasible.

Start with:

- solution -> projects
- project -> project references
- project -> package references
- project -> source files
- type -> namespace
- type -> base type
- type -> implemented interfaces
- type -> containing project
- test project -> production project references

Design this so richer relationships can subsequently be added:

- method calls
- symbol references
- dependency injection registrations
- test-to-production-symbol relationships

Do not introduce vector embeddings in the first implementation.

Keep the structural graph separate so semantic/vector retrieval can be added later.

## Configuration

Support repository configuration such as:

```json
{
  "version": 1,
  "solution": "MySolution.sln",
  "tests": {
    "enabled": true,
    "baselineMode": "all",
    "verifyMode": "affected-first"
  },
  "analysis": {
    "roslyn": true,
    "qodana": false
  },
  "cleanup": {
    "command": "dotnet format",
    "enabled": true
  }
}
```

Do not hardcode assumptions that prevent this being reused across multiple repositories.

## Performance

Record timings for expensive stages.

Avoid repeating work when repository inputs have not changed.

It should eventually be possible to cache structural indexes based on:

- source hashes
- project hashes
- HEAD SHA
- dependency/configuration hashes

However, correctness is more important than caching in the first implementation.

## Output modes

Every significant command should support:

```bash
--format text
--format json
```

Text is optimised for humans and LLM contexts.

JSON is optimised for tooling.

Avoid dumping thousands of diagnostics into normal text output.

Provide counts plus newly relevant information.

## Implementation quality

Use a clean architecture appropriate for a developer tool, without creating unnecessary abstraction.

Include:

- unit tests
- integration tests where practical
- cancellation-token support
- process execution abstraction
- sensible error handling
- logging
- deterministic JSON output
- documentation

Do not swallow failures from external tools.

Clearly distinguish:

- command unavailable
- command failed
- analysis produced findings
- repository failed to build/test

These are different states.

## Development approach

Implement this incrementally.

Start with:

1. repository discovery
2. configuration
3. Git baseline
4. build baseline
5. test baseline
6. diagnostic normalisation
7. baseline persistence
8. verification/delta calculation
9. concise status output
10. structural repository indexing
11. affected-test calculation
12. optional Qodana integration
13. optional cleanup support

At each stage, add tests before moving to unnecessary sophistication.

Do not implement vector embeddings in the initial version.

Once the deterministic structural system is stable, leave clear extension points for semantic indexing.
