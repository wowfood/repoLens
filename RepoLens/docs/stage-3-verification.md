# Stage 3 verification checklist

The canonical skill is
[`start-coding-task`](../../.agents/skills/start-coding-task/SKILL.md). It is instruction-only:
`dev-context` owns deterministic repository facts, while Codex owns reconnaissance, implementation,
and review. Its repository and user locations follow the
[official OpenAI skill documentation](https://learn.chatgpt.com/docs/build-skills).

## Requirement evidence

| Requirement | Evidence |
| --- | --- |
| Codex-compatible skill | `SKILL.md` has trigger-focused `name` and `description` frontmatter; `agents/openai.yaml` supplies UI metadata and invocation policy |
| Immutable baseline | Creates a baseline before application-source inspection, reuses it for a continuing task, and resets generated context only when the user explicitly starts a new logical task |
| Pre-existing conditions | Records branch, HEAD, dirty files, build/tests, diagnostics, and architecture; baseline problems are not classified as task regressions |
| Focused reconnaissance | Starts from status and structural data, uses `dev-context affected` when useful, and expands source inspection through evidenced dependencies |
| Minimal implementation | Requires the smallest coherent change, preserves conventions and boundaries, and excludes unrelated cleanup, upgrades, and refactoring |
| Focused development checks | Runs narrow builds/tests during implementation and compares failures with the baseline |
| Conditional cleanup | Reads `.dev-context/config.json`, runs `dev-context clean` only when enabled, and reviews cleanup-produced modifications |
| Delta verification | Handles `dev-context verify` exit codes `0`, `1`, and `2`, fixes only task regressions, and never regenerates the baseline during correction |
| Final review | Reviews status, staged and unstaged diffs, diff whitespace errors, and relevant untracked files without changing the Git index |
| Honest completion | Separates successful verification, pre-existing problems, unresolved regressions, and unavailable or skipped checks |

## Invocation hierarchy

`$start-coding-task` is the primary task-level workflow and allows implicit invocation for requests
that change a Git/.NET repository. `$dev-context-baseline` is an explicit-only primitive for cases
where baseline capture and verification are the complete request. This avoids both skills matching
the same ordinary implementation prompt.

Useful trigger checks:

- Should trigger implicitly: “Implement this feature in the current .NET repository.”
- Should trigger implicitly: “Fix this regression, add coverage, and verify the result.”
- Should trigger explicitly: “Use `$start-coding-task` to make this WPF view-model change.”
- Should not trigger: “Explain what `dev-context verify` does.”
- Should not trigger: “Review this design document without changing repository files.”
- Should not trigger implicitly: “Edit this non-.NET repository where `dev-context` is unavailable.”

## Validate

From the repository root:

```powershell
python "$HOME\.codex\skills\.system\skill-creator\scripts\quick_validate.py" `
  ".agents\skills\start-coding-task"
python "$HOME\.codex\skills\.system\skill-creator\scripts\quick_validate.py" `
  ".agents\skills\dev-context-baseline"
```

Both validators pass. The Phase 3 implementation baseline was
`20260815181002-7b68e74fcf4b`: it captured a dirty repository with no initial commit, an existing
build failure caused by sandbox denial when reading the existing user `NuGet.Config`, 28 passing
tests, and zero analyzer diagnostics. Those facts remain preserved as pre-existing state. Final
verification with normal NuGet configuration access built successfully, passed all 28 tests, and
reported no diagnostics, execution failures, or regressions.

## User-wide discovery

The canonical repository skill is linked at:

```text
C:\Users\Aiden\.agents\skills\start-coding-task
```

The link targets `.agents/skills/start-coding-task` in this repository. Validation succeeds from
both paths and the skill and metadata hashes match, so updates remain single-source.

## Manual acceptance test

1. Restart Codex only if `$start-coding-task` does not appear in the skill picker.
2. Open a Git/.NET repository where `dev-context` is installed and initialized.
3. Ask: “Use `$start-coding-task` to make a small, test-covered change.”
4. Confirm Codex runs `dev-context baseline` and `dev-context status` before application-source
   inspection or editing.
5. If an existing baseline belongs to another task, confirm Codex asks for or relies on explicit
   authorization before running `dev-context reset`.
6. Confirm reconnaissance starts with captured structure and uses `dev-context affected` only when
   it can narrow inspection or tests.
7. Confirm cleanup runs only when `cleanup.enabled` is `true`.
8. Confirm completion includes the baseline ID, focused checks, final `dev-context verify` result,
   relevant pre-existing problems, and any skipped or unavailable checks.
