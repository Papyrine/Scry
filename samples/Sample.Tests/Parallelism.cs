// The Browser fixture (UiSnapshotTests) is [Parallelizable]; cap how many of its tests run at once.
// Each browser test boots its own mono-wasm runtime + Roslyn workspace, which is CPU- and
// memory-heavy, so a handful in flight is the sweet spot — more just contends for cores (and would
// over-subscribe smaller CI machines). On a many-core box a higher value shaves a little more:
// measured on 16 cores, 4 → ~35s, 8 → ~30s for the 16-test fixture.
[assembly: LevelOfParallelism(4)]
