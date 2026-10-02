# Debugging Architecture

> Developer tooling is planned from the start. It ships in phases alongside the systems it
> inspects. Related: [PERFORMANCE § 7](PERFORMANCE.md#7-verification), [SAVE_AND_MIGRATION § 7](SAVE_AND_MIGRATION.md#7-failed-migration-and-quarantine).

## Contents

1. [Goals](#1-goals)
2. [Logging policy](#2-logging-policy)
3. [Dev actions](#3-dev-actions)
4. [Validators](#4-validators)
5. [Timing instrumentation](#5-timing-instrumentation)
6. [Headless tests](#6-headless-tests)
7. [Inspector windows](#7-inspector-windows)

---

## 1. Goals

- **Silent in release play.** Normal play logs nothing except real problems, and each problem
  class is logged once.
- **Everything explainable.** Every decision that matters (verdicts, refusals, bands, fates,
  invalidations) records reason codes that a tool can show.
- **Everything forceable in dev mode.** Any state a player could reach can be forced by a dev
  action, to reproduce bugs and to test content.
- **Validation is a tool, not a hope.** Invariants are checked by code on load and on demand.

## 2. Logging policy

| Level | When | Default |
|---|---|---|
| `Error` | invariant broken; exception in a job or consumer; failed migration | always on, **once per (category, key)** per session |
| `Warning` | external interference (a pawn discarded by another mod); downgraded save; one-time compatibility notices | always on, once per key per save (`diagnostics.oneTimeWarnings`) |
| `Info` | bootstrap summary; load summary (counts, migrations run, invalidations aggregated) | one line per load |
| `Verbose` | per-event, per-job and per-decision traces | **off**; toggled in Mod Settings ("Detailed Network logging") and per category |

- The prefix is always **`[TheNetwork]`**, followed by the category: `[TheNetwork][Intel] …`.
- Categories: `Kernel`, `Scheduler`, `Events`, `History`, `Actors`, `Custody`, `Intel`,
  `Opportunities`, `Contracts`, `Resolver`, `Catalog`, `Compat`, `Save`.
- **Aggregation**: missing-reference notices are grouped per def or faction and logged once, for
  example "3 references to missing ThingDef 'BOR_Tenebrite' invalidated (2 intel, 1 opportunity)".
- **Zero-cost when off**: `NetLog.Verbose(category)` is a bool field check. Message formatting
  happens only inside the guard.
- **Build stamp**: the assembly logs its version and a build stamp once at startup, the same
  approach as Grandmaster21's `Gm21BuildStamp`. Stale DLLs are then easy to spot in Player.log.

## 3. Dev actions

These are `[DebugAction("The Network", …, allowedGameStates = Playing)]` entries in the vanilla
debug menu. No Harmony. Each action logs what it did and bumps `StateVersion`.

| Group | Action | Phase |
|---|---|---|
| Catalog | Rebuild item catalog · Explain item… · Catalog report (CSV) | 1 |
| Cast | Global cast report (settings: templates, provenance, quarantine) · World cast snapshot report · Regenerate generated cast (dev; keeps custom entries) · Inspect Fixer… | 1 |
| Inspect | Inspect actor… · Inspect character… · Inspect contract/operation… · Inspect opportunity… · Print knowledge of actor… · Print relations of actor… · Print summaries of actor… · Event journal (window) · History ledger (window) | 1–2 |
| Intel | Run the next search round now… · Force archetype for the next round… · Force divergence class… · Force no-lead… · Explain source resolution for item… (candidates, evidence, filters, scores) · Reroll the current round (nonce++) · Toggle comms-gate override (dev only) | 1 |
| Opportunities | Materialize opportunity… · Expire now… · Force claim… · Spawn test opportunity at the selected tile | 1 |
| Contractors | Create contractor (template…) · Set morale… · Set relationship A→B… · Kill leader… · Force retirement… · Force fragmentation… (Ph.6) · Force merger… (Ph.6) | 2 / 5 / 6 |
| Contracts | Post a test procurement… · Force outcome band… · Force capture… · Force delay… · Force failed expedition site… (Ph.3) · Complete now… | 2–3 |
| Custody | Materialize character… · Begin test deployment… · Reconcile deployment now… · Registry audit | 3 |
| Simulate | Fast-forward N abstract days (runs scheduler handlers with no map ticking; dev only) · Simulate N thousand abstract contracts (headless resolver stats: band distribution, casualty rates, prices) | 2 |
| Validate | Validate all (IDs, orphans, custody, scheduler, caps) · Quarantine report · Failed consumers report · Clear quarantine entry… | 1 |
| Performance | Print timing report · Reset timing counters · Print counts vs caps | 1 |
| Save | Prepare save for removal (also exposed in Mod Settings) | 1 |

`Fast-forward` and `Simulate` never touch maps or pawns. They drive only the abstract layer, so
they are safe to run in a test save to observe long-run dynamics (population, legends, save size).

**Phase 2 as implemented** (debug menu category **"The Network (Phase 2)"**). Each action runs the
ordinary code path sooner or with a forced draw. None skips a state's bookkeeping.

| Group | Action |
|---|---|
| Contractors | Inspect contractor… (dev-only numbers: doctrine values, strength, skill) · Create world-generated contractor · Kill contractor (Solo) or its leader… · Force leader succession… · Set morale descriptor… · Run upkeep now (every contractor, plus the population manager) |
| Contracts | Post procurement contract (dev)… · Close bidding window now… · Dump contract (bidders, refusals, quote components, ledger as `money <direction> <purpose> <silver> [with #linked contract]`, operation, delivery)… · Force an offer from a contractor… · Accept the cheapest open offer… · Apply the pending decision's grace default now… |
| Operations | Run next operation checkpoint now… (also resolves a Troubled deadline) · Force outcome band on next resolution… · Force partial result · Force catastrophe · Force 3-day delay · Force Troubled (missing) · Force "worse than expected" on next engagement · Force Last Known Location on next loss · Force a newcomer bidder on the next open contract |
| Delivery | Deliver now… · Force the next 3 delivery attempts to fail (→ Hold) · Delivery plan report |
| Relations and knowledge | Dump relationships (strongest 40) · Dump knowledge… |
| Simulate | Simulate procurement (soak harness: 100 contractors, 360 days, synthetic items) · Simulate procurement with this game's catalog. Both run in a scratch in-memory world and never touch the save. |

**Phase 2.5 as implemented** (debug menu category **"The Network (Phase 2.5)"**). Exact tile ids
appear in these readouts only, never in normal UI; nothing is drawn on the world map.

| Group | Action |
|---|---|
| Spatial truth | Inspect contractor spatial state… · Dump all spatial states (with counts by status and the counters) · Initialize spatial state (all Uninitialized) · Catch up one contractor now… |
| Movement | Send contractor somewhere nearby now… · Force ambient relocation now… · Invalidate a contractor's destination… (the next catch-up recovers) · Rebuild route caches (as a load does) |
| Operations and consequences | Move a running operation's work region… (timeline unchanged) · Force Missing at the contractor's position on next resolution · Create a Last Known Location near an operation's contractor… · Force Disaster, not Troubled, on next resolution · Force the next Troubled deadline: group found / written off |
| Charter (ADR-045) | Inspect an operation's spatial plan (ground or charter)… (hub, landing, fallback; the contractor's current segment) · Send a running operation across water (charter test)… (retargets to a tile with no ground route) · Invalidate an operation's charter hub… (reconciles from current truth) |
| Field Log and performance | Inspect a contract's Field Log… (keys, words, ticks) · Spatial performance counters |

**Phase 2.75 as implemented** (debug menu category **"The Network (Phase 2.75)"**, development mode only).

| Group | Action |
|---|---|
| Careers | Inspect contractor career… (fame, reputation score and the next band, experience, career stage, `opsCompleted`, `legacyResolved`, the detailed career record, funds, operating reserve, equipment tier and condition, `CareerNeed`, derived Tags, last advancement, advancement count, and why it can or cannot advance now) · Grant reputation to contractor… (same path as real play: the band re-derives) · Add test funds to contractor… (saturating) · Run career advancement now… (the same rules: committed, cooldown, reputation, funds + reserve, recovery) · Dump career distribution… (fame, experience, tiers, needs, Tags, funds and reputation min/median/max, upgrades, this session's career tallies) |

**Phase 2.9 as implemented** (debug menu category **"The Network"**, `Runtime tests: ...`, developer infrastructure only;
[RUNTIME_TESTING](RUNTIME_TESTING.md)). Unlike every action above, these **change nothing in the colony**: they
run the production services in an isolated in-memory sandbox and read the live game strictly read-only.

| Group | Action |
|---|---|
| Run | Runtime tests: **Quick smoke** (RT-SMOKE-001..008, stops at the first failure) · **Full safe regression** (smoke + live scan + the procurement, career and spatial sandbox scenarios; verbose; does not stop) · **Live integration scan** (RT-LIVE-001..006, read-only) |
| Control | Runtime tests: **Status** (suite, test, step, progress, counts, options, preserved failure) · **Cancel current run** (clean, at the next safe boundary) |
| Report | Runtime tests: **Last report** (to the log) · **Export last report** (`<SaveDataFolderPath>/TheNetwork/runtime-tests-YYYYMMDD-HHMMSS.txt`) · **Inspect preserved failure** (the kept sandbox of the last failed test, in memory only) |

A run logs a summary when it starts and when it ends and shows one Message (positive, negative or neutral);
it sends no letter. It **never starts or repairs the live Network**: in a game loaded paused (never ticked) the
tests that need a started Network report SKIP with the advice to unpause for one tick and rerun, and RT-INFRA-001
says nothing was verified. A WARN result means production code logged a warning or error while a test ran (the
lines are in the report and the real log). The runner restores every static dev override to its previous value after every step, so
the forced draws above are never changed by a run.

Spatial log lines use the `[TheNetwork][Spatial]` category ([SPATIAL § 13](SPATIAL.md#13-diagnostics)).

The forced draws live in `ProcurementDevOverrides` (runtime only, never saved) and are consumed by
the next matching decision. The headless tests use the same overrides.

## 4. Validators

`NetValidator.RunAll(mode)`. The mode is `OnLoad` (automatic, budgeted) or `Full` (dev action).
Each check reports findings with the entity reference and a reason code, and may auto-repair when
the repair is safe.

| Check | Validates | Auto-repair |
|---|---|---|
| **ID uniqueness** | every entity ID is unique and `< nextId`; typed ID kinds match their stores | raise `nextId` above the maximum of every kind that draws from it (actors, characters, intel requests, leads, opportunities, history records, contracts, offers, operations) on load; quarantine duplicates |
| **Orphan references** | each `ActorId`, `ContractId`, … field resolves to an entity or a tombstone; `EntityRef` kinds are consistent | clear optional refs; quarantine entities with required orphans |
| **External references** | DefRefs, FactionRefs, WorldObjectRefs and TileRefs resolve | raise `Reference.Invalidated` (subsystem policy) |
| **Custody invariants** | I-1…I-10 ([ABSTRACT_PHYSICAL_LIFECYCLE § 3](ABSTRACT_PHYSICAL_LIFECYCLE.md#3-invariants)); reserved set = custody records; tags present | re-reserve; re-tag; mark Lost when the pawn is gone |
| **Scheduler agreement** | every entity-side due tick has a job and every job has a live target (including each live contractor's `contractor.upkeep` job) | recreate or delete jobs; a missing upkeep job keeps its saved due tick when valid, runs shortly when past due, otherwise gets the normal stagger; never for ended or quarantined contractors |
| **State-machine sanity** | each state is valid for its entity type; terminal entities have outcomes; timers exist for waiting states | quarantine |
| **Lineage** | contract, opportunity and actor lineage graphs are acyclic; depth is within the cap | cut cycles and log |
| **Roster arithmetic** | `committed ≤ healthy + wounded`; no negative counts | clamp and log |
| **Money** | ledger sums are consistent with contract states | none (report only) |
| **Caps** | history, journal, legends and characters are within caps; the bound-pawn **soft** cap is reported when exceeded (protected characters are never released to meet it) | schedule a retention sweep; release only unprotected dormant characters |

## 5. Timing instrumentation

- `NetProfiler` wraps every scheduler job and every event consumer in a `Stopwatch` sample
  **when profiling is enabled** (a Mod Settings toggle, off by default). It aggregates count,
  total, max and a p95 estimate per job kind or consumer.
- **Budget warnings**: if a single job exceeds 5 ms, or a tick's Network work exceeds the budget
  three ticks in a row, one warning is logged with the job kind (profiling on only).
- The report prints via dev action as a table sorted by total time.
- Optional: `DeepProfiler.Start/End("TheNetwork.<kind>")` markers, so RimWorld's own profiler
  (dev mode) attributes time to Network work.
- Counts vs caps (history, journal, bound pawns, jobs) are shown in the same report.

## 6. Headless tests

The Domain and Kernel layers are written so their **pure logic** can run without the game:

- Resolver math, willingness, pricing, reputation inference, retention policy, state-machine
  transition tables, NetRng and migrations are pure functions of plain inputs.
- **Build approach** (adapted from Grandmaster21's `Tests/` and `tools/verify-real.sh`): compile
  a small test executable against the real `Assembly-CSharp` and the Network DLL, and run
  test cases that do not require a running game. `IExposable` round-trips are tested through
  Scribe in memory where that is feasible, otherwise through fixture XML parsed by the
  tolerant loader.
- **Fixtures**: `Tests/Fixtures/vN/*.xml` store `NetworkWorldComponent` node snapshots per save
  version, for migration tests ([SAVE_AND_MIGRATION § 4.6](SAVE_AND_MIGRATION.md#46-testing)).
- **Determinism tests**: the same seed and inputs must give the same output; resolving twice
  must equal resolving once (idempotency).
- **Reflective target verification** (only once Harmony or reflection is used): verify every
  reflective target and parameter name against the shipped assembly at build time.

### 6.1 Runtime regression tests (Phase 2.9)

The headless suite cannot show that the loaded mod, the real Defs, the real catalog and the real adapters still
agree with the logic in a running game. [RUNTIME_TESTING](RUNTIME_TESTING.md) adds an in-game runner for that,
under the same rules as everything here: it is Dev Mode only, **never saved**, uses no Harmony, and is silent
unless a Dev action starts it (idle cost: one static null check per frame).

- **Where the code is.** `Source/TheNetwork/Diagnostics/RuntimeTests/` (runner, sandbox, fingerprint, invariant
  scan, report, game host) and `Suites/` (`RuntimeSmokeSuite`, `RuntimeLiveSuite`, `ProcurementRuntimeSuite`,
  `CareerRuntimeSuite`, `SpatialRuntimeSuite`).
- **Headless coverage of the runner** is `Tests/TheNetwork.Tests/RuntimeRunnerTests.cs` (filter: `TEST_FILTER=Runner.`).
  The sandbox suites also run headlessly through the real runner against a synthetic live world, so the
  scenarios are checked on every build and only the game-only smoke and live suites need the game.
- **Reading a failure.** The log line and the report give the stable ID (`RT-PROC-007`), expected and actual,
  the exception, the sandbox tick, the entities, and the scratch world's own log lines. *Inspect preserved
  failure* dumps the failed sandbox. Re-run the one scenario headlessly by adding its ID to a filter in
  `RuntimeRunnerTests.cs`, or in the game with *Full safe regression*.
- **Do not** call `NetValidator.RunAll` from a runtime test (it repairs); use `LiveInvariants.Scan`.
- **A run that finds a live-state change** reports **RT-INFRA-001** with the store and entity that moved
  (`contracts.contracts[3]#17`): that is a bug in a test (or in a service that was handed a live object), not in
  the colony. **A run whose fingerprint could not be captured also FAILS RT-INFRA-001** (it fails closed), with the
  end (before / after), the slice and the exception. Its scope is the Network's durable fields plus the colony
  sentinel ([RUNTIME_TESTING § 6](RUNTIME_TESTING.md#6-safety-model)); a runtime-only cache field that a read-only call
  fills in must be named `cached*` or the walker will report it.

## 7. Inspector windows

These are dev-mode windows, shipped with the phase that introduces each subsystem:

- **Network Inspector**: a tree of stores, then entities, then fields, with jump-to for any ID
  and live refresh on `StateVersion`.
- **Event Journal**: a filterable list (type, importance, subject) with payload details and
  consumer errors.
- **Deployment Monitor** (Phase 3): each entry's pawn, its current observed state, the pending
  fate and the reconciliation trace.
- **Relationship Graph** (Phase 4+): text-based adjacency with standing and trust per edge,
  filtered by actor.
