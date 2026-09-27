# S18 — Performance smoke and save size

**Verdict: PARTIAL — the harness and the real Scribe ran headlessly under Mono with the numbers below; the in-game
run (dev action, TPS observation, a real save) is still required.**

## Question

With 10,000 scheduler jobs and 5,000 history records: is the per-tick idle cost unmeasurable, is job cost within the
budget, and is save-size growth within the [EVENTS_AND_HISTORY § 11](../EVENTS_AND_HISTORY.md#11-save-size-budget)
targets?

## Build

Source commit `293e363`. Code under test: `Kernel/Scheduler.cs`, `History/HistoryService.cs`,
`Diagnostics/PerfHarness.cs`, every store's `ExposeData`.

## Environment of this pass

Mono 6.8 JIT on a shared Linux container ([README](README.md#environment-of-this-pass)); timings are indicative,
not RimWorld/Unity numbers.

## Steps run

`Tests/run-tests.sh` → `Kernel.S18Harness` (`PerfHarness.Run(10000, 5000)`, the same code as the dev action) and
`Persist.S18SaveSize` (a stress-sized Network node written through the real Scribe: the full generated cast
snapshot, 300 searches with leads and opportunities, 5,000 claims pushed through the real History consumer so
its caps apply, and 10,000 synthetic jobs).

## Result (headless)

| Measure | Value | Target |
|---|---|---|
| Idle per-tick cost (`Clock.Now < NextDueTick`) | ~10 ns per tick (5.4 ns in the re-run after the pre-runtime fix pass; the start-up state check added then is one enum comparison and allocates nothing) | "unmeasurable": one comparison |
| Scheduling 10,000 jobs | ~3 ms total | — |
| All 10,000 jobs overdue at once | drained over 626 ticks, **at most 16 jobs per tick** | budget 16 jobs / 1.5 ms |
| Cost of a busy tick (16 trivial jobs) | median 0.017 ms, p95 0.026 ms, one outlier ~2.5 ms (GC or JIT; the time budget is checked between jobs, so a single slow job can exceed it). Re-run after the fix pass: median 0.011 ms, p95 0.016 ms, max 2.4 ms | 1.5 ms |
| 5,000 history records through the consumer | ~18 ms total (after a fix below) | O(1) per record |
| Full retention sweep | ~3 ms (budgeted at 500 records per run) | every 15 days |
| Save: stress node without the synthetic jobs | **3.2 MB** (history 2,882 records after caps; 300 requests; 300 opportunities; 108 cast entries; 256 journal entries) | typical 1.2–1.8 MB, ceiling ~3.5 MB |
| Save: same with 10,000 synthetic jobs | 4.7 MB (a real Phase 1 save holds a handful of jobs) | — |
| Bytes per history record (XML, before indentation) | ~514 | 350 (estimate) |

## Findings fixed during this spike

1. **History inserts beyond the global cap were O(n).** Once the Notable cap (3,000) was reached, every new record
   rebuilt the ledger index: 5,000 inserts took ~1.2 s. Fixed with running counters and trimming to 95 % of the cap
   (hysteresis); 5,000 inserts now take ~18 ms.
2. **Save size.** The first measurement was 5.9 MB for the stress node (~620 bytes per record). Fixed without
   changing any cap or the schema's meaning: enums at their default and empty lists are no longer written (a
   missing node loads as the default, as Scribe does for values), participants persist as compact
   `A17|role|p` strings, and the derivable narrative seed and redundant notes are not stored. Result: 3.2 MB.

## Open finding for the owner

At ~500–600 bytes per record in the file, a ledger at the full § 11 caps (3,000 Notable + 1,500 Major + 400
Legendary in later phases) would be ~2.5–3 MB on its own, so the § 11 per-record estimate (350 bytes) and the
"≤ 1.7 MB" history line are optimistic. Phase 1 alone stays under the ceiling (Major and Legendary records are
rare or absent). Options for a later phase, not taken here: lower the Notable cap, compress older records further,
or accept a higher ceiling. EVENTS_AND_HISTORY § 11 is annotated with the measured value.

## Owner steps (in game)

1. Mod Settings → The Network → **Network timing (profiling)** on.
2. Debug menu → The Network → **S18 performance harness (synthetic)**: attach the printed table (it runs on a
   scratch scheduler and ledger; the save is untouched).
3. Play a colony with two searches running and a Network site for a few in-game days. **Print timing report**:
   per job kind and consumer counts, mean, p95, max. Compare TPS with the mod disabled on the same save.
4. **Save-size report (Network node)**: writes `TheNetwork/network-node.xml` under the save-data folder and prints
   its size.

## Consequence

If the in-game idle cost is measurable or job costs exceed the budget in practice, the fix is local to the scheduler
or the offending handler; the architecture assumes no per-tick work beyond the idle comparison.
