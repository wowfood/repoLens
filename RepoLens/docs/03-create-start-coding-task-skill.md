# Start Coding Task Skill Prompt

Create an enhanced Codex-compatible "start coding task" skill built around the local `dev-context` CLI.

Its purpose is to establish a baseline, perform initial repository reconnaissance, guide implementation, and verify the result against the original baseline.

The resulting skill should implement the following workflow.

## Step 1 — Establish baseline

Run:

```bash
dev-context baseline
```

Do not replace an existing baseline unless explicitly instructed to start a new task.

Then obtain the concise repository state:

```bash
dev-context status
```

Treat the baseline as the definition of repository state before this task began.

## Step 2 — Record pre-existing conditions

Before modifying code, understand:

- whether the repository currently builds
- which tests already fail
- which analyzer diagnostics already exist
- which files were already modified before this task began
- the current branch and HEAD
- relevant solution/project architecture

Pre-existing problems are not regressions caused by this task.

Do not opportunistically fix unrelated pre-existing problems.

## Step 3 — Analyse the requested work

Identify likely entry points from the user's request.

Use structural repository information before broad source scanning where appropriate.

Use:

```bash
dev-context affected
```

when existing changes or identified symbols make affected-code analysis useful.

Retrieve only the source required to understand the task.

Expand outward through dependencies when necessary rather than loading large unrelated portions of the repository.

## Step 4 — Perform the work

Make the smallest coherent change that satisfies the request.

Preserve repository conventions and architectural boundaries.

Do not introduce unrelated cleanup or refactoring.

Run focused tests during development where useful.

## Step 5 — Deterministic cleanup

Once semantic changes are complete, run:

```bash
dev-context clean
```

if cleanup is configured.

Review any modifications produced by cleanup.

Cleanup must not introduce unrelated semantic changes.

## Step 6 — Verify against baseline

Run:

```bash
dev-context verify
```

Focus on the delta from the baseline.

Investigate and correct any regression introduced by the task.

Do not fail the task merely because a baseline failure remains present.

If corrections are required, make them and run verification again.

## Step 7 — Final review

Review the final Git diff.

Check that:

- every changed file is relevant
- no user modifications were accidentally overwritten
- no unexpected files were introduced
- there are no unexplained new warnings
- there are no unexplained new test failures
- the implementation satisfies the requested behaviour

When reporting completion, distinguish clearly between:

- successful verification
- pre-existing repository problems
- regressions that could not be resolved
- tests or analysis that could not be executed

## Baseline lifecycle

The baseline should have a unique ID and remain immutable throughout the logical task.

A new logical task/context should create a new baseline rather than mutating the previous one.

Implement/document this skill in the appropriate format for the local Codex environment.
