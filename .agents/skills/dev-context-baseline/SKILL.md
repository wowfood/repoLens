---
name: dev-context-baseline
description: Establish and preserve deterministic dev-context repository baselines for coding tasks, retrieve grounded evidence and structural references during implementation, and verify task-introduced regressions before completion. Use before analysing or modifying application source in a Git/.NET repository where dev-context is available, and when finishing that same logical task. Do not use for read-only explanations or repositories without the CLI unless explicitly requested.
---

# Dev Context Baseline

Treat the invocation as one logical coding task. Keep its baseline immutable from the first source
inspection through final verification.

## Establish context

1. If `.dev-context/config.json` does not exist, run `dev-context init` first. It writes validated
   configuration without creating a baseline, so settings are explicit and reviewable before
   anything depends on them. Other commands write defaults silently if you skip it.

2. Before analysing or modifying application source, run:

   ```bash
   dev-context baseline
   ```

   Do not run cleanup first.

3. Handle the result precisely:

   - If a baseline is created, continue.
   - If the command reports an existing baseline, do not run `reset`, `baseline --replace`, or
     otherwise replace it. Continue with that baseline.
   - If the CLI is unavailable, the repository is unsupported, or another error occurs, stop
     before modifying application source and report the failure.

   Replace generated context only when the user explicitly authorizes starting a new logical task.
   Never infer that authorization merely because an existing baseline was found.

4. Run:

   ```bash
   dev-context status
   ```

5. Record the baseline ID and treat the status as authoritative for:

   - baseline branch, HEAD, and pre-existing working-tree changes;
   - existing build and test state;
   - existing compiler and analyzer diagnostics; and
   - captured solution and project structure.

Do not classify pre-existing failures as regressions. Do not fix them unless they are relevant to
the requested work. Preserve all pre-existing user changes.

## Work from the baseline

Prefer stored `dev-context` facts over repeatedly rediscovering the same build, test, diagnostic, or
project state, and retrieve evidence rather than scanning source at random:

| You need | Use |
| --- | --- |
| Source relevant to a task or question | `dev-context query "<task>"` |
| An exact structural relationship | `dev-context refs "<symbol>" --relation <r>` |
| Which projects own or are affected by a path | `dev-context explain <path>` |
| What changed since the baseline and what it touches | `dev-context affected` |

Prefer `refs` whenever the question has an exact answer — "who calls this", "what implements this",
"which tests cover this". Relations are `callers`, `callees`, `implementers`, `implementations`,
`overrides`, `subtypes`, `constructors-of`, `readers`, `writers`, `tests-covering`, and
`injected-into`.

**Exit `3`, or `shouldAbstain: true`, means do not assert.** The result reports a
`Sufficient`/`Partial`/`Insufficient` decision with the analysis gaps behind it. An empty `refs`
result is proof of absence only when the relevant compilation records are complete; otherwise it
means "not found in what could be analysed". Widen the search with normal repository tools and say
so in your report rather than turning an abstention into a claim.

Treat every relationship as a candidate, not proof: inspect relevant source whenever semantic
understanding is required, and broaden the search when the graph reports itself incomplete.
Reflection, dependency-injection wiring, Razor, XAML, and generated code can reach past it.

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
   - Exit `2`: verification could not execute reliably — an unavailable command, a command that
     exceeded its timeout, or a stored artifact this version cannot read. Investigate and do not
     report successful verification. A run that could not execute is not a run that found nothing.

Do not require unrelated baseline failures to be fixed. Do not replace the baseline while
correcting regressions. If a regression or execution failure cannot be resolved within scope,
report it explicitly.

## Exit codes

- `0`: completed; verification found no regressions.
- `1`: verification completed and found regressions.
- `2`: usage, configuration, repository, or external-command failure, including a timeout.
- `3`: `query` or `refs` completed but evidence was insufficient — abstain.
- `4`: the retrieval benchmark failed an acceptance criterion.

## Report completion

Use the final `verify` result in the completion summary. State:

- the baseline ID;
- the final verification outcome;
- relevant pre-existing failures or modifications;
- cleanup changes, when cleanup ran; and
- any tests or analysis that could not execute, including structural questions that came back
  insufficient.

Never describe a skipped or failed check as passing, and never present an abstaining result as an
answer.
