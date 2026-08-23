---
name: start-coding-task
description: "Run a complete coding-task workflow in a Git/.NET repository with dev-context: establish an immutable baseline, record pre-existing state, perform focused reconnaissance, implement the smallest coherent change, clean when configured, verify the task delta, and review the final diff. Use when starting or continuing an implementation, fix, or refactor where repository files will change. Do not use for read-only explanations or repositories without dev-context unless explicitly requested."
---

# Start Coding Task

Treat the invocation as one logical coding task. Use `dev-context` for deterministic repository
facts and normal repository tools for reasoning, source inspection, editing, and focused tests.

## 1. Establish the task baseline

1. Before analysing or modifying application source, run:

   ```bash
   dev-context baseline
   ```

   Do not run cleanup first.

2. Handle the result precisely:

   - If a baseline is created, keep its ID for the whole task.
   - If a baseline already exists and this request continues that logical task, use it unchanged.
   - If the user explicitly started a new logical task, run `dev-context reset`, then create a
     fresh baseline. Reset only generated context; retain repository configuration.
   - If task continuity is ambiguous, ask whether to continue the current baseline or start a new
     logical task before replacing anything.
   - If the CLI is unavailable, the repository is unsupported, or baseline creation fails, stop
     before application-source edits and report the problem.

   Never replace an existing baseline merely because it exists. Do not use `baseline --replace` to
   mutate task history.

3. Run:

   ```bash
   dev-context status
   ```

4. Record the baseline ID and the authoritative pre-task state:

   - branch and HEAD;
   - pre-existing working-tree modifications;
   - build and test results, including existing failures;
   - compiler and analyzer diagnostics; and
   - relevant solution, project, framework, and reference structure.

Do not classify baseline problems as task regressions. Do not opportunistically fix unrelated
problems. Preserve every pre-existing user change.

## 2. Scope and inspect the requested work

1. Translate the request into observable behavior and identify likely entry points. Ask a concise
   question only when an undiscoverable choice would materially change the implementation.
2. Prefer the structural facts already captured by `status` before broad source scanning.
3. When changes after the baseline make impact data useful, run:

   ```bash
   dev-context affected
   ```

   Use reported projects, declarations, symbols, and likely tests to choose the next files to
   inspect. Treat these relationships as candidates, not proof; Razor, XAML, generated code,
   reflection, and runtime wiring can require broader inspection.
4. Read only the source needed to understand the behavior. Expand outward through project,
   symbol, configuration, and test dependencies as evidence requires.
5. Form a short implementation plan that preserves the repository's architecture and conventions.

Do not regenerate the baseline during reconnaissance or implementation.

## 3. Implement the smallest coherent change

1. Change only what is required to satisfy the requested behavior.
2. Preserve established naming, structure, error handling, public contracts, and architectural
   boundaries unless the request requires changing them.
3. Avoid unrelated cleanup, dependency upgrades, formatting churn, or refactoring.
4. Add or update focused tests when behavior changes or a regression needs coverage.
5. Run the narrowest useful build or tests during development. Use `dev-context affected` again
   after edits when it can improve test selection.

Compare any failure with the baseline before deciding it was introduced by the task.

## 4. Run configured cleanup

Inspect `.dev-context/config.json`. If `cleanup.enabled` is `true`, run:

```bash
dev-context clean
```

If cleanup is disabled, skip it and say so. Review every cleanup-produced modification and remove
unrelated semantic or formatting changes without overwriting pre-existing user work.

## 5. Verify against the immutable baseline

Run:

```bash
dev-context verify
```

Interpret the exit code and delta:

- Exit `0`: verification completed without task-introduced regressions.
- Exit `1`: investigate new build failures, policy-violating diagnostics, and newly failing tests.
  Correct only task-related regressions, then rerun `verify`.
- Exit `2`: verification did not execute reliably. Investigate the unavailable or failed command
  and do not claim successful verification.

Do not fail the task merely because an unchanged baseline problem remains. Do not regenerate or
replace the baseline while correcting regressions. Report unresolved regressions or unavailable
checks explicitly.

## 6. Review the final repository delta

Review the final Git state without changing the index:

```bash
git status --short
git diff --check
git diff
git diff --cached
```

Inspect relevant untracked files directly because Git diffs do not include their contents. Compare
the final file list with the baseline's pre-existing modifications and confirm:

- every task-introduced file is relevant;
- no user modification was overwritten;
- no unexpected generated or temporary file remains;
- no new warning, diagnostic, or test failure is unexplained; and
- the implementation satisfies the requested behavior.

## Report completion

State:

- what changed and the behavior delivered;
- the immutable baseline ID;
- focused checks run during implementation;
- the final `dev-context verify` result;
- relevant pre-existing failures or modifications;
- whether cleanup ran and what it changed; and
- unresolved regressions or checks that could not execute.

Distinguish successful verification, pre-existing repository problems, unresolved task regressions,
and unavailable checks. Never describe a skipped or failed check as passing.
