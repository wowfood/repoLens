# Repository Baseline Skill Prompt

Create a Codex-compatible repository skill/instruction that uses the local `dev-context` tool to establish deterministic repository context before beginning a coding task.

The resulting skill should follow these rules.

## When invoked

Before analysing or modifying application source code, run:

```bash
dev-context baseline
```

If the command reports that an existing baseline already exists, do not replace it automatically.

Then run:

```bash
dev-context status
```

Use the returned information as authoritative context for:

- existing build state
- existing failing tests
- existing analyzer diagnostics
- pre-existing working-tree modifications
- repository/project structure

Do not attempt to fix pre-existing failures or diagnostics unless they are relevant to the user's requested work.

Do not interpret an existing failure as a regression introduced during this task.

Do not run source cleanup before creating the baseline.

## During the task

Use normal repository tools as required.

Prefer repository information already available through `dev-context` over repeatedly rediscovering the same information.

When determining affected code or tests, use:

```bash
dev-context affected
```

before performing broad repository searches where appropriate.

Do not treat the repository graph as infallible. Inspect source code when semantic understanding is required.

## Before considering the task complete

If cleanup is enabled for the repository, run:

```bash
dev-context clean
```

Then run:

```bash
dev-context verify
```

Address regressions introduced by the current task.

A regression includes:

- a new build failure
- a new compiler diagnostic classified as an error
- a new analyzer diagnostic that violates repository policy
- a newly failing test

Do not require pre-existing failures to be fixed unless the requested work explicitly concerns them.

After correcting regressions, rerun:

```bash
dev-context verify
```

Use the final verification result when summarising completed work.

## Important

The baseline represents repository state at the beginning of the current logical task.

Do not regenerate or replace it during the task.

A new baseline should only be created when beginning a new logical task/context.

Implement/document this skill in the appropriate format for the local Codex environment. Keep the skill itself small: orchestration belongs in the skill; deterministic analysis belongs in `dev-context`.
