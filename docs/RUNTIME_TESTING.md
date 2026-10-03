# Runtime Regression Testing (Phase 2.9)

> Developer infrastructure, not gameplay. A small in-game test runner that exercises the **real**
> production services inside the **running RimWorld process**, without ever touching the colony it
> runs in. It complements the headless suite; it does not replace it.
> Related: [DEBUGGING](DEBUGGING.md), [ARCHITECTURE](ARCHITECTURE.md), [PERFORMANCE](PERFORMANCE.md),
> [RISKS R-27](RISKS.md), [ADR-047](DECISIONS.md), [IMPLEMENTATION_PHASES § 5C](IMPLEMENTATION_PHASES.md).
>
> **Status: merged (PR #6) and owner runtime-validated.** The owner ran Quick smoke, Full safe regression and
> the Live integration scan in a fresh Dev Quicktest colony and in the real, heavily modded, ongoing colony,
> with zero runtime FAILs and the live colony unchanged by manual check ([§ 15](#15-owner-observed-runtime-evidence)).
> That is runtime evidence for *this framework*, not a claim that every mod interaction or every RimWorld state is
> proven ([§ 16](#16-what-was-and-was-not-validated)). Phase 3's physical scenarios will **not** run in the safe
> suites: they need their own separate, explicit tier with a **session-only arm** and, by default, **their own generated test
> map**; the guard never tries to infer whether a save is disposable
> ([PHYSICAL_LIFECYCLE § 21](PHYSICAL_LIFECYCLE.md#21-runtime-qa-strategy)).

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
16. [What was and was not validated](#16-what-was-and-was-not-validated)

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

The runtime runner itself is also tested headlessly (the 32 `Runner.*` tests, § 12 and [DEBUGGING](DEBUGGING.md)):
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
                           when idle)                          ├─ capture the live state (before): a fingerprint, or why there is none
                                                               ├─ run steps of the current test, each wrapped:
                                                               │     capture owner's dev overrides → apply neutral
                                                               │     → run step (exception contained)
                                                               │     → detect leak → restore the PREVIOUS values
                                                               ├─ a Wait step ends the slice at once
                                                               └─ capture the live state (after) → must be equal; a capture that THROWS is a FAIL
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
* **Live-state capture is explicit.** `IRuntimeTestHost.CaptureFingerprint()` returns a `FingerprintCapture`,
  never "a fingerprint or null": `Available` (the Network is running and was fingerprinted),
  `NetworkUnavailable` (no live Network exists, e.g. a headless host), `NetworkNotStarted` (it exists but the game
  has not started it) or `Failed` (the capture threw). Only the first three are states; the fourth is a defect in
  the safety sentinel itself, and RT-INFRA-001 FAILS (it fails closed, § 6). The host also exposes
  `ProbeNetwork()`, a read-only description of the start-up state, so tests can *ask* whether the Network is
  running and never start it.
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

* the real `NetworkWorldComponent` and `NetworkRuntime` exist; **if the game has started the Network**: the
  save version is understood, the stores and indexes are present, no subsystem failed at start-up. If the game
  has not started it yet (a save just loaded, nothing has ticked), those tests report `SKIP` with the advice to
  allow one normal game tick and rerun: a runtime test **never starts the Network on the game's behalf** (§ 6);
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
2. **A fingerprint checks it** — a `LiveFingerprint` of the Network's **durable truth** plus selected
   safety-critical colony/world state (scope below) is captured before and after **every slice** (one
   synchronous call, so the game cannot legitimately move in between). **RT-INFRA-001** (live Network state
   unchanged) compares them. A test that touches live truth fails the run and names what moved.
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

**A runtime test never starts, reconciles or repairs the live Network.** The game starts the Network on its
first tick, the Network tab or a command; that start-up (load reconciliation, deferred migration, contractor
instantiation, spatial initialisation, validation, scheduler repair, event publication) is legitimate when the
*game* does it and is not something a "safe" action may do. So `RT-SMOKE-002` only **asks** (`ProbeNetwork()`):

| The live Network is | RT-SMOKE-002 | Other tests that need a settled Network (003, 004, 007, 008; RT-LIVE-006) |
|---|---|---|
| running | PASS | run |
| exists, not started yet | **SKIP** — "The Network has not started in this game session yet. Unpause for one game tick or use The Network normally, then rerun the runtime tests." | SKIP with the same advice |
| start-up failed | **FAIL**, with the failed stage and message | 008 FAILS too; the others SKIP |
| absent | FAIL (no runtime was built) | — |

The tests that do not depend on the Network's start-up (the catalog, the procurement kind, the comms gate, the
payment environment, the world graph, the drop plan, and every sandbox scenario) still run, so *Full safe
regression* on a never-ticked game runs what it validly can and reports the rest as SKIP; RT-INFRA-001 then
SKIPs honestly ("nothing about the live Network was verified"), and the end-of-run Message says so. There is no
start-up exemption in the runner: if a test *did* start the Network, availability would change inside a slice
and RT-INFRA-001 would FAIL (`Runner.StartupFromATestIsNotHidden`). If the game itself starts the Network
*between* two frames of a run, earlier slices count as "not started" and later slices are compared normally.
A source scan forbids `.EnsureStarted(`, `.StartNow(`, `RunStartup(`, `.Active` and `NetValidator.` in runtime-test
code (comments excepted; production code outside `RuntimeTests` is not scanned).

**The safety sentinel fails closed.** RT-INFRA-001 distinguishes three situations and never confuses them:

1. **No live Network exists** (a headless host): `NetworkUnavailable`; RT-INFRA-001 may SKIP.
2. **The Network exists but the game has not started it**: `NetworkNotStarted`; RT-INFRA-001 SKIPs, says the
   Network was not started and that nothing about it was verified. The run does not start it.
3. **The Network is running but the capture throws** (a bug in the fingerprint, an unreadable store, a RimWorld
   API surprise in the colony sentinel): `Failed`. **RT-INFRA-001 FAILS**, reporting that the capture failed,
   *before* or *after*, the slice number, the exception type and message (and the stack trace in the detailed
   report). The run can never be all green with a broken sentinel. A host that returns no capture at all is the
   same failure. (`Runner.FingerprintExceptionFailsClosed`.)

### What RT-INFRA-001 fingerprints, and what it does not

It fingerprints **the Network's durable truth plus selected safety-critical colony/world state (including
payment silver and world objects) before and after each test slice.** It is a tripwire for the effects a
runtime test could cause; it does **not** prove that every possible piece of RimWorld state is untouched.

*Network side (`LiveFingerprint.Of`, read-only, every store by CONTENT, never by count alone).* The walker reads
every persisted field (public or private; nested value types such as typed ids and tiles by content; lists
element by element in order; derived objects by content) of: every actor and its components (public
reputation, contractor funds, equipment, doctrine, morale, commitments, career record, spatial truth,
fixer and organisation profiles), characters, cast entries, intel requests and leads, opportunities, every
contract (parties, request, terms, objectives, acquisition and delivery state, ledger records with their typed
`fromOwnFunding` / `fullReversal` fields, Field Log entries, lineage, terminal truth) and offer, every
operation (checkpoints, frozen inputs, committed outcome, career flags, spatial plan, troubled and incident
truth), every relation edge, knowledge book and entry, history record (participants, magnitudes, notes), actor
summary, journal event (every payload field) and pending consequence, plus the scheduler's jobs (sequence, due
tick, kind, target, argument) and the three id counters. Because it walks fields, **a newly persisted field is
covered automatically**. Each list element also has its own hash, so a difference names the entity that moved
(`contracts.contracts[3]#17`), not just a store. Mutating relation familiarity, an existing Field Log entry, a
career field, a contract term, an operation checkpoint, a knowledge entry, a history record or a journal event
**without changing any count** is detected (`Runner.FingerprintDetects*Mutation`).

*Not hashed, on purpose:* dictionaries, sets and queues (the rebuildable indexes), delegates, engine objects,
`[NonSerialized]` fields, and runtime-only caches named `cached*` (for example
`ContractorSimulation.cachedStrength`, which a read-only call may legitimately fill) and the derived
`HistoryRecord.narrativeSeed`. Read-only access to the live world (the invariant scan, index lookups, history
and career reads, filling the strength cache, rebuilding the history index) leaves the fingerprint unchanged
(`Runner.FingerprintIgnoresReadOnlyAccess`).

*Colony/world side (`ColonySentinel`, in the game host only; bounded; never scans pawns).* Per player home map:
the beacon-reachable silver (the query the payment adapter charges against), the spawned-thing count, and the
haulable items' count, stack total and a hash of (id, def, stack) of each (above 100,000 items only counts and
the stack total are taken). World-wide: the home-map count and ids, every world object (id, def, tile, faction),
and the letter-stack and archive counts. A test that spent silver, spawned or removed cargo, destroyed a world
object or sent a letter has a realistic chance of being caught. **Not covered:** pawn state, terrain, buildings,
research, storyteller, relations with factions, anything not listed. The headless suite substitutes a fake host
fingerprint (payment silver, charges, deliveries) for the colony side; the real adapter was written without a
game and has since run in the owner's game in both QA environments ([§ 15](#15-owner-observed-runtime-evidence)):
no FAIL, no false alarm, and the colony's contractor count and silver were unchanged by manual check.

## 7. Stable IDs and suites

IDs are permanent: a test is never renumbered or reused, so a report from last month still means the same
thing. A new behaviour gets a new ID. A plan with a duplicate or empty ID is refused when it is built.

**Smoke — `RT-SMOKE-*` (game only, read-only)**

| ID | Checks |
|---|---|
| RT-SMOKE-001 | `NetworkWorldComponent` exists |
| RT-SMOKE-002 | `NetworkRuntime` exists; the game has started it. **Inspects only**: PASS if running, SKIP if the game has not started it yet, FAIL if its start-up failed (§ 6) |
| RT-SMOKE-003 | the save version is understood (SKIP until the game has started the Network) |
| RT-SMOKE-004 | stores and indexes are present and addressable (SKIP until started) |
| RT-SMOKE-005 | the item catalog builds from the loaded Defs |
| RT-SMOKE-006 | the procurement contract kind is registered |
| RT-SMOKE-007 | actor indexes resolve the known actors consistently (SKIP until started) |
| RT-SMOKE-008 | no subsystem failed at start-up (FAIL if it did; SKIP until started) |

**Live integration scan — `RT-LIVE-*` (game only, read-only)**

| ID | Checks |
|---|---|
| RT-LIVE-001 | the real catalog holds base-game goods |
| RT-LIVE-002 | the comms gate reads the colony truthfully |
| RT-LIVE-003 | the payment environment can be inspected without spending |
| RT-LIVE-004 | the world-graph adapter reads the actual world |
| RT-LIVE-005 | the drop-pod plan is deterministic and spawns nothing |
| RT-LIVE-006 | the live Network passes the read-only invariant scan (SKIP until the game has started it) |

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

**Physical lifecycle, abstract half — `RT-PHYS-*` (sandbox, Phase 3.0, over the fake `PhysicalWorldPort`)**

Every case runs the production `PhysicalLifecycleService`, planner, Applier and stages in the sandbox; the fake port only hands out
tokens and answers scripted observations. No case can reach a pawn, thing, map, faction, WorldPawns, silver, letter or the live
Network. The cases [PHYSICAL_LIFECYCLE § 21.1](PHYSICAL_LIFECYCLE.md#211-tier-s-the-abstract-half-safe-headless-and-in-game-over-a-fake-port)
marks headless-only (007, 011, 015, 027, 028) are in the headless suite (`Phys.*`); 020–024 and 030 belong to 3.1/3.2.

| ID | Checks |
|---|---|
| RT-PHYS-001 | one person materializes exactly once (custody `Deployed`, one membership, one binding) |
| RT-PHYS-002 | the same person cannot materialize twice, including while a closed episode's RELEASE is pending (P3-INV-001) |
| RT-PHYS-003 | a normal exit reconciles exactly once under duplicate wake-ups (signal, watch, load, dev) |
| RT-PHYS-004 | rematerialization reuses the same binding (token), never a new pawn (P3-INV-006) |
| RT-PHYS-005 | a physical wound becomes the abstract recovery truth (`woundedUntilTick` or a wounded bucket) once |
| RT-PHYS-006 | physical death is final: no abstract availability or resurrection, even with abstract jobs running (P3-INV-004) |
| RT-PHYS-008 | held, unknown or absent is never `Returned` (P3-INV-005, -010) |
| RT-PHYS-009 | a group member's death changes that member only; tier conservation holds (P3-INV-007) |
| RT-PHYS-010 | map removal cannot silently erase a person |
| RT-PHYS-012 | an unsupported custody quarantines; the pawn is untouched, the person blocked |
| RT-PHYS-013 | a reconcile that throws restores the exact durable state; the retry applies once |
| RT-PHYS-014 | publication resumes at its cursor; nothing accepted is published twice; a throwing consumer is not redispatched (P3-INV-025) |
| RT-PHYS-016 | a person is never owned by an episode and an operation at once (ADR-039 extended) |
| RT-PHYS-017 | the spatial anchor is frozen while physical and written once at close (P3-INV-009) |
| RT-PHYS-018 | prepare-for-removal settles every open episode; no tag left; nothing deleted |
| RT-PHYS-019 | the validator reports episode contradictions and repairs none |
| RT-PHYS-025 | truthful aging asks for the full, uncapped interval (1, 10, 70 years; rare = frequent) (P3-INV-022) |
| RT-PHYS-026 | commit fault sweep: a throw after every step restores the fingerprint; coverage proof; applied once (P3-INV-023) |
| RT-PHYS-029 | release interruption keeps the gate closed until COMPLETE; zero `PassToWorld` for `WorldFree`; an invalid request is rejected (P3-INV-029, -031) |

**Infrastructure — `RT-INFRA-*` (appended to every run)**

| ID | Checks |
|---|---|
| RT-INFRA-000 | the runner itself did not throw outside a test (only present when it did) |
| RT-INFRA-001 | the live Network's durable-truth fingerprint plus the selected colony/world sentinel was identical before and after every slice. **PASS** when compared and identical; **FAIL** when anything moved, when a capture threw (fail closed), or when the Network's availability changed inside a slice; **SKIP** only when there is no live Network, or the game has not started it (stated plainly: nothing was verified) |
| RT-INFRA-002 | dev overrides and service toggles were restored exactly after every step |
| RT-INFRA-003 | no runtime-test job kind is in the live persisted scheduler |
| RT-INFRA-004 | every sandbox was discarded or deliberately preserved |

**Plans.** *Quick smoke* = RT-SMOKE-001..008. *Live integration scan* = RT-LIVE-001..006.
*Full safe regression* = smoke + live + the 52 sandbox scenarios (66 tests) + the INFRA checks.

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

Run them in a game that has ticked at least once (or in which you have opened the Network tab). In a game that
has just been loaded paused, the Network has not started: the tests that need it report `SKIP` with the advice to
unpause for one tick and rerun, `RT-INFRA-001` SKIPs and says nothing about the live Network was verified, and
the end-of-run Message says so. A runtime test never starts the Network for you (§ 6).

## 9. PASS / FAIL / WARN / SKIP

* **PASS** — every assertion held.
* **FAIL** — an assertion did not hold (expected and actual are shown), the test threw (type, message,
  stack), it timed out (`TIMEOUT`), or it left a dev override set. A FAIL means a defect to fix.
* **WARN** — the test's assertions held, but something is worth a look: the test recorded a warning, or
  **production code logged a warning or an error while the test ran**. Production `NetLog` output is
  redirected away from the real log for the duration of a step (so a scratch world cannot spam it or use up
  the real once-per-session warnings), and that used to hide the lines; now any such warning or error turns an
  otherwise passing result into a WARN, the lines are kept in the report, and the WARN is written to the real
  log when it happens. A scenario that deliberately provokes a production warning (RT-CAR-002's commit fault)
  declares it with `ctx.ExpectLog("...")`; only a matching line is exempt, anything else still warns. Warnings
  never fail a run, and the original log sink and the once-keys are restored exactly after every step.
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
* **During a run:** at most the slice budget (8 ms) of real time per frame for the tests, plus two live-state
  captures per frame (a Network fingerprint and, in the game, the colony sentinel). The Network fingerprint
  walks every durable field, so it is more expensive than the count-and-hash version it replaced, and linear in
  the Network's size. Measured headlessly (`Runner.FingerprintCostIsBounded`), on a synthetic world of 365
  actors, 61 contracts and operations and 67 history records (about 25,000 objects and lists, 2,400 named
  entities): **about 5 ms per capture**. The first capture of a process also compiles one accessor pair per
  data type (about 70 types): **about 23 ms once**. The reflective fallback used if expression trees are
  unavailable hashes the same world identically in **about 44 ms**. So a run adds roughly 10 ms per frame to the
  frames it runs in (two captures), for the second or two a run lasts, and nothing at all otherwise. The
  colony sentinel's cost (silver by beacon, haulable-item hash, world objects) is bounded (no pawn scan; item
  hash capped at 100,000 items per map) and is **not measured separately**; it is included in the owner's
  in-game whole-run timings below.
* **Headless elapsed time** of the sandbox scenarios (production services over a synthetic host, the cost
  of the logic only): Procurement 11 tests ≈ 8 ms, Career 14 ≈ 7 ms, Spatial 8 ≈ 9 ms, all 33 sandbox
  tests plus INFRA ≈ 23 ms over 7 slices. Phase 3.0 adds Physical 19 ≈ 30 ms (the 70-year aging and the fault sweep dominate);
  all 52 sandbox tests plus INFRA ≈ 60 ms over 9 slices.
* **In-game elapsed time, as the runner's own summary reported it to the owner** (the run total, including the
  live-state inspection and both fingerprints per slice): Quick smoke ≈ 84 ms (fresh Quicktest colony) and
  ≈ 133 ms (the real modded colony); Full safe regression ≈ 160 ms and ≈ 222 / 96 ms (first / second run in
  the modded colony); Live integration scan ≈ 3 ms and ≈ 9 ms. These are whole-run times from single
  owner runs, not a controlled benchmark, and they say nothing about the per-frame cost of the sentinel
  alone. They do show that a run in a real, heavily modded colony costs a fraction of a second once. The
  *warm* second run was faster than the first, which is the expected warm-cache effect (the one-time accessor
  compile and the JIT).

Headless proof (all in `Tests/TheNetwork.Tests/RuntimeRunnerTests.cs`): `Runner.TestsExecuteInStableOrder`,
`ExceptionBecomesFailureNotCrash`, `StopOnFirstFailureWorks`, `ContinueAfterFailureWorks`,
`CancelRestoresOverrides`, `TimeoutFailsCleanly`, `StableIdsAreUnique`, `PreservedFailureIsRuntimeOnly`,
`CompletedSandboxIsDiscarded`, `LiveScanCannotMutateState`, `ReportCountsAreExact`,
`ExportFormattingStable`, `OverrideSnapshotRestoresPreviousValues`,
`NoTestControlJobEntersPersistedScheduler`, `SafeSuiteDoesNotMutateLiveNetworkState`, plus
`SlicesYieldToTheGame`, `FingerprintDetectsAMutation`, `FingerprintCostIsBounded`, `IdleCostIsOneNullCheck` and the three
`Sandbox*Suite` runs. The post-review safety correction adds `UnstartedNetworkIsSkippedNotStarted`,
`StartupFromATestIsNotHidden`, `FingerprintExceptionFailsClosed`, `NoLiveNetworkFingerprintMaySkip`,
`FingerprintDetectsRelationMutation`, `FingerprintDetectsContractMutation`, `FingerprintDetectsOperationMutation`,
`FingerprintDetectsHistoryOrJournalMutation`, `FingerprintIgnoresReadOnlyAccess` and `CapturedWarningProducesWarn`.

**Mutation checks.** A deliberate defect is introduced into the real source, the headless suite is re-run, a
named test must fail, and the defect is reverted. First pass (eight): a technical Void that no longer reverses
the contractor in full → RT-CAR-010; the career applied-flag set although the commit failed → RT-CAR-002;
`PayBalance` not resuming delivery → RT-PROC-007; an "arrived" Field Log beat told without spatial truth →
RT-SPAT-004; overrides not restored after a step; the live fingerprint never compared; the real-time and wait limits
removed; sandbox `Dispose` doing nothing. Safety-correction pass (the runner and fingerprint themselves):

| Mutation | Caught by |
|---|---|
| `EnsureStarted()` reintroduced into a runtime test | the source scan in `Tests/run-tests.sh` (the run fails before any test) |
| a fingerprint capture exception swallowed into "unavailable" (the original fail-open) | `FingerprintExceptionFailsClosed` |
| a capture failure not recorded | `FingerprintExceptionFailsClosed` |
| the availability-change check removed (a start-up blind spot) | `StartupFromATestIsNotHidden` |
| relation store not fingerprinted | `FingerprintDetectsRelationMutation` |
| contract store not fingerprinted | `FingerprintDetectsContractMutation` |
| operation store not fingerprinted | `FingerprintDetectsOperationMutation` |
| history ledger not fingerprinted | `FingerprintDetectsHistoryOrJournalMutation` |
| event journal not fingerprinted | `FingerprintDetectsHistoryOrJournalMutation` |
| live silver ignored by the fingerprint | `FingerprintDetectsAMutation`, `FingerprintDetectsHistoryOrJournalMutation` |
| dev overrides not restored after a step | `CancelRestoresOverrides`, `OverrideSnapshotRestoresPreviousValues` |
| real-time timeout removed / wait limit removed | `TimeoutFailsCleanly` |
| sandbox `Dispose` doing nothing | `CompletedSandboxIsDiscarded`, `PreservedFailureIsRuntimeOnly` |
| captured warnings no longer surfacing | `CapturedWarningProducesWarn` |
| runtime-only cache fields no longer excluded | `FingerprintIgnoresReadOnlyAccess` |

## 13. What is not automated

By design, not built in this phase and not part of any default suite:

* **Physical delivery.** The sandbox delivery port records; the live suites only *plan* (RT-LIVE-005). No
  drop pod is launched by a test. The full physical drop-pod path remains a manual, owner-run check (§ 15).
* **Save / load, quit, restart, reload-a-backup** automation. A save-reload scenario must be run by a person.
* **Destructive or physical suites.** Anything that spends real silver, spawns real items or pawns, creates
  real world objects or edits the live Network needs its own isolated, explicit, opt-in tier. None exists yet;
  Phase 3's is designed (a separate Dev menu, a typed **session-only arm**, a dedicated generated test map by default, a stronger
  second gate for any home-colony scenario, no inference of "disposable", never part of Quick or Full safe) in
  [PHYSICAL_LIFECYCLE § 21](PHYSICAL_LIFECYCLE.md#21-runtime-qa-strategy) and [ADR-049](DECISIONS.md).
* **Cancelling a run that is in progress.** The owner pressed *Cancel current run* after a run had finished
  (the game showed the normal "no runtime test is running" Message) so the in-progress path has no manual
  evidence; cancel, cleanup and override restoration are covered headlessly (`Runner.CancelRestoresOverrides`).
* **Starting, reconciling or repairing the live Network.** The game does that on its first tick; a runtime test
  only asks (§ 6) and reports SKIP when the game has not. Likewise `NetValidator` (it repairs) is never called.
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

**Phase 3.0 status:** the safe-tier `RT-PHYS-001…019` and `025…029` are **implemented** (the in-game-safe ones in the sandbox suite
`PhysicalRuntimeSuite`, part of *Full safe regression*; the headless-only ones in `Tests/TheNetwork.Tests/PhysicalLifecycleTests.cs`).
They have run headlessly; **they have not yet been run inside RimWorld**. No `RT-PHYX-*` case exists.

Phase 3's multi-step lifecycle is what the stepped cases and the preserved sandbox are for, in two tiers: the
**abstract half** (authority transitions, episode bookkeeping, reconciliation, exactly-once, custody rules over
fake physical ports) runs in the existing safe suites against the sandbox; the **physical half** (real pawns on
a real map) cannot, and runs only in the separate physical tier
([PHYSICAL_LIFECYCLE § 21](PHYSICAL_LIFECYCLE.md#21-runtime-qa-strategy)). The safe suites stay safe on a real colony.
The amendment pass adds to the safe half: a **fault-injection sweep** over the reconciliation commit (a throw after every
step must leave the deep fingerprint unchanged), a parity test with the abstract casualty path, fame-invariance of
projection, the truthful-aging contract and the concretization policy (`RT-PHYS-020…028`, 28 cases at that point).
**The correction pass** adds two safe-tier cases (30 in all): `RT-PHYS-029` (a **release interruption**: a throw after each
release action, an explicit completion marker, and the authority gate staying closed until release completes) and
`RT-PHYS-030` (**time-independent identity**: a role or composition never changes because the player first observed it
years later); it also rewrites `RT-PHYS-014` (**publication interruption**: per-event durable progress, no event submitted
to the bus twice, a throwing consumer never redispatched), `RT-PHYS-020` (role correction raises only a role-defining
skill's base level and never touches passion) and `RT-PHYS-023` (a company is not promoted by presence). In the physical
tier it adds role-constrained creation, truthful aging and concretization on real pawns (`RT-PHYX-011…014`). Nothing is
implemented and no physical case has been run.
**The micro-correction** extends `RT-PHYS-029` (the fake port records every `PassToWorld`: a member observed `WorldFree` produces
none, and a call that violates the three-part precondition is rejected) and adds two physical-tier regressions that are **gated by
the mandatory spike S31** and **not run**: `RT-PHYX-015` (a retained named pawn leaves by a normal vanilla `ExitMap`: no Network
`PassToWorld`, no "already here" error, no window in which the pawn is reusable or redressable, RELEASE once, abstract authority
only after RELEASE, the same `Pawn` after a save/load and a rematerialization) and `RT-PHYX-016` (the map-removal variant, which
has no `LeftMap`).

## 15. Owner-observed runtime evidence

Recorded here accurately, and **not** as more than it is. Three observations: the Phase 2.9 runtime validation in
two environments (§ 15.1 and § 15.2, what they establish in § 15.3), and the earlier legacy procurement run
that predates Phase 2.9 and is why RT-PROC-007 exists (§ 15.4).

### 15.1 Phase 2.9 owner runtime validation, environment 1: fresh Dev Quicktest colony

> **PHASE 2.9 OWNER RUNTIME VALIDATION: PASS (environment 1 of 2).**

The owner created a fresh Dev Quicktest colony in the real game: a simple base, stockpiles, silver, powered
infrastructure, a Comms Console and an orbital trade beacon over the payment stockpile. They ran Quick smoke,
Full safe regression and Live integration scan in turn.

| Run | PASS | FAIL | WARN | SKIP | Elapsed |
|---|---:|---:|---:|---:|---:|
| Quick smoke | 12 | 0 | 0 | 0 | ≈ 84 ms |
| Full safe regression | 49 | 0 | 2 | 0 | ≈ 160 ms |
| Live integration scan | 10 | 0 | 0 | 0 | ≈ 3 ms |

The two WARNs were the runner's slow-step profiler telemetry, not correctness failures: **RT-PROC-001** (slow
Network work, ≈ 6.65 ms) and **RT-SPAT-008** (≈ 6.97 ms). They also showed, in a real game, that the corrected
production-warning capture path surfaces a warning as WARN without failing the run. No Network error or
exception was observed.

Exercised in an actual game process for the first time: the Dev actions appear and execute; `WorldComponentUpdate`
pumps the runner; the real `NetworkWorldComponent` / runtime inspection; real Def and catalog access; real
Comms Console detection; real beacon and payment inspection; real world-graph inspection; real drop-pod
*planning* (no physical delivery); the live invariant scan; the isolated Procurement, Career and Spatial
sandboxes; and the RT-INFRA live-state sentinel.

### 15.2 Phase 2.9 owner runtime validation, environment 2: the real, heavily modded, ongoing colony

> **PHASE 2.9 OWNER RUNTIME VALIDATION: PASS (environment 2 of 2).**

The owner then ran the merged build in their real ongoing colony with the full mod list. Manual before-state:
**130 contractors, 4,114 silver.** They re-checked both after each runtime run, in this order: Quick smoke →
Full safe regression → Live integration scan → Full safe regression again → *Cancel current run*. Both numbers
were **unchanged after every step.**

| Run | PASS | FAIL | WARN | SKIP | Elapsed |
|---|---:|---:|---:|---:|---:|
| Quick smoke | 12 | 0 | 0 | 0 | ≈ 133 ms |
| Full safe regression #1 | 50 | 0 | 1 | 0 | ≈ 222 ms |
| Live integration scan | 10 | 0 | 0 | 0 | ≈ 9 ms |
| Full safe regression #2 | 51 | 0 | 0 | 0 | ≈ 96 ms |

The single WARN (**RT-SPAT-008**, slow `job:consequence.followup`, ≈ 5.01 ms) was the slow-step telemetry; the
scenario passed. It did not recur on the second run, consistent with warm caches and the profiler threshold,
and was not treated as a correctness issue. *Cancel current run* was pressed after the run had already
finished, so the game showed its normal Message (not a Letter) that no runtime test was running, and live state
stayed unchanged. Nothing observed by hand: missing or extra silver, test cargo, test contracts, changed
contractor careers or reputation, fake history, fake gameplay Letters, moved contractors, leftover test world
objects. No Network error or exception was observed from the runtime suites.

### 15.3 What 15.1 and 15.2 do and do not establish

Established, with the owner's two runs as evidence: the framework runs in a real game without a FAIL; it is safe
to press in a real colony *as far as the owner's manual checks and the fingerprint sentinel can see*; the game
host, `ColonySentinel`, the `RT-SMOKE-*` / `RT-LIVE-*` suites and the Dev actions work.

**Not** established, and not claimed: that every possible mod interaction is proven; that every RimWorld state is
fingerprinted (the sentinel's scope is in § 6: pawns, terrain, buildings and more are not covered); that the
in-progress *Cancel* path was exercised by hand (it was not; it has headless coverage only); or a formal Phase
2.5 **S20** result (§ 15.4). The unmeasured per-frame cost of `ColonySentinel` is bounded by the whole-run times
above, not measured separately.

### 15.4 Owner-observed procurement run (Phase 2 / 2.75)

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

## 16. What was and was not validated

* **Validated headlessly (when the phase was built):** the runner, the plans, the sandbox and all 33 sandbox
  scenarios (through the real runner against a synthetic live world); that a run against a never-started live
  Network starts nothing and SKIPs (`UnstartedNetworkIsSkippedNotStarted`); that a fingerprint capture that
  throws on a running Network FAILS RT-INFRA-001 (`FingerprintExceptionFailsClosed`); that the fingerprint
  detects a mutation of an existing relation, contract, operation, history record, journal event, knowledge
  entry, career field, silver or scheduler job without any count changing; that read-only access leaves it
  unchanged; that the compiled and reflective walkers hash a world identically; that captured warnings surface
  as WARN with the sink and once-keys restored; the override snapshot; the source scan; the idle and fingerprint
  cost; the mutation checks (§ 12); and the full pre-existing suite and soaks. Baseline of the merged build:
  **315 tests, 20,331 checks, 0 failures.** Phase 3.0 adds the 19 `RT-PHYS` sandbox cases and 23 headless `Phys.*` tests
  (18, plus five regressions from the PR #8 review: a refused `PassToWorld` precondition blocks RELEASE, `AlreadyInWorldPawns`
  is an observed no-op, duplicate anonymous tier rows are aggregated, a returned `Missing`/`Captured` person is resolved, `Dead`
  and `Lost` stay immutable): **339 tests, 22,299 checks, 0 failures**, all headless (not yet run in the game).
* **Validated by the owner in the running game (§ 15.1, § 15.2):** the `GameRuntimeTestHost`, `ColonySentinel`,
  `RuntimeTestGame`, the `RT-SMOKE-*` and `RT-LIVE-*` suites and the Dev Mode actions all executed. Quick smoke,
  Full safe regression and Live integration scan produced **0 runtime FAILs in both environments** (a fresh Dev
  Quicktest colony and the real, heavily modded, ongoing colony), the slow-step WARNs were profiler telemetry,
  and the live colony (130 contractors, 4,114 silver in the second environment) was unchanged by manual check.
* **Still not covered, and not claimed:**
  * every possible mod interaction (the live scan reads whatever is loaded; the second environment is one mod
    list);
  * every RimWorld state: the sentinel is a tripwire for the state listed in § 6, and pawns, terrain, buildings
    and more are outside it;
  * the *in-progress* Cancel path by hand (the owner's click arrived after the run had finished); it is covered
    headlessly;
  * a formal Phase 2.5 **S20** pass ([spikes/S20](spikes/S20-abstract-spatial-routing.md)) is still *NOT RUN —
    owner runtime validation required*; the Phase 2.9 runtime pass is a different thing and does not
    complete it. The runtime tests' RT-SPAT scenarios run in the sandbox over a synthetic world graph and so do
    not replace S20's real-world-map checks either;
  * save/load, quit/restart and physical delivery automation ([§ 13](#13-what-is-not-automated)).
