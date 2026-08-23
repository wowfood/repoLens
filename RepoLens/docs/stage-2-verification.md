# Stage 2 verification checklist

The canonical skill is
[`dev-context-baseline`](../../.agents/skills/dev-context-baseline/SKILL.md). It is intentionally
instruction-only: deterministic behavior remains in the `dev-context` CLI.
Its `.agents/skills` location follows the
[official OpenAI skill documentation](https://learn.chatgpt.com/docs/build-skills).

| Requirement | Evidence |
| --- | --- |
| Codex-compatible skill | `SKILL.md` contains only `name` and trigger-focused `description` frontmatter; `agents/openai.yaml` supplies UI metadata |
| Establish context before source work | Runs `dev-context baseline`, handles existing-baseline refusal, then runs `dev-context status` |
| Immutable lifecycle | Prohibits implicit `reset` and `baseline --replace`; replacement requires explicit user authorization for a new logical task |
| Pre-existing conditions | Treats baseline build, tests, diagnostics, Git changes, and project structure as authoritative and not regressions |
| Focused work | Uses `dev-context affected` when impact data is useful, while requiring source inspection for semantic understanding |
| Conditional cleanup | Reads `.dev-context/config.json` and runs `dev-context clean` only when cleanup is enabled |
| Delta verification | Handles exit codes `0`, `1`, and `2` separately and reruns verification after correcting regressions |
| Honest completion | Reports baseline ID, final verification, relevant pre-existing state, cleanup changes, and unavailable checks |
| Composable invocation | `agents/openai.yaml` disables implicit invocation so Phase 3 can own the full coding-task trigger; explicit `$dev-context-baseline` invocation remains available |

## Validate

From the repository root:

```powershell
python "$HOME\.codex\skills\.system\skill-creator\scripts\quick_validate.py" `
  ".agents\skills\dev-context-baseline"
```

Useful trigger checks:

- Should trigger explicitly: “Use `$dev-context-baseline` before making this fix.”
- Should defer to Phase 3 implicitly: “Implement this feature in the current .NET repository.”
- Should not trigger: “Explain what a repository baseline is.”
- Should not trigger: “Review this prose without changing repository source.”

## Use in other repositories

Codex discovers repository skills under `.agents/skills` from the working directory up to the Git
root. To use this skill in every repository, create a user-wide junction once from the repository
root:

```powershell
New-Item -ItemType Directory -Path "$HOME\.agents\skills" -Force
New-Item -ItemType Junction `
  -Path "$HOME\.agents\skills\dev-context-baseline" `
  -Target (Resolve-Path ".agents\skills\dev-context-baseline")
```

Codex normally detects skill changes automatically. Restart Codex if the skill does not appear.
