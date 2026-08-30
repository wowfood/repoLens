---
name: start-coding-task
description: "Run a complete coding-task workflow in a Git/.NET repository with dev-context: establish an immutable baseline, record pre-existing state, ground the work in retrieved repository evidence, implement the smallest coherent change, clean when configured, verify the task delta, and review the final diff. Use when starting or continuing an implementation, fix, or refactor where repository files will change. Do not use for read-only explanations or repositories without dev-context unless explicitly requested."
---

# Start Coding Task

Treat the invocation as one logical coding task. Use `dev-context` for deterministic repository
facts and normal repository tools for reasoning, source inspection, editing, and focused tests.

`dev-context` answers questions about what exists, what depends on what, and what broke. It never
guesses: when the underlying analysis is incomplete it says so and requires you to abstain rather
than returning a confident wrong answer. Honour that contract — see *Reading an abstention* below.

## 1. Establish the task baseline

1. If `.dev-context/config.json` does not exist, run `dev-context init` first. It writes validated
   configuration without creating a baseline, so the settings are explicit and reviewable before
   anything depends on them. Other commands write defaults silently if you skip it.

2. Before analysing or modifying application source, run:

   ```bash
   dev-context baseline
   ```

   Do not run cleanup first.

3. Handle the result precisely:

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

4. Run:

   ```bash
   dev-context status
   ```

5. Record the baseline ID and the authoritative pre-task state:

   - branch and HEAD;
   - pre-existing working-tree modifications;
   - build and test results, including existing failures;
   - compiler and analyzer diagnostics; and
   - relevant solution, project, framework, and reference structure.

Do not classify baseline problems as task regressions. Do not opportunistically fix unrelated
problems. Preserve every pre-existing user change.

## 2. Ground the work in retrieved evidence

Translate the request into observable behavior. Ask a concise question only when an undiscoverable
choice would materially change the implementation.

Then retrieve evidence before reading source at random. Grepping the repository re-derives facts
`dev-context` already holds, and it cannot tell you when it has missed something.

### Choosing the right command

| You need | Use | Why |
| --- | --- | --- |
| Source relevant to a task or question | `dev-context query "<task>"` | Ranked, token-bounded excerpts with selection reasons. Start here. |
| An exact structural relationship | `dev-context refs "<symbol>" --relation <r>` | Resolves one symbol and filters the typed dependency graph. No ranking, no guessing. |
| Which projects own or are affected by a path | `dev-context explain <path>` | Evaluated MSBuild ownership and the reverse project-reference closure. |
| What changed since the baseline and what it touches | `dev-context affected` | Changed files, impacted declarations/projects, and likely tests. |

Prefer `refs` whenever the question has an exact answer — "who calls this", "what implements this",
"which tests cover this". `query` ranks by relevance and can be wrong about what matters; `refs`
either resolves the symbol or tells you it was ambiguous.

```bash
dev-context query "add retry to the payment submission path and its focused tests"
dev-context refs "PaymentSubmitter.SubmitAsync" --relation callers
dev-context refs "IPaymentGateway" --relation implementers
dev-context explain src/Payments/PaymentSubmitter.cs
```

Supported relations: `callers`, `callees`, `implementers`, `implementations`, `overrides`,
`subtypes`, `constructors-of`, `readers`, `writers`, `tests-covering`, `injected-into`.

Useful bounds: `--max-tokens` (minimum 256), `--max-results`, `--graph-depth 0..3`, and for `query`
the `--changed`, `--project`, `--kind`, and `--exclude-tests` filters.

### Reading an abstention

`query` and `refs` report an evidence decision — `Sufficient`, `Partial`, or `Insufficient` — plus
an explicit abstention flag and any analysis gaps.

- **Exit `3`, or `shouldAbstain: true`, means do not assert.** The tool is telling you it could not
  see enough to answer. Do not convert that into a claim, and in particular do not read an empty
  result as proof that nothing exists.
- An empty `refs` result proves absence **only** when the relevant compilation records are complete.
  Otherwise it means "not found in what could be analysed", which is a different statement.
- Read the reported analysis gaps. They name what was missing — an unbuilt project, a target
  framework that was not indexed, markup-generated code that was unavailable.

When a result abstains, widen the search with normal repository tools and say in your report that
the structural answer was incomplete.

Treat every relationship as a candidate rather than proof. Reflection, dependency-injection wiring,
Razor, XAML, generated code, and runtime configuration can reach past the graph.

Finally, read the source the evidence points at, expand outward as needed, and form a short
implementation plan that preserves the repository's architecture and conventions.

Do not regenerate the baseline during reconnaissance or implementation.

## 3. Implement the smallest coherent change

1. Change only what is required to satisfy the requested behavior.
2. Preserve established naming, structure, error handling, public contracts, and architectural
   boundaries unless the request requires changing them.
3. Avoid unrelated cleanup, dependency upgrades, formatting churn, or refactoring.
4. Add or update focused tests when behavior changes or a regression needs coverage.
5. Run the narrowest useful build or tests during development. Use `dev-context affected` again
   after edits when it can improve test selection, and `dev-context refs <symbol> --relation
   tests-covering` to find the tests that exercise what you changed.

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
- Exit `2`: verification did not execute reliably — an unavailable command, a command that exceeded
  its timeout, or a stored artifact this version cannot read. Investigate and do not claim
  successful verification. A run that could not execute is not a run that found nothing.

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

## Exit codes

- `0`: completed; verification found no regressions.
- `1`: verification completed and found regressions.
- `2`: usage, configuration, repository, or external-command failure, including a timeout.
- `3`: `query` or `refs` completed but evidence was insufficient — abstain.
- `4`: the retrieval benchmark failed an acceptance criterion.

## Report completion

State:

- what changed and the behavior delivered;
- the immutable baseline ID;
- focused checks run during implementation;
- the final `dev-context verify` result;
- relevant pre-existing failures or modifications;
- whether cleanup ran and what it changed; and
- unresolved regressions, checks that could not execute, and any structural question that came back
  insufficient.

Distinguish successful verification, pre-existing repository problems, unresolved task regressions,
and unavailable checks. Never describe a skipped or failed check as passing, and never present an
abstaining result as an answer.
