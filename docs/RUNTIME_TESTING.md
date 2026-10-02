# Runtime Regression Testing (Phase 2.9)

> Developer infrastructure, not gameplay. A small in-game test runner that exercises the **real**
> production services inside the **running RimWorld process**, without ever touching the colony it
> runs in. It complements the headless suite; it does not replace it.
> Related: [DEBUGGING](DEBUGGING.md), [ARCHITECTURE](ARCHITECTURE.md), [PERFORMANCE](PERFORMANCE.md),
> [RISKS R-27](RISKS.md), [ADR-047](DECISIONS.md), [IMPLEMENTATION_PHASES § 5C](IMPLEMENTATION_PHASES.md).

## Contents

1. [Why this exists](#1-why-this-exists)
2. [Headless tests versus runtime tests](#2-headless-tests-versus-runtime-tests)
3. [Architecture](#3-architecture)
4. [The sandbox](#4-the-sandbox)
5. [The live scan](#5-the-live-scan)
6. [Safety model](#6-safety-model)
7. [Stable IDs and suites](#7-stable-ids-and-suites)
8. [How to run](#8-how-to-run)
9. [PASS / FAIL / WARN / SKIP](#9-pass--fail--warn--skip)
10. [Preserved failed sandbox](#10-preserved-failed-sandbox)
11. [Exported reports](#11-exported-reports)
12. [Cost](#12-cost)
13. [What is not automated](#13-what-is-not-automated)
14. [Adding a suite in a later phase](#14-adding-a-suite-in-a-later-phase)
15. [Owner-observed runtime evidence](#15-owner-observed-runtime-evidence)
16. [What was and was not validated in this phase](#16-what-was-and-was-not-validated-in-this-phase)

---

## 1. Why this exists

The headless suite (`Tests/run-tests.sh`, Mono, no RimWorld) proves the *logic*. It cannot prove that
the loaded mod, the real Defs, the real catalog, the real world graph and the real adapters still agree
with that logic in a running game, and the owner had been the only runtime test. Phase 3 (physical
contractor lifecycle) will add far more multi-step behaviour than can be re-checked by hand after every
change, so the framework is built **now**, while The Network is still almost entirely abstract, and
Phase 3 adds its own runtime scenarios to a framework that is already proven.

It must be safe to press in a real colony: a regression test that spends silver, spawns cargo or leaves
objects behind is itself a bug generator, and nobody will run it twice.

## 2. Headless tests versus runtime tests

| | Headless suite (`Tests/`) | Runtime tests (this phase) |
|---|---|---|
| Runs in | Mono, no RimWorld | the running game, Dev Mode only |
| Proves | the logic and every invariant, over thousands of generated cases and multi-year soaks | the *integration* in a real process: loaded Defs, real catalog, real world graph, real startup |
| Mutates | a scratch world per test | **never** the live Network; scenarios run in an isolated in-memory sandbox |
| Determinism | seeded | seeded from the test ID + a fixed Phase 2.9 seed |
| Owns the code under test | the production DLL, compiled into the test binary | the production DLL, **running in the game** |
| Required to merge | yes | no (it is run by a person; see § 16) |

The runtime runner itself is also tested headlessly (the 23 `Runner.*` tests, § 12 and [DEBUGGING](DEBUGGING.md)):
its exception containment, ordering, timeouts, cancel, override restore, report counts, and above all
that a safe run leaves a synthetic live world's fingerprint identical. The production assembly never
references the test project; the sandbox suites compile into the mod and are run headlessly through a fake
host.

## 3. Architecture

All of it lives under `Source/TheNetwork/Diagnostics/RuntimeTests/` and is reachable from exactly one
place: eight Dev Mode actions (§ 8) and one per-frame call from `NetworkWorldComponent.WorldComponentUpdate`.

```
 Dev Mode action ──► RuntimeTestGame.Start ──► RuntimeTestRunner.Start(plan, options)
                                                       │
 every rendered frame:                                 ▼
 WorldComponentUpdate ──► RuntimeTestGame.PumpFrame ──► Pump(): one SLICE (default 8 ms real time)
                          (one static null check               │
                           when idle)                          ├─ fingerprint the live Network (before)
                                                               ├─ run steps of the current test, each wrapped:
                                                               │     capture owner's dev overrides → apply neutral
                                                               │     → run step (exception contained)
                                                               │     → detect leak → restore the PREVIOUS values
                                                               ├─ a Wait step ends the slice at once
                                                               └─ fingerprint the live Network (after) → must be equal
```

* **`RuntimeTestCase`** — stable ID (`RT-PROC-007`), suite, name, a body, `NeedsSandbox`, a real-time
  timeout (default 20,000 ms) and a maximum number of waits (4,000). Either **immediate** (one function) or
  **stepped** (called repeatedly; each call returns `Pass`, `Continue`, `Wait(what)`, `Fail(message)` or
  `Skip(reason)`).
* **`RuntimeTestContext` / `RuntimeAssert`** — the tiny assertion API every test uses: `True`, `False`,
  `Equal`, `NotEqual`, `NotNull`, `Null`, `Zero`, `AtLeast`, `AtMost`, `Fail`, and
  `Eventually(condition, what)` for waits. A failed assertion carries *expected* and *actual*.
* **`RuntimeTestRunner`** — pumped, never blocking. A pump runs steps until its real-time budget is spent
  (always at least one step) and then yields to the game; a `Wait` yields at once. Per-test exception
  containment: any exception becomes that test's `FAIL` with type, message and stack, and the run
  continues (or stops, if `stopOnFirstFailure`). The runner never uses the persisted Network scheduler for
  its own bookkeeping — there is **no `devtest.*` job** — and nothing about a run is saved.
* **`RuntimeTestSession` / `RuntimeTestReport`** — results in order, counts, the report text.
* **`RuntimeTestGame`** — the RimWorld-side entry points: `Start`, `Cancel`, `StatusText`, `LastReportText`,
  `Export`, `InspectPreserved`, and the per-frame `PumpFrame`. A run started in one world is abandoned if a
  different world component is later updated (the player loaded another game).
* **Options.** `stopOnFirstFailure`, `verbose`, `preserveFailedSandbox`. *Quick smoke*: stop on first
  failure, quiet, preserve. *Full safe regression*: do not stop, verbose, preserve. *Live scan*: do not stop,
  verbose, nothing to preserve.

**Determinism.** A test's `Rng` is a `NetRng` seeded from `NetHash.Combine(20290, testId)`, so the same ID
always draws the same numbers, independent of the order of the plan. The spatial scenarios that need a
particular world shape (a journey that travels; a world that needs a charter) find it with a fixed derived
seed search, so the world is the same every run.

**Timeouts.** Every test has a finite real-time timeout *and* a finite number of waits, and a sandbox's own
clock only moves through bounded `AdvanceUntil(condition, maxTicks, stepTicks)`. A timeout is reported as
`TIMEOUT` with the step, the sandbox tick, the number of pending sandbox jobs and the entities the test
registered.

**Cancel.** `Cancel` sets a flag honoured at the next safe boundary (between steps): the current sandbox is
discarded, the overrides are already restored (they are restored after every step), the run ends as
`Cancelled`, and a Message says so.

## 4. The sandbox

`RuntimeTestSandbox` is a complete, private mini-Network built from the **production** classes — the same
way the in-game soak harness already builds a scratch `DomainContext` — sharing nothing writable with the
live Network:

* its own `IdAllocator`, `ManualClock`, `NetScheduler` (jobs registered by the production
  `NetworkRuntime.RegisterPhaseTwoJobs`), `EventJournal`, `DiagnosticsState`, `NetworkEventBus`,
  `HistoryLedger` / `SummaryStore` / `HistoryService`, and every store;
* **sandbox ports** in place of the game adapters: `SandboxComms` (always open), `SandboxPayment` (default
  1,000,000 silver, counts charges and refunds), `SandboxCatalog` (synthetic goods `RT_Steel`, `RT_Rifle`
  with deterministic facts; a real item's facts can be copied in **by value**), `SandboxWorldFacts`,
  `SandboxSites`, `SandboxDelivery` (Success / NoDropSpot / CreationFailure — it records, it spawns
  nothing), and `GridWorldGraph`, a synthetic world instead of the planet;
* `SandboxEventRecorder`, a Presentation-stage consumer that records what would have been announced — no
  letter, no message, no history outside the sandbox;
* production services run unmodified over those ports: `ProcurementService`, `OperationService`, the
  resolver, `CareerService`, `SpatialService`, the money and ledger paths, the consequence engine.

A sandbox can be **disposed**; a disposed sandbox refuses further use. The scratch world's log output is
captured (and the real once-per-session warning memory snapshotted and restored) so a scenario can neither
spam the real log nor use up the real "warn once" slots. Nothing a sandbox holds is a `IExposable`, is added
to a `Scribe`, or is reachable from the save.

## 5. The live scan

`RT-LIVE-*` and `RT-SMOKE-*` inspect the **actual** loaded game, strictly read-only:

* the real `NetworkWorldComponent` and `NetworkRuntime` exist and started; the save version is understood;
  the stores and indexes are present; no subsystem failed at start-up;
* the real item catalog built from the loaded Defs and contains base-game goods; the procurement contract
  kind is registered;
* the real comms gate reads the colony truthfully; the real payment adapter can be **inspected** (one
  enumeration of silver; `CanCharge(reachable)` true and `CanCharge(reachable + 1)` false — it never
  charges); the real world graph adapter reads the real world; the real drop-pod **plan** is deterministic and
  spawns nothing;
* the live Network passes `LiveInvariants.Scan`, a Verse-free, read-only invariant scan (ids, the player's
  proxy, reputation / funds / tier bounds, ledger attribution including typed full reversals, a voided
  contract holds nothing, operation flags, scheduler orphans as notes). It deliberately does **not** call
  `NetValidator`, which repairs.

Nothing here mutates anything. A live scan with a colony that lacks a precondition (no comms console, no
home map) reports `SKIP` or `WARN`, never a failure invented from the colony's state.

## 6. Safety model

The default suites (Quick smoke, Full safe regression, Live integration scan) are designed so that pressing
them in a real colony changes **nothing the player can observe or keep**. In particular they never:

* spend the player's silver or any item (the sandbox has its own payment port; the live adapter is only
  *inspected*);
* delete a player item, spawn a drop pod, spawn cargo or any world object, send a letter or message other
  than the single end-of-run Message;
* create a contract, operation, history entry, relationship or contractor in the live Network, or alter any
  contractor's career, reputation, funds, equipment, location or history;
* move a real contractor, create a site, or leave any test entity behind;
* require reloading a backup, quit, restart or reload a save.

How that is **enforced** rather than hoped for (each is checked, see § 12):

1. **Isolation by construction** — the sandbox shares no writable state with the live Network (§ 4).
2. **A fingerprint proves it** — a `LiveFingerprint` hashes the live actors, contracts, ledgers, operations,
   history, relations, careers, scheduler jobs and id counters (plus their counts). It is captured before and
   after **every slice** (one synchronous call, so the game cannot legitimately move in between) and the run
   reports **RT-INFRA-001** (live Network state unchanged) from it. A test that touches live truth fails the
   run and names what moved.
3. **Dev overrides are snapshotted and restored exactly** — `ProcurementDevOverrides` (10 values),
   `IntelDevOverrides` (6) and `ServiceToggles` (2): 18 values. Each step captures the owner's current values,
   presents the neutral state a scenario expects, runs, then restores the **previous** values (never a blind
   reset: the owner may already have the comms override on) — even for a frame they are never visible to the
   game. A step that returns normally leaving an override set **fails its test** (restored anyway):
   **RT-INFRA-002**.
4. **No control job in the persisted scheduler** — **RT-INFRA-003** verifies no runtime-test job kind is in
   the live scheduler (the runner has none).
5. **Sandboxes are accounted for** — every sandbox created is disposed or deliberately preserved:
   **RT-INFRA-004**.
6. **A source scan in `Tests/run-tests.sh`** (ADR-047) forbids runtime-test code from spawning, spending,
   launching, drop-podding, sending letters, or touching the live scheduler or `Scribe`.
7. **No Harmony, no save-version change** — the mod stays Harmony-free; the save version stays 4; runner and
   sandbox state is never `Scribe`d.

**The one thing a run can cause in the live Network** is the game's *own* start-up gate. In a game that has
just been loaded paused, nothing has ticked yet, so the Network has not started; `RT-SMOKE-002` ("`EnsureStarted`
succeeds") calls the same gate the first tick, the Network tab and every command call. In any game that has
ticked, or in which a Network tab or command was used, it is already running and the call changes nothing.
When it does start the Network, the test notes it, the runner takes no live-state comparison for a slice that
started with no settled state to compare (the host returns no fingerprint until the Network is running), and
RT-INFRA-001 says how many slices could not be compared. Every later slice is compared normally, and a test
that touches live state after the start-up is still caught (`Runner.UnstartedNetworkIsNotAFalseAlarm`). The
Live scan alone never starts the Network: RT-LIVE-006 reports `SKIP` for a Network that has not started.

## 7. Stable IDs and suites

IDs are permanent: a test is never renumbered or reused, so a report from last month still means the same
thing. A new behaviour gets a new ID. A plan with a duplicate or empty ID is refused when it is built.

**Smoke — `RT-SMOKE-*` (game only, read-only)**

| ID | Checks |
|---|---|
| RT-SMOKE-001 | `NetworkWorldComponent` exists |
| RT-SMOKE-002 | `NetworkRuntime` exists and started |
| RT-SMOKE-003 | the save version is understood |
| RT-SMOKE-004 | stores and indexes are present and addressable |
| RT-SMOKE-005 | the item catalog builds from the loaded Defs |
| RT-SMOKE-006 | the procurement contract kind is registered |
| RT-SMOKE-007 | actor indexes resolve the known actors consistently |
| RT-SMOKE-008 | no subsystem failed at start-up |

**Live integration scan — `RT-LIVE-*` (game only, read-only)**

| ID | Checks |
|---|---|
| RT-LIVE-001 | the real catalog holds base-game goods |
| RT-LIVE-002 | the comms gate reads the colony truthfully |
| RT-LIVE-003 | the payment environment can be inspected without spending |
| RT-LIVE-004 | the world-graph adapter reads the actual world |
| RT-LIVE-005 | the drop-pod plan is deterministic and spawns nothing |
| RT-LIVE-006 | the live Network passes the read-only invariant scan |

**Procurement — `RT-PROC-*` (sandbox)**

| ID | Checks |
|---|---|
| RT-PROC-001 | post, bid, offer, accept: the operation starts |
| RT-PROC-002 | full success: requested equals secured |
| RT-PROC-003 | the balance is paid exactly once |
| RT-PROC-004 | delivery commits the exact payload once |
| RT-PROC-005 | the terminal contract closed exactly once |
| RT-PROC-006 | repeated terminal calls change nothing |
| RT-PROC-007 | **payment hold → funds restored → the SAME contract resumes → exact delivery** (stepped; the balance is *derived from the generated terms*, never hard-coded) |
| RT-PROC-008 | a payment hold survives scheduler passes with no duplicate charge or contract (stepped, uses `Wait`) |
| RT-PROC-009 | the partial-result decision path is deterministic (twin sandboxes) |
| RT-PROC-010 | a technical Void leaves the current contractor no windfall |
| RT-PROC-011 | a *real* catalog item (its facts copied by value) is priced and bid through production services; `SKIP` outside the game |

**Careers — `RT-CAR-*` (sandbox)**

| ID | Checks |
|---|---|
| RT-CAR-001 | a new eligible operation applies one `CareerRecord` result |
| RT-CAR-002 | the applied flag is set only after the durable career commit |
| RT-CAR-003 | repeated Finish, retry and validation do not duplicate the result |
| RT-CAR-004 | numeric reputation changes and the fame band follows the score |
| RT-CAR-005 | Fame stays independent from Experience |
| RT-CAR-006 | contractor payment enters funds exactly once |
| RT-CAR-007 | an ordinary refund claws back proportionally |
| RT-CAR-008 | a replacement's `TransferIn` is not the replacement's income |
| RT-CAR-009 | an insurance payout itself does not claw contractor money |
| RT-CAR-010 | insurance payout then technical Void: the current contractor retains zero |
| RT-CAR-011 | equipment advances one tier only, needing fame, money and reserve, with a cooldown |
| RT-CAR-012 | `CareerNeed` is deterministic |
| RT-CAR-013 | Tags describe state and change no strength by being read |
| RT-CAR-014 | `Augmented` is not emitted without augmentation truth |

**Spatial — `RT-SPAT-*` (sandbox)**

| ID | Checks |
|---|---|
| RT-SPAT-001 | every contractor receives valid initial hidden spatial truth |
| RT-SPAT-002 | a reachable procurement goes origin → work region → return |
| RT-SPAT-003 | the Field Log says "reached the area" when the contractor actually arrived |
| RT-SPAT-004 | a blocked or late arrival never creates a false arrival |
| RT-SPAT-005 | an unreachable-by-ground destination plans a committed charter (the `TransportArranged` beat appears at the Prep checkpoint, not at plan time) |
| RT-SPAT-006 | the used outbound charter is the committed way back |
| RT-SPAT-007 | losing the provider after the outbound charter degrades safely |
| RT-SPAT-008 | the Last Known Location agrees with the hidden spatial truth |

**Infrastructure — `RT-INFRA-*` (appended to every run)**

| ID | Checks |
|---|---|
| RT-INFRA-000 | the runner itself did not throw outside a test (only present when it did) |
| RT-INFRA-001 | the live Network's fingerprint was identical before and after every slice (`SKIP` in a host with no live state) |
| RT-INFRA-002 | dev overrides and service toggles were restored exactly after every step |
| RT-INFRA-003 | no runtime-test job kind is in the live persisted scheduler |
| RT-INFRA-004 | every sandbox was discarded or deliberately preserved |

**Plans.** *Quick smoke* = RT-SMOKE-001..008. *Live integration scan* = RT-LIVE-001..006.
*Full safe regression* = smoke + live + the 33 sandbox scenarios (47 tests) + the INFRA checks.

## 8. How to run

Dev Mode must be on. In a loaded game, **Dev Mode → Debug actions → The Network**:

| Action | Does |
|---|---|
| Runtime tests: Quick smoke | RT-SMOKE-001..008; stops at the first failure; seconds |
| Runtime tests: Full safe regression | everything above plus the sandbox scenarios; does not stop; verbose |
| Runtime tests: Live integration scan | RT-LIVE-001..006, read-only |
| Runtime tests: Status | the running (or last) suite, test, step, progress, counts, options, preserved failure |
| Runtime tests: Cancel current run | stops at the next safe boundary; overrides already restored; sandbox discarded |
| Runtime tests: Last report | writes the last full report to the log |
| Runtime tests: Export last report | writes `runtime-tests-YYYYMMDD-HHMMSS.txt` under `<SaveDataFolderPath>/TheNetwork/` and logs the path |
| Runtime tests: Inspect preserved failure | writes the preserved sandbox's description (§ 10) to the log |

A run logs a one-block summary when it starts and when it completes, and shows exactly one Message at the
end: positive (all passed), negative (any failed) or neutral (cancelled). It never sends a letter. Only one
run can be active; starting a second reports that one is in progress. The pump runs from
`WorldComponentUpdate`, i.e. every rendered frame **including while the game is paused**, so a run finishes
whether or not game time advances.

## 9. PASS / FAIL / WARN / SKIP

* **PASS** — every assertion held.
* **FAIL** — an assertion did not hold (expected and actual are shown), the test threw (type, message,
  stack), it timed out (`TIMEOUT`), or it left a dev override set. A FAIL means a defect to fix.
* **WARN** — the test's assertions held, but something is worth a look: a test recorded a warning, or the
  scratch world logged an error line while the test passed. The captured log lines are in the report. A WARN
  does not fail the run.
* **SKIP** — a precondition the test needs is genuinely absent (for example a real catalog item outside the
  game, or a colony with no home map). A SKIP is not a pass and is counted separately; a run with only
  PASS and SKIP is a pass.

The run is **green** when it has zero FAIL. The end Message and the log summary give the four counts and
the elapsed time.

## 10. Preserved failed sandbox

When a sandboxed test fails and `preserveFailedSandbox` is on (the default for Quick and Full), its sandbox
is **kept in memory** instead of disposed, so the state that failed can be read. **Runtime tests: Inspect
preserved failure** logs it: the test and its message, then the sandbox's tick, purse and the entities the test tracked —
contracts (status, money, delivery, the typed ledger, Field Log), operations (outcome, checkpoints, spatial
plan), contractors (fame, funds, equipment, career, spatial state) — and the scheduler jobs of those entities. It is **runtime-only**: never saved, never reachable from
the save, discarded when the next run starts (keeping at most one), and gone when the game exits.

## 11. Exported reports

The report is plain text: header (suite, build, start time, options, state and counts), then every result
in order — stable ID, suite, name, outcome, elapsed — with, for anything that is not a plain pass, the
message, expected and actual, exception, entity ids, notes and captured log lines. Export writes it to
`<SaveDataFolderPath>/TheNetwork/runtime-tests-YYYYMMDD-HHMMSS.txt` (the same folder the in-game soak
export uses; created on demand). Exporting does not touch the save.

## 12. Cost

* **Idle:** with no run in progress, the whole cost is one static field read and a null check per rendered
  frame in `WorldComponentUpdate`: **no scan, no allocation, no enumeration, no job**. Measured headlessly:
  about 2.5–3.4 ns per call, **0 bytes** allocated over 5,000,000 calls (`Runner.IdleCostIsOneNullCheck`
  asserts both and prints the number). The runner, the host, every suite and every sandbox are created
  only when an action starts a run.
* **During a run:** at most the slice budget (8 ms) of real time per frame, plus two fingerprints per frame
  (a hash over the live Network's collections, linear in its size). Measured headlessly
  (`Runner.FingerprintCostIsBounded`): **about 0.1–0.16 ms per fingerprint** on a synthetic world of 365 actors, 61
  contracts and operations.
* **Headless elapsed time** of the sandbox scenarios (production services over a synthetic host, the cost
  of the logic only): Procurement 11 tests ≈ 8 ms, Career 14 ≈ 7 ms, Spatial 8 ≈ 9 ms, all 33 sandbox
  tests plus INFRA ≈ 23 ms over 7 slices. **The in-game elapsed time of each action was not measured**
  (RimWorld could not be launched here); it adds the live-state inspection, the fingerprints, and one
  `Message` and log block per run. Run *Full safe regression* once and read the elapsed time off the
  summary or the report.

Headless proof (all in `Tests/TheNetwork.Tests/RuntimeRunnerTests.cs`): `Runner.TestsExecuteInStableOrder`,
`ExceptionBecomesFailureNotCrash`, `StopOnFirstFailureWorks`, `ContinueAfterFailureWorks`,
`CancelRestoresOverrides`, `TimeoutFailsCleanly`, `StableIdsAreUnique`, `PreservedFailureIsRuntimeOnly`,
`CompletedSandboxIsDiscarded`, `LiveScanCannotMutateState`, `ReportCountsAreExact`,
`ExportFormattingStable`, `OverrideSnapshotRestoresPreviousValues`,
`NoTestControlJobEntersPersistedScheduler`, `SafeSuiteDoesNotMutateLiveNetworkState`, plus
`SlicesYieldToTheGame`, `FingerprintDetectsAMutation`, `FingerprintCostIsBounded`, `UnstartedNetworkIsNotAFalseAlarm`, `IdleCostIsOneNullCheck` and the three
`Sandbox*Suite` runs. The infrastructure was also **mutation-checked**: eight deliberate defects were introduced one at a time into
the real source and the headless suite re-run; each made at least one named test fail, and each was reverted.
Three were production defects the *sandbox scenarios* must catch (a technical Void that no longer reverses the
contractor in full → RT-CAR-010; the career applied-flag set although the commit failed → RT-CAR-002;
`PayBalance` that no longer resumes delivery → RT-PROC-007), one was a production Field Log defect (an
"arrived" beat told without spatial truth → RT-SPAT-004), and four were defects in the runner or sandbox
itself (overrides not restored after a step → `CancelRestoresOverrides`, `OverrideSnapshotRestoresPreviousValues`
and RT-INFRA-002; the live fingerprint never compared → `FingerprintDetectsAMutation`,
`SafeSuiteDoesNotMutateLiveNetworkState`, `ReportCountsAreExact`; the real-time and wait limits removed →
`TimeoutFailsCleanly`; `Dispose` doing nothing → `CompletedSandboxIsDiscarded`,
`PreservedFailureIsRuntimeOnly`).

## 13. What is not automated

By design, not built in this phase and not part of any default suite:

* **Physical delivery.** The sandbox delivery port records; the live suites only *plan* (RT-LIVE-005). No
  drop pod is launched by a test. The full physical drop-pod path remains a manual, owner-run check (§ 15).
* **Save / load, quit, restart, reload-a-backup** automation. A save-reload scenario must be run by a person.
* **Destructive or physical suites.** Anything that spends real silver, spawns real items, creates real
  world objects or edits the live Network would need its own isolated, explicit, opt-in design and its own
  ADR; none exists.
* **Visual UI.** The tabs and layouts are not exercised.
* **Third-party mod combinations.** The live scan reads whatever is loaded; it does not enumerate mods.
* **A formal S20 pass** ([spikes/S20](spikes/S20-abstract-spatial-routing.md)): still *NOT RUN — owner runtime
  validation required*.

## 14. Adding a suite in a later phase

1. Write the cases as `RuntimeTestCase.Immediate(...)` or `.Stepped(...)` with the next free stable ID in a
   new prefix (for example `RT-PHYS-*`), and a name that states the invariant, not the implementation.
2. Put them in `Suites/`, expose `Cases(IRuntimeTestHost)`, and add them to `RuntimeTestPlans.FullSafe`
   (and a named plan if it deserves its own Dev action).
3. Mutable scenarios use a `RuntimeTestSandbox` (`NeedsSandbox: true`); read the real game only through the
   host and only read-only. Anything needing a new port (for example a physical delivery) gets a *sandbox*
   port that records.
4. Multi-step behaviour is a **stepped** case: return `Wait(...)` while something is pending, and let the
   runner own the timeout. Never loop, never sleep, never block the game thread, and never use the persisted
   scheduler for bookkeeping.
5. Derive expected numbers from the generated state (a balance from the contract's own terms), never from a
   magic constant copied from one run.
6. Add the headless coverage of any new runner or sandbox behaviour to `RuntimeRunnerTests.cs`, and keep
   the safe-suite source scan green.

Phase 3's multi-step lifecycle (custody, deployment, in-person delivery, rescue follow-ups) is exactly what
the stepped cases and the preserved sandbox are for.

## 15. Owner-observed runtime evidence

Recorded here accurately, and **not** as more than it is.

> **OWNER-OBSERVED RUNTIME PASS: Legacy active procurement → post-update continuation → payment hold →
> payment recovery → full drop-pod delivery.**

The owner reported, from a real colony:

* an **existing active procurement** contract from a save made before the Phase 2.x updates: **10,000
  Plasteel**, about **200,000 silver** of contract value, a contractor team **Horizon Tide Crew** reached
  through the Fixer **Yusra Hale**;
* after the update the contract **continued**; **58,335 silver** was due while about **56,000** was
  available, so the contract went on **payment hold** (**Deep Drilling**);
* when the funds were available the **same contract resumed**;
* **10,000 / 10,000** Plasteel were delivered by **vanilla drop pods**;
* **no errors, no duplicate contract, no duplicate delivery, nothing stuck.**

Suggested ID if it is ever made repeatable by hand: **RT-PROC-LEGACY-001**.

What this is **not**: it is an observation of one run, by the owner, in the real game. It is **not** the formal
spike S20 checklist (abstract spatial continuity and charter transport), which is still **NOT RUN — owner
runtime validation required**; it did not exercise charters, the Field Log's every beat, or any other S20
step; and it is not an automated test. It is the reason RT-PROC-007 exists in the sandbox: the same
*invariant* (payment hold → funds restored → the **same** contract resumes → exact delivery), with the balance
derived from the generated terms and never the observed 58,335.

## 16. What was and was not validated in this phase

* **Validated headlessly:** the runner, the plans, the sandbox and all 33 sandbox scenarios (through the real
  runner against a synthetic live world); the fingerprint (it detects deliberate mutations of actors, silver
  and the scheduler); the override snapshot; the source scan; the idle cost; the 8 mutation checks (§ 12); and the full pre-existing suite and soaks.
* **Compile-checked only:** the `RT-SMOKE-*` and `RT-LIVE-*` suites (they need the loaded game), the
  `GameRuntimeTestHost`, `RuntimeTestGame`, and the eight Dev Mode actions.
* **RimWorld could not be launched in the environment this phase was built in.** No claim is made that any
  of the in-game paths have run in a real game. The first in-game run of *Quick smoke*, then *Live
  integration scan*, then *Full safe regression* is the owner's first action on this PR, and a green run's
  exported report is the evidence.
