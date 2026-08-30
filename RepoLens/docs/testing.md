# Testing `dev-context`

Use two layers of testing:

1. the repository verification matrix for fast implementation feedback; and
2. the isolated smoke harness for observable CLI behavior and context-size comparison.

## Fast verification matrix

Run from the repository root:

```powershell
dotnet build RepoLens.sln --nologo
dotnet test RepoLens.sln --no-build --no-restore --nologo
dotnet format RepoLens.sln --verify-no-changes --no-restore
dotnet pack RepoLens.Api/RepoLens.Api.csproj --configuration Release --no-restore --nologo
dotnet pack RepoLens/RepoLens.csproj --configuration Release --no-restore --nologo
pwsh -File scripts/test-package-consumer.ps1
dotnet run --project RepoLens/RepoLens.csproj -- `
  benchmark RepoLens/benchmarks/evidence-corpus.json --format json
```

This checks compilation, unit/integration tests, formatting, analyzers, API packaging, .NET tool
packaging, and the checked-in retrieval-quality corpus. The CI workflow runs the build/test/pack,
smoke, and corpus gates. Formatting remains a local check. `.editorconfig` pins `end_of_line = lf` so
that `dotnet format` agrees with `.gitattributes`: without it the formatter used the platform newline
for any line it rewrote and then reported its own output as a whitespace error on Windows.

## Isolated smoke test

Run:

```powershell
pwsh -File scripts/run-smoke-test.ps1
```

The script builds `dev-context`, creates a unique temporary Git repository, and generates a small
calculator library plus MSTest project. It then verifies this sequence:

1. a clean baseline builds and its test passes;
2. a second baseline is rejected with exit code `2`;
3. text and JSON status output can be read;
4. changing production formatting and a test expectation is mapped by semantic `affected` data;
5. `verify` runs the selected test first, stops on failure, normalizes the formatter finding, and
   exits with code `1`;
6. the graph cache created by `affected` is reused by `verify`;
7. restoring both files makes the full confirmation suite pass with exit code `0`;
8. a task evidence query retrieves the production method and focused test within fixed result and
   token bounds;
9. `refs --relation callers` resolves the production method exactly and reports the test method as a
   caller without abstaining — a cross-project edge, so this also guards against the reference graph
   losing project boundaries again;
10. committing the regression keeps it visible, with `Committed` provenance in `affected` and a
    still-failing `verify`, which is the commit-aware half of the delta;
11. `verify --against <ref>` reviews a feature branch against its merge base without reading or
    writing baseline state; and
12. disabled cleanup reports `Skipped` without changing files.

The temporary fixture is deleted after success or failure. Retain it for manual investigation with:

```powershell
pwsh -File scripts/run-smoke-test.ps1 -KeepFixture
```

Use `-NoBuild` to skip rebuilding the main solution, or `-Configuration Release` to exercise the
release binary. Every recursive cleanup target is validated as a uniquely named directory beneath
the system temporary directory before deletion.

## Smoke artifacts

Each run writes an ignored directory beneath `artifacts/smoke/<timestamp>/` containing:

- `baseline.txt` and `status.json`;
- `affected.txt` and `affected-committed.json`;
- `evidence-query.txt`, `evidence-query.json`, and `refs.json`;
- `verify-regression.txt`, `verify-committed.txt`, `verify-restored.txt`, and
  `verify-against-ref.json`;
- `cleanup.json`;
- `raw-context.txt`;
- `compact-context.txt`;
- `token-proxy.json`; and
- `summary.json`.

`summary.json` is the quickest pass/fail report. The individual transcripts make failures
inspectable without retaining the temporary repository.

## Context and token comparison

The smoke test models two ways an agent can obtain the same deterministic facts:

- **Raw context:** SDK information, Git state, solution discovery, evaluated MSBuild metadata,
  normal build output, normal test output, and the Git diff.
- **Compact context:** `dev-context status`, `dev-context affected`, and `dev-context verify`.
- **Task evidence:** `dev-context query` source excerpts and relationships selected for one
  concrete change, with an enforced approximate-token ceiling.

`token-proxy.json` reports characters, UTF-8 bytes, lines, and `ceil(characters / 4)` for both
transcripts. The four-character heuristic follows the rule of thumb published by the
[OpenAI Tokenizer](https://platform.openai.com/tokenizer), but it is not a billing or full-task
usage measurement. Code, paths, punctuation, and the selected model tokenizer can change the
ratio.

`token-proxy.json` records all three measurements. The evidence query also reports its own
`approximateTokens`, and the smoke test fails if that exceeds the requested 1,200-token bound.
The deterministic API benchmark separately checks expected-file retrieval recall; see
[`evidence-benchmarks.md`](evidence-benchmarks.md).

For an exact tokenizer count, install OpenAI's `tiktoken` package in an isolated Python
environment and run:

```powershell
python scripts/measure-context-tokens.py `
  artifacts/smoke/<timestamp>/raw-context.txt `
  artifacts/smoke/<timestamp>/compact-context.txt
```

The helper defaults to `o200k_base`; pass `--encoding` when the model being evaluated uses a
different encoding.

## Measuring actual agent usage

Transcript size measures only the deterministic context that could be inserted into a model
request. It does not measure source files retrieved later, prompt caching, generated output,
reasoning tokens, or tool-call overhead.

For an actual A/B comparison:

1. use two fresh copies of the same fixture at the same Git commit;
2. use the same model, reasoning setting, user prompt, and cache conditions;
3. in the control run, let the agent discover repository/build/test facts using normal tools;
4. in the treatment run, provide `dev-context status` and require final `dev-context verify`;
5. record input, cached-input, output, and reasoning tokens separately when the client or API
   exposes those fields; and
6. repeat several times and compare medians rather than relying on a single agent run.

A suitable identical task for both fixtures is:

```text
Add a Subtract(int left, int right) method to Calculator, add a focused test, and verify the result.
Do not change unrelated files.
```

Keep task correctness and final diffs alongside token totals. A smaller prompt that causes more
searching or a worse change is not a useful optimization.
