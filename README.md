# The Network

**RimWorld 1.6 · standalone · status: Phase 2 merged and owner-tested; Phase 2.5 merged (hidden spatial continuity and the Field Log; the formal in-game S20 checklist has not been run); Phase 2.75 merged (the contractor career foundation); Phase 2.9 merged and owner-runtime-tested (in-game runtime regression test infrastructure, developer-only); Phase 3 (abstract ↔ physical lifecycle) is next: design reviewed and amended, no Phase 3 code yet**

The Network is a persistent, procedural contractor ecosystem that runs behind the normal RimWorld
game. The player hires outsiders to find and fetch things they cannot easily get themselves. The
contractors, brokers and factions they deal with remember what happened, and that memory changes
how they behave afterwards.

```
Player intent → world reaction → opportunity → success / failure → consequences
             → remembered history → changed future behaviour → new stories
```

The first thing a player will do with the mod is look for a scarce item such as Tenebrite. That is
only the way in. What the mod is really about is **persistent procedural relationships and world
history**. The goal is for a player to say *"Remember Dead Red?"* and then tell a story that no
developer wrote.

---

## Repository status

**Phase 1 (Foundation + Intel + Fixers)** is implemented on top of the frozen Phase 0 architecture:
the global cast in Mod Settings, the world cast snapshot, Fixers, the Comms Console gate, the item
catalog, Intel searches in rounds with zero to many leads (never a quantity), source resolution,
vanilla sites with the injected comp, partial recovery without extermination, history, the
Network tab (Intel, History), letters, dev actions and prepare-for-removal. No Harmony.

**Phase 2 (abstract contractors + procurement)** adds the invisible contractor economy:
- **Contractors.** About 100 persistent contractors come from the world's cast snapshot: Solos and
  organizations with doctrine, morale, equipment, funds, careers, rosters, wounded recovery,
  recruitment and minimal succession. Known Characters are records only; there are no pawns.
- **Procurement.** Ask for an **exact item and quantity** through a Fixer, Open or Direct. Real
  contractors decide whether they want the job and give their reasons when they refuse. One to
  three quotes come in, assembled by the Fixer from each contractor's own bid. The deposit is
  charged at award and is normally lost on failure. Insurance is optional and partial.
- **The work.** Abstract operations resolve once, from frozen inputs, into outcome bands with
  casualties, captures, delays and partial cargo. The client decides on partial results and
  renegotiations.
- **Consequences.** A loss can leave a last known location (a real vanilla site). Successful goods
  arrive by drop pod.
- **Memory.** Relationships and knowledge make history change how contractors act and what they
  charge.
- **UI.** The tabs are Intel, Procurement, Contracts, Contractors and History.

**Phase 2.5 (abstract spatial continuity + player-contract Field Log)** gives every contractor a
hidden, approximate place in the world ([docs/SPATIAL.md](docs/SPATIAL.md)):
- **Hidden geography.** Each contractor has one anchor tile, travels coarsely and lazily (no per-tick
  work, no icons, no caravans, no pawns) and occasionally relocates when idle. The player never sees
  where anyone is.
- **Operations with a place.** A job starts from where the contractor really is and happens in a
  hidden work region that fits the quoted timeline, measured in real route steps; the resolver still
  decides what happens. A Last Known Location appears near where the contractor actually was.
- **Charter across water.** A job on an island (or across an impractically long detour) may be
  reached by an abstract, reusable two-way charter from a high-tech settlement: no shuttle object, no
  extra silver, one line in the Field Log.
- **Field Log.** While a contractor works a player's contract, the contract card shows short reports
  ("has set out", "running behind schedule", "499 of 500 secured"). It ends with the contract.

**Phase 2.75 (contractor career foundation)** lets work change a contractor
([docs/CAREERS.md](docs/CAREERS.md)). It extends what already exists; it adds no second reputation,
wealth or stat system:
- **Reputation.** A numeric score sits beneath the fame band (Unknown, Local, Established, Famous,
  Legendary), which is now derived from it. A finished job earns reputation by how dangerous it really
  was and how it ended, once; easy work stops counting above a ceiling, so no number of safe hauls makes
  a legend. Fame is public standing and stays independent of experience.
- **Money.** A contractor's pay lands in its funds exactly once; a refund takes back the proportional
  part; the Fixer's fee, insurance payouts and a replacement's carried-over deposit never touch it.
