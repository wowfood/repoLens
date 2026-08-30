# Evidence retrieval benchmarks

RepoLens treats task-context retrieval as a measured product behavior rather than judging output
only by prompt size. `DevContextApi.BenchmarkAsync` and the `dev-context benchmark` command run a
versioned JSON corpus and report file recall/precision, relationship misses, approximate tokens,
cold/warm latency, evidence sufficiency/abstention conformance, and deterministic repeated output.

## The two corpora

| Corpus | Runs against | Role |
| --- | --- | --- |
| `RepoLens.Tests/Fixtures/BenchmarkRepo/corpus.json` | a synthetic fixture repository | The gate. Its ground truth changes only when a case is deliberately rewritten. |
| `RepoLens/benchmarks/evidence-corpus.json` | this repository | A sanity check at realistic scale. Its ground truth moves whenever RepoLens's own files move. |

Retrieval quality cannot be compared between commits on a target that changes with each commit, so
the tight assertions — precision floors, relationship edges, token ceilings — belong to the fixture
corpus. The self-referential corpus keeps looser budgets and is expected to need occasional
refreshing; `EvidenceBenchmarkTests.SelfCorpus_ExpectsFilesThatStillExist` fails with the real cause
when an expected path has simply been renamed away, rather than letting it look like a retrieval
regression.

Run them with:

```powershell
pwsh -File scripts/run-benchmark-fixture.ps1

dotnet run --project RepoLens/RepoLens.csproj -- `
  benchmark RepoLens/benchmarks/evidence-corpus.json --format json
```

`scripts/run-benchmark-fixture.ps1` materializes the fixture into a temporary directory, because its
source files are checked in with a trailing `.fixture` extension. Without that, adding a fixture
project to this repository would silently enlarge RepoLens's own graph and move the self-referential
corpus's results.

The broader smoke harness runs a separate 1,200-token query against its calculator fixture and
writes both prompt-ready and JSON artifacts:

```powershell
pwsh -File scripts/run-smoke-test.ps1
```

## Acceptance conditions

A case passes when all of the following hold. Each reports the specific reason it did not.

- **Recall.** Every path in `expectedFiles` appears in the bundle.
- **Relationships.** Every entry in `expectedRelationships` resolves. `"method-call"` requires an
  edge of that kind anywhere in the bundle; `"method-call: src/A.cs -> src/B.cs"` requires an edge of
  that kind whose source and target blocks live in those files. The second form is what actually
  pins retrieval behaviour — and note that the edge must be between two blocks the bundle
  *selected*, not merely present in the graph.
- **Precision.** The share of retrieved files that were expected is at least `minPrecision`. Recall
  alone cannot fail a case that pads the budget with irrelevant files, so a case without a precision
  floor is only half a gate.
- **Tokens.** The bundle's approximate token count is at most `maxApproximateTokens`. This is
  deliberately separate from `maxTokens`: `maxTokens` is the budget the query is *given*, and a
  bundle can never exceed the budget it was handed, so asserting against it detects nothing. Set the
  ceiling from what the case costs today.
- **Sufficiency.** `expectedSufficiency` and `expectAbstention` match, where specified.
- **Determinism.** Three runs agree: cold, warm, and one taken after the in-memory graph is dropped
  and rehydrated from its persisted form. The third is the one with teeth — comparing the warm run to
  the cold one only proves that a single in-memory graph object yields a single bundle.

Both corpora include an abstention control. One asks about a subject that is plausible for a
software repository but absent from this one; the other uses a query that cannot appear in C# source
at all. The plausible control is the one that protects the abstention path in practice, and it is
currently a known gap on the self-referential corpus — see below.

## Fixtures must be restored

`scripts/materialize-benchmark-fixture.ps1` runs `dotnet restore` on the fixture, and that is load
bearing rather than tidiness. Each project is compiled separately and sees its project references
through `Compilation.ToMetadataReference()`. Symbols reached that way compare equal to the declaring
compilation's symbols only while the two compilations share identical reference sets — which is true
of an unrestored toy fixture and false of every real repository.

A fixture that skips the restore will therefore resolve cross-project references that a real
repository does not, and any assertion about them passes for the wrong reason. This is not
hypothetical: it is why the symbol graph shipped with no cross-project edges at all while two tests
appeared to cover them.

## Advisory cases

A case marked `"advisory": true` is measured and its failures reported, but it does not fail the
run. It is reserved for behaviour that is known to be wrong today and queued to be fixed: encoding
the wrong answer as the expected one would make the corpus lie, and deleting the case would hide the
deficiency. `dev-context benchmark` prints the count as **Known gaps**, and each such case is listed
as `KNOWN GAP` with its reasons. Remove the flag once the gap closes.

The self-referential corpus currently carries four, all of them real product deficiencies rather
than corpus bookkeeping:

- three agent-phrased questions (`where are the command line flags for the query command parsed`,
  and two like it) retrieve **none** of their target files. The ranker matches identifiers, so a
  query that shares few literal identifiers with its target scores near zero; and
- a plausible out-of-scope question does not abstain on a repository this size — the lexical
  fallback finds spurious matches and the result is reported as `Partial` rather than `Insufficient`.

## Cross-platform determinism

The CI matrix builds on Windows, Linux, and macOS. `scripts/dump-normalized-index.ps1` emits one
canonical text file per leg and the `determinism` job diffs them, so "deterministic output" is a
tested claim rather than an assertion. Normalization drops timestamps, durations, input hashes, the
SDK version, and the versions of framework reference packs — all of which legitimately differ per
runner image — replaces absolute paths with their leaf, and sorts object keys. Array order and
content hashes are preserved, because those are the determinism signal.

## Adding benchmarks

Add a case when introducing a retrieval signal, framework adapter, or graph relationship. Define
expected files and relationships before tuning the selector, and set the precision floor and token
ceiling from a measured run so that later drift is visible. A smaller prompt is not an improvement if
recall or task correctness falls.

The token measure is `ceil(characters / 4)`, so it is a stable budgeting proxy rather than an exact
model billing count. Use `scripts/measure-context-tokens.py` on saved smoke artifacts when an exact
supported tokenizer comparison is needed.
