---
name: dev-context-baseline
description: Establish and preserve deterministic dev-context repository baselines for coding tasks, use affected-code data during implementation, and verify task-introduced regressions before completion. Use before analysing or modifying application source in a Git/.NET repository where dev-context is available, and when finishing that same logical task. Do not use for read-only explanations or repositories without the CLI unless explicitly requested.
---

# Dev Context Baseline

Treat the invocation as one logical coding task. Keep its baseline immutable from the first source
inspection through final verification.

## Establish context

1. Before analysing or modifying application source, run:

   ```bash
   dev-context baseline
   ```

   Do not run cleanup first.

2. Handle the result precisely:

   - If a baseline is created, continue.
   - If the command reports an existing baseline, do not run `reset`, `baseline --replace`, or
     otherwise replace it. Continue with that baseline.
   - If the CLI is unavailable, the repository is unsupported, or another error occurs, stop
     before modifying application source and report the failure.

   Replace generated context only when the user explicitly authorizes starting a new logical task.
   Never infer that authorization merely because an existing baseline was found.

3. Run:

   ```bash
   dev-context status
   ```

4. Record the baseline ID and treat the status as authoritative for:

   - baseline branch, HEAD, and pre-existing working-tree changes;
   - existing build and test state;
   - existing compiler and analyzer diagnostics; and
   - captured solution and project structure.

Do not classify pre-existing failures as regressions. Do not fix them unless they are relevant to
the requested work. Preserve all pre-existing user changes.

## Work from the baseline

Use normal repository tools for semantic understanding and implementation. Prefer stored
`dev-context` facts over repeatedly rediscovering the same build, test, diagnostic, or project
state.

When changes exist and impact information would help focus inspection or tests, run:

```bash
dev-context affected
```

Use its project, symbol, and test relationships to narrow the next step. Treat them as candidates,
not proof: inspect relevant source whenever semantic understanding is required, and broaden the
search when the graph is incomplete.

Do not regenerate the baseline during implementation.

## Verify the task delta

1. Inspect `.dev-context/config.json`. If `cleanup.enabled` is `true`, run:

   ```bash
   dev-context clean
   ```

   Review every cleanup-produced modification and keep only changes relevant to the task.

2. Run:

   ```bash
   dev-context verify
   ```

3. Interpret the result:

   - Exit `0`: verification completed without task-introduced regressions.
   - Exit `1`: inspect and address new build failures, policy-violating diagnostics, and newly
     failing tests introduced after the baseline. Rerun `verify` after corrections.
   - Exit `2`: verification could not execute reliably. Investigate the unavailable or failed
     command and do not report successful verification.

Do not require unrelated baseline failures to be fixed. Do not replace the baseline while
correcting regressions. If a regression or execution failure cannot be resolved within scope,
report it explicitly.

## Report completion

Use the final `verify` result in the completion summary. State:

- the baseline ID;
- the final verification outcome;
- relevant pre-existing failures or modifications;
- cleanup changes, when cleanup ran; and
- any tests or analysis that could not execute.