- **Advancement.** Equipment advances one tier at a time from the daily upkeep when reputation, funds
  (plus an operating reserve) and health allow, never during a job.
- **Need and Tags.** A derived `CareerNeed` and descriptive Tags (`WellEquipped`, `Wealthy`,
  `EliteCombat`, `BattleTested`, `LongRange`, `RapidTransport`, `HeavyLift`, `SpacerCapable`,
  `LegendaryReputation`) are computed from existing state: descriptors, never bonuses.
- **Old saves.** Format 4 keeps every visible fact (each reputation score starts at its band's floor)
  and invents no past: a running operation never earns career credit.

**Phase 2.9 (runtime test infrastructure; merged, owner-runtime-tested)** is developer tooling, not gameplay
([docs/RUNTIME_TESTING.md](docs/RUNTIME_TESTING.md)). With Dev Mode on, **Dev Mode → The Network → Runtime
tests: Quick smoke / Full safe regression / Live integration scan** (plus Status, Cancel, Last report, Export
and Inspect preserved failure) runs stable-ID regression tests inside the running game. The mutable scenarios
(procurement, careers, spatial) run in an **isolated in-memory sandbox** built from the real production services;
the live game, catalog, comms gate, payment environment and world graph are only **read**. A safe run does not
spend silver, spawn cargo, create contracts or history, send letters or move contractors, and it never starts or
repairs the live Network (a never-ticked game gets SKIP). It checks that: a fingerprint of the Network's durable
data plus selected colony/world state (payment silver, cargo, world objects, letters) is compared before and after
every slice (RT-INFRA-001), and if that check itself fails the run FAILS. It is a tripwire for those effects, not a
proof that all of RimWorld is untouched. It costs one null check per frame when idle,
stores nothing in the save, and uses no Harmony. It complements the headless suite and does not replace it.

**Phase 3 (abstract ↔ physical lifecycle) is next; its design has been reviewed and amended, and no Phase 3 code exists**
([docs/PHYSICAL_LIFECYCLE.md](docs/PHYSICAL_LIFECYCLE.md), [ADR-048 to ADR-051](docs/DECISIONS.md)). The design, audited against
the 1.6 game assemblies: **one authority per person at a time** (abstract, physical, or held by vanilla); `Actor ≠ Person ≠
Pawn`; a named contractor keeps **one pawn for life** and ages truthfully while stored; a materialized contractor must never
contradict what the Network already established (**Operational Roles** and **role composition** constrain only what is necessary;
RimWorld's randomness fills the rest), small recurring crews keep their recognisable members while large companies stay
ephemeral; a physical **Episode** records who is out there and is **reconciled exactly once**, atomically, from observed state;
death is final; custody beyond the map is never mistaken for "home"; **no Harmony** for the recommended slice. Four subphases:
3.0 the abstract foundation (no pawn), 3.1 one controlled physical episode (first real pawn, in a separate, session-armed test
tier on its own test map), 3.2 custody, rescue and groups (first player-visible content), 3.3 procurement fulfillment / physical
handoff (**design direction only**). Full Safe Regression stays safe on a real colony. Open questions and the spikes that settle
them are listed, not hidden; none has been run.

The contract board, the player as contractor and the social layer are later phases
([docs/IMPLEMENTATION_PHASES.md](docs/IMPLEMENTATION_PHASES.md)).

**Test status.** The headless tests pass (315 tests, 20,331 checks, 0 failures, including an 18-in-game-year
procurement soak, three 20-in-game-year career soaks with daily money, capacity, spatial and career invariant
checks, and the runtime-runner tests).

**Owner runtime evidence** ([RUNTIME_TESTING § 15](docs/RUNTIME_TESTING.md#15-owner-observed-runtime-evidence)):

- **Phase 2.9 runtime validation: PASS**, in two real-game environments. In a fresh Dev Quicktest colony: Quick
  smoke 12 PASS, Full safe regression 49 PASS / 2 WARN, Live integration scan 10 PASS, zero FAIL. In the real,
  ongoing, heavily modded colony (130 contractors, 4,114 silver, both unchanged by manual check after every
  run): Quick smoke 12 PASS, Full safe regression 50 PASS / 1 WARN then 51 PASS / 0 WARN, Live integration
  scan 10 PASS, zero FAIL. The WARNs were slow-step profiler telemetry, not correctness failures. This is
  evidence for the runtime test framework; it does **not** mean every mod interaction or every RimWorld state is
  proven, the in-progress Cancel path was not exercised by hand (it has headless coverage), and it is **not** the formal S20 checklist.
- **Phase 2 / 2.75 procurement:** the owner observed a legacy active procurement continue after an update, go on
  payment hold, recover when silver was obtained, and deliver in full by vanilla drop pods with no observed
  errors (10,000 / 10,000 Plasteel; **not** the formal S20 checklist).

**Not yet run:** the formal runtime-spike checklists, including **S20** (Phase 2.5 spatial continuity and charter
transport), remain *NOT RUN — owner runtime validation required*. Their records and owner steps are in
[docs/spikes/](docs/spikes/README.md).

### Build

```
./build.sh "<RimWorld>/RimWorldWin64_Data/Managed"     # or RIMWORLD_MANAGED=<dir> ./build.sh
```

Output: `1.6/Assemblies/TheNetwork.dll` (net472, C# 7.3). Game DLLs are external references and are
never committed. The first `[TheNetwork]` line in `Player.log` shows the build stamp.

### Tests

```
Tests/run-tests.sh "<RimWorld Managed folder>" "<path to 0Harmony.dll>"
```

Runs the real `TheNetwork.dll` against the real `Assembly-CSharp` under Mono with fake adapter ports.
`0Harmony` is used by the **test runner only** (to stub Unity-only logging); the mod references no
Harmony. These tests prove Network logic, not vanilla behaviour.

The in-game counterpart (Dev Mode, no setup) is described in [docs/RUNTIME_TESTING.md](docs/RUNTIME_TESTING.md).

> **Design authority.** The [master design document](The%20Network%20%E2%80%94%20Full%20Mod%20Design%20-%20Master%20Implementation%20Brief.md) defines product and gameplay intent. The
> architecture documents define technical implementation. Where they conflict, the design takes
> precedence unless a reviewed ADR explicitly records a necessary deviation
> ([DECISIONS](docs/DECISIONS.md)). The first Phase 0 pass was written before the master design
> was on `main`; the architecture has since been reconciled against it
> ([ARCHITECTURE § 14](docs/ARCHITECTURE.md#14-master-design-reconciliation)).

## Documentation map

The master design is the canonical **product and gameplay specification**. The documents in
`docs/` are the canonical **technical specification** for anyone, human or coding agent,
implementing The Network. When the code and the docs disagree, fix whichever one is wrong in the
same change.

| Document | Read it for |
|---|---|
| [The Network — Full Mod Design - Master Implementation Brief](The%20Network%20%E2%80%94%20Full%20Mod%20Design%20-%20Master%20Implementation%20Brief.md) | **Canonical product/gameplay specification**: what the mod is, how it should play, and what it must never do |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System diagram, subsystem contracts, stable-vs-replaceable map, self-review, master-design reconciliation |
| [docs/DATA_MODEL.md](docs/DATA_MODEL.md) | Identifiers, external references, every persistent entity and its fields |
| [docs/EVENTS_AND_HISTORY.md](docs/EVENTS_AND_HISTORY.md) | Network events, history ledger, summaries, reputation, awareness, gossip, legends |
| [docs/STATE_MACHINES.md](docs/STATE_MACHINES.md) | Intel, opportunity, contract, procurement, offer, operation, custody, actor lifecycles |
| [docs/SIMULATION.md](docs/SIMULATION.md) | Scheduler, abstract resolver, willingness/refusal, morale, determinism and RNG |
| [docs/PHYSICAL_LIFECYCLE.md](docs/PHYSICAL_LIFECYCLE.md) | **Phase 3 design (normative, not implemented):** authority, identity and progressive concretization, Episodes, provenance, Operational Roles and role composition, team cohesion, truthful aging, custody, atomic reconciliation, save/load, the equipment seams, the Phase 3.3 handoff direction, RimWorld API audit, invariants, subphases |
| [docs/ABSTRACT_PHYSICAL_LIFECYCLE.md](docs/ABSTRACT_PHYSICAL_LIFECYCLE.md) | The Phase 0 lifecycle design (confirmed in its core; superseded in part by PHYSICAL_LIFECYCLE) |
| [docs/RIMWORLD_INTEGRATION.md](docs/RIMWORLD_INTEGRATION.md) | What vanilla 1.6 APIs we reuse, avoid or wrap; Harmony policy; runtime spikes |
| [docs/SAVE_AND_MIGRATION.md](docs/SAVE_AND_MIGRATION.md) | Save layout, `NetworkSaveVersion`, migrations, mod add/remove behaviour |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | Item catalog, external Def safety, DLC and optional systems |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | Work classes, budgets, scheduling frequencies, anti-patterns |
| [docs/DEBUGGING.md](docs/DEBUGGING.md) | Logging policy, dev actions, validators, timing instrumentation |
| [docs/RUNTIME_TESTING.md](docs/RUNTIME_TESTING.md) | In-game runtime regression tests: sandbox, live scan, safety model, stable test IDs, how to run, what is not automated |
| [docs/IMPLEMENTATION_PHASES.md](docs/IMPLEMENTATION_PHASES.md) | Phase plan, Phase 1 vertical slice and acceptance criteria |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Architectural decision records, including the alternatives we rejected |
| [docs/RISKS.md](docs/RISKS.md) | Technical risk register |

## Core principles

1. **Story over statistics.** Numbers exist so there is something to tell stories about. The
   player sees what actors did, not stat sheets.
2. **History is load-bearing.** Meaningful events are recorded once and consumed by several
   systems (relationships, reputation, morale, gossip, willingness). History is not decoration.
3. **Abstract by default, physical only when relevant.** Off-map contractors are records, not
   ticking pawns. Real pawns and sites exist only where the player can touch them.
4. **Uncertainty is part of the game.** Intel describes what somebody *reports*, which is kept
   apart from what is actually true in the world. Leads can be wrong.
5. **Failure is meaningful and branches.** A failed contract closes its own record and can open
   new stories: a rescue, a recovery, a grudge.
6. **Built for heavily modded games.** Nothing assumes a Def, faction, DLC or mod still exists.
   A missing reference cancels the affected activity cleanly and leaves the rest of the save alone.
7. **Invisible to TPS.** No per-tick contractor processing, no repeated Def scans and no history
   rescans. Work is scheduled, staggered and budgeted.
8. **Safe across long saves.** Persistent data is versioned from the first release and migrated
   forward. History stays bounded.
9. **Interest, not need.** The player says what they are interested in. The Network never
   infers what the colony needs and never reads research, stockpiles or demand. Intel is about an
   item, never an amount; only Procurement names a quantity.
10. **A recurring cast, a local history.** The cast of contractors and Fixers is shared across
    saves through `ModSettings`; what happens to them belongs to each world alone and is never
    written back.

## Conventions

| Item | Convention |
|---|---|
| Target | RimWorld 1.6, .NET Framework 4.7.2 class library |
| packageId | `aRed.TheNetwork` |
| Assembly / root namespace | `TheNetwork` |
| Log prefix | `[TheNetwork]` |
| Def name prefix | `TheNetwork_` |
| Signal / quest-tag prefix | `TheNetwork.` |
| Harmony | None. Not required for Phases 1–3: the Phase 3 design review found no required lifecycle event that needs it (see [RIMWORLD_INTEGRATION.md § Harmony](docs/RIMWORLD_INTEGRATION.md#3-harmony-policy), [PHYSICAL_LIFECYCLE § 14](docs/PHYSICAL_LIFECYCLE.md#14-event-detection)) |
| Dependencies | None. Grandmaster21 and RegenNanites were read as references only. The Network does not depend on them or assume they are installed. |
| Settings | `ModSettings`: preferences, catalog overrides and the global cast (with its own `NetworkSettingsVersion`). Never runtime history. |
| Access | A usable vanilla Comms Console is required for Network actions (Phase 1). |

Repository layout:

```
About/                 About.xml (Preview.png is left to the owner)
1.6/Assemblies/        TheNetwork.dll (build output)
1.6/Defs/              TheNetwork_MainButton, the name pools (the only Phase 1 Defs)
1.6/Patches/           the single patch adding the site comp to the vanilla Site WorldObjectDef
Languages/English/     Keyed strings
Source/TheNetwork/     Kernel/ Domain/ History/ Integration/ Core/ UI/ Diagnostics/ Settings/ Persist/
Tests/                 headless tests (TheNetwork.Tests), fixtures, run-tests.sh
docs/                  the architecture; docs/spikes/ holds the runtime spike records
```

## License

To be decided by the repository owner before the first release.
