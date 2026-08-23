# Stage 1 verification checklist

This checklist maps the implementation sequence in `01-build-dev-context-cli.md` to observable
evidence in the first release candidate.

| Increment | Implemented evidence | Verification evidence |
| --- | --- | --- |
| Repository discovery | Parent traversal recognizes `.git` directories and worktree files | `RepositoryLocator_FindsGitRootFromNestedDirectory`; CLI integration test runs from a nested directory |
| Configuration | Versioned solution, test modes, analysis providers, cleanup, storage, warning policy, and cache settings | Invalid modes are rejected during configuration load; generated configuration is retained by `reset` |
| Git baseline | Branch, optional HEAD, porcelain state, and SHA-256 for existing changed files | Git delta tests cover additions, removals, unchanged dirty files, and further edits |
| Build baseline | Normal `dotnet build`, explicit execution states, normalized compiler diagnostics | Diagnostic parser tests and smoke baseline build |
| Test baseline | Per-project TRX results with stable test identities | TRX parser test; smoke baseline executes a passing MSTest project |
| Diagnostic normalization | Roslyn, `dotnet format` JSON, and Qodana SARIF use one stable schema | Parser tests for all three sources; formatter smoke regression |
| Persistence | Atomic JSON writes, schema-v3 documents, pending baseline/current directories, optional raw logs | Nullable round-trip test; duplicate baseline refusal; reset integration test |
| Verification/deltas | File/declaration, diagnostic, test, build, formatter, and provider deltas with exit codes | Expanded smoke expects regression exit `1`, restoration exit `0`, and unavailable-provider state |
| Status | Bounded text and full JSON forms | CLI integration test parses status JSON; dogfood baseline/status output |
| Structural indexing | Evaluated MSBuild metadata and project-file ownership plus cached cross-project Roslyn compilations | Cache invalidation test; real Razor/WPF evaluation tests; semantic type relationship test |
| Affected tests | Explicit/nearest/shared project ownership plus project closure, method-call, construction, DI, and test-to-production references | MAUI/Blazor/WPF, linked-item, shared-input, and cross-project method-to-test tests; smoke verifies focused failing test and skips the full suite |
| Qodana | Optional executable, unavailable state, SARIF normalization | SARIF normalization and unavailable-provider tests; disabled by default |
| Cleanup | Direct configured command execution and before/after Git comparison | Command tokenizer test; smoke confirms disabled cleanup is `Skipped` |
| Caching | SDK/config/project/source input hash with atomic graph cache replacement | Cache hit and source invalidation test; smoke checks reuse between `affected` and `verify` |
| Distribution | Packable .NET tool named `dev-context` | `dotnet pack` produces `DevContext.Cli.0.2.1.nupkg` |

## Acceptance commands

```powershell
dotnet build RepoLens.sln --nologo
dotnet test RepoLens.sln --no-build --no-restore --nologo
dotnet format RepoLens.sln --verify-no-changes --no-restore
dotnet pack RepoLens/RepoLens.csproj --configuration Release --no-restore --nologo
pwsh -File scripts/run-smoke-test.ps1 -NoBuild
```

The smoke test is the behavioral acceptance test. Unit tests protect individual schemas and
algorithms; the smoke test proves the baseline-to-regression-to-restoration lifecycle through the
published CLI surface.
