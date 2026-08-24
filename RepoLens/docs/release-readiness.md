# Release readiness

RepoLens 0.11 establishes the release-hardening surface intended to mature into 1.0. A release
candidate is ready only when the following gates pass from clean Windows, Linux, and macOS runners.

## Automated gates

- restore and Release build succeed with warnings treated as errors;
- all unit and integration tests pass on .NET 10;
- `RepoLens.Api` packs as a net8-compatible library and `DevContext.Cli` packs as a .NET tool;
- a clean net8 console restores, compiles, and runs against the produced API package;
- the isolated baseline/affected/query/verify smoke test passes;
- the checked-in evidence corpus retrieves every expected file/relationship within its token
  ceiling and produces identical output on its repeated warm run; and
- `.nupkg` and `.snupkg` files plus the smoke summary are retained as CI artifacts.

The workflow in `.github/workflows/ci.yml` enforces these gates. Qodana and SonarQube are not
release requirements.

## Compatibility contract

- Public API target: .NET 8 or later.
- CLI target: .NET 10.
- Current persisted schema: 10.
- Readable persisted schemas: 5 through 10. Artifacts outside that window fail explicitly.
- Configuration: v2 current, v1 read/migrate supported.
- CLI/API package version: 0.11.0.
- MCP transport: stdio through the official C# SDK, exposing typed structured results for the
  seven supported repository tools.

Executable manifest migration fixtures now cover every readable version, and `dev-context schema`
publishes the current persisted-document contracts. Before publishing 1.0, freeze public
record/property names, document the deprecation policy, and complete the manual product matrix
below using only produced packages.

## Manual release checks

1. Install `DevContext.Cli` from the produced local package into a clean tool path.
2. Run `doctor`, coverage-enabled `baseline`, `affected`, a filtered `query`, `benchmark`, `mcp`,
   `report`, `trend`, and `verify` against one Blazor, WPF, and MAUI repository.
3. Confirm per-target completeness and generator gaps in JSON; never interpret a partial analysis
   as proof that no dependency exists.
4. Confirm `doctor` displays the source-generator trust warning. Disable generator execution and
   verify that the corresponding completeness gap is present.
5. Compare corpus recall, precision, total approximate tokens, and warm latency with the previous
   release; investigate regressions before publishing.
6. Check package metadata, README rendering, license expression, symbols package, and changelog.

## Security boundary

Source generators are third-party executable code loaded into the RepoLens process. Run the tool
only on repositories and restored dependencies you trust, or set
`indexing.executeSourceGenerators` to `false`. File reads used for indexed source, markup, and
evidence are size-bounded and repository-contained; generated-source text is held in the versioned
cache and evidence bundle rather than read through an arbitrary filesystem path.
