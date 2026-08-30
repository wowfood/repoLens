# Benchmark fixture repository

A small, self-contained .NET repository used as the retrieval benchmark's stable corpus. Its cases
live in `corpus.json` and are executed by `EvidenceBenchmarkTests` and by
`scripts/run-benchmark.ps1`.

Source files carry a trailing `.fixture` extension so that nothing in this repository's own
toolchain — MSBuild, `dotnet test`, and RepoLens's own self-analysis — treats them as buildable
input. Materializing the fixture strips that extension. Without it, adding a fixture project here
would silently enlarge RepoLens's own graph and move the results of the self-referential corpus.

The fixture exists because the self-referential corpus asserts on RepoLens's own file paths, so
every refactor of RepoLens moves the benchmark's ground truth. Retrieval quality cannot be compared
across commits on a target that changes with each commit. This repository does not change unless a
benchmark case is deliberately rewritten.

## Shape

- `src/Inventory` — a leaf project. `StockLedger` holds the reservation logic; `WarehouseSync` is a
  deliberate near-miss distractor that shares vocabulary with it.
- `src/Ordering` — references `Inventory`. `OrderService` is the composition point that a query
  about placing an order should reach; `PricingCalculator`, `IOrderRepository`, and its
  implementation are its direct collaborators. `Notifications` is a second distractor cluster.
- `tests/Ordering.Tests` — references both, so `tests-covering` and test-evidence selection have
  something to resolve.

Distractors are the point. A corpus where every file is relevant cannot measure precision, because
retrieving everything scores perfectly.
