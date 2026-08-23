# Evidence retrieval benchmarks

RepoLens treats task-context retrieval as a measured product behavior rather than judging output
only by prompt size. `DevContextApi.BenchmarkAsync` and the `dev-context benchmark` command run a
versioned JSON corpus and report file recall/precision, relationship misses, approximate tokens,
cold/warm latency, evidence sufficiency/abstention conformance, and deterministic repeated output. The repository corpus is
`RepoLens/benchmarks/evidence-corpus.json`; an isolated API fixture remains in
`EvidenceQuery_BenchmarkFindsFeatureDependenciesAndTestWithinTokenBudget`.

## Initial fixture and acceptance contract

The fixture contains a dashboard refresh method, an interface method it calls, and a test method
that calls the dashboard method. For the query `refresh dashboard songs`, the benchmark requires:

- 100% recall across the three expected source files;
- at least one semantically resolved method-call relationship;
- exact positive source ranges, content hashes, and selection reasons for every block;
- no absolute repository-root leakage in the prompt;
- no more than eight evidence blocks and 1,400 approximate prompt tokens; and
- identical bundle IDs and rendered prompts on a repeated unchanged query.

The corpus also contains a no-evidence control whose unique query must return no blocks,
`Insufficient` sufficiency, and `ShouldAbstain = true`. This prevents retrieval changes from turning
an unknown answer into an apparently supported repository claim.

Run it directly with:

```powershell
dotnet test RepoLens.Tests/DevContext.Tests.csproj --no-restore --nologo `
  --filter EvidenceQuery_BenchmarkFindsFeatureDependenciesAndTestWithinTokenBudget

dotnet run --project RepoLens/RepoLens.csproj -- `
  benchmark RepoLens/benchmarks/evidence-corpus.json --format json
```

The broader smoke harness runs a separate 1,200-token query against its calculator fixture and
writes both prompt-ready and JSON artifacts:

```powershell
pwsh -File scripts/run-smoke-test.ps1
```

## Adding benchmarks

Add a corpus case or isolated fixture when introducing a retrieval signal, framework adapter, or
graph relationship. Define expected files and relationship kinds before tuning the selector.
Corpus success requires all expected evidence, any configured sufficiency/abstention decision,
deterministic output, and the case token ceiling;
precision remains visible even where graph expansion intentionally retrieves supporting files. A
smaller prompt is not an improvement if recall or task correctness falls.

The token measure is `ceil(characters / 4)`, so it is a stable budgeting proxy rather than an exact
model billing count. Use `scripts/measure-context-tokens.py` on saved smoke artifacts when an exact
supported tokenizer comparison is needed.
