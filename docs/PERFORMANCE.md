# Performance

> Goal: during ordinary play The Network's TPS cost should be **effectively invisible**, meaning
> it cannot be measured against the noise of a normal colony. Related: [SIMULATION § 1–2](SIMULATION.md#1-scheduler),
> [DEBUGGING § 5](DEBUGGING.md#5-timing-instrumentation).

## Contents

1. [Targets](#1-targets)
2. [Work classes](#2-work-classes)
3. [Scale assumptions and cost estimates](#3-scale-assumptions-and-cost-estimates)
4. [Banned patterns and what replaces them](#4-banned-patterns-and-what-replaces-them)
5. [Staggering and spikes](#5-staggering-and-spikes)
6. [Memory and save size](#6-memory-and-save-size)
7. [Verification](#7-verification)

---

## 1. Targets

| Metric | Target |
|---|---|
| Per-tick cost when nothing is due | one `int` comparison (`Clock.Now < nextDue`), plus the start-up state check (one enum comparison) |
| Per-tick cost when jobs are due | ≤ 1.5 ms hard budget, ≤ 16 jobs. Typical job < 0.05 ms. |
| Average added cost over an in-game day (about 100 contractor identities, 10 active contracts) | < 0.01 ms per tick amortized |
| Off-map Known Character pawns | suspended (quest-reserved) **and** mothball-eligible after store-time normalization, so vanilla ticks them once per 15,000 ticks ([ABSTRACT_PHYSICAL_LIFECYCLE § 4.3](ABSTRACT_PHYSICAL_LIFECYCLE.md#43-consequences-of-suspension-frozen-pawns)) |
| UI | no allocation-heavy work per frame. Read models are rebuilt only when `StateVersion` changes. |
| Load | Network load plus validation < 50 ms for a 10-year save |
| Catalog build | once per session, target < 60 ms on a heavy mod list |

## 2. Work classes

| Class | What | When | Cost control |
|---|---|---|---|
| **Initialization-only** | catalog build; compat module registration; recipe and trader indexes; reading the global cast from `ModSettings` | first need in a session | once; timed; can be split across frames if needed |
| **World-import** | copying the enabled cast templates into the world snapshot; instantiating Fixer (and, from Phase 2, contractor) **records** | once per world, on the first tick | a few hundred small records; no pawn generation |
| **Load-time** | tolerant load; migrations; cache rebuild; validation; custody audit | once per load (validation on the first tick) | linear in entity counts (bounded by caps) |
| **Event-driven** | event dispatch and consumers (summary counters, edges, morale, knowledge) | when something happens | O(consumers) per event, each O(1); cascades capped at 64 |
| **World-level scheduled** | Intel resolution, operation checkpoints, org upkeep, bidding passes, retention sweeps, population manager, gossip | due ticks, staggered | per-tick job and time budget; periodic jobs phase-offset by seed |
| **Physical-only periodic** | site claim sampling; deployment watchdog | every 2,500 ticks, **only while** a Network site map or active deployment exists; plus one final sample when vanilla is about to remove a Network site map (checked in the site comp's `CompTickInterval`: two field checks on a map-less site, vanilla's own `ShouldRemoveMapNow` only once no player pawn is on the map) | O(things of one def on one map) via `listerThings.ThingsOfDef` |
| **UI-only** | read models, sorting, filtering, narrative formatting | while the window is open | cached per `StateVersion`; lists virtualized (only visible rows drawn) |
| **Opportunity-generation spikes** | source/context resolution, tile finding, Thing creation for stashes, site creation | when Intel resolves (rare) | the resolver reads a session index `packageId → FactionDefs` and one pass over live factions (tens); `TileFinder` is vanilla and bounded; at most one per job; follow-ups are separate jobs |
| **Map-generation spikes** | vanilla map generation for a Network site | when the player arrives | vanilla cost (the same as any item-stash quest); the Network adds only its comp callbacks |
| **Materialization spikes** (Phase 3) | pawn generation for deployments | when the player becomes involved | capped deployment size (default ≤ 12 pawns); generation spread over frames with `LongEventHandler` if > 6. *Phase 3 design: ≤ 8 people per episode; role and cohesion verification adds a **bounded** retry (K attempts, each at most vanilla's own 120 tries), measured by S25; a group may be created one pawn per tick if it spikes* |

## 3. Scale assumptions and cost estimates

| Quantity | Typical | Upper bound designed for |
|---|---|---|
| Contractor identities (actor records, Solos to companies) | ~100 (the default setting) | 300 |
| Fixers | a small set | 40 |
| Known Characters (records; every Solo and Fixer embodies one) | 200 | 600 |
| Bound (real) pawns in Network custody | 40 | 150 (a soft cap, § 2 of the lifecycle doc: only dormant, low-notability characters are released, and the cap is exceeded rather than break an active story) |
| Active contracts | 5–10 | 40 |
| Scheduled jobs at any time | 60–150 | 1,000 |
| Relation edges | 500 | 3,000 |
| History records | 1,000 | 4,900 (caps) |
| Events per in-game day | 5–30 | 200 |

**Estimated steady-state work per in-game day (60,000 ticks), typical case:** about 100 NPC
contractor upkeeps (Solo upkeep is smaller than organization upkeep), about 10 operation
checkpoints, a few Intel rounds, about 5 bidding passes, about 20 events with about 8 consumers
each. That is roughly 330 small operations per day, or **≈ 0.006 operations per tick**. Even at
0.1 ms each, the amortized cost is ~0.0006 ms per tick.

**The ~100-identity cast is not 100 simulations.** It is about 100 lightweight records with one
staggered daily job each. No cast member is a pawn until it is physically needed (and then only
Known Characters that matter), nothing iterates the cast per tick, prices are computed once per
offer and persisted, and no contractor travels as a live world caravan. The global roster in
`ModSettings` is small static data read once per session and copied once per world.

**Search costs that must never scale with history size**: every behavioural query reads
summaries or edges in O(1), or knowledge books in O(64) at most.

## 4. Banned patterns and what replaces them

| Banned | Replacement |
|---|---|
| Per-tick contractor or organization processing | daily staggered upkeep jobs |
| Live pawn simulation for off-map contractors | headcount records; suspended reserved pawns for Known Characters |
| Repeated `DefDatabase` scans | session catalog and resolution caches (misses cached too) |
| LINQ or allocations in tick paths | the tick path has no LINQ, no closures and no boxing; job handlers may use LINQ only on small bounded collections, and never in the idle check |
| Repeated relationship recomputation | sparse edges with lazy decay on read and write |
| History rescans for decisions | incremental summaries; the ledger is read only by UI and legend promotion |
| Scanning all world pawns or all maps | reverse maps (`Pawn → CharacterId` / `DeploymentId`); signals for wake-ups; reconciliation limited to Network-owned entities |
| Polling faction relations | checkpoint checks at contract transitions |
| Resolving every org at once (for example a "world turn") | per-entity due ticks with seeded phase offsets |
| String building for logs when logging is off | guarded `if (NetLog.Verbose)`; format only inside the guard |

## 5. Staggering and spikes

- Periodic jobs use `firstDue = now + Hash(seed, kind) mod period`, with ±10% jitter on each
  reschedule. With N entities and period P, the expected jobs per tick is N/P, spread evenly.
- **Catch-up bursts** (after load or a long absence) are handled by the budget. Upkeep coalesces
  overdue periods ([SIMULATION § 1.2](SIMULATION.md#12-tick-entry)).
- **Bidding windows** spread evaluations across open, mid and close passes.
- **Retention sweeps** are budgeted at 500 records per run and continue in the next run.
- **Gossip** has at most 12 recipients per hop and at most 2 hops.
- **Follow-up generation** is one job per follow-up, never generated inside dispatch.

## 6. Memory and save size

- Runtime caches are dictionaries keyed by int IDs. With the typical scale above, total managed
  memory is < 5 MB.
- Save-size budget and pruning: [EVENTS_AND_HISTORY § 11](EVENTS_AND_HISTORY.md#11-save-size-budget).
  Typical ~1–1.5 MB of Network XML after 10 in-game years. Each bound pawn adds vanilla pawn
  size (10–40 KB), which is why bound pawns are capped.
- Terminal entities are compacted after 1 in-game year.

## 7. Verification

1. **Spike S18 (Phase 1)**: a dev harness schedules 10,000 synthetic jobs and publishes 5,000
   synthetic events with realistic consumer work. It measures per-tick cost, the budget
   behaviour and save size.
2. **Phase 2 soak**: simulate 60 orgs for 20 in-game years with the dev fast-forward action
   (abstract resolution only). Record timing histograms per job kind and the save size at each
   year. **As implemented:** `Diagnostics/SoakHarness` runs a scratch in-memory world with the
   real Domain services: about 100 contractors, 6 Fixers, 14 procurement contracts every 7 days,
   daily staggered upkeep, the weekly population manager, history sweeps and compaction. A
   headless test runs 1,080 days (18 in-game years, 2,160 contracts) with these results:
   - no stuck contract, no NPC-issued contract, the population held;
   - every daily invariant at zero: commitments above job capacity, named people on two live
     operations, empty or negative money records, duplicate refunds or payouts, Last Known Location
     cargo above what was secured, drift between the payment port and the ledgers, and transfers
     that do not sum to zero;
   - 360 acceptances fell back to another quote because the cheapest bidder had filled its job
     capacity since quoting (before the correction pass these overcommitted the contractor);
   - bounded stores: about 700 relation edges, 100 knowledge books, 660 history records after
     sweeps, and contracts archived a year after closing;
   - about 1.2 ms of Network work per simulated day (p95 1.7 ms, max 3.6 ms);
   - a Network save node of about 2.5 MB.

   The soak posts far more contracts than a normal game would. The same harness is a dev action,
   with synthetic items or with a sample of the running game's catalog. No per-tick contractor
   logic exists: contractors cost only their daily upkeep job, and contracts cost only their
   window, checkpoint, decision and delivery jobs.

   **Phase 2.5 (spatial continuity, after the correction pass).** The same soak runs on a synthetic
   64 × 40 **charter world** (a sea band with one land bridge, two sealed islands, 41 settlements with a
   high-tech charter provider at every fourth, one provider settlement replaced every half year) with
   ambient relocation, spatial operation plans, Last Known Locations placed near incidents, live Field
   Logs, and a simulated load every 97 days that drops every route cache. Over the 1,080 days: 133
   contractors anchored, ~137,900 catch-ups (≈ 128 a day: the upkeep calls plus checkpoints), ~5,400
   routes built (≈ 5 a day; 12 rebuilt after simulated loads), ~2,880 ambient journeys (all on foot),
   ~1,950 operation plans of which 95 by charter (78 chartered legs, 78 crossings; 43 charter searches
   found no provider in reach), 46 journeys made later than committed because the contractor could
   not walk them sooner, 4 recovered Troubled groups reconciled home, 0 blocked journeys, 0 invalid
   destinations, 0 spatial faults, 6 Last Known Locations all placed near the incident. Daily
   invariants all zero: contractors without a valid anchor, teleports (the longest daily move was 14
   tiles), **route-budget violations** (a contractor farther between two observations than its own
   speed band allows between the ticks each position was reached), ended contractors that moved,
   recovered groups out of place, ambient charters, a live chartered leg disagreeing with its plan, a
   used charter's return on foot, a false "reached the area", duplicated Field Log lines, Field Log
   data on another issuer's or a closed contract. Discontinuities with a stated reason (85: crossings,
   reconciliations, re-anchoring) are counted separately, never silently. Movement work averages ~235
   units a day (a unit is a catch-up, a step advanced, or 1/20 of a route built). The spatial data adds
   about 127 KB to the 18-year save (2.6 → 2.7 MB). With the synthetic graph the harness spends about
   2.1 ms per simulated day in total (about 1.2 ms before spatial; the difference is mostly the
   synthetic breadth-first searches).

   **Archipelago stress** (`Soak.ArchipelagoCharterStress`, 360 days): the same charter world with the
   land bridge closed, so the world's two halves have no ground connection. 34 charter plans, 24
   crossings, all invariants zero, about 1.7 ms per simulated day. Synthetic timings are harness costs,
   **not** RimWorld TPS; S20 measures the real world graph.
   **Phase 2.75 (contractor careers).** No new per-tick work and no new scheduler job: the equipment
   advancement check rides the existing staggered daily upkeep (≈ 0.43 µs per contractor per day, measured:
   100 contractors ≈ 0.04 ms a day, cheap checks first, no allocation), the career result rides the
   operation's existing end-of-lifecycle (a few arithmetic operations and one danger evaluation), and
   contractor money rides the existing ledger transitions. Tags and `CareerNeed` are derived on demand
   (≈ 4 µs per contractor, dev tools and read models only); nothing scans every contractor from a contract.
   Same-seed A/B of the 1,080-day soak (100 contractors; five runs each, this machine): main 2.25 ms per
   simulated day (p95 3.4), branch 2.42 ms (p95 3.5): +0.17 ms, of which the advancement check itself is
   about 0.04 ms; the rest is the two worlds diverging (careers change who is quoted and who wins work),
   inside the run-to-run spread. Three further soaks of 20 in-game years (1,200 days) with all career
   invariants zero: 100 contractors and a person-like client (random quote, 131 cancellations, 56 technical
   voids) 2.49 ms (p95 3.7); the Phase 2 cheapest-quote client 2.41 ms (p95 3.5); **300 contractors** (7,200
   contracts, 201 cancellations, 96 voids) 11.4 ms (p95 14.9, max 21.7). Save size: the career data (score,
   record, flags, ledger attribution) is about **1.6 %** of the save (100 contractors: 48 KB of 3.05 MB;
   300: 113 KB of 7.0 MB). In the same-seed 18-year soak the Network node grows 2,697 → 2,829 KB (+132 KB,
   +4.9 %), of which the actors' nodes are +37 KB; the rest is history, summaries and journal records of the
   diverged world (including the new fame and advancement records, capped by the existing retention).
   **Phase 2.9 (runtime test infrastructure).** Nothing new runs in a normal game: the whole idle cost is one
   static field read and null check per rendered frame in `NetworkWorldComponent.WorldComponentUpdate`
   (`RuntimeTestGame.PumpFrame`), with no scan, enumeration, allocation, scheduler job or per-tick work. The
   runner, its host, the suites and every sandbox are created only when a Dev Mode action starts a run, and
   nothing about a run is saved ([RUNTIME_TESTING § 12](RUNTIME_TESTING.md#12-cost)). Measured headlessly
   (`Runner.IdleCostIsOneNullCheck`, 5,000,000 calls): **about 2.5–3.4 ns per idle frame, 0 bytes allocated**.
   While a run is in progress it is time-sliced to at most 8 ms of real time per frame and captures the live
   state twice per frame. The Network fingerprint hashes every durable field by content (it replaced a cheaper
   count-and-hash version that missed mutations of existing state); measured headlessly
   (`Runner.FingerprintCostIsBounded`, a synthetic world of 365 actors, 61 contracts and operations, 67 history
   records, about 25,000 objects and lists): **about 5 ms per capture**, linear in the live Network's size, plus a
   one-time **about 23 ms** to compile the per-type accessors at the first capture of a process; the reflective
   fallback (no expression trees) takes about 44 ms per capture. A run therefore adds roughly 10 ms to each frame
   it runs in, for the second or two it lasts, and nothing otherwise. The colony sentinel's cost in game (silver by
   beacon, a hash of haulable items, world objects) is bounded and not measured separately. Headless elapsed times of the isolated scenarios through the real runner: Procurement
   (11 tests) ≈ 8 ms, Career (14) ≈ 7 ms, Spatial (8) ≈ 9 ms, all 33 plus the infrastructure checks ≈ 23 ms in 7
   slices. **In-game, as the runner's own summary reported to the owner** (single runs, whole-run totals including both
   fingerprints per slice and the colony sentinel; not a controlled benchmark): fresh Dev Quicktest colony: Quick
   smoke ≈ 84 ms, Full safe regression ≈ 160 ms, Live scan ≈ 3 ms; real heavily modded colony (130 contractors):
   ≈ 133 ms, ≈ 222 ms (first run) / ≈ 96 ms (second run), ≈ 9 ms. No normal-game cost was reported or observed
   (the idle cost is the one null check). The only slow-step WARNs were ≈ 5–7 ms single steps (RT-PROC-001,
   RT-SPAT-008), profiler telemetry rather than a defect.
3. **Phase 3 soak**: a bounded number of persisted physical-identity records (target 150 bound people) plus 5
   concurrent physical episodes, with the idle (nobody physical) cost measured separately and required to be
   near zero. Compare TPS with and without The Network on the same save (the prepared-removal path).
   Budgets and the required measurements are in [PHYSICAL_LIFECYCLE § 18](PHYSICAL_LIFECYCLE.md#18-performance).
   **Phase 3 design budgets (targets, nothing measured yet; no Phase 3 code exists).** Nobody physical and nobody
   vanilla-held: **zero** (no job, no per-tick work). An Open episode (≤ 8 people): one `episode.watch` job every 250
   ticks (≈ 15 hash/contains operations per person); held people: one global `custody.watch` every 2,500 ticks, only
   while someone is held; signals O(1); reconcile < 1 ms. The one cost that is **not** ours to bound is the registry
   reservation: vanilla evaluates `IsReservedByAnyQuest` (quests × parts × `List<Pawn>.Contains`) for each
   non-mothballed world pawn per tick, so the reserved list length *R* (the stored named people, soft cap ≈ 150) is
   a multiplier to **measure** in the soak ([PHYSICAL_LIFECYCLE § 7.4](PHYSICAL_LIFECYCLE.md#74-the-registry-reservation-retained-pawns-only)).
   Forbidden: scanning all pawns, all maps or all world pawns on a timer.
   **Added by the amendment pass (targets, nothing measured):** the atomic reconciliation commit is O(the touched set: ≤ 8
   characters and three small objects), a snapshot and, only on failure, a restore, well under 0.2 ms; role and cohesion
   verification is reads of one unbound candidate and ≤ 7 teammates inside a bounded retry; truthful aging catches up once
   per materialized stored pawn (a *periodic* variant would be ≤ 150 calls per game-year, only while a pawn is stored); encounter
   evidence is ≤ 8 play-log / battle-log lookups per reconcile; role-composition apportionment is O(≤ 8). Forbidden: a per-tick
   aging job for stored pawns; scanning the play log per tick. The soak also reports **retained-pawn growth under progressive
   concretization** (R-36) and the *R* × *W* registry cost ([PHYSICAL_LIFECYCLE § 18](PHYSICAL_LIFECYCLE.md#18-performance)).
4. **Regression gate**: the timing report (see [DEBUGGING § 5](DEBUGGING.md#5-timing-instrumentation))
   is attached to each phase's PR, with the p50, p95 and max per job kind.
