# Phase 3 Design: Abstract ↔ Physical Lifecycle

> **Status: DESIGN REVIEW. Nothing in this document is implemented.** Written against `main` `6d0352d`
> (Phase 2.9 merged and owner-runtime-validated; save format stays **4**). Every RimWorld fact below was read
> from the decompiled `Assembly-CSharp 1.6.9676.17735` (file and member named), not recalled; where the code
> could not settle a question it is marked **OPEN** with the narrowest spike that would. Class and method
> names for new things are **candidates, not frozen**.
>
> This document is the normative Phase 3 design. It **refines** the Phase 0 design
> ([ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md), ADR-013, ADR-014) and **corrects** it where
> the API audit disagreed ([Appendix E](#appendix-e-what-the-audit-changed-from-the-phase-0-design)).
> Related: [ARCHITECTURE § 6.7](ARCHITECTURE.md), [STATE_MACHINES § 7–8](STATE_MACHINES.md),
> [SPATIAL](SPATIAL.md), [CAREERS](CAREERS.md), [RUNTIME_TESTING](RUNTIME_TESTING.md),
> [RISKS](RISKS.md), [DECISIONS ADR-048, ADR-049](DECISIONS.md).

> **Amendment pass (design only, on top of `5f16a71`).** Ten further owner decisions refine this design without
> touching its foundation (authority, identity, the Episode, observation-based reconciliation): **Operational
> Roles**, **role composition**, **progressive concretization**, **team cohesion**, the **reputation / fame /
> capability** split, a stronger **reconciliation-atomicity** discipline, **truthful aging**, a generalized
> **equipment seam** (Lease vs Notable Asset), **Phase 3.3** (procurement fulfillment / physical handoff, design
> direction only) and a stricter **physical-test guard**. Every statement an amendment changed is edited in place
> and listed in [Appendix F](#appendix-f-amendment-log). Nothing here is implemented, no runtime spike has been
> run, and the save format is still **4**.

## The decisions on one page

1. **One authority at a time, per person.** A person is *Abstract* (the Network record is truth), *Physical*
   (the real Pawn is truth for the facts the projection owns), or *Vanilla-held* (a vanilla system holds the pawn:
   prisoner, colonist, kidnapped, in a caravan). Nothing advances a person in two layers. One central question,
   `CanSimulateAbstractly(person)`, gates every abstract writer ([§ 3](#3-authority-model), [Appendix C](#appendix-c-interaction-classification)).
2. **Three identities, never conflated.** *Actor* (`ActorId`: the contractor or organization) ≠ *Person*
   (`CharacterId` for a named person, an anonymous *slot* for rank-and-file) ≠ *Pawn* (a RimWorld object whose
   `thingIDNumber` is a binding, **never** identity) ([§ 4](#4-identity-model)).
3. **A named person has at most one pawn, for life.** First materialization creates it (a *role-constrained*
   projection, [§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)); it is bound once and never
   regenerated or rerolled. If it is lost, the person is `Lost`. **Rank-and-file of a large organization are
   ephemeral**: a fresh pawn per episode, released to vanilla afterwards, with only headcount deltas persisted.
   **Small recurring organizations concretize progressively**: a seat the player has physically met becomes a
   named, bound person, bounded by the organization's existing named-people caps, so a five-person crew never
   swaps its medic for a stranger between visits ([§ 4.5](#45-progressive-concretization)).
4. **A new durable concept is justified: the Physical Episode** (candidate name). It owns *presence* facts only
   (who is out there, where, since when, what has been seen, whether reconciliation has been applied) and no
   consequences of its own: consequences are applied through the **existing** casualty, career, spatial and event
   services ([§ 5](#5-materialization-model)).
5. **Provenance is a Network-persisted binding** (`CharacterId`/episode slot ↔ pawn pointer + `thingIDNumber`),
   with **quest tags** only as a signal-routing aid and a **registry reservation** only for retained pawns that
   would otherwise be `Free` ([§ 7](#7-physical-provenance)). No Hediff, no ThingComp, no name matching.
6. **Reconciliation is observe → decide → plan → validate → atomic durable commit → flag → release → follow-up →
   publish.** It is idempotent and driven by *observed* pawn state; a signal is only a wake-up
   ([§ 15](#15-reconciliation-algorithm)). The commit is all-or-nothing for the Network's durable state (a pure
   plan, a snapshot of the exact small touched set, a restore on any throw, the flag **last**) and contains **no**
   publication, scheduler or vanilla effect, because the existing casualty, succession and actor-ending paths
   interleave exactly those. "Not spawned" is never evidence of anything ([§ 9](#9-custody-model)).
7. **Death is monotonic.** A physical death is final; resurrection by another mod is observed, never initiated,
   and never makes the person `Active` again ([§ 10](#10-death-and-injury)).
8. **Only identity-defining truth survives abstraction, and age is truthful.** Permanent physical truth stays on
   the retained pawn (it is real); temporary injury becomes the existing abstract recovery; gear is real loot and
   never mirrored into the abstract equipment tier ([§ 10](#10-death-and-injury), [§ 11](#11-gear-semantics)). A stored
   pawn is never under-aged: chronological age is derived from the game clock, and biological age is brought fully
   up to date (never capped per materialization) before anything can observe it
   ([§ 6.4](#64-truthful-aging-of-a-retained-pawn)).
9. **No Harmony is required for the recommended slice.** Every needed event has a vanilla signal, a component
   callback or a bounded poll; the real gaps (downed, resurrection, caravan join) are polls, documented
   ([§ 14](#14-event-detection)).
10. **Event-driven, near-zero idle.** No scan of pawns, maps or world pawns per tick; jobs exist only while an
    episode or a vanilla-held person exists ([§ 18](#18-performance)).
11. **Physical tests are a separate, session-armed tier on their own map.** The safe suites never spawn a pawn. The
    physical tier creates and owns a dedicated test map by default, needs an explicit *session-only* arm, and
    **never guesses** whether a save is disposable ([§ 21](#21-runtime-qa-strategy), ADR-049).
12. **Four subphases, an owner review gate after each.** 3.0 abstract foundation (headless + safe suites over a fake
    physical port), 3.1 the controlled physical episode (first real pawn), 3.2 custody, rescue and groups (first
    player-visible content), 3.3 procurement fulfillment / physical handoff (**design direction only**:
    [§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)). Leases, sponsorship, notable-asset
    grants, contract inheritance and ambient visits stay **out** of Phase 3 ([§ 22](#22-phase-3-vertical-slice),
    [§ 23](#23-suggested-subphases)).
13. **Operational Roles constrain only what must not be contradicted.** A role is a durable, semantic *function*
    (Marksman, Medic, Heavy, …), not a class or a perk tree. An organization has a persistent **role composition**
    (what it broadly contains); each mission picks a **mission composition** (the subset it needs). RimWorld's
    randomness fills everything the Network never established and may never contradict what it did
    ([§ 6.6–6.8](#66-operational-roles)).
14. **Team cohesion is an initial-projection constraint only.** A professional veteran team must not appear
    already hating its leader; once the pawns are real, vanilla social history is real and is never sanitized
    ([§ 6.9](#69-team-cohesion)).
15. **Professional reputation ≠ fame/visibility ≠ capability.** Today one score is shown as "fame" and gates
    equipment rungs; the intended future separates the three, and physical projection never reads fame as a proxy
    for skill ([§ 6.10](#610-professional-reputation-fame-and-capability)).
16. **Equipment stays abstract unless an exact item matters.** Two different future durable relationships exist, a
    *Lease* and a *Notable Asset*, each persisted only when the exact item is narratively or contractually
    important; generic gear never becomes an inventory ([§ 11](#11-gear-semantics)).

## Contents

1. [Goals and non-goals](#1-goals-and-non-goals)
2. [Existing architecture audit](#2-existing-architecture-audit)
3. [Authority model](#3-authority-model)
4. [Identity model](#4-identity-model) (incl. [4.5 progressive concretization](#45-progressive-concretization))
5. [Materialization model](#5-materialization-model)
6. [Projection rules](#6-projection-rules) (incl. [6.4 truthful aging](#64-truthful-aging-of-a-retained-pawn),
   [6.6 operational roles](#66-operational-roles), [6.7 composition](#67-organization-and-mission-composition),
   [6.8 role-constrained creation](#68-role-constrained-creation-validate-then-the-smallest-correction),
   [6.9 team cohesion](#69-team-cohesion), [6.10 reputation, fame and capability](#610-professional-reputation-fame-and-capability))
7. [Physical provenance](#7-physical-provenance)
8. [Lifecycle state machine](#8-lifecycle-state-machine)
9. [Custody model](#9-custody-model)
10. [Death and injury](#10-death-and-injury)
11. [Gear semantics](#11-gear-semantics) (incl. the Lease / Notable Asset seam)
12. [Spatial integration](#12-spatial-integration)
13. [Faction and AI model](#13-faction-and-ai-model)
14. [Event detection](#14-event-detection)
15. [Reconciliation algorithm](#15-reconciliation-algorithm) (incl. [15.6 failure semantics of the commit](#156-failure-semantics-of-the-commit-service-by-service))
16. [Save/load semantics](#16-saveload-semantics)
17. [Failure recovery](#17-failure-recovery)
18. [Performance](#18-performance)
19. [Mod compatibility](#19-mod-compatibility)
20. [Prepare-for-removal](#20-prepare-for-removal)
21. [Runtime QA strategy](#21-runtime-qa-strategy)
22. [Phase 3 vertical slice](#22-phase-3-vertical-slice)
23. [Suggested subphases](#23-suggested-subphases)
24. [Risks](#24-risks)
25. [Open questions and spikes](#25-open-questions-and-spikes)
26. [Acceptance criteria](#26-acceptance-criteria)
27. [Phase 3.3: procurement fulfillment and physical handoff (design direction)](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)

Appendices: [A. RimWorld 1.6 API audit](#appendix-a-rimworld-16-api-audit) ·
[B. Formal invariants](#appendix-b-formal-invariants) ·
[C. Interaction classification](#appendix-c-interaction-classification) ·
[D. Glossary](#appendix-d-glossary) ·
[E. What the audit changed from the Phase 0 design](#appendix-e-what-the-audit-changed-from-the-phase-0-design) ·
[F. Amendment log](#appendix-f-amendment-log)

---

## 1. Goals and non-goals

**Goal.** A persistent abstract actor can temporarily become physically present in real RimWorld; what then
happens physically is authoritative; and that reality returns to The Network **exactly once**, with no clones, no
resurrection bugs, no lost custody, no duplicated consequences, no save corruption and no hidden second
authority. *Physical Halvard may now acquire legs. He may not acquire clones.*

| # | Goal |
|---|---|
| G1 | One authority per person at any time, enforced in one place, provable by invariant and test |
| G2 | A named person keeps one pawn for life; rank-and-file of a large organization is a projection that leaves no persistent trace beyond headcount; a small recurring organization's members concretize progressively and are never casually replaced ([§ 4.5](#45-progressive-concretization)) |
| G3 | Physical outcomes (death, wounds, capture, defection, loss) reconcile into existing Network truth through existing services, once |
| G4 | Save/load at any moment of a physical episode neither duplicates, rerolls, abstracts, resurrects nor loses anyone |
| G5 | Custody beyond the map (prisoner, kidnapped, caravan, world pawn) is tracked and never mistaken for "gone home" |
| G6 | Zero Harmony; event-driven; near-zero cost when nobody is physical |
| G7 | Heavily modded games (races, xenotypes, gear, health systems, factions, transports, resurrection mechanics) degrade gracefully through capabilities, never through named-mod code |
| G8 | Destructive physical QA exists, but as a separate explicit tier that cannot be pressed by accident and never guesses whether a save matters ([§ 21.2](#212-tier-p-the-physical-integration-suite-armed-per-session-dedicated-test-map-by-default)) |
| G9 | A first projection never contradicts durable Network truth (a role, a name, a composition, a granted notable asset); everything the Network never established stays vanilla-random ([§ 6.6](#66-operational-roles)) |
| G10 | A retained person ages truthfully while stored, and the reconciliation commit is all-or-nothing for the Network's durable state ([§ 6.4](#64-truthful-aging-of-a-retained-pawn), [§ 15](#15-reconciliation-algorithm)) |
| G11 | The first consumer of the lifecycle, procurement fulfillment by physical handoff, has a compatible seam before it is built ([§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)) |

**Non-goals (frozen for Phase 3).**

| # | Non-goal | Why |
|---|---|---|
| N1 | Physical contractor caravans for ordinary background movement | `SpatialState` is the one geographic truth; background movement stays abstract |
| N2 | Exact off-map inventory, bullet counts, apparel history, vehicle ownership, hidden implants | Projection, not secret inventory simulation ([§ 6](#6-projection-rules)) |
| N3 | A mirror of every Hediff in an abstract health simulator | Only identity-defining truth survives; temporary injury maps to the existing recovery model ([§ 10](#10-death-and-injury)) |
| N4 | One RimWorld faction per contractor, or altering world diplomacy for AI convenience | ADR-006; [§ 13](#13-faction-and-ai-model) |
| N5 | A contractor tactical-AI framework | Vanilla `Lord`/`LordJob`/`Duty` do the work |
| N6 | Phase 4 compensation, sponsorship, **leases, notable-asset grants**, the contract board, the player-as-contractor, NPC-issued contracts, rival simulation | A **seam** is defined ([§ 11](#11-gear-semantics), [§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)); nothing is built |
| N7 | Ambient contractor visits as content | Deferred; the machinery makes them technically possible |
| N8 | Mod-specific logic in core rules (Grandmaster21, RegenNanites, Beyond Our Reach, Isekai, …) | Capabilities and ordinary Def/API truth only; those mods are owner QA environments |
| N9 | Scanning all pawns, all maps or all world pawns on a timer | [§ 18](#18-performance) |
| N10 | Resurrection as a feature | Observed and tolerated, never initiated ([§ 10](#10-death-and-injury)) |
| N11 | Fixing the dev-tool or UI surface beyond a read-only monitor | A read-only Episode Monitor only ([DEBUGGING § 7](DEBUGGING.md)) |
| N12 | An RPG class framework: Operational Roles as classes, perks, stat bonuses or unlock trees | A role is a semantic *function* that bounds first projection and nothing else ([§ 6.6](#66-operational-roles)) |
| N13 | Persisting an exact skill sheet, passion list or trait list for a person who has never materialized | The role is durable truth; the pawn becomes the detailed truth after its first creation |
| N14 | A social simulator, mind control, or continuous "sanitizing" of real pawn relationships | Cohesion constrains *first generation* only ([§ 6.9](#69-team-cohesion)) |
| N15 | A persistent roster of every anonymous company member | Rank-and-file of large organizations stay ephemeral ([§ 4.5](#45-progressive-concretization)) |
| N16 | Using fame, reputation score or visibility as a proxy for combat or any other skill | Capability is its own concept ([§ 6.10](#610-professional-reputation-fame-and-capability)) |
| N17 | Persistent trucks, ships, vehicles or freight simulation; rival interception; payment-timing variants; building Phase 3.3 | Design direction only ([§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)) |

---

## 2. Existing architecture audit

Audited from the merged code, not from the design documents. Everything in this section is a fact about
`main` `6d0352d`.

### 2.1 What exists, and what Phase 3 needs from it

| Concept | Where | Phase 3 relevance |
|---|---|---|
| `NetworkActor` (`ActorKind` Organization, Individual, FactionProxy, PlayerProxy, Institution; `ActorStatus`) | `Domain/Actors/NetworkActor.cs` | The *actor* is a contractor or organization. It is **not** a person and never has a pawn. |
| `ActorBindings.embodies` / `KnownCharacter.embodiedBy` | `NetworkActor.cs`, `ContractorService.cs:222`, `ActorService.cs:166` | A **Solo** contractor is an `Individual` actor that *embodies* exactly one `KnownCharacter`. The person is the character, not the actor. |
| `KnownCharacter` (`role`, `org`, `embodiedBy`, `status`, `custody`, `notability`, `woundedUntilTick`, `diedTick`, `deathCauseKey`, `NameSnapshot`) | `NetworkActor.cs:237` | The persisted *person* record. It has **no pawn binding, no gender, no age, no appearance, no operational role**: exactly the minimum a Phase 3 binding must add. Its existing `role` is `CharacterRole` (Leader, Lieutenant, Specialist, Member, Freelancer, Retired): *organizational standing*, **not** an operational function, and it must not be overloaded ([§ 6.6](#66-operational-roles)). |
| `CustodyState` (Unmaterialized, Stored, Deployed, OutOfCustody, Released, Lost) | `NetworkActor.cs:222` | Persisted on every character since Phase 2 and **never written**: every saved value is `0`. Its meanings can be fixed now at zero migration cost ([§ 8](#8-lifecycle-state-machine)). |
| `CharacterStatus` (Active, Wounded, Captured, Missing, Dead, Retired, Defected, Lost) | `NetworkActor.cs:210` | Abstract *story* status. Orthogonal to custody ("who controls the pawn"). |
| `OrganizationProfile` (leader, ≤ 2 lieutenants, ≤ 6 `knownMembers`, `tiers` headcount, `woundedRecovery` buckets, `committed`) | `Persist/ContractorComponents.cs:537` | Named people are records; everyone else is a *count by tier*. A crew is `knownMembers` + headcounts. There is no roster of anonymous individuals, **no role composition**, and Phase 3 must not create a roster. The existing caps (1 + 2 + 6 = at most 9 named people) are the ceiling that bounds progressive concretization ([§ 4.5](#45-progressive-concretization)). |
| `ContractorSimulation` (`equipment` tier/condition/specialties, `mobility`, `spatial`, `career`, `commitments`, `funds`, `skill`, runtime `cachedStrength`) | `ContractorComponents.cs:381` | Capability to *project* from. `commitments` is `List<OperationId>` and drives job capacity. |
| `PublicReputation` (a numeric `score`; `FameBand` Unknown…Legendary **derived** from it), `ExperienceBand` (Green…Legendary), `CareerPolicy` | `NetworkActor.cs:87`, `Bands.cs`, `CareerPolicy.cs` | **Capability** is already a separate band (`ExperienceBand`). But the one `score` is built from *completed work* (`ReputationGain`) yet is exposed, displayed and used as "fame", and `RequiredFame(tier)` gates equipment advancement on it: professional record and public visibility are one number today ([§ 6.10](#610-professional-reputation-fame-and-capability)). |
| `SpatialState` (anchor, destination, status Idle/Travelling/OnAssignment/Blocked, charter ends) | `ContractorComponents.cs:199`, `SpatialService.cs` | The one abstract geographic truth ([SPATIAL](SPATIAL.md)). Driven daily from `UpkeepService.UpkeepJob → Spatial.Upkeep(a) → CatchUp + MaybeRelocate`. |
| `Operation` (`forces` tier counts, `characters`, `status` incl. reserved `Physical = 3`, `outcome` with `fates`, `troubledKey`, `troubledDeadlineTick`) | `Domain/Operations/OperationModel.cs` | The abstract job. Its `forces`/`characters` are exactly the people a **rescue** episode would re-home. `OpStatus.Physical` is already reserved ("never entered in Phase 2"). |
| `OperationOutcome`/`CasualtyReport`/`CharacterFate` (Killed, Wounded, Captured, Missing) | `OperationModel.cs`, `ContractorService.ApplyCasualties` | The **existing vocabulary of consequences**. Physical outcomes should be expressed in it, not in a parallel one ([§ 15](#15-reconciliation-algorithm)). |
| `OperationService.TroubledDeadline` | `OperationService.cs:478` | "Phase 2 has no physical rescue, so it resolves abstractly": the group turns up (`found`) or is written off. This is the abstract stand-in a physical rescue would replace. |
| `ConsequenceEngine` (rule `LastKnownLocation`) | `Domain/Consequences/ConsequenceEngine.cs` | Creates an Opportunity and a vanilla site for a catastrophic loss, missing or stranded contractor. Documented: "Never contractor pawns, survivors, captives or bodies (Phase 3)". |
| `SiteAdapter` / `WorldObjectComp_NetworkSite` / `SiteCallbacks` | `Integration/` | Vanilla `Site` with the vanilla `ItemStash` part, one threat part, vanilla `TimeoutComp`, the injected comp (`PostMapGenerate`, `PostMyMapRemoved`, …). The pattern a rescue site extends. |
| `SignalBridge` | `Integration/SiteCallbacks.cs:113` | One registered receiver with a stale-bridge guard, currently routing only `TheNetwork.Opp.<id>.MapSettled`. The pattern a pawn-signal route extends. |
| `NetworkState` reserved stores `deployments`, `leases` | `Core/NetworkState.cs` | `ReservedStore` writes an empty node; "the phase that fills the slot replaces this type at the same XML label". **The save layout does not shift.** |
| `EntityKind.Deployment = 9`, `Lease = 10` | `Kernel/Ids.cs` | Id kinds already declared over the shared counter. A new id kind must also be added to `NetworkState.MaxEntityId` (a test checks every kind). |
| `NetScheduler` persisted jobs, `UpkeepJob`, `CompactionService`, `RemovalPreparer`, `NetValidator` | `Kernel/`, `Core/`, `Integration/` | The existing seams for watch jobs, compaction, prepare-for-removal and invariants. |
| Phase 2.9 runner, sandbox, `LiveFingerprint`, `ColonySentinel` | `Diagnostics/RuntimeTests/` | The safe tier. `LiveFingerprint` walks every persisted field by reflection, so a new store is covered automatically; `ColonySentinel` deliberately does **not** scan pawns. |

### 2.2 Reserved, declared and unused (the slots Phase 3 fills)

`deployments` and `leases` store slots · `KnownCharacter.custody` (never written) · `OpStatus.Physical` (never
entered) · `EntityKind.Deployment` / `Lease` · the comment on `EquipmentProfile` ("Leases are Phase 3+ and
deliberately absent") · the comment on `KnownCharacter` ("Pawn binding (PawnRef) is Phase 3 and deliberately
absent") · `ConsequenceEngine`'s "no survivors" rule.

### 2.3 The writer inventory: every abstract site that must honour authority

If one of these keeps simulating a person who is physical, the "Abstract Halvard healthy / Physical Halvard dead
in a ditch" bug exists. Phase 3.0 puts **one** gate in front of all of them.

**A second audit finding (amendment 6).** These writers are not only *writers*: `ApplyCasualties`, `RunSuccession`
and `EndActor` each interleave durable mutation with inline `ctx.bus.Publish`, scheduler cancellation and calls into
other services (the spatial facade swallows its own faults). Phase 3 therefore cannot call them as one "block of
primitive assignments"; [§ 15.6](#156-failure-semantics-of-the-commit-service-by-service) says what it does instead.

| Site | What it does to a person today | Phase 3 rule |
|---|---|---|
| `ContractorService.AvailabilityOf` (`:559`) | derives availability from `status` | physical/held ⇒ a new unavailable reason; **no refusal text may say the person is simply "busy"** |
| `ContractorService.Checkout` / `Occupied` (`:666`, `:679`) | picks `knownMembers` for an operation; excludes people already on one | exclude people in a Planned/Open episode or held by vanilla ([ADR-039](DECISIONS.md) extended) |
| `ContractorService.Strength` (`:476`) | counts available people | physical/held people do not count as available |
| `ContractorService.ApplyCasualties` (`:794`) | writes `status`, `diedTick`, morale shock, `opsCompleted++`, skill gain, succession, `EndActor` | physical outcomes reuse the *fate* handling, **not** the per-operation bookkeeping ([§ 15](#15-reconciliation-algorithm)) |
| `ContractorService.RunSuccession` (`:888`) | skips `Captured`/`Missing`/dead | also skips physical and held people |
| `UpkeepService.HealCharacter` (`:221`) | `Wounded → Active` at `woundedUntilTick` | skipped while the person is not abstract |
| `OperationService.TroubledDeadline` (`:478`, `:499`) | sets `Missing → Wounded`; resolves the group abstractly | a Troubled operation with a rescue episode is `OpStatus.Physical` and this job is cancelled ([§ 22](#22-phase-3-vertical-slice)) |
| `OperationService.ContractorCanWork` (`:253`) | alive, not captured/missing | also not physical/held |
| `SpatialService.Upkeep/CatchUp/MaybeRelocate` (`:280`, `:481`, `:758`) | advances journeys and relocates idle actors | skipped for an actor whose person is physical ([§ 12](#12-spatial-integration)) |
| `CareerService` advancement | equipment advances "never during a job" | an open episode counts as a job |
| `ProcurementService` candidate selection | filters by `AvailabilityOf` | inherits the gate |
| `NetValidator` | repairs derived data | **must not** repair custody/episode truth ([§ 16](#16-saveload-semantics)) |

### 2.4 What the audit changed

The Phase 0 design was written before the code existed and before the 1.6 assemblies were decompiled with this
question in mind. The audit **confirmed** most of it (the registry quest, the world-pawn GC reasons, the redress
risk, per-organization temporary factions) and **corrected** several points that would have produced bugs. The
corrections are listed in [Appendix E](#appendix-e-what-the-audit-changed-from-the-phase-0-design); the ones that
shape the design most are: normal pawn death **does** send the `Killed` signal; `PassToWorld` of a `Free` pawn
**rewrites its faction**; a reference to a dead pawn saves as `null` unless asked not to; `GeneratePawn` can
return **someone else's world pawn** unless told to generate a new one; map removal passes non-colonist pawns to
the world **without** a `LeftMap` signal; and a temporary faction is removed (and its pawns' faction nulled) as soon
as nobody is spawned under it.

---

## 3. Authority model

### 3.1 The principle

> **ONE AUTHORITY AT A TIME.** A person is advanced by exactly one layer. When the layer changes, the change is
> a single, recorded, reversible-only-by-reconciliation transition. Never both.

```
            ┌────────────────────────── ABSTRACT ──────────────────────────┐
            │  Network record is truth. Upkeep, recovery, spatial travel,   │
            │  career, procurement and succession may act on the person.    │
            └───────────────┬───────────────────────────────▲──────────────┘
                            │ Materialize                    │ Reconcile (exactly once)
                            │ (episode Planned → Open)       │ observed-terminal outcome only
                            ▼                                │
            ┌────────────────────────── PHYSICAL ──────────────────────────┐
            │  The real Pawn is truth for the physical facts the projection │
            │  owns (location, health, gear, life/death). No abstract layer │
            │  may write them. The Network only observes and records.        │
            └───────┬───────────────┬─────────────────┬────────────────────┘
                    │ pawn is held   │ dies             │ pawn disappears with no evidence
                    ▼                ▼                  ▼
           ┌─── VANILLA-HELD ───┐  ┌── DEAD ──┐   ┌── LOST / QUARANTINE ──┐
           │ prisoner, slave,    │  │ terminal │   │ binding lost; person   │
           │ colonist, kidnapped,│  │ never    │   │ is unavailable, never  │
           │ caravan, pod, held  │  │ abstract │   │ "back home"; diagnosed │
           │ by another faction. │  │ again    │   │                        │
           │ Network observes    │  └──────────┘   └────────────────────────┘
           │ only (custody watch)│
           └──────────┬──────────┘
                      │ released / escaped / rescued ⇒ a NEW episode or a custody event,
                      │ then reconcile back to ABSTRACT
                      ▼
                   ABSTRACT
```

### 3.2 Who owns which fact

| Fact | Abstract | Physical | Vanilla-held |
|---|---|---|---|
| Alive / dead | `KnownCharacter.status` | the Pawn (`Dead`) | the Pawn |
| Location | `SpatialState` (hidden, coarse) | the Pawn's position/map | the holder (map, caravan, faction) |
| Health | `woundedUntilTick` + abstract recovery | the Pawn's `Hediff`s | the Pawn |
| Gear | `EquipmentProfile` tier/condition (a capability, not items) | real `Apparel`/`ThingWithComps` | real |
| Loyalty / faction | Network relations and `org` | the Pawn's `Faction` (temporary) | the Pawn's faction / host |
| Reputation, career, funds | Network (always) | Network (always) | Network (always) |
| Whether the person may take a job | Network | **no** | **no** |

Reputation, careers and money are **never** transferred to RimWorld; they live only in the Network, and a
physical episode changes them only through events published after reconciliation.

### 3.3 Operational rules

- **A1. A single gate.** `CanSimulateAbstractly(person)` (candidate name `AuthorityGate`) is true only for custody
  `Unmaterialized` or `Stored` with no Planned/Open episode membership. Every site in
  [§ 2.3](#23-the-writer-inventory-every-abstract-site-that-must-honour-authority) calls it. A new abstract writer
  of person state that does not call it is a review failure and a validator finding.
- **A2. Authority changes only in two places:** `Materialize` (abstract → physical) and `Reconcile` (physical →
  abstract or held). Nothing else assigns `custody`.
- **A3. Absence of evidence never transfers authority.** Physical → abstract requires a *positive* terminal
  observation ([§ 9](#9-custody-model), P3-INV-005, P3-INV-010).
- **A4. The Network never reaches into a physical pawn to make it match the record.** It does not heal a pawn
  to match `Wounded`, move it to match `SpatialState`, or re-equip it to match a tier. Projection happens once, at
  creation ([§ 6](#6-projection-rules)); afterwards the pawn is real.
- **A5. The abstract record never "catches up" past a physical fact.** If the pawn died, the record says dead,
  whatever any abstract job would have rolled.

---

## 4. Identity model

### 4.1 Three identities

```
 ContractorActor A12  "Horizon Tide Company"                    (ActorId — never a pawn)
     │   OrganizationProfile
     │     ├── leader      K41  "Halvard 'Dead Red' Voss"      (CharacterId) ──► Pawn #5120  (bound, retained)
     │     ├── lieutenant  K42  "Mara Teng"                     (CharacterId) ──► Pawn #5188  (bound, retained)
     │     ├── knownMember K43  "Oskar Lind"                    (CharacterId) ──► (no pawn yet: record only)
     │     └── tiers: Recruit 14 · Regular 9 · Veteran 4        (headcount — no identity)
     │
     └── Episode E7 (this visit)
           ├── member  K41 → Pawn #5120      named, retained
           ├── member  K42 → Pawn #5188      named, retained
           ├── member  slot 0  Regular  → Pawn #9031   anonymous, ephemeral
           └── member  slot 1  Regular  → Pawn #9032   anonymous, ephemeral

 Solo contractor A30 "Reeve" (Individual)  ──embodies──►  K77  ──► Pawn #6004
```

A crew is **not** one pawn because its actor has one `ActorId`: the actor is an organization, the physical
people are `Character → Pawn` pairs and episode slots (the diagram shows a *company*, whose rank-and-file are
ephemeral; a small crew's placed seats become named people instead, [§ 4.5](#45-progressive-concretization)). An *Individual* (Solo) actor embodies one character; the
actor, the character and the pawn are still three distinct things.

### 4.2 The tiers (confirmed, with two changes)

| Tier | Representation | Persists | Pawn |
|---|---|---|---|
| **T0 anonymous member** | a count in `OrganizationProfile.tiers`; during an episode an **episode slot** (it carries its tier and the *seat role* it fills) | headcount only | ephemeral: a new pawn per episode, released to vanilla at the end. **This is the policy for the rank-and-file of a *large* organization**; small organizations concretize instead ([§ 4.5](#45-progressive-concretization)) |
| **T1 Known Character (record)** | `KnownCharacter`, `custody = Unmaterialized`, with its operational role ([§ 6.6](#66-operational-roles)) | ≈ 0.4 KB | none |
| **T2 Known Character (bound)** | `KnownCharacter` + `PawnRef`, `custody ∈ {Stored, Deployed, OutOfCustody}` | the record + a real pawn in the save | **one, for life** |

Two changes from Phase 0. **First**, a *named* person becomes T2 **when it is first materialized**, not only when it
is "met". Reason: the moment the Network puts a named person into the world, vanilla can start referencing the pawn
(relations, play log, battle log, tales, a quest). From that instant a regenerated twin would be a clone, so the pawn
must be the person. **Second (amendment 3)**, "anonymous members are ephemeral" was too ephemeral for *small
recurring* organizations: a five-person crew must not visibly swap its medic for a different human every visit. T0 is
now the policy for large organizations only; below that size a seat the player has physically met crystallizes into
T1/T2 ([§ 4.5](#45-progressive-concretization)).

### 4.3 The questions, answered

| Question | Answer |
|---|---|
| Does every physical person need a persistent `CharacterId`? | **No.** Only people the Network already names (leaders, lieutenants, notable members, every Solo), people a **small** organization's seats concretize, and people promoted by evidence. A rank-and-file pawn of a *large* organization has an *episode slot* and vanishes from Network state when the episode closes. |
| Do anonymous members gain a durable record only when materialized? | **Never merely because they were materialized.** A record follows the concretization policy ([§ 4.5](#45-progressive-concretization)): by seat for small organizations, by evidence for large ones. "It was spawned once" is not a reason. |
| When is a generated pawn a persistent `KnownCharacter`? | At reconciliation (decided in the **plan**, so it is atomic with the rest, [§ 15](#15-reconciliation-algorithm)), if the policy of § 4.5 says so: a small organization's placed seat; or any of **captured, recruited, rescued or enslaved by the player**; **named in a Network letter or event**; **killed or downed a colonist** (or was downed by one and survived); **vanilla now holds a stake in it** (it is in the PlayLog/BattleLog, has a colonist relation, or `EverBeenColonistOrTameAnimal`). Promotion is deterministic and once. A *held* person is never refused a record ([§ 4.5.5](#455-promotion-of-rank-and-file)). |
| How does a returning pawn remain recognizably the same person? | **By being the same `Pawn`.** A named person's pawn is retained (reserved, suspended) and reused. Nothing is regenerated to "look like" them, and they are **truthfully older** ([§ 6.4](#64-truthful-aging-of-a-retained-pawn)). |
| What is the minimum persisted identity? | `CharacterId` (exists) + `NameSnapshot` (exists) + an operational role (new, one byte) + a `PawnRef` (new) + `custody` (exists) + the episode link (new) + `firstEncounterTick` (new). No appearance, gender, age, skills or traits are stored: the pawn *is* them. |
| Should pawn IDs be durable Network identity? | **No.** `Thing.ThingID` is `def.defName + thingIDNumber`, saved as `id`, unique within one save, stable across save/load for as long as that *object* exists, and different for any recreated, duplicated or replaced pawn (`Thing.cs:392`, `ThingIDMaker.cs`). It is a **binding attribute**: valid for the life of one pawn object, checked by *pointer identity*, never an identity. |
| What if a pawn becomes important through interaction? | Promotion above. A generic pawn the player arrests becomes a Known Character (T2, `OutOfCustody`) at reconciliation, so the Network never forgets a prisoner it created. |
| How is organization membership tied back to physical people? | `KnownCharacter.org` for named people; `Episode.actor` + slot `tier` + seat role for anonymous ones; the headcount `committed → returned/lost` in `OrganizationProfile`. **Never** inferred from `pawn.Faction` or name. |

### 4.4 What this deliberately does not do

No persisted roster of anonymous individuals; no appearance snapshot; no regeneration from a seed to recreate a
person (generation depends on the mod list, ADR-013); no cap on *physical visits*, only a performance policy on
retained pawns that can never break the identity of a person the player has met ([§ 4.5.4](#454-retention-and-the-performance-cap)).

### 4.5 Progressive concretization

> **Abstract people may remain abstract while nobody has a reason to care who they are. Once the player
> meaningfully encounters a person, that identity does not casually evaporate.** *Abstract commodities; preserve
> stories.*

#### 4.5.1 The ladder

```
 headcount (tiers)  ──►  seat (a role position of the composition, § 6.7)  ──►  episode slot  ──►  crystallized person
 "14 Regulars"            "Medic ×1, Rifleman ×2"                               "a Rifleman, this      K44 "Mara Teng"
  nobody in particular     abstract until met                                    visit only"            + bound pawn, for life
```

A **seat** is a role position of the organization's composition. It is *abstract* until a person fills it; it is
*concretized* when a living `KnownCharacter` with that operational role is pinned to it. Concretization is
**monotone and bounded**: a concretized seat is never silently refilled by a different human while its *encountered* person
lives (P3-INV-020), and the number of named seats never exceeds the organization's *existing* named-people caps
(1 leader + ≤ 2 lieutenants + ≤ 6 known members). No new roster is created: a concretized seat *is* an existing
`KnownCharacter` record.

#### 4.5.2 The policy by organization size (a bounded policy; thresholds are tuning, the principle is frozen)

| Organization (living headcount at placement) | Already named (leader, lieutenants, known members, every Solo) | Other people placed physically | Why |
|---|---|---|---|
| **Solo** (1) | the one person: named and bound at first materialization | none | one actor, one character, one pawn |
| **Duo / small crew** (≲ 6 living) | named and bound | **every placed person crystallizes at first placement** into a seat, up to the named caps | the player *meets* them; five people never turn into strangers |
| **Team / mid-size** (≲ 12) | named and bound | persons whose seat is **role-defining** for the composition (the Medic, the Marksman) crystallize at first placement; the rest only by evidence | continuity where it is visible, economy elsewhere |
| **Company** (larger) | named people only (leader, lieutenants, known members) | **rank-and-file stay ephemeral**; promotion by evidence ([§ 4.5.3](#453-encounter-evidence-observed-at-reconciliation-never-scanned)) | repeated detachments need no giant roster |

Size is the organization's **actual living headcount** at the moment of placement, not its `ContractorForm` label (a
crew that grew into a company is a company). Thresholds are **OPEN O-2** (tuning, soak). A *Solo* always concretizes.

#### 4.5.3 Encounter evidence (observed at reconciliation, never scanned)

Concretization by evidence uses facts the Network can read cheaply for the ≤ 8 members of one episode, at the plan
step. None of them requires a scan, a per-tick check or a persisted log.

| Evidence | Observed through (verified in 1.6.9676) |
|---|---|
| **E0 presence** | the member was `Present` on a **player-visible map** (the home map, or a map a player pawn occupies) for the episode's observed span; a dev/test map is **not** an encounter |
| **E1 interacted or fought with someone who matters** | `Find.PlayLog.AnyEntryConcerns(pawn)` and `Find.BattleLog.AnyEntryConcerns(pawn)` (both public; vanilla's own `WorldPawnGC` uses them for `InPlayLog` / `InBattleLog`, `WorldPawnGC.cs:226–232`) |
| **E2 vanilla already holds a stake** | the pawn would be kept by vanilla's world-pawn GC for a relationship, an active tale or "ever a colonist" (`WorldPawnGC.GetCriticalPawnReason`, public `AccumulatePawnGCDataImmediate`) |
| **E3 a material outcome** | captured, recruited, rescued, enslaved; downed a colonist or was downed by one and survived (the member's `ObservedKind` and faction transition) |
| **E4 the Network names them** | the plan itself decides to name the person in an event or letter |

`PlayLog.AnyEntryConcerns` is O(log entries) per call; with ≤ 8 members at reconcile it is negligible and **never
runs per tick**. Which of these are cheap and reliable in a heavily modded game is **spike S27**.

#### 4.5.4 Retention and the performance cap

The soft cap of ≈ 150 retained pawns is a **performance policy, not permission to break identity**. When it is
exceeded the order of release is:

1. people **never encountered** (E0–E4 all false: bound, but only ever placed on a test or dev map). Nobody has ever seen
   them, so nothing visible is contradicted: such a person is excluded from mission selection (`neverRematerialize`), the
   seat counts as abstract again, and the record stays as history. **No second pawn is ever generated for the same person;**
2. people who **no longer have a living seat**: members of a dissolved organization, `Retired` people. Releasing a
   pawn to vanilla keeps *the same human* in the world (an ordinary world pawn); the Network merely stops
   materializing it (`neverRematerialize`), so no *different* human ever takes its seat;
3. **never** an encountered member of a living organization: the cap is **exceeded** instead (one daily log line and
   an Episode Monitor line).

Because a person the player met is never released, the Network never has to "replace" a remembered person with a
stranger.

#### 4.5.5 Promotion of rank-and-file

Promotion is for anyone not crystallized by the size policy but whom the evidence says matters (a company soldier the
player captured, a rescued survivor named in a letter). It happens **in the plan**, before the pawn is released:
the plan contains the new `KnownCharacter` (record, binding, operational role from the slot) so the promotion commits
**atomically with everything else** ([§ 15](#15-reconciliation-algorithm)). The organization's named caps bound
*seat-based* crystallization. A **held** person is never refused a record because a cap is full: the Network must
always be able to track a prisoner, a recruit or a kidnapped person it created (the record carries `org`; whether it
also joins the `knownMembers` list when full is **OPEN O-2**).

#### 4.5.6 A worked example

```
 Crew "Kestrel" (5 living)  composition: Leader 1 · Rifleman 2 · Medic 1 · Heavy 1
 Visit 1 — mission composition: Leader, Medic, Rifleman
    Leader   → K41 "Halvard Voss"      already named                              → pawn #5120 (bound)
    Medic    → NEW K44 "Mara Teng"     placed on the player's map ⇒ crystallized  → pawn #5301 (bound)
    Rifleman → NEW K45 "Oskar Lind"    crystallized                               → pawn #5302 (bound)
 Visit 2 — a rescue; mission composition: Heavy, Medic, Rifleman × 2
    Medic    → K44 "Mara Teng"         THE SAME PERSON (pawn #5301, truthfully older)
    Rifleman → K45 "Oskar Lind"        THE SAME PERSON
    Rifleman → NEW K46                 crystallized now (the second Rifleman seat was still abstract)
    Heavy    → NEW K47                 crystallized now

 Company "Ironvale" (40 living): leader, 2 lieutenants and 3 known members are named.
 A detachment of 6 riflemen is placed: 6 ephemeral slots, released afterwards. The one the player captured is
 promoted (E3) into a record + binding in the plan; the other five vanish from Network state. No roster of 40.
```

#### 4.5.7 What it persists

`KnownCharacter.firstEncounterTick` (−1 = never; set at reconcile when E0–E4 holds; drives § 4.5.4) and
`EpisodeMember.seatRole`. Nothing else. A concretized seat is an ordinary `KnownCharacter`; the composition itself
is the organization's persisted role template ([§ 6.7](#67-organization-and-mission-composition)).


---

## 5. Materialization model

### 5.1 What is materialized

| Actor form | What becomes physical | What stays abstract |
|---|---|---|
| **Solo** (Individual, embodies one character) | the one character → **one retained pawn** | the actor's funds, career, relations, reputation (always), and everything while the person is not physical |
| **Duo / crew / team** (small organization) | the people the **mission composition** needs ([§ 6.7](#67-organization-and-mission-composition)): *concretized seats first* (named people, for continuity; the leader only when the purpose demands it), then new seats by the concretization policy ([§ 4.5](#45-progressive-concretization)), each as **its own pawn** | the rest of the headcount, the org's morale, funds, doctrine, career, spatial body |
| **Company / larger organization** | a **detachment only**, bounded by purpose (a rescue: exactly the people the operation already committed; a visit: a small fixed party), chosen by the mission composition; rank-and-file as ephemeral slots | the main body, which keeps its own `SpatialState` and upkeep, like a concurrent job's `detached` plan |
| **Named / known character** | one retained pawn with real, persistent identity | the record (role, org, notability, status) |

A crew is never one pawn. A company is never every member. Headcount conservation is exact:
`materialized slots = returned + killed + wounded-returned + held + missing + lost` for each tier, and
`materialized ≤ the healthy headcount the purpose may take` (P3-INV-007).

### 5.2 Does a new durable concept have to exist?

The brief asked to check whether existing structures can own the truth before inventing one.

| Candidate owner | Why it cannot own physical presence |
|---|---|
| **`Operation`** | A visit has no operation. A rescue site's people are *attached* to an operation but the physical story outlives its checkpoints. Operations are compacted a year after closing (`CompactionService`), which would delete the exactly-once flag. `Operation.forces/characters` are *reused* as the member source for a rescue, but they cannot record where a pawn is. |
| **`KnownCharacter`** | It is the right owner of *who the person is and whether they are held*, and gains `pawn`, `episode`, `heldBy`. It cannot own a *group event*: a crew of five named + anonymous people sharing one map, one faction, one cause and one reconciliation. |
| **`Opportunity` / site** | A site is one possible *cause*. A visit and (later) a delivery have no site. |
| **`SpatialState`** | It is the one abstract geography; it must not gain a second, physical meaning ([§ 12](#12-spatial-integration)). |
| **A new durable `PhysicalEpisode`** | **Required.** It owns exactly the facts nothing else can: *who is out there under which cause, since when, authority state, what has been observed, and whether reconciliation has been applied.* |

So **one new store** is justified, in the already-reserved `deployments` slot. The rule that keeps it from
becoming a second state machine: **an episode owns presence facts only. It has no consequences of its own.**
Death, wounds, capture, headcount, morale, succession, career and money are applied by the *existing* services
through the existing vocabulary ([§ 15](#15-reconciliation-algorithm)). (Phase 0 called this concept a
`Deployment`; "episode" is used here because a visit, a rescue and a delivery are all episodes, and only some of
them are a *deployment* of troops. The name is not frozen.)

### 5.3 The persisted data (candidate shapes)

**`PhysicalEpisode`** (store label stays `deployments`; `EntityKind.Deployment = 9` is renamed `Episode`, value unchanged)

| Field | Meaning |
|---|---|
| `id: EpisodeId` | shared id counter, added to `MaxEntityId` |
| `actor: ActorId` | the contractor or organization whose people are present |
| `purposeKey: string` | `Visit` (3.1), `Rescue` (3.2); later `Delivery`, …; unknown keys are tolerated and quarantined, never guessed |
| `cause: {contract?, operation?, opportunity?}` | what caused the appearance; at least one, or an explicit `Dev` key |
| `state` | `Planned → Open → Closed`, or `Quarantined` ([§ 8](#8-lifecycle-state-machine)) |
| `createdTick`, `openedTick`, `closedTick`, `closeReasonKey` | lifecycle ticks and why it ended |
| `consequencesApplied: bool` | set as the **last statement of the guarded commit**, after every other durable assignment succeeded (a restore on any throw also restores its old value); the exactly-once flag ([§ 15](#15-reconciliation-algorithm)) |
| `committedTick: int` | when the durable commit succeeded (diagnostics; the *finish-pending pass* resumes the post-commit stages from the markers below) |
| `publishedTick: int` | set after the post-commit stages finish publishing; a retry after a throw during publish republishes the *deterministic, keyed* events and **never** reapplies a consequence ([§ 15.2](#152-the-steps)). The other post-commit stages have markers too: *release done* = no member carries the episode tag; *follow-up done* = the linked operation has left `OpStatus.Physical` |
| `attempts: int`, `lastError: string` | bounded reconcile retries, then `Quarantined` |
| `where: {tile: TileRef, mapId: int}` | context only; **not** the actor's location |
| `faction: FactionRef?` | the temporary encounter faction, if any |
| `seed: int` | seeds the *first* creation of anonymous pawns |
| `members: List<EpisodeMember>` | the people (≈ 60 B each) |

**`EpisodeMember`**

| Field | Meaning |
|---|---|
| `character: CharacterId` | none for an anonymous slot |
| `slot: int`, `tier: Tier`, `seatRole` | the slot's index, tier and the **operational role of the seat it fills** ([§ 6.6](#66-operational-roles)) |
| `pawn: PawnRef` | the binding ([§ 7](#7-physical-provenance)) |
| `state` | `Planned → Created → Present → Done` |
| `outcome` | `Pending` until `Done`, then **set once**: `Returned`, `Killed`, `HeldByPlayer`, `JoinedPlayer`, `Kidnapped`, `HeldByOther`, `Missing`, `Lost`, `NeverPlaced` |
| `observed: ObservedKind`, `observedTick` | last classification and when (diagnostics and the long-open warning) |

**`KnownCharacter` additions:** `pawn: PawnRef` (null when no pawn exists), `episode: EpisodeId` (the exclusive
open membership), `heldBy: HeldKind` + `heldSinceTick` (what vanilla holds them as), `opRole` (the **operational
role**, one byte, [§ 6.6](#66-operational-roles); distinct from the existing organizational `role`) and
`firstEncounterTick` (−1 = never met; [§ 4.5](#45-progressive-concretization)). Nothing else: not gender, not age, not
appearance, not skills, not traits.

**`PawnRef`:** the pawn **pointer** (saved with `saveDestroyedThings: true`), `thingIDNumber`, the pawn's `def`
name (a sanity check), `boundTick`. Write-once per character. Plus **`agedThroughTick`**: the game tick up to which the
pawn's *biological* age has been brought current; the one piece of bookkeeping truthful aging needs
([§ 6.4](#64-truthful-aging-of-a-retained-pawn)). It is updated only by that catch-up and when a pawn becomes `Stored`.

**Organization addition (3.2):** `OrganizationProfile.composition`: a small list of (`role`, `weight`) that is the
organization's persisted **role template**, *established at first use* and then never regenerated
([§ 6.7](#67-organization-and-mission-composition)); absent in old saves and meaning "not yet established".

**Operation marker:** when an episode resolves a Troubled operation, one durable marker records the result
(`found` / `writtenOff`) in the same commit; the operation's own resolution then runs afterwards through its
existing guarded entry points ([§ 15.5](#155-reuse-of-the-existing-services-no-parallel-rules)).

Estimated cost: ≈ 0.5 KB per episode plus 60 B per member plus ≈ 50 B per bound character (+ 8 B per composition
entry, ≤ 8 per organization): negligible beside the pawns, which vanilla saves in whatever holder owns them. The full minimum-new-persisted-truth list and the
migration implications are in [§ 16](#16-saveload-semantics).

### 5.4 Placement mechanisms (by anchor)

Verified against 1.6.9676; the *recommended slice* uses only the first row.

| Anchor | Mechanism | Verified facts | Notes |
|---|---|---|---|
| **Player map, walk-in visit** (3.1) | `PawnGenerator.GeneratePawn(request)` → `RCellFinder.TryFindRandomPawnEntryCell(out cell, map, roadChance)` → `GenSpawn.Spawn` → `LordMaker.MakeNewLord(faction, new LordJob_VisitColony(faction, chillSpot, durationTicks), map, pawns)` | `LordJob_VisitColony(Faction, IntVec3, int? durationTicks)` is public and handles dangerous temperature and a wounded-guest toil (`LordJob_VisitColony.cs`); vanilla `IncidentWorker_VisitorGroup` does exactly this | spawn, then verify: `Pawn.SpawnSetup` can **discard** a pawn that spawns in an invalid state (`Pawn.cs:1358`) |
| **Opportunity site, before the map exists** (3.2) | add the pawns to `SitePart.things` (a `ThingOwner`, saved `Deep`) consumed by a vanilla GenStep | `GenStep_DownedRefugee` and `GenStep_PrisonerWillingToJoin` both take `parms.sitePart.things[0]` as the pawn; `SitePart.PostDestroy` calls `things.ClearAndDestroyContentsOrPassToWorld()` (pawns → `PassToWorld`) | `DownedRefugee` **damages the pawn until downed** and sets `WillJoinColonyIfRescued`: a rescue site needs the Network's own seeding, or a different step. **OPEN, S11** |
| **Existing generated map, other** | `GenSpawn.Spawn` near an edge plus a Lord | `Pawn.SpawnSetup` removes the pawn from `WorldPawns` automatically | 3.2+ if needed |
| **Drop pod / transport** | `DropPodUtility.DropThingsNear` | transporter handling is vanilla | not in Phase 3 |

---

## 6. Projection rules

### 6.1 The principle

> The question is never "what does this contractor secretly own?" It is **"given capability X, what believable
> physical person appears?"** The pawn is created once from a *request*, then it is simply real.

Never invented: exact off-map inventory, bullet counts, every apparel piece owned for years, bank purchases,
vehicles, hidden implants (unless a future design persists one explicitly).

### 6.2 Anonymous (ephemeral) pawns

These settings also govern the **first creation** of a seat that is about to concretize ([§ 4.5](#45-progressive-concretization));
the only difference is what happens *afterwards* (retained and bound, or released).

| Request setting | Value | Why (verified) |
|---|---|---|
| `ForceGenerateNewPawn` | **`true`** | Without it `GeneratePawn` may **return an existing `Free` world pawn** (redress) instead of creating one (`PawnGenerator.cs:199–222`). The chance is `min(0.02 + 0.01 × FreePawnCount/10, 0.8)` per generation (`:1146`): in a long game with many world pawns it approaches certainty. A materialized "anonymous mercenary" must never be somebody else's pawn (or a retained Network pawn that lost its reservation). |
| `CanGeneratePawnRelations` | `false` | avoids creating relatives as new world pawns with relations to colonists |
| `AllowDead`, `AllowDowned` | `false` | healthy people only |
| `Faction` | the episode's temporary faction ([§ 13](#13-faction-and-ai-model)) | AI, hostility and UI need one |
| kind | chosen by capability from the org's template family, falling back along a chain, then to vanilla defaults; always an **existing** `PawnKindDef` (a runtime kind would not survive a save: `Pawn.kindDef` is saved by def name, `Pawn.cs:4571`) | no named mods; **OPEN O-3**: the equipment-tier → kind/loadout mapping |
| operational role | the seat's role ([§ 6.6](#66-operational-roles)) as request fields and validators, then **verified** and at most minimally corrected ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)) | validators are an optimization, not a guarantee: vanilla drops them after 100 tries |
| cohesion | the organization's existing ideology and a pairwise screen against teammates ([§ 6.9](#69-team-cohesion)) | initial projection only |
| gear | whatever vanilla generates for that kind | the pawn's gear is **real** ([§ 11](#11-gear-semantics)); the Network never builds a loadout by hand |
| `ForbidAnyTitle`, ideology | default vanilla | no Royalty/Ideology logic of our own |
| randomness | `Rand.PushState(NetHash(episode.seed, slot))`, popped in a `finally` | repeatable for debugging within one build only; **identity never depends on regeneration** |

### 6.3 Named pawns: first creation, then reuse

*First* materialization of a named person creates their pawn once (same request as above plus the pins the
`NameSnapshot` and the org template provide):

- the name: `NameSnapshot.first/nick/last` map onto the pawn's `NameTriple`; if a mod's race forbids it, a single
  name is used and the failure is logged once (**OPEN O-7**);
- gender and age: chosen from `NetHash(character.id, networkSeed)` — **the Network has never said anything about
  either** (no narrative text uses gender or age), so there is no earlier statement to contradict;
- race/kind/xenotype: from the org template by capability;
- the **operational role**: the constraint set of [§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction), verified before the pawn is bound;
- the **teammates** already real or generated in the same batch: the cohesion screen of [§ 6.9](#69-team-cohesion).

The pins are **inputs, not stored**. The pawn is bound at once ([§ 7](#7-physical-provenance)) and from then on *it* is the
person: scars, bionics, genes, addictions, skills, relations, tales and every mod's pawn-level state live in the real
pawn, which is why a heavily modded game is better served by keeping the pawn than by describing it.

*Every later* materialization reuses the retained pawn: unreserve it, apply the truthful catch-up of [§ 6.4](#64-truthful-aging-of-a-retained-pawn), place it. It is **never
regenerated and never rerolled**. If the pointer is null or the pawn is `Discarded`, the person is `Lost`
([§ 17](#17-failure-recovery)).

### 6.4 Truthful aging of a retained pawn

> **Chronological elapsed age must remain truthful. If 10 RimWorld years pass, a retained contractor is 10 years
> older.** A rarely met contractor must never age more slowly than a frequently met one. (P3-INV-022)

The previous revision said a suspended pawn's elapsed age catch-up should be **capped per materialization**. That is
withdrawn: a cap under-ages exactly the people the player meets least. No cap, no skipped interval.

**What the audit found** (decompiled 1.6.9676.17735):

| Fact | Evidence |
|---|---|
| **Chronological age is derived, so it is truthful for free.** `AgeChronologicalTicks = GenTicks.TicksAbs − BirthAbsTicks`; nothing about a pawn being stored changes either operand. The Network **never writes `BirthAbsTicks`** | `Pawn_AgeTracker.cs:117–127` |
| **Biological age is an accumulator** that advances only when the pawn ticks: `AgeTickInterval` runs inside the `!Suspended` block of `Pawn.TickInterval`, and the world-pawn bulk path `Pawn.TickMothballed(int)` also does nothing when `Suspended` | `Pawn.cs:1669–1727`, `:1743–1749` |
| A registry-reserved pawn **is** `Suspended` (`ReservedByQuest`), so its biological age, health and jobs freeze; needs freeze through `Need.IsFrozen` | `Pawn.cs:1112–1124`, `Need.cs:63–68` |
| Biological age drives **life stage, growth, every birthday effect** (age-related hediffs via `AgeInjuryUtility.RandomHediffsToGainOnBirthday`, bed unclaim, work-type enabling, juvenile growth moments) and everything that reads `AgeBiologicalYears` | `Pawn_AgeTracker.cs:585–696` |
| The rate is **not 1:1**: `BiologicalTicksPerTick` is the storyteller's child/adult aging multiplier times the gene `BiologicalAgeTickFactor` | `Pawn_AgeTracker.cs:236–269` |
| **Public surface** (candidates, not inventions): `AgeTickMothballed(int interval)` ticks biological age by `interval × BiologicalTicksPerTick`, recalculates growth, checks age-reversal demand and then runs `BirthdayBiological(i)` for **every** birthday crossed. `AgeBiologicalTicks` has a public setter that recalculates growth and life stage but runs **no birthday**. `BirthAbsTicks` has a public setter (forbidden here: it would falsify chronological age) | `Pawn_AgeTracker.cs:486–496`, `:103–115`, `:87–97` |
| Vanilla itself catches a mothballed pawn up on exit from the world pool (`p.TickMothballed(TicksGame % 15000)` in `RemovePawn`), but never a *suspended* one | `WorldPawns.cs:236–253` |
| Letters from `BirthdayBiological` are suppressed unless the pawn is a player-faction pawn | `Pawn_AgeTracker.cs:624` |

**The contract** (mechanism-independent, frozen): whenever a retained, non-ticking pawn can be *observed* (it is
materialized, displayed, or read by Network code), its chronological age equals the elapsed time since birth and its
**biological age has been advanced by the full elapsed time since `agedThroughTick`** through the pawn's own
biological rate, crossing every birthday, **uncapped**. `agedThroughTick` is the only new bookkeeping
([§ 5.3](#53-the-persisted-data-candidate-shapes)); it is set when the pawn becomes `Stored` and after each catch-up.

**Mechanism: a runtime spike question (S12), not a decision.** Two candidates, neither chosen here:

| Candidate | Behaviour | Verdict for now |
|---|---|---|
| **M1:** one `AgeTickMothballed(elapsed)` call (chunked per game-year if `elapsed` exceeds the `int` range, ≈ 596 years) | vanilla's own mothball semantics, **including every birthday's consequences** | the preferred candidate; S12 verifies it on a pawn that is not ticking (no letter, no exception, life stage and growth consistent, hediffs sane, `PostResolveLifeStageChange` handled on the next tick) |
| **M2:** assign `AgeBiologicalTicks` | the *number* is right, the *consequences* (birthday hediffs, work-type unlocks) are skipped | **not acceptable by itself**: aging would be truthful in the digits and untruthful in what aging does. Only a documented fallback, paired with an explicit birthday pass |

**When the catch-up runs** (a scheduling policy; either satisfies the contract):

* **Lazy (recommended for 3.1):** immediately before the first observation after storage. No idle cost, no job.
* **Periodic (hardening, if S12 shows other observers matter, for example a mod that lists world pawns):** one
  once-per-game-year job that exists only while at least one pawn is `Stored`, at most ≈ 150 calls per firing.

In both cases the *elapsed interval is exact*. What a person who has aged past usefulness means for the abstract
record is a **content decision (OPEN O-12)**; Phase 3 only guarantees truth: materialization checks capability
*after* the catch-up, and an unfit person is not placed (the plan aborts `NeverPlaced` with a diagnostic, and the
person is neither deleted nor mutated).

The remaining catch-up rules, unchanged in spirit:

| Catch-up | Rule |
|---|---|
| **age** | **full, uncapped**, as above; chronological age is never touched |
| needs | reset to comfortable; the organization fed them off-map |
| health | already normalized at storage ([§ 10.6](#106-recovery-runs-once)); materialization only checks `woundedUntilTick`. Aging may *add* chronic age-related hediffs: that is the truthful outcome |
| gear | exactly as the pawn left it |
| faction | set to the episode's temporary faction **after** unreserving (the `PassToWorld` faction rule, [§ 13](#13-faction-and-ai-model)) |

The exact APIs are **OPEN (spike S12)**.

### 6.5 Projection inputs (capability → effect)

| Abstract input | Physical effect |
|---|---|
| **the seat's / person's operational role** ([§ 6.6](#66-operational-roles)) | the constraint set that a first projection must satisfy ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)) and a kind preference |
| `ExperienceBand` (capability) of the actor, adjusted by the person's tier and notability | the *floor* of the role-defining skills only; everything else stays vanilla-random |
| equipment tier / condition / specialties | which existing kind or loadout class is requested (O-3). Not an item list |
| mobility | the **arrival mode** (walk-in vs pod/shuttle), not a pawn property |
| doctrine | the `LordJob` chosen where a choice exists; also an input of the derived cohesion band ([§ 6.9](#69-team-cohesion)) |
| team cohesion band | the validation of a first projection against its teammates |
| established statements: name snapshot, composition, granted notable assets | honoured exactly |
| **fame, reputation score, visibility, career, funds** | **nothing physical, ever** (P3-INV-019). They may influence *which* people or organizations the Network offers; they never decide what a pawn can do |
| wounded state | wounded people are not projected (they stay home), except a rescue target, which is seeded explicitly through vanilla damage APIs |
| form / size | how many people, bounded by the purpose |
| derived Tags (`WellEquipped`, `EliteCombat`, …) | descriptive only; no bonuses |

### 6.6 Operational Roles

#### 6.6.1 What a role is, and is not

An **Operational Role** is a *semantic job* (candidates, **names not frozen**): Leader, Marksman, Rifleman, Heavy,
Breacher, Medic, Scout, Engineer, Logistician, Technician, Negotiator, Specialist. It exists to stop a physically
materialized contractor from **contradicting** the identity the Network already established: an abstractly
described crack marksman must not appear with Shooting 1, Melee 19, the Brawler trait or no capacity for violence,
and a legendary medic must not be incapable of doctoring.

It is **not** a character class, a perk tree, a stat bonus or an unlock. It changes nothing the abstract resolver
computes (that still reads `ExperienceBand`, equipment and headcount). It is **distinct from the existing
`CharacterRole`** (Leader, Lieutenant, Specialist, Member, Freelancer, Retired), which is *organizational standing*; a
Medic can be the Leader after a succession. (`Specialist` appears in both lists: a candidate rename is noted, not
decided.)

> **RimWorld's randomness may fill everything the Network has never established. It may not contradict durable
> Network truth.** (P3-INV-018) And a role constrains **only what is necessary**.

#### 6.6.2 What persists

The role (one byte, `KnownCharacter.opRole`) is established when a person is created or crystallized and is
**durable semantic truth**. An episode slot carries `seatRole`. **No skill sheet, passion list or trait list is
persisted for a person who has never materialized** (N13). After the first materialization the **pawn is the detailed
truth**: the role is never re-projected, and a later change in the pawn's skills does not revoke it.

#### 6.6.3 Illustrative constraints (tuning, not decisions)

Constraints are expressed in vanilla **capabilities** (work tags, skill defs), never in mod content. "Floor" is a
function of the person's *capability band* (§ 6.5).

| Role | Must hold (hard) | Role-defining skill floor | Preferred (soft) | Left to vanilla |
|---|---|---|---|---|
| Leader | not barred from Social work; if the organization is combat-oriented, not barred from violence | Social | Social passion | everything else |
| Marksman | capable of violence **and** of Shooting; no trait that bars its weapon class (vanilla's **Brawler** refuses ranged weapons: `FloatMenuOptionProvider_Equip.cs:55`, and raises `Alert_BrawlerHasRangedWeapon`) | Shooting (a high floor) | Shooting passion; a ranged kind | Melee, everything unrelated |
| Rifleman | capable of violence and Shooting | Shooting (a lower floor) | a ranged kind | the rest |
| Heavy | capable of violence | Shooting **or** Melee | heavy weapon / armor utility (kind selection) | Social, Cooking, … |
| Breacher | capable of violence | Melee **or** Shooting | melee / armor utility | the rest |
| Medic | able to doctor: `Medicine` not totally disabled, not barred from Caring | Medicine | Medicine passion | combat skills |
| Scout | able to move | none required | a light kit | the rest |
| Engineer / Technician | not barred from the specialty's work (crafting, construction, intellectual) | the specialty skill | | the rest |
| Logistician | not barred from hauling work | none required | | the rest |
| Negotiator | not barred from Social work | Social | | the rest |
| Specialist | whatever the actor's named specialty requires | per specialty | | the rest |

Illustrative capability-band → skill-floor shape (**example only, OPEN O-8**): Green none · Experienced 4 · Seasoned
7 · Veteran 10 · Elite 13 · Legendary 16. Only the role-defining skills are floored; a Heavy needs no Social at all.

#### 6.6.4 Why this is not a class system

No role grants anything. It is consulted exactly once per person, at first creation, to reject or minimally correct
a candidate ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)); it is read for no other
purpose except choosing a mission composition ([§ 6.7](#67-organization-and-mission-composition)).

### 6.7 Organization and mission composition

A company is **not** "spawn twenty random people". RimWorld already uses composition-like ideas (raid groups,
trader caravans, visitors); the Network adopts the *idea* without depending on those generators.

#### 6.7.1 Organization composition: what the organization broadly contains

A small, persisted **role template** (≤ 8 `(role, count)` entries at full strength), *established at first use* by a
pure, deterministic generator from the actor's own durable facts (seed, form, specialties, doctrine, experience) and
then **never regenerated**: once the Network has said or used "a crew of five with a medic", that is established
truth. Examples:

```
 5-person crew:                   Leader 1 · Rifleman 2 · Medic 1 · Heavy 1
 8-person technical salvage team: Leader 1 · Security 2 · Technician 2 · Medic 1 · Logistics 2
```

It is **not** a roster: no anonymous person is stored. Existing actors have no template; it is created lazily at
first use, so old saves migrate by doing nothing (the field is absent ⇒ "not yet established").

#### 6.7.2 Pinned seats and apportionment

* A **concretized** seat is a living `KnownCharacter` with that operational role (§ 4.5.1). It **pins** a seat of the
  template: a named Medic *is* the organization's Medic.
* At a given headcount the seats still **abstract** are apportioned from the template by a pure, deterministic rule
  (largest remainder; the leader seat fixed at 1). Abstract casualties change the headcount, and the apportionment
  follows; the abstract resolver is **not** made role-aware.
* When a pinned person dies, the existing fate path removes them; the seat is simply abstract again (the organization
  may later have *another* medic: the old one is dead, so nothing is contradicted).

**OPEN O-9 (refinement, only if the soak shows a visible contradiction):** an *observed* physical loss of an
anonymous Rifleman is invisible to the template, so a later projection might show one fewer Medic instead. The cheap
fix, if needed, is a per-role `vacant` count written by reconciliation. It is deliberately not designed in more
detail until it is shown to matter.

#### 6.7.3 Mission composition: the subset this purpose needs

Per episode, derived from the **purpose** (a rule table, bounded to ≤ 8 people):

| Purpose | Needs (illustrative) |
|---|---|
| Procurement visit / scouting | Scout, Logistician, Security (Rifleman/Heavy), maybe Medic |
| Rescue | Combat roles (Marksman, Rifleman, Heavy), Medic, Breacher |
| Civilian / representative visit | Leader or Negotiator, Security, maybe a Specialist |
| Delivery handoff (3.3) | Negotiator/representative, Security; carriers stay abstract ([§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)) |

Selection: **(1)** concretized seats pinned to a needed role that are available (`CanSimulateAbstractly`, Active, not
wounded, not on an operation); **(2)** the shortfall from abstract seats of the template; **(3)** a still-unfilled
need makes the mission smaller or refuses it with a diagnostic: a **wrong-role pawn never fills a need**. No tactical
AI: vanilla `Lord`/`Duty` own behavior.

### 6.8 Role-constrained creation: validate, then the smallest correction

**What vanilla can and cannot guarantee** (decompiled 1.6.9676.17735):

| Fact | Evidence |
|---|---|
| `PawnGenerationRequest` carries real constraint fields: `MustBeCapableOfViolence`, `ForcedTraits`, `ProhibitedTraits`, `ValidatorPreGear`, `ValidatorPostGear`, `FixedIdeo`, `ForcedXenotype` / `AllowedXenotypes`, `BiologicalAgeRange`, `FixedBiologicalAge`, `OnlyUseForcedBackstories`, `ForceNoBackstory`, `CanGeneratePawnRelations`, `ForceNoGear` | `PawnGenerationRequest.cs:31–149` |
| Vanilla retries **up to 120 times**; after 70 it ignores scenario requirements and **after 100 it ignores the validators** (each with a `Log.Error`); after 120 it returns `null` | `PawnGenerator.cs:687–722` |
| `MustBeCapableOfViolence` is enforced by discarding the candidate; the validators run before gear (`:1009`) and after gear (`:1023`) | `PawnGenerator.cs:967`, `:1009`, `:1023` |
| **A validator is therefore an optimization, never a guarantee.** The returned pawn must be re-verified | the 100-try fall-through above |
| Skills come from backstory gains, trait gains, age curves and the kind's own `skills` ranges (a rolled value outside a kind's range is re-rolled into it): the same clamp a role floor imitates | `PawnGenerator.cs:1957–2015` |
| **`SkillRecord.Level` is asymmetric**: the getter returns `GetLevel()` (base **plus aptitudes**, `:334–346`), the setter writes the *base* `levelInt` clamped to 0–20 (`:56–66`). A correction must target the effective level through the base: `Level = floor − Aptitude` | `SkillRecord.cs` |
| An incapability is a *consequence* of a backstory/trait/gene (`TotallyDisabled`, `Pawn.WorkTagIsDisabled`). Changing it means changing the person's backstory or traits, i.e. their **identity**. The story setters do not even refresh the caches (`Pawn_StoryTracker.Adulthood` clears only `backstoriesCache`; `Pawn.Notify_DisabledWorkTypesChanged` must be called) | `Pawn_StoryTracker.cs:47–70`, `Pawn.cs:4513`, `TraitSet.cs:258,325` |
| Backstory selection already honours a kind's `requiredWorkTags` and slot compatibility | `PawnBioAndNameGenerator.cs:112–115`, `:180–186` |
| A kind's weapon comes from its own `weaponTags` and `weaponMoney`; no weapon if violence is disabled; no ranged weapon if Shooting is disabled | `PawnWeaponGenerator.cs:58`, `:66` |
| `Pawn.kindDef` is saved **by def name**, so a runtime-created `PawnKindDef` would not survive a save: roles must map to **existing** kinds | `Pawn.cs:4571` (`Scribe_Defs.Look(ref kindDef, "kindDef")`) |
| `DiscardGeneratedPawn` (vanilla's own disposal of a rejected candidate) is **private**; a candidate the Network rejects after `GeneratePawn` returned is an unbound, unspawned, unreferenced object | `PawnGenerator.cs:1109` |

**The procedure** (it runs only inside the real `PhysicalWorldPort` adapter; the safe tier tests the *pure* verdict
and correction functions over fake pawns):

```
Project(person):                               // 3.1+; pure helpers in 3.0
  spec = RoleSpec(person.opRole, capabilityBand(person))                      // § 6.6, pure
  req  = base request (§ 6.2 / 6.3) plus, from the spec:
           MustBeCapableOfViolence, ProhibitedTraits, FixedIdeo / age range (cohesion, § 6.9),
           ValidatorPreGear = c => spec.Holds(c) && cohesion.Holds(c, teammates)     // optimisation: vanilla discards for us
  repeat up to K times (K is tuning, small):
      c = PawnGenerator.GeneratePawn(req)                                     // vanilla's own <=120 tries; validators dropped after 100
      v = Verify(c, spec, teammates)                                          // AUTHORITATIVE, reads only, c is unbound and unspawned
      if v.Holds                                : return c
      if v.OnlyRoleSkillsBelowFloor             : Correct(c); if Verify(c).Holds: return c      // the smallest correction
      Dispose(c)                                                              // unbound, unspawned, unreferenced: dropped (S23: residue check)
  abort the placement: member NeverPlaced, nothing persisted, one log line (role, kind, failed clause)
```

**`Correct` is the smallest legal correction, and it is deliberately tiny:** it may **raise** a role-defining skill's
base level so the *effective* level reaches the floor (and may set a minor passion the role prefers), because skills
are numbers inside vanilla's own generation noise, exactly what a kind's `skills` range does natively. It may **not**
lower anything, and it may **never** touch a backstory, trait, gene, hediff, age, gender, name or **incapability**: a
candidate that is incapable of the role's work is *rejected*, never "fixed". Because the candidate is unbound and
unspawned, nobody has observed it; the correction falsifies no history.

**The cost is bounded** (K outer attempts, each at most vanilla's own 120) and is **S25's** to measure on a heavily
modded list; if it spikes, a group's creation is spread across ticks (one pawn per tick). The role → existing-kind
mapping is O-3. A race with no skills or no tool use cannot satisfy a skill-bearing role; kind selection filters to
humanlike tool-users ([§ 19](#19-mod-compatibility)).

### 6.9 Team cohesion

The risk: the Network materializes an established, professional team, and vanilla-generated traits, backstories or
ideology make its members dislike each other, so the player arrives to a "veteran team" beating its own leader. That
is wonderful **after organic physical history causes it**; it must not be an accidental contradiction on first
generation. Cohesion is a *lightweight projection constraint*, **not** a social simulator, not mind control, and it
never forbids a future fight.

#### 6.9.1 The band (derived, never stored)

Semantics (names and tuning not frozen): **Dysfunctional · Loose · Professional · TightKnit · VeteranBonded**. The
band is a *pure function of durable state* (doctrine professionalism and discipline, the experience band, jobs
resolved, organization size), computed on demand like `CareerNeed` and the Tags and **not persisted**, so it cannot
drift and needs no migration. It uses only stable inputs (not current morale).

#### 6.9.2 What can be done at first generation (verified)

| Fact | Evidence |
|---|---|
| With `CanGeneratePawnRelations = false`, `GeneratePawnRelations` is skipped: **no generated family, ex or rival relations** among them or with colonists | `PawnGenerator.cs:824` |
| Opinion is `OpinionOf(other)` = relation offsets + `thoughts.TotalOpinionOffset(other)`, scaled by hediffs and clamped to −100…100 | `Pawn_RelationsTracker.cs:592–629` |
| Social fights are **runtime** events after an insult: `SocialFightChance` = interaction base × capacities × hediffs × an opinion factor (4× at −100 down to 1× at 0) × trait `socialFightChanceFactor` × age-gap factor × gene factors | `Pawn_InteractionsTracker.cs:355–371`, `:434–486` |
| Levers available at generation: `ProhibitedTraits`, `ForcedTraits`, `FixedIdeo`, `ForcedXenotype` / `AllowedXenotypes`, `BiologicalAgeRange`, the validators (capturing the teammates) | `PawnGenerationRequest.cs` |

#### 6.9.3 The rule

* **Prevention by construction:** relations off; a new member is generated with the **ideology of the organization's
  existing real members** (`FixedIdeo`, Ideology only), since a per-episode faction would otherwise give each visit
  a different ideology.
* **Screening:** the first projection of a member is checked, as a pure read, against its teammates **already real
  or generated in the same batch**: mutual `OpinionOf` must clear the band's floor (illustrative: Loose rejects open
  hatred, Professional rejects clear dislike, TightKnit and VeteranBonded reject any meaningful negative and any
  known friction-trait pairing). A failing candidate is rejected exactly like a role failure ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)).
* **Initial consistency only (P3-INV-021).** Once a pawn is bound, **no Network code writes its relations, opinions,
  thoughts, memories or traits**. If the Heavy later insults the leader and punches his head off, *that happened*:
  reconciliation records it through the ordinary death path and the history remembers it.

**OPEN S26:** whether `OpinionOf` is meaningful for an *unspawned candidate* (its situational social thoughts may
need a map), how often generated crews would be hostile in practice, and the friction-trait list. **Fallback:**
trait-level screening through `ProhibitedTraits` for the high bands. If neither is feasible, cohesion is documented
as **best-effort** and the owner decides before 3.2 ships groups. The Network never "fixes" cohesion by injecting
positive memories or relations: that would be mind control.

### 6.10 Professional reputation, fame and capability

Three concepts are **correlated but not equivalent**, and the design must represent them separately:

| Concept | Meaning | Grows from | Used for | Never used for |
|---|---|---|---|---|
| **Professional reputation** | what the work market thinks of your *track record* | completed work, reliability, outcomes (and, later, per region) | willingness, trust, pricing, eligibility, access to suppliers and kit | what a pawn can physically do |
| **Fame / visibility / notoriety** | how widely you are *known* | status, incidents, notoriety, a deliberate profile (it can be high without competence, or low by choice) | narrative, who knows you exist (Knowledge, intel, rivals' awareness), how attractive a target you are | capability, equipment access |
| **Capability** | what you can actually *do* | experience, skill, equipment, mobility, the person's role | resolution, **projection** | public standing |

**Implemented truth today (and only this exists).** `PublicReputation` holds one numeric `score` and a **derived**
`FameBand` (Unknown, Local, Established, Famous, Legendary at 100 / 300 / 800 / 2000). The score is earned *only from
completed work* (`CareerPolicy.ReputationGain`, with an anti-farming taper), so it is, in substance, a
**professional-record score**; yet it is named, shown and used as "fame": narrative descriptors, the
`LegendaryReputation` tag, a template's `startingFame`, and **equipment advancement**, which is gated on the band
(`CareerPolicy.RequiredFame` / `TierSupportedByFame`, CareerNeed `Equipment`). *Capability* is already separate
(`ExperienceBand`; `skill` never reads reputation; `EliteCombat` reads experience and kit, not fame). Nothing in
Phase 3 changes this code.

**Where the terminology conflates them** (corrected only in wording, in
[CAREERS § 2](CAREERS.md) and [DATA_MODEL](DATA_MODEL.md)): the class name `PublicReputation` holding "fame"; the
`FameBand` comment "Public reputation tier" (`Bands.cs:26`); CAREERS § 1 ("Public reputation … `FameBand` derived")
and § 6 ("its fame meets the rung"); `RequiredFame` documented as "the reputation needed to be sold or lent the next
tier": an *access* rule written in fame. None is wrong in isolation, but **one number carries two meanings.**

**Intended future separation (design intent; not implemented; no code, no save change):**

| Example (owner) | What the future model says | What today's model can / cannot do |
|---|---|---|
| **A. Low-profile veteran:** extreme capability, excellent record, deliberately low visibility | capability high; professional reputation high; **visibility low** | state is representable (Unknown fame, Legendary experience) but **cannot advance equipment**, because the rung needs the fame band; "excellent record, low visibility" needs two numbers |
| **B. Glitterworld adventure kid:** high visibility from status, expensive kit, mediocre skill, **one extremely competent bodyguard** | visibility high; capability of the *principal* low; a **named bodyguard** with a high-grade role seat | `startingFame` + a low experience band covers the principal; the **bodyguard needs per-person capability** (a role-seat grade: OPEN O-8), which one `ExperienceBand` per actor cannot express |
| **C. Unknown professional:** excellent capability, low *market* reputation because new to the region | capability high; reputation low **in this region** | Unknown fame + high experience is representable, but "new to this region" needs **regional** reputation scope |

**Binding rules for Phase 3 now:** physical projection **never reads fame, reputation score or visibility** (P3-INV-019;
test RT-PHYS-022); the equipment-tier → kind mapping uses tier, condition and specialties only; and the *access*
meaning of the equipment rung is a Phase 4 concern, so Phase 3 leaves `CareerPolicy` untouched.

**Migration and design implications for the later focused phase** (candidate home: a prerequisite pass before
compensation and the contract board): (1) add a separate **visibility** value, initialized for every existing actor
from its current band so nothing visibly changes on upgrade, then allowed to diverge; (2) treat the existing score
as **professional reputation** and keep `RequiredFame` reading it at first (behaviour unchanged), then let kit access
depend on reputation, need and funds, not on visibility; (3) scope reputation by region; (4) the persisted XML labels
`fame` and `score` mean a C# rename is free but a label change needs a migration; (5) decide whether the
`LegendaryReputation` tag follows reputation or visibility; (6) string keys (`TheNetwork_Fame_*`). None of this is
built here.

---

## 7. Physical provenance

### 7.1 What every physical pawn must be able to answer

| Question | Answered by |
|---|---|
| Which Network actor produced this? | `episode.actor` |
| Which person is this? | `member.character` (named) or `member.slot` + `tier` (anonymous) |
| Which episode owns it? | the member entry, the `TheNetwork.Ep.<id>` tag, and the pointer |
| Which contract/operation/opportunity caused it? | `episode.cause` |
| Network-authoritative or physically-authoritative? | `character.custody` and `episode.state` |
| Has reconciliation happened? | `episode.consequencesApplied` |

### 7.2 Mechanisms audited

| Mechanism | Verdict | Evidence |
|---|---|---|
| **Network-persisted binding** (`PawnRef`: pointer + `thingIDNumber`, on the member and the character) | **Adopt. This is the authority.** | World-level objects already hold pawn references vanilla-persisted by `Reference` (`KidnappedPawnsTracker`, `Lord.ownedPawns`, `WorldPawns.pawnsForcefullyKept…`); a pawn has exactly one *deep* owner (map, holder, `WorldPawns`, corpse), so a reference never keeps it alive or duplicates it |
| **Quest tags on the pawn** (`TheNetwork.Ep.<id>`, `TheNetwork.Char.<id>`) | **Adopt as a routing aid only.** Never proof. | `Thing.questTags` is persisted (`Thing.cs:41,1291`); vanilla sends `<tag>.<Signal>` for `Spawned`, `Despawned`, `Destroyed`, **`Killed`**, `TookDamage(FromPlayer)`, `ChangedFaction*`, `LeftMap`, `Kidnapped`, `Arrested`, `Rescued`, `Released`, `Recruited`, `Enslaved`, `Banished`, `ChangedHostFaction` (`SendQuestTargetSignals`). Tags are inert strings: nothing parses them; no Def; no error on mod removal |
| **Registry reservation** (a hidden quest whose part returns true from `QuestPartReserves(Pawn)`) | **Adopt for retained pawns that would otherwise be `Free`. Gated by spike S9r.** | `WorldPawns.GetSituation → ReservedByQuest` makes the pawn non-`Free` (not redress-eligible), `Pawn.Suspended` true, GC-kept (`QuestUtility.cs:493`, `WorldPawns.cs:293`, `WorldPawnGC.cs:174–247`) |
| `Hediff` marker | Reject | needs a Network `HediffDef`; visible in the health tab; on removal vanilla logs "had a null def" and drops it per pawn (`HediffSet.cs:263`) |
| `ThingComp` on pawns | Reject | needs `<comps>` added to *every* race `ThingDef` (modded races, load order); comps are instantiated from `def.comps` (`ThingWithComps.cs:193`) |
| `Pawn.relations` custom relation | Reject | needs a Def; changes relation UI and the GC's keep-set |
| `WorldPawns.ForcefullyKeptPawns` alone | **Insufficient** | a `KeepForever` pawn is still `Free`: `GetSituation` never consults it (`WorldPawns.cs:267`) |
| A runtime-only map `thingIDNumber → pawn` | Use as a cache only | there is **no vanilla "pawn by ID" lookup** outside the load-time directory (`LoadedObjectDirectory`); the cache is rebuilt from the stores |
| Name / faction / label matching, "find a similar pawn" | **Forbidden** | not evidence |

### 7.3 The mechanism, precisely

1. **Binding is written before the pawn is spawned, and only after the first-projection checks.**
   `generate → verify role and cohesion (§ 6.8–6.9) → bind (member.state = Created) → spawn → Present`. A candidate that fails
   verification is never bound.
   Because the binding precedes the spawn, a throw during the spawn cannot leave a *spawned* pawn that nothing records
   ([§ 17](#17-failure-recovery)); an unspawned, unreferenced candidate is not saved at all.
2. **`PawnRef` is saved with `saveDestroyedThings: true`.** By default `Scribe_References.Look` writes `null` for a
   *destroyed* thing (`Scribe_References.cs`), and a dead pawn is `Destroyed` (`Pawn.Destroy(KillFinalize)`), so a
   default reference to a dead contractor would silently turn into "nobody" at the next save. (`WorldPawns` itself
   saves `pawnsDead` with the same flag.)
3. **A pointer that cannot be resolved at load** produces a vanilla *warning*
   (`LoadedObjectDirectory.cs:130`) and a `null`. `null` plus evidence that the pawn once existed is **evidence of
   loss**, never of "returned".
4. **Tags are applied at creation and re-applied after load if missing; a tag on a pawn that no binding claims is
   stripped.** Signal handlers compare the signal's `SUBJECT` pawn with the bindings by **reference equality**
   (P3-INV-012). A copy another mod made that carries our tag is ignored. (Vanilla copies tags in one place we found:
   `QuestPart_ReplaceLostLeaderReferences`; `Pawn_DuplicateTracker` copies none — verify at runtime, **S17**.)
5. **Runtime index** `thingIDNumber → (episode, member)` and `CharacterId → member`, rebuilt from the stores at
   `FinalizeInit`; O(1) routing for signals.
6. **Rejected-by-construction hazards:** the Network never stores a pawn in its own `ThingOwner` (it would leave
   vanilla's pawn systems and vanish with the mod, ADR-014); it never matches by name.

### 7.4 The registry reservation (retained pawns only)

Phase 0's design stands, with three audit corrections and one open comparison.

- **What it protects.** Only a retained pawn that is *neither spawned nor held by a vanilla system*: i.e. a
  character with `custody = Stored`. While a pawn is spawned, in a caravan or a pod, a prisoner, kidnapped, or a
  faction leader, vanilla already keeps it (`WorldPawnGC.GetCriticalPawnReason`: `Spawned`, `CaravanMember`,
  `TransportPod`, `Kidnapped`, `Colonist`, …). So the reserved set is **small**: the stored named people.
- **Why `Free` is unacceptable (quantified).** A `Free` world pawn is a candidate for *any* generation request
  whose faction matches (or that sets `WorldPawnFactionDoesntMatter`; vanilla's
  `PrisonerWillingToJoinQuestUtility` does), with a per-generation chance up to 0.8. A `Free` retained contractor
  can be **redressed into a raid, a visitor or a "prisoner willing to join"**: `RedressPawn` then *mutates* it
  (apparel, hediffs by chance, `removeOnRedress` genes). Null-faction pawns are not safe either: drifters, for
  example, are requested with `faction: null` (`GenStep_Monolith.cs:84`, `GenStep_ScatterCaveDebris.cs:124`).
- **Correction 1, order of operations.** `Pawn.Notify_PassedToWorld` reassigns the faction of a `Free` humanlike
  pawn whose faction is null (or the player's, or Ancients') to a **random** non-colony faction
  (`Pawn.cs:1851–1882`). A pawn that is `ReservedByQuest` at the moment it is passed is not `Free`, so it is
  untouched. **Reserve first, then pass to the world.**
- **Correction 2, cost.** `Pawn.Suspended` evaluates `GetSituation` which evaluates `IsReservedByAnyQuest(pawn)`:
  every active quest × every part × a `List<Pawn>.Contains`. That runs for each non-mothballed world pawn each tick.
  The reserved list length *R* therefore multiplies a vanilla per-tick cost; keep *R* small (the stored named people
  only, soft cap ≈ 150) and **measure** it in the Phase 3 soak ([§ 18](#18-performance)).
- **Correction 3, hooks.** `QuestManager.Notify_PawnKilled` reaches only `Ongoing` quests, `Notify_PawnDiscarded`
  reaches all of them (`QuestManager.cs:163,239`). A Network-owned quest part gets both for free, and vanilla then
  also *drops* the part with an error if the mod is removed, which releases the pawns (self-healing, accepted).
- **Open comparison (S9r).** Vanilla has its own `QuestPart_ReservePawns` (public `List<Pawn> pawns`,
  `QuestPartReserves → pawns.Contains`). A registry built only of vanilla classes and a vanilla `QuestScriptDef`
  root has **no Network type in the save** (no removal error) but also **no self-healing**: after an *unprepared*
  removal the pawns would stay reserved and suspended forever. The Network-owned part fails safe on removal; the
  vanilla part fails safe only through *Prepare for removal*. The spike measures both; the default stays the
  Network-owned part.
- **Fallback ladder if S9r fails.** F1: `PassToWorld(KeepForever)` plus a **non-null faction that no vanilla
  generator requests** (redress needs `pawn.Faction == request.Faction`), accepting the small
  `WorldPawnFactionDoesntMatter` surface and detecting a hijack at reconciliation (a stored person who is suddenly
  a colonist or prisoner is observed as `OutOfCustody`); F2: the documented, **unimplemented** contingency patch C-1
  ([RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)).
  Neither is built in this design pass.

---

## 8. Lifecycle state machine

Two small machines and one derived rule. The design deliberately keeps the *durable* machine tiny: the reconciliation
commit is a synchronous, **all-or-nothing** step and every post-commit stage is idempotent and carries its own marker, so
"Reconciling" never needs to be a persisted state.

### 8.1 The Episode machine (durable)

```
                 plan ok                      ≥ 1 pawn placed
   (none) ──────────────► PLANNED ───────────────────────────► OPEN ──────────────┐
                              │                                  │                 │ every member has a
                              │ nothing placed / map gone /      │ unrecoverable   │ terminal observed
                              │ generation failed                │ inconsistency   │ outcome
                              ▼                                  ▼                 ▼
                       CLOSED(NeverPlaced)                  QUARANTINED         CLOSED(Reconciled)
                       custody reverted, nothing            blocks its members   consequencesApplied = true
                       physical ever existed                from abstraction;    (set last, in the commit)
                                                            diagnosed, retried   │
                                                                                 ▼ publish events
```

| State | Meaning | Authority of its members | Exits |
|---|---|---|---|
| **Planned** | members chosen; named members' custody is already `Deployed`; pawns are being created and bound | physical (reserved for the episode) | → Open when ≥ 1 member is `Present`; → Closed(NeverPlaced) if none is |
| **Open** | at least one pawn exists in play | physical | → Closed(Reconciled) when every member is `Done`; → Quarantined |
| **Closed** | terminal. `consequencesApplied` is true (Reconciled) or nothing physical ever happened (NeverPlaced/Detached) | abstract, held or dead per member outcome | none |
| **Quarantined** | the Network cannot reconcile safely (an invariant broke, an unsupported custody was observed, retries exhausted) | **blocked from abstraction**; pawns untouched | → Closed by a later successful reconcile; never auto-resolved to "returned" |

`Closed(Reconciled)` is reached by the atomic durable commit ([§ 15](#15-reconciliation-algorithm)). What remains
afterwards is **not** a set of extra persisted states: each post-commit stage carries its own marker, and a *finish-pending
pass* (at load and from the episode watch) resumes whichever stage is unfinished, exactly once per stage:

| Post-commit stage | Marker that says it is done | If interrupted |
|---|---|---|
| **RELEASE** (vanilla side: strip tags, pass pawns to the world, normalize named pawns, scheduler clean-up) | no member carries the episode tag; scheduler/derived data repaired by the validator | the pass finds a `Closed` episode whose members still carry its tag and finishes the release |
| **FOLLOW-UP** (the linked operation's own resolution, through its existing guarded entry points) | the operation has left `OpStatus.Physical` | the pass finds a `Physical` operation whose episode is `Closed(Reconciled)` and runs the entry point, which is guarded by the operation's existing flags |
| **PUBLISH** (events, history, letters) | `publishedTick` is set | republish the deterministic, keyed events; **never** reapply a consequence |

### 8.2 The character custody machine (durable; reuses the persisted `CustodyState`)

The enum already exists and every saved value is `0`. Giving the values their Phase 3 meaning is free.

| `CustodyState` | Authority | Pawn | Meaning |
|---|---|---|---|
| `Unmaterialized` (0) | Abstract | none | never materialized, or released to vanilla (`neverRematerialize`) |
| `Stored` (1) | Abstract | **retained**, registry-reserved, suspended | a bound pawn waiting between episodes |
| `Deployed` (2) | **Physical** | in play | a member of a Planned/Open episode |
| `OutOfCustody` (3) | **Vanilla-held** | held by vanilla; `heldBy` says how | prisoner/slave/colonist/guest of the player, kidnapped, in a player caravan or pod, held by another faction; observed by the custody watch |
| `Released` (4) | none | the pawn (or corpse) belongs to vanilla | dead, or deliberately released; terminal |
| `Lost` (5) | none (diagnosed) | binding unresolvable | the pawn is gone with no evidence; never "home", never regenerated |

```
 Unmaterialized ──Materialize(first)──► Deployed ──Reconcile: Returned──► Stored ──Materialize(again)──► Deployed
        ▲                                  │ │ │
        │                                  │ │ └─ Reconcile: Killed ─────────► Released   (status = Dead, terminal)
        │                                  │ └─── Reconcile: Held / Joined ──► OutOfCustody ──custody watch──┐
        │                                  │                                        │  released / escaped /    │
        │                                  │                                        │  rescued back            ▼
        │                                  │                                        └────────────────────► (new episode,
        │                                  └─── Reconcile: Missing / Lost ──► Lost                        or Stored directly
        └─ never placed (Planned aborted): custody reverts                                              when the pawn is a
                                                                                                          world pawn again)
```

`CharacterStatus` (Active, Wounded, Captured, Missing, Dead, Defected, Lost, …) stays the *story* status and is
written **only** by reconciliation (and by the existing abstract resolver while the person is abstract). The two
fields move together at reconcile: `Killed ⇒ status Dead + custody Released`; `HeldByPlayer ⇒ status Captured +
custody OutOfCustody(heldBy=PlayerPrisoner)`; `JoinedPlayer ⇒ status Defected + custody OutOfCustody(PlayerColonist)`;
`Kidnapped ⇒ status Captured + custody OutOfCustody(Kidnapped)`; `Missing ⇒ status Missing + custody Lost`.

### 8.3 What runs, per authority

| Question | Abstract (`Unmaterialized`, `Stored`) | Physical (`Deployed`) | Vanilla-held (`OutOfCustody`) | Dead / Released / Lost |
|---|---|---|---|---|
| Abstract upkeep runs on the person? | yes | **no** (the actor's own upkeep continues for funds, morale, … ) | **no** | no |
| May take or be assigned a job? | yes | **no** | **no** | no |
| `SpatialState` moves them? | yes | **no** (frozen; reconcile writes the anchor once) | **no** | no |
| Career advancement? | yes (existing rules) | **no** (an open episode counts as a job) | no | no |
| Wound recovery simulation? | yes | **no** (the real pawn heals or not) | no | no |
| Procurement may select them? | yes | **no** | **no** | no |
| May be re-materialized? | yes (if healthy, off any job) | **no** (already physical: P3-INV-001) | **no** until released | **no, ever** (dead) / no (Lost, until diagnosed) |
| Is reconciliation legal? | n/a | **yes, only on a positive terminal observation** | observation only; a custody *event* may start a new episode | n/a |

For an organization the table applies **per person**: the org's other members and its main body stay abstract.

---

## 9. Custody model

### 9.1 The rule

> A pawn that is **not currently spawned is not therefore gone.** It may be a prisoner on another map, a captive
> in a faction's kidnapped list, a member of a caravan, a passenger in a pod, held in a container, a world pawn
> with continuing relevance, or a corpse. Authority returns to the abstract layer only on a *positive* observation
> that the pawn is a free, living world pawn that left through a legal exit.

### 9.2 How RimWorld 1.6 represents each state (verified)

| State | Representation | Where the Network reads it |
|---|---|---|
| Spawned on a map | `pawn.Spawned`, `pawn.Map` | `Pawn.cs` |
| Downed | `pawn.health.Downed` (`healthState == Down`); **no signal** | poll |
| Carried / in a bed / in a container | `ParentHolder`; `SpawnedOrAnyParentSpawned` | `Thing.ParentHolder`, `Pawn.InContainerEnclosed` |
| Prisoner / slave / guest of a host | `pawn.guest` (`Pawn_GuestTracker`: `GuestStatus` Guest/Prisoner/Slave, `HostFaction`); `IsPrisoner`, `IsPrisonerOfColony`, `IsSlave`, `IsSlaveOfColony` | `Pawn.cs:612–648`; saved *with the pawn* |
| Recruited / colonist | `pawn.Faction == Faction.OfPlayer`; `EverBeenColonistOrTameAnimal` | records |
| Kidnapped | `Faction.kidnapped.KidnappedPawnsListForReading` (saved by `Reference`); vanilla **recruits a kidnapped pawn into the captor's faction with MTB ≈ 30 days** (`KidnappedPawnsTracker.cs`) | `PawnUtility.IsKidnappedPawn` |
| Caravan member | `pawn.GetCaravan()`; the pawn is in the caravan's `ThingOwner<Pawn>`; `Caravan.Notify_MemberDied` adds a corpse to the caravan | `CaravanUtility` |
| In transit by pod | `ThingOwnerUtility.AnyParentIs<ActiveTransporterInfo / TravellingTransporters>` | `PawnUtility.cs:92` |
| World pawn | `Find.WorldPawns.Contains(pawn)`; `GetSituation`: Free, Dead, FactionLeader, Kidnapped, CaravanMember, ReservedByQuest, … | `WorldPawns.cs:267` |
| Dead | `pawn.Dead`; the pawn is `Destroyed`; with a corpse if it died spawned, in a caravan or in a container, **no corpse** if it died as a world pawn | `Pawn.Kill` (`Pawn.cs:2088`) |
| Discarded | `pawn.Discarded`; references resolve to `null` | `Thing.Discarded` |

### 9.3 Classification

The `PhysicalWorldPort` returns one `ObservedKind` per pawn, evaluated **in this order** (first match wins):

| # | `ObservedKind` | Test |
|---|---|---|
| 1 | `Gone` | pointer null, or `Discarded`, or (`Destroyed` and not `Dead`) |
| 2 | `Dead` | `pawn.Dead` (sub-state: corpse spawned / absent / unknown) |
| 3 | `HeldByPlayer` | player-hosted prisoner or slave |
| 4 | `JoinedPlayer` | faction is the player's and not held |
| 5 | `Kidnapped` | in a faction's kidnapped list |
| 6 | `HeldByOther` | a host faction that is not the player |
| 7 | `InCaravan` / `InTransport` | caravan or travelling transporter (player's or another's) |
| 8 | `Spawned` | `Spawned`, or any parent spawned (carried, in a bed or container): carry the map id; downed or mobile; on the episode map or elsewhere |
| 9 | `WorldFree` | a world pawn, alive, `GetSituation` Free (or None while contained), faction not the player's |
| 10 | `WorldOther` | a world pawn in another situation (leader, for sale, borrowed, …) |
| 11 | `Unknown` | anything else, including a holder this build does not recognise |

`Returned` (authority back to abstract) is permitted **only** for `WorldFree` *and* the episode's exit evidence
(`LeftMap`, or the episode map no longer holding the pawn). `Spawned elsewhere`, `InCaravan`, `Unknown` and
`WorldOther` keep the member `Present` (or hold it), never `Returned`.

### 9.4 Held people: the custody watch

An episode closes **exactly once** with a terminal outcome per member, so a prisoner does not keep an episode open
for years. The *continuing* truth lives on the character: `custody = OutOfCustody`, `heldBy`, `heldSinceTick`.
A **custody watch** (a persisted scheduler job, only while ≥ 1 person is `OutOfCustody`, every 2,500 ticks) plus the
pawn's tag signals observe: released, escaped, recruited, sold, rescued back, died, discarded. A transition does one
of: **reconcile back to abstract** (a world pawn again), **start a new episode** (the person is physical in a Network
context again), or **record death**. An observation the Network cannot classify keeps the person held; it never
abstracts them.

### 9.5 What this never does

It never infers custody from the *absence* of a pawn; it never "rescues" a held pawn by regenerating it; it never
treats a kidnapped contractor as `Missing` merely because the pawn is not on a map; it never returns a person to
`SpatialState` from an unobserved location.

---

## 10. Death and injury

### 10.1 Death is real and monotonic

> **PHYSICAL DEATH IS REAL.** Once an authoritative physical person dies, no abstract path may make them alive,
> available, wounded, healed, captured or missing. (P3-INV-004)

| Actor form | A physical death means |
|---|---|
| **Solo** | the existing death path: `status = Dead`, `diedTick`, `deathCauseKey = "Physical"`, `custody = Released`, and **`EndActor(a, "Died")`**: the contractor ends |
| **Crew member (named)** | `Fate.Killed` for that `CharacterId`: roster and capability change, morale shock, succession if it was the leader; the organization continues |
| **Crew member (anonymous)** | headcount −1 by tier; no record remains |
| **Organization leader** | the existing `RunSuccession` (lieutenant, best living known member, promoted veteran, or dissolution) |

Today `ContractorService.SetStatus` does not itself forbid changing a `Dead` character, because every caller
checks `IsAlive` first. Phase 3.0 turns that convention into a **validator invariant and a test**: no writer may
change the status of a `Dead` character.

### 10.2 Observing death, and the trap in the signal

Normal pawn death **does** send the quest-tag signals `Destroyed` and `Killed`: `Pawn.Kill` ends with
`if (!base.Destroyed) base.Kill(...)`, `Thing.Kill → Destroy(KillFinalize)`, and `Thing.Destroy` sends both
(`Pawn.cs:2088…`, `Thing.cs:1043–1099`). (The Phase 0 document said otherwise; [Appendix E](#appendix-e-what-the-audit-changed-from-the-phase-0-design).)
But the signal fires **in the middle** of `Pawn.Kill`, *before* `Find.QuestManager.Notify_PawnKilled`,
`Find.FactionManager.Notify_PawnKilled`, the ideology and history notifications. Therefore:

- a handler **only enqueues a wake-up** (a runtime-only queue, flushed from a scheduled job at the next tick); it
  never reads final state, mutates vanilla, or reconciles inline;
- the reconciliation that follows **reads `pawn.Dead`**, which is the truth; the signal is not;
- the quest part's `Notify_PawnKilled` (if the pawn is registry-reserved) and the poll are independent second and
  third paths. Any one is enough.

### 10.3 Corpses and bodies

Vanilla owns corpses. The Network records the death and does nothing else: no "return the body", no corpse
tracking. A pawn that dies **as a world pawn leaves no corpse** (`Pawn.Kill`: `holdingOwner != null ||
IsWorldPawn() ⇒ Corpse.PostCorpseDestroy`); a dead world pawn with no remaining reason to be kept can be discarded
by the next GC pass (`WorldPawnGC.GetCriticalPawnReason`: `CorpseExists`, `InPlayLog`, … ). **So a death must be
captured when it happens** (signal, poll or the quest part) and *persisted as Network truth immediately*; it cannot
be "looked up later". A missing body is not an error.

### 10.4 Resurrection (tolerated, never initiated)

`ResurrectionUtility.TryResurrect(Pawn, params)` is public and acts on the **same `Pawn` object** (it requires
`!Discarded`; it rebuilds the pawn's state and re-spawns it if the corpse was spawned) and sends **no signal**
(`ResurrectionUtility.cs:29`). A retained pawn binding is therefore exactly what makes a resurrection *visible*.
Rule: a character with `status = Dead` whose bound pawn is no longer `Dead` is recorded as **"returned from death"**
(history), `custody = OutOfCustody(Unaffiliated)`, and **`status` stays `Dead`**. The person does not rejoin the
organization automatically; what a resurrected contractor means is a *content* decision. Detection is opportunistic
(any Network code that touches the character, plus an optional bounded sweep of dead characters that still hold a
`PawnRef`). **OPEN O-6.**

### 10.5 What health truth survives abstraction

| Truth | Kind | Where it lives | Rule |
|---|---|---|---|
| missing body part, permanent injury, scar, bionic/added part, chronic condition (`HediffUtility.IsPermanent`, `chronic`), addiction, genes, xenotype, skills, traits, age | **identity-defining / long-term** | **the retained pawn** (real) | not mirrored; survives because the pawn survives. Age is kept *truthful* ([§ 6.4](#64-truthful-aging-of-a-retained-pawn)); aging may add chronic conditions and never removes one |
| temporary injury, bleeding, infection/disease, intoxication | **temporary noise** | the pawn while physical | at the end of an episode becomes the existing **abstract recovery**: `woundedUntilTick` (named) or an `AddWounded` bucket (anonymous) |
| death | terminal | `status`, `diedTick` | monotonic (§ 10.1) |
| pregnancy | long-term and awkward | the pawn | a stored pawn is *frozen* (suspended); not normalized. **OPEN O-14** (a long-suspended pregnancy) |

The mapping from observed physical injury to abstract recovery is a **bounded, monotone function** of the pawn's
`health.summaryHealth.SummaryHealthPercent` (after tending), `Downed`, and whether any non-permanent hediff
remains: e.g. ≥ 0.9 ⇒ none; 0.7–0.9 ⇒ ≈ 3 days; 0.4–0.7 ⇒ ≈ 8; < 0.4 ⇒ ≈ 15 (tuning later), always passed through the same
`woundDays` the abstract resolver already uses. **No per-Hediff mirror, no health simulator.**

### 10.6 Recovery runs once

A person is either recovering *abstractly* (stored, with `woundedUntilTick`) or *physically* (a real pawn that
heals under vanilla), never both. **Store-time normalization** (Phase 0 § 4.3, kept): when a named person returns to
`Stored`, their temporary injuries are *converted* (to `woundedUntilTick` first, then healed on the pawn through
vanilla hediff APIs), so the frozen pawn never carries wounds the abstract record would heal a second time and
vanilla does not keep ticking an injured, un-mothballable pawn forever (`WorldPawns.ShouldMothball` refuses a pawn
with a non-permanent hediff, `:365`). A modded hediff that still blocks mothballing is left alone and measured.
**OPEN S12.** Invariant: when a `Stored` person is materialized, `woundedUntilTick ≤ now`, except a rescue target
seeded injured on purpose.

---

## 11. Gear semantics

### 11.1 The rules

| # | Rule |
|---|---|
| G1 | **Projected gear is real.** A pawn's gear is whatever vanilla generation gave it; it is real loot the moment it exists. The Network never constructs a loadout by hand and never marks projected gear as "temporary". |
| G2 | **Authority follows the object.** While physical, the equipment is what the pawn actually holds. A player may strip, loot, trade or gift; vanilla rules apply; the Network has no lock. |
| G3 | **No mirror into the abstract tier.** `EquipmentProfile.tier/specialties` is a *capability*, not an inventory. It is never recomputed from a pawn's items. |
| G4 | **Loss may cost `condition`, bounded.** If a named person's weapon and armor are observed lost (destroyed or looted) at reconciliation, `EquipmentProfile.condition` may fall by a bounded step; the tier does not. **Tuning OPEN (O-3).** |
| G5 | **A retained named pawn keeps everything it carries,** including items the player gave it (legendary, biocoded, royalty-locked): they are real `Thing`s on a real pawn, so no identity bookkeeping is needed for them. |
| G6 | **An anonymous pawn leaves with whatever it carries.** A gift to an anonymous member is gone from the player's perspective. That is why Phase 4's compensation must be *explicit durable records*, not gifts to pawns. |

### 11.2 The future equipment seam (not built): a Lease is not a Notable Asset

The first design pass (and Phase 0) treated the reserved `leases` slot as *the* future exact-equipment seam. That
conflated two durable relationships that are fundamentally different. Generic equipment **stays abstract**: there is
no off-map inventory simulator, and nothing in Phase 3 builds either record.

> **Persist an exact item only when its identity is narratively or contractually important.**

| | **Lease** | **Notable Asset / equipment grant** | **Bulk or generic grant** |
|---|---|---|---|
| Meaning | the player or a faction *lends* an item | a contractor *permanently receives* one specific important item | e.g. five generic armor sets as quality-equivalent compensation |
| Ownership | stays **external** (the lender) | **transfers** to the contractor | transfers, but as a quantity |
| Expectation | return expected; condition tracked; consequences if not returned | none: it is theirs | none |
| Example | a sponsored or loaned artifact | a named legendary pistol given as compensation, rare armor | generic assault rifles, standard armor |
| What is persisted | an explicit lease record (the reserved `leases` slot) | an explicit record **owned by the person/contractor**, *not* in `leases` | a **compact advancement / provenance record** feeding the abstract `EquipmentProfile`; an individual piece that is narratively significant is promoted to a Notable Asset |
| Physical reappearance | while leased, if the borrower is physical | whenever its owner is physical and still retained | never as items: the abstract tier is simply higher |
| Phase | 4 | 4 | 4 |

**Rules (added to G1–G6):**

| # | Rule |
|---|---|
| G7 | **Before a person has a pawn, a Notable Asset is a promise; after, the real item is the truth.** For a never-materialized recipient, projection must ensure the new pawn carries a *real* item matching the record: **the one case where the Network creates an item**. Once the person is materialized, the pawn's own inventory is the authority (a retained pawn already carries it, G5) and the record is only *observed* (still carried / lost / transferred) at reconciliation. There is never a second authority over an item. |
| G8 | **A Lease keeps ownership external**, so its record stays authoritative for *who owns it* while the real item is authoritative for *where it is and in what state*; return and loss are reconciled by observation. Designed in Phase 4. |
| G9 | **Nothing generic is persisted as an exact item.** An equipment tier never becomes an inventory, however often it is projected. |
| G10 | **Phase 4 may not be forced to pretend every exact item is a lease.** The two relationships have separate seams. |

**What Phase 3 implements:** the single **loadout-input point** of projection, with exactly one input today, **(a)** the
kind's own generation. The design reserves, for later, **(b)** the person's Notable Assets and **(c)** active leases as
additional inputs at the same point. No record shape for (b) or (c) is fixed here beyond the rule above; the reserved
`leases` slot is *one* of the two seams, not the whole seam
([DATA_MODEL § 11](DATA_MODEL.md#11-deployments-and-equipment-leases)).

### 11.3 Edge answers

| Case | Answer |
|---|---|
| Player kills and loots a contractor | real loot; the death is reconciled |
| Contractor leaves carrying colony property | real; the Network observes nothing; theft consequences are content, deferred |
| Gear destroyed | G4 |
| `PawnKindDef.destroyGearOnDrop` kinds | vanilla behaviour (the gear is destroyed on drop); no Network rule |
| Modded weapons/apparel | vanilla generation; no special case |
| Quality / stuff / biocoding / royalty locks | untouched; they belong to the real items |
| A legendary named pistol granted as compensation (Phase 4) | a **Notable Asset** ([§ 11.2](#112-the-future-equipment-seam-not-built-a-lease-is-not-a-notable-asset)): its identity matters, so it is persisted, rides on the person, and is projected once before a first materialization (G7) |
| Five generic armor sets given as quality-equivalent compensation (Phase 4) | a compact advancement / provenance record feeding the abstract tier, not five items (unless one piece is individually significant) |
| A generic assault rifle implied by the abstract equipment tier | stays abstract: the tier only (G9) |
| A sponsored artifact loaned to a contractor | a **Lease**: ownership stays external (G8) |

---

## 12. Spatial integration

`SpatialState` stays **the one abstract geographic truth**. The physical layer never gets a second one.

### 12.1 Entering the physical world

- **No write to `SpatialState` at materialization.** The person's spatial entry is *frozen*: `SpatialService.Upkeep`
  returns early for an actor whose person is physical (a Solo); for an organization's detachment the main body
  continues, exactly as for a concurrent job's `OperationSpatialPlan.detached`.
- **Materialize where an abstract journey ended.** A content trigger should *bring* the person to the physical
  anchor through the existing machinery (a journey whose destination is the episode tile; the existing `Arrived`
  beat), then materialize. The dev trigger of 3.1 is a **flagged, recorded relocation** (an explicit `Dev` cause),
  not a travel.
- The episode's `where` (tile, map id) is **context** for diagnostics and the long-open warning. It is not the
  actor's location.

### 12.2 Leaving it: reconcile writes the anchor once

At close, per the observed outcome: `Returned ⇒ SpatialState.status = Idle, anchor = the tile of the map the person
left (or the episode tile), lastUpdateTick = now`, via a new `Spatial.OnPhysicalEpisodeClosed(actor, tile)`; dead ⇒
`OnActorEnded`; held ⇒ the spatial entry stays frozen at its last tile (the person is not simulated). Never a
route, never travel time, never invented. The anchor write is a **direct assignment inside the atomic commit**
([§ 15.6](#156-failure-semantics-of-the-commit-service-by-service)); `OnPhysicalEpisodeClosed` must *report or throw*, because the
existing spatial facade contains every fault and returns a default, which would make a failed anchor write invisible.

### 12.3 The exits

| Exit | What happens | What the Network concludes |
|---|---|---|
| Map edge, normal | `Pawn.ExitMap → PassToWorld`, then `LeftMap` signal | observe `WorldFree` ⇒ `Returned`; anchor = the map's tile |
| Map removed | `MapDeiniter` passes non-colonist pawns to the world **without** a `LeftMap` signal (only colonists and player-hosted pawns get one) | the site comp's `PostMyMapRemoved` / the `MapRemoved` signal wake a reconcile; observe each pawn; **a map removal can never silently erase a person** (RT-PHYS-010) |
| Caravan | player caravan containing the pawn (arrest, rescue) | `InCaravan` ⇒ held, **not** returned |
| Transport pod / shuttle | `ThingOwnerUtility.AnyParentIs<ActiveTransporterInfo / TravellingTransporters>` | `InTransport` ⇒ `Present`, never returned |
| Site destroyed | `SitePart.PostDestroy → ClearAndDestroyContentsOrPassToWorld` | pawns become world pawns; observe |
| Kidnapped | `KidnappedPawnsTracker.Kidnap` (sends `Kidnapped`) | held; later recruited by the captor with MTB ≈ 30 days ⇒ `ChangedFaction` signal |
| Teleported / moved by another mod | no signal; `Pawn.teleporting` flag | observe `Spawned` elsewhere ⇒ stays `Present` with the new map id |
| Player takes the pawn to another map | `Spawned` on a different map | stays `Present`; **never** "vanished ⇒ home" |

---

## 13. Faction and AI model

### 13.1 Options considered

| Option | Verdict | Reason (verified) |
|---|---|---|
| One **permanent faction per contractor** | Reject | faction count grows without bound; permanent factions cannot be removed (`FactionManager.Remove` refuses non-temporary) |
| A **shared hidden "Network" faction** | Reject | one relation with the player for all organizations; per-org hostility impossible |
| The contractor's **origin faction** | Reject | hostility leaks to the whole parent faction; breaks when the origin is gone (ADR-006) |
| **No faction (null)** | Reject | `Notify_PassedToWorld` rewrites a `Free` null-faction pawn's faction at random; null-faction pawns match `faction: null` generation requests such as drifters (redress); AI semantics are undefined |
| **Guest of the player** | Reject | changes the player's UI and economy and invites `GuestStatus` handling |
| **One temporary hidden faction per episode** | **Adopt** | vanilla supports it end to end (below) |

### 13.2 Why per-episode, not per-organization (a change from Phase 0)

Phase 0 reused one temporary faction per organization across visits. The audit shows vanilla removes a temporary
faction as soon as *nothing is spawned under it, no caravan holds it, no world object is its own and no quest
reserves it* (`FactionManager.FactionCanBeRemoved`, `:398`; it is queued from `Notify_PawnLeftMap`), and `Remove`
sets **every** pawn of that faction, alive or dead, in maps or world, to **faction null** (`:109–140`). So the
faction **dies with the episode**, naturally, and keeping it alive would require a reservation we do not want. That
is harmless, because membership is the Network's (`KnownCharacter.org`, episode slots), never `pawn.Faction`.
Each episode creates its own; goodwill is seeded from the Network relation and **mirrored back at reconcile** (as
events, not a live feed), so history survives faction recreation.

- *Creation:* `FactionGenerator.NewGeneratedFactionWithRelations(def, relations, hidden: true)`, then
  `faction.temporary = true` (a public field) and `FactionManager.Add`.
- *Def choice:* by capability (humanlike, non-player, not a permanent enemy, tech level near the organization's),
  never by name; if none exists the episode is not created.
- *Hostility mid-episode* (the player attacks them, goodwill falls): **vanilla AI handles combat**; reconcile records
  what happened.
- **OPEN S10:** UI visibility of a hidden temporary faction, whether a faction `leader` is required, and the letter
  behaviour (`canSendHostilityLetter: !temporary` already suppresses hostility letters).

### 13.3 Network relationship ≠ RimWorld faction relation

The Network's `RelationStore` (actor ↔ player: standing, trust, counters) is authoritative and independent. The
faction relation exists only for the episode's AI. A reviewed bridge exists in exactly two places: *seeding* the
temporary faction's goodwill at creation, and *reading* it (plus the observed outcomes) at reconcile to publish
ordinary Network events. No other coupling; no change to world diplomacy for convenience.

### 13.4 AI, Lord and Duty

Vanilla only. The Network never issues jobs. A visit is `LordJob_VisitColony(faction, chillSpot, durationTicks)`
(verified: it has an exit subgraph, a defend-point toil and a wounded-guest toil). A rescue site's pawns are spawned
by the vanilla GenStep. Attack, flee, arrest, recruit and exit are RimWorld doing RimWorld things.

---

## 14. Event detection

### 14.1 The audit

**A** = a vanilla public/protected hook or signal exists · **B** = polling or state reconciliation is safer ·
**C** = a component callback exists · **D** = Harmony would be needed.

| Transition | A (signal / hook) | B (poll) | C (component) | D | Notes (verified) |
|---|---|---|---|---|---|
| spawned | `Spawned` (`Thing.cs:914`) | | `WorldObjectComp.PostMapGenerate` (site) | no | |
| despawned | `Despawned` (`Thing.cs:1032`) | `pawn.Spawned` | | no | also on exit |
| **killed** | `Destroyed` + `Killed` (`Thing.cs:1094,1098`); `QuestPart.Notify_PawnKilled` (reserved pawns, Ongoing quest) | `pawn.Dead` | | no | fires **mid-kill**; wake-up only |
| **downed** | **none** | `pawn.health.Downed` on active members | | no | **a genuine gap: poll** |
| arrested / made prisoner | `Arrested` (`JobDriver_TakeToBed`), `ChangedHostFaction` (`Pawn_GuestTracker.cs:553`) | `IsPrisonerOfColony` | | no | |
| rescued | `Rescued` | state | | no | |
| recruited | `Recruited`, `ChangedFactionToPlayer` | `Faction == OfPlayer` | | no | |
| enslaved / released / banished | `Enslaved`, `Released`, `Banished` | state | | no | |
| kidnapped | `Kidnapped` (`KidnappedPawnsTracker.cs`) | the captor's list | | no | later `ChangedFaction` (MTB recruit) |
| faction changed | `ChangedFaction`, `…ToPlayer`, `…ToNonPlayer` (`Thing.cs:1769`) | | | no | |
| **map exit** | `LeftMap` (`Pawn.cs:2594`) | `WorldPawns.Contains` | | no | **not** sent for non-colonists when a *map is removed* |
| **map removed** | `MapRemoved` on `MapParent` tags (`MapParent.cs:72`) | map lookup | `WorldObjectComp.PostMyMapRemoved` | no | the site comp already exists (Phase 1) |
| caravan transfer | **none** for `Caravan.AddPawn` | `pawn.GetCaravan()` | `WorldObjectComp.PostCaravanFormed` (site) | no | poll while the person is `Present` or held |
| world-pawn transfer | `LeftMap` | `WorldPawns.Contains`, `GetSituation` | | no | |
| site destroyed | `Destroyed` on the site (`WorldObject.cs:510`) | | `PostDestroy` | no | |
| pawn joins/leaves player | `ChangedFactionTo(Non)Player` | | | no | |
| discarded | `QuestPart.Notify_PawnDiscarded` (reserved) | pointer null / `Discarded` | | no | |
| **resurrected** | **none** | `Dead → alive` on a retained dead pawn | | **OPEN** | opportunistic poll ([§ 10.4](#104-resurrection-tolerated-never-initiated)) |
| teleported | **none** (`Pawn.teleporting`) | observe `Spawned` elsewhere | | no | |
| save/load while physical | `ExposeData` / `FinalizeInit` / `AfterLoad` of the world component | | yes | no | [§ 16](#16-saveload-semantics) |

### 14.2 Conclusion on Harmony

**No Harmony is required for the recommended slice or for 3.2.** Every transition has a signal, a component
callback or a bounded poll. The three genuine hook gaps (**downed, caravan join, resurrection**) are polls over a
tiny set (the members of Open episodes plus the `OutOfCustody` people). If a spike later shows a poll is
insufficient (for example a held person is lost between polls), the narrowest documented contingency is **a
postfix on the one method that changes the state, specified in [RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)
format and not implemented here.** Harmony remains forbidden by default.

### 14.3 Handler discipline

1. **Signals are wake-ups.** `SignalBridge` gains two prefix routes (`TheNetwork.Ep.`, `TheNetwork.Char.`). A handler
   resolves the `SUBJECT` pawn against the bindings by reference equality, **enqueues** an episode wake-up, and
   returns. It never reconciles, mutates vanilla state or throws.
2. **Dropped signals are survivable.** `SignalManager` caps at 3,000 signals per frame and drops the rest with one
   error (`SignalManager.cs`). The watch job and the load pass recompute from observed state, so a missed signal only
   delays a result.
3. **Handling is idempotent.** A wake-up for a Closed episode does nothing. The stale-bridge guard of the existing
   `SignalBridge` is kept (only the current runtime's bridge acts).
4. **The poll set is bounded:** members of Open episodes (every 250 ticks) and `OutOfCustody` people (every 2,500
   ticks); nothing else, ever.

---

## 15. Reconciliation algorithm

It must be **idempotent and exactly-once**, in the discipline that made the Phase 2.75 career commit safe, extended for
a commit that crosses several records: *observe → decide → plan → validate → atomic durable commit → flag last → release →
follow-up → publish*, never *flag first, mutate later*, and never *publish inside the transaction*.

### 15.1 Wake-ups (none of them is trusted to say what happened)

A tagged signal (death, left map, map removed, arrested, kidnapped, …) · the Open-episode **watch job** (250 ticks) ·
the site comp's map callbacks · the **load pass** · a purpose-specific end (a visit's duration elapsed) · a dev
action. Every wake-up calls the same `Reconcile(episode)`.

### 15.2 The steps

The shape is the one that made the Phase 2.75 career commit safe ([CareerService.CommitOutcome](../Source/TheNetwork/Domain/Contractors/CareerService.cs):
a **pure plan** that reads state and changes none, a **snapshot of exactly the small durable state it mutates**, a
restore if the commit throws, the flag set only after the mutation really committed, and publication last in its own
guard), extended to a commit that touches several records. The first revision of this section said the commit was "one
synchronous block of primitive assignments (cannot fail half-way)". **That was too optimistic** and is withdrawn: the
existing casualty, succession and actor-ending paths interleave durable mutation with inline publication, scheduler
calls and fault-swallowing facades ([§ 15.6](#156-failure-semantics-of-the-commit-service-by-service)), so "these
assignments cannot throw" is exactly the hand-wave to avoid.

```
Reconcile(episode):                         // main thread; never re-entrant (a runtime guard queues wake-ups that arrive mid-commit)
  0 GATE      if episode.state == Closed: return                               // idempotent: a duplicate wake-up is a no-op
              if runtime.reconciling: enqueue(episode); return                  // no re-entrancy
  1 OBSERVE   for each member without an outcome:                               // pure read: mutates nothing in vanilla or the Network
                obs = port.Observe(member.pawn)                                 // ObservedKind + facts (§ 9.3), via the PhysicalWorldPort
  2 DECIDE    outcome(member) = Terminal(obs, exit evidence) or Pending         // § 15.3
              any Pending: return (stays Open; long-open warning at 30 days, never auto-closed)
              unsupported / contradictory observation: Quarantine and return
  3 PLAN      build an immutable ReconciliationPlan from the observations and a READ-ONLY view of durable state:
                member results · character deltas (status, custody, held, binding, wound days, first encounter) ·
                CasualtyReport / fates · headcount / committed deltas · succession spec · actor-end spec ·
                promotions (new records, § 4.5.5) · spatial anchor · equipment-condition step · operation marker ·
                RELEASE actions · FOLLOW-UP actions · keyed event specs
              the plan is a pure function of its inputs; it may throw; nothing has been applied
  4 VALIDATE  the COMPLETE plan, still before anything changes (throws PlanInvalid ⇒ nothing touched):
                every member belongs to this episode · character.episode == episode.id · custody == Deployed · the actor
                exists · per-tier conservation (§ 5.1) · no two members share a pawn · no op other than death targets a
                Dead character · no duplicate op on one target · named-seat caps (§ 4.5) · the ids it needs are free
  5 COMMIT    through ONE bounded mutation layer (the Applier), inside a try:
                snapshot = clone of exactly TouchedSet(plan)                    // § 15.6; includes the id allocator when the plan adds a record
                Applier.Apply(plan)         // a flat, ordered list of TOTAL assignments: no service call, no bus, no
                                            //   scheduler, no vanilla call, no randomness, no logging that can throw
                episode.state = Closed ; member outcomes ; episode.committedTick
                episode.consequencesApplied = true                              // the LAST statement inside the try
              catch: restore the snapshot; attempts++; lastError; return        // the flag was never set: state is exactly as before
  6 RELEASE   idempotent, self-healing, never un-flags:
                vanilla side  — strip tags · anonymous pawns PassToWorld(Decide) · named pawns normalize → RESERVE → pass (§ 7.4)
                runtime side  — cancel the actor's upkeep job · Spatial.OnActorEnded (both are repaired by the validator if skipped)
  7 FOLLOW-UP the linked operation's own resolution, through its EXISTING guarded entry points (found / written off:
              ReturnForces, OnRecovered, CommitOutcome(op, true), Finish, OnWrittenOff): idempotent by op status and its own flags
  8 PUBLISH   events / history / letters built from the plan's KEYED specs, each in its own guard; a failure logs and continues;
              episode.publishedTick = now after the loop. A throw here NEVER reapplies a consequence
```

A **process crash cannot split stage 5**: it is one synchronous block and a save cannot interleave inside it. The only
hazards are an *exception* (handled by the restore) and *re-entrancy* (handled by the gate and the runtime guard).
Stages 6–8 may be interrupted by a load; each has a marker ([§ 8.1](#81-the-episode-machine-durable)) and the
finish-pending pass resumes it.

| If it throws in … | State left behind | Recovery |
|---|---|---|
| 1–4 | nothing changed (`attempts++`, `lastError`) | retried by the watch with backoff; after the bound ⇒ `Quarantined` |
| **5** | **nothing: the snapshot is restored and the flag was never set** | retried; the plan is recomputed from observation, so the retry decides from the world as it is *now* |
| 6 | consequences applied (`Closed`) | the finish-pending pass finishes the release from the tags; scheduler and derived data are repaired by the existing validator |
| 7 | consequences applied; the operation is still `Physical` | the pass runs the guarded entry point again; it is idempotent by status and flags |
| 8 | consequences applied; `publishedTick` unset | republish the keyed events; **never** reapply |

### 15.3 Observation → outcome → abstract effect

| Observation (with evidence) | Member outcome | Abstract effect (existing vocabulary) | Pawn handling |
|---|---|---|---|
| `Dead` | `Killed` | named: `Fate.Killed`, `status Dead`, `diedTick`, `custody Released`, leader ⇒ succession, Solo ⇒ `EndActor`; anonymous: tier headcount −1 | left to vanilla |
| `WorldFree` **and** exit evidence, alive, healthy | `Returned` | named: `custody Stored`, anchor written; anonymous: headcount back to healthy | named: normalize, reserve, pass; anonymous: pass (Decide) |
| as above but injured | `Returned` (+ recovery) | named: `status Wounded`, `woundedUntilTick`; anonymous: `AddWounded` bucket | as above |
| `HeldByPlayer` | `HeldByPlayer` | named: `Fate.Captured`, `status Captured`, `custody OutOfCustody(PlayerPrisoner/Slave)`; anonymous: **promoted** to a Known Character first (record + binding), then the same; headcount −1; event | untouched; binding and char tag kept |
| `JoinedPlayer` | `JoinedPlayer` | `status Defected`, `custody OutOfCustody(PlayerColonist)`, relation hit (unless the recruitment was a rescue the org wanted) | untouched |
| `Kidnapped` | `Kidnapped` | `Fate.Captured`, `status Captured`, `custody OutOfCustody(Kidnapped)`; `Contractor.Captured` with the captor | untouched |
| `HeldByOther` | `HeldByOther` | as `Kidnapped` | untouched |
| `Gone` (pointer null, `Discarded`) | `Lost` | named: `status Lost`, `custody Lost`, history "vanished"; anonymous: headcount −1 | none |
| `Spawned` elsewhere, `InCaravan`, `InTransport`, `WorldOther`, `Unknown` | **`Pending`** | none: the episode stays Open (or the person is held) | untouched |
| `Spawned` on the episode map after the planned end | **`Pending`** | none | untouched |
| never created (`Planned`) | `NeverPlaced` | custody reverted; headcount returned | none |

### 15.4 Why a duplicate trigger cannot apply a consequence twice

| Scenario | Why it is safe |
|---|---|
| `Killed` signal **and** the watch poll **and** the quest part's `Notify_PawnKilled` all fire | all three call `Reconcile`; the first closes the episode; the others hit the gate |
| the map is removed while members are still `Present` | the comp wakes a reconcile; members are observed; the result is `Returned` / `Missing` / `Lost` from *state*, once |
| a save/load lands between commit and release, follow-up or publish | `Closed` is persisted with `consequencesApplied`; the finish-pending pass resumes each unfinished stage from its marker ([§ 8.1](#81-the-episode-machine-durable)) |
| contract/operation reaches a terminal state while an episode is open | the operation is `OpStatus.Physical`; it waits for the episode's `OnPhysicalResolved` (§ 15.5); its own deadline job is cancelled |
| reconcile is called by a dev action twice | the gate |

### 15.5 Reuse of the existing services (no parallel rules)

- **Fates and casualties are *split*, not called.** The per-person branches of `ContractorService.ApplyCasualties`
  (status, death fields, leader loss, morale shock by loss share, the succession *decision*, the actor-end *state*)
  become pure functions / deltas the Applier consumes; their *events, scheduler and cross-service calls* move to
  RELEASE and PUBLISH ([§ 15.6](#156-failure-semantics-of-the-commit-service-by-service)). The *per-operation*
  bookkeeping (`opsCompleted++`, skill gain, morale on success) stays with the operation's own resolution and is **not**
  repeated for a physical episode unless it resolves an operation. The Phase 2 abstract resolver may keep calling the
  combined path unchanged in 3.0, **provided a parity test proves the split and the combined path produce identical
  durable state for the same `CasualtyReport`** (RT-PHYS-027), so there is one set of rules, not two.
- **Careers.** `CareerService.CommitOutcome` is called only for an operation-linked episode, only at the authoritative
  end (FOLLOW-UP, never inside the physical commit), and is guarded by the existing `careerOutcomeApplied` flag and its
  own plan/snapshot/flag discipline: P3-INV-014.
- **A rescue replaces `TroubledDeadline`'s dice, not its branches.** When the episode resolves a Troubled operation,
  `OperationService` gains `OnPhysicalResolved(op, found)` that runs the existing *found* branch (`ReturnForces`,
  `OnRecovered`) or *written-off* branch (`CommitOutcome(op, true)`, `Finish`, `OnWrittenOff`) as the FOLLOW-UP stage.
- **Spatial.** `Spatial.OnPhysicalEpisodeClosed(actor, tile)` is a **reporting or throwing** entry used by the Applier:
  the existing facade methods catch every fault and return a default (`SpatialService.OnActorEnded`, `IncidentTile`,
  `IsAtWork`), and a *swallowed* fault inside a commit would be an invisible partial apply ([§ 12.2](#122-leaving-it-reconcile-writes-the-anchor-once)).
- **History and letters.** New event keys (`Episode.Opened/Closed`, `Character.CapturedByPlayer`, …) go through the
  existing bus, **published after the commit**, built from keyed specs; letters are a `LetterConsumer` concern as
  today.

### 15.6 Failure semantics of the commit, service by service

Legend: **D** durable Network data (inside the transactional commit) · **S** scheduler / derived / runtime effect
(after the flag; idempotent; self-healing) · **V** vanilla-side effect (RELEASE) · **X** another service's own
multi-step resolution (FOLLOW-UP through its existing guarded entry point) · **P** publication (PUBLISH).

| Concern | What the existing path does today (verified) | Class | In the physical commit | If it fails |
|---|---|---|---|---|
| **Casualty application** | `ApplyCasualties` (`ContractorService.cs:794`) sets statuses, death fields and wound ticks, applies the morale shock, bumps `opsCompleted` and skill, **and** calls `ctx.bus.Publish` inline (`CharacterKilled`, `LeaderKilled`, `ContractorCasualties`) and `MoraleShiftCheck` (publishes) | D + P | only the **D** half, as a pure fate delta applied by the Applier; the events become keyed specs for PUBLISH; operation bookkeeping excluded (§ 15.5) | restore of the touched `KnownCharacter`s, `OrganizationProfile` and `ContractorSimulation` snapshots: never half-applied |
| **Succession** | `RunSuccession` (`:888`) picks a successor, may **create a record** (`ctx.characters.Add`, id allocation, name generation), rewrites org lists, role, notability, doctrine drift and morale, may call `EndActor`, and publishes | D + P (+ S via `EndActor`) | the **decision** in PLAN (pure; a generated successor is deterministic from `(actor.seed, "succession", successions)`, so a restore and retry yields the same person); the D half in the Applier; events deferred | the snapshot removes the added record and restores the lists and the allocator; ids are gap-tolerant anyway (`NetworkState.MaxEntityId`, the validator) |
| **Actor ending** | `EndActor` (`:958`) sets `status`, `endedTick`, `endReasonKey`, `ContractorProfile.suspended`, **cancels the upkeep job**, calls `Spatial.OnActorEnded`, publishes, bumps `StateVersion` | D + S + P | **D** in the Applier; the scheduler cancel and `OnActorEnded` in RELEASE (S); the event in PUBLISH | D restored on a commit failure. **S is self-healing:** `UpkeepJob` returns at once for a non-Active actor and `NetValidator` removes orphan upkeep jobs (`NetValidator.cs:297–301`), so a skipped cancel is harmless |
| **Headcount** | `OrganizationProfile.TierOf(t).{healthy, wounded}`, `Committed`, `woundedRecovery` buckets (`AddWounded`, `:770–785`): plain integer arithmetic | D | in the Applier; **conservation validated in PLAN** (§ 5.1) so the arithmetic cannot go negative | the `OrganizationProfile` snapshot |
| **Custody and binding** | none yet (`custody` is never written today); new `pawn`, `episode`, `heldBy`, `heldSinceTick`, `firstEncounterTick`, `opRole` | D | plain fields in the Applier; the binding is write-once, so a restore returns it to its prior value | the character snapshots |
| **Spatial anchor** | the facade methods **contain their own faults** and return a default (`SpatialService.cs:336–340`, `:346–360`): a fault there is *silent* | D (S for `OnActorEnded`) | a **direct assignment** of the anchor, status and `lastUpdateTick` in the Applier (no travel, no job); the facade is never used inside the commit | the `SpatialState` snapshot |
| **Operation state** | the operation's resolution (`ReturnForces`, `OnRecovered`, `CommitOutcome(op, true)`, `Finish`, `OnWrittenOff`) spans contracts, payments, comms and careers | X | one **D marker** on the operation (the episode's result) in the Applier; the resolution itself is FOLLOW-UP | marker restored with the snapshot if the commit failed; FOLLOW-UP retried if the commit succeeded (idempotent by `OpStatus` and the operation's own flags) |
| **Career outcome** | `CareerService.CommitOutcome` (`:297`) already does plan → snapshot → commit → flag → publish and is guarded by `careerOutcomeApplied` | X | **not in the commit**; called from FOLLOW-UP | its own retry (the validator re-applies a missing result), unchanged (P3-INV-014) |
| **Event publication** | `ctx.bus.Publish` dispatches **synchronously to every consumer** (history, letters, the Consequence Engine …); a consumer can itself mutate state and schedule jobs | P | **never inside the commit**: running foreign code mid-transaction would break atomicity. After the flag, each keyed event in its own guard | logged and skipped; **never** replays a consequence; a lost or duplicated history line is a cosmetic defect, not a state defect |
| **Promotions and ids** | `ctx.characters.Add` and `IdAllocator.NextId()` (a plain counter) | D | in the Applier; the **allocator is part of the touched set** whenever the plan adds a record | the snapshot restores the counter |

`TouchedSet(plan)` is computed by **one** function from the plan: the episode, the ≤ 8 member characters, the actor's
`OrganizationProfile` and `ContractorSimulation`, the actor header, its `SpatialState`, at most one operation header,
and the id allocator. Each Applier operation *declares* what it writes, so the snapshot's coverage is checkable.

### 15.7 How atomicity is demonstrated (a Phase 3.0 deliverable)

The Applier is a flat ordered list of *N* assignments, so atomicity is **testable, not argued**:

1. **Fault-injection sweep (RT-PHYS-026).** In the sandbox, for every step *i* in 0…*N*, run the commit with a throw
   injected after step *i*; assert that the deep durable-state fingerprint (the same reflection-based
   `LiveFingerprint` that already proves the safe tier changes nothing) is **identical to the state before**, and
   `consequencesApplied` is false. Then run with no fault: applied once, fingerprint as expected. Run again: a no-op.
2. **Coverage proof.** A reflection test checks that every field an Applier operation declares it writes lies inside
   `TouchedSet(plan)`.
3. **Mutation checks**, each caught by a named test: an operation writing outside its declared set · the flag set
   before the last operation · a publication, scheduler or vanilla call placed inside the Applier (the source scan,
   extended) · a restore that omits the id allocator.
4. **Parity (RT-PHYS-027)** with the abstract casualty path, so the split introduces no second rule set.
5. **Interruption (RT-PHYS-028):** a save/load between stages 5 and 6, 6 and 7, 7 and 8 resumes each stage exactly once.

---

## 16. Save/load semantics

### 16.1 Save at every point

Vanilla saves a pawn wherever it lives; the Network saves only its stores and bindings. A pawn has exactly one *deep*
owner; our `PawnRef` is a non-owning reference.

| Saved while the person is… | Who saves the pawn | What the Network saved | At load |
|---|---|---|---|
| materialized and healthy (spawned) | the map | episode `Open`, member `Present`, binding, tags | pointer resolves; tags re-added if missing; watch job restored (persisted scheduler); nothing is generated or spawned |
| downed | the map | same | same; the next poll sees `Downed` |
| a prisoner of the player | the map (the pawn's `guest` is part of the pawn) | `OutOfCustody(PlayerPrisoner)` | the custody watch observes it; the person is **not** abstracted |
| in a caravan | the caravan (`ThingOwner<Pawn>`) | `OutOfCustody(Caravan)` or `Present` | observed via `GetCaravan()`; held |
| carried by another pawn | the carrier's `carryTracker` | `Present` | observed as `Spawned` (a carried pawn is held by a spawned pawn) |
| kidnapped | `WorldPawns` and the captor's `kidnapped` list (`Reference`) | `OutOfCustody(Kidnapped)` | observed through the captor's list |
| dead, corpse exists | the corpse holds the pawn | `Closed(Reconciled)` or `Open` + `Dead` pending | reconcile reads `pawn.Dead` ⇒ `Killed` once |
| dead, no corpse (world pawn) | `WorldPawns.pawnsDead` (saved with `saveDestroyedThings`) | binding saved with `saveDestroyedThings: true` | `Killed`; if GC already discarded it, the **persisted** death (written at the time) is the truth |
| its map is being removed | `MapDeiniter` passes pawns to the world during the removal; saves do not interleave with a tick | `Open` | members observed as world pawns ⇒ `Returned` / held / `Lost` |
| `Closed`, release not finished | the pawn's owner | `Closed` + `consequencesApplied` | the load pass finishes the release |
| `Planned` (a crash or save between create and spawn) | an unspawned generated pawn is **not saved** unless something holds it | `Planned` + `Created` members | members whose pointer resolves ⇒ treated as `Present`; none ⇒ `Closed(NeverPlaced)`, custody reverted |
| `Stored` (between episodes) | `WorldPawns` (`pawnsAlive` or `pawnsMothballed`, saved `Deep`) | `custody = Stored`, binding, `agedThroughTick` | the runtime registry is rebuilt from the characters store in `FinalizeInit` (§ 16.3); the truthful age catch-up runs at the next observation ([§ 6.4](#64-truthful-aging-of-a-retained-pawn)); nothing is aged at load |

### 16.2 What load must never do

Duplicate a pawn · spawn another copy · abstract a still-physical pawn · forget a capture · resurrect a dead
contractor · reroll identity or gear · rerun a consequence. **Load never generates, spawns or destroys a pawn.**
It observes, corrects tags, rebuilds runtime indexes and (only for `Planned` episodes) reverts or promotes by
evidence. A bound pointer that is `null` where the member says `Created` or `Present` is *evidence of loss*, never
a reason to regenerate.

### 16.3 The registry across load

The registry quest part holds **no persisted pawn list** of its own. `QuestPartReserves(Pawn)` delegates to a
**runtime registry** (`HashSet<Pawn>` built from the characters with `custody = Stored`) owned by the Network
runtime; no runtime ⇒ false. The runtime registry is built in `FinalizeInit`/`BuildRuntime` (after cross-references
resolve, before any first tick), so no `Stored` pawn is `Free` between load and the first tick. The stores are the
single source of truth; the quest only asks. (If the Network is absent, the part answers false and the pawns become
`Free`: the self-healing property.)

### 16.4 The minimum new persisted truth

| New persisted truth | Where | Old saves |
|---|---|---|
| the episode store (members included) | the reserved `deployments` slot (`ReservedStore` → `EpisodeStore`, same label) | load an empty store |
| `KnownCharacter.pawn`, `.episode`, `.heldBy`, `.heldSinceTick`, `.firstEncounterTick` | the characters store | absent ⇒ defaults (no pawn, no episode, not held, never met), which is **correct**: nobody has ever been materialized, and every saved `custody` is `0 = Unmaterialized` |
| `KnownCharacter.opRole` (operational role, one byte) | the characters store | absent ⇒ *unset*: **established at first use** (first projection or first display) from the person's durable facts, then persisted ([§ 6.6](#66-operational-roles)). No migration pass touches 130 contractors |
| `PawnRef.agedThroughTick` | inside the binding | written with the binding; n/a for old saves (no binding exists) |
| `OrganizationProfile.composition` (the role template, ≤ 8 entries) | the actor component | absent ⇒ *not yet established*: created at first use, never regenerated ([§ 6.7](#67-organization-and-mission-composition)); 3.2 |
| one operation marker (the episode's result for an `OpStatus.Physical` operation) | `Operation` | absent ⇒ none; `OpStatus.Physical` never occurs in an old save |
| `PhysicalEpisode.committedTick`, `EpisodeMember.seatRole` | the episode store | new with the store |
| a new entity-id kind | `EntityKind.Deployment` renamed `Episode` (value 9), added to `NetworkState.MaxEntityId` and its test | n/a |
| (3.1) the registry quest | created lazily by vanilla's `QuestManager`; **not** a Network store | n/a |

Not persisted, by rule: the runner, sessions, sandboxes, the wake-up queue, the runtime index, the runtime registry,
anything about the physical test tier.

### 16.5 Migration and version implications (the number is **not** chosen here)

- The first Phase 3 build (3.0) will perform **the format bump** (a "next version" migration `…ToN+1PhysicalLifecycle`). It
  performs no data change (the new fields default correctly) but *marks* the save as written by a Phase 3 build. Fields
  added in later subphases are additive with defaults; **OPEN O-11:** a player-released later subphase that adds persisted
  fields should bump again (a cheap no-op) so that a downgrade to an earlier subphase build warns instead of silently
  dropping data. Recommendation: **declare in 3.0 every shape this document freezes** (the Episode store, `PawnRef`, the
  custody fields, `opRole`), and treat shapes still OPEN (`grade`, `vacant`, notable-asset and lease records) as later
  additive changes. **No version number is chosen here, and the save format is still 4.**
- **Why bump if nothing migrates?** An older build treats `deployments` as a `ReservedStore` and would **silently
  drop** episode data at its next save, orphaning pawns the Network had bound. The existing newer-version guard
  (`diagnostics.downgradedFrom`, "saved by a newer Network: best-effort") exists for exactly this; only a bump
  triggers it.
- **Validator.** New findings are **report-and-quarantine**, never repair-by-guessing: a character `Deployed` with no
  open episode; an episode member whose character does not point back; two members sharing a pawn; an `Open` episode
  older than the long-open threshold; a `Stored` character with no pawn; a `Dead` character that is not `Released`; an
  episode with `consequencesApplied` but no `committedTick`, or an `Open` episode with `consequencesApplied`; a concretized
  seat whose character is not in the organization.
  The existing `NetValidator` repairs *derived* data; it must not "fix" custody.
- **Compaction.** `Closed` episodes are compacted a year after closing **only if** no character still references
  them; a character with a bound pawn or `OutOfCustody` is never compacted.
- **Old-save + new-build.** A world with 130 contractors loads, every character `Unmaterialized`, the store empty:
  behaviour is identical to Phase 2.9 until something materializes.

---

## 17. Failure recovery

Prefer **fail safe, preserve truth, quarantine and diagnose, retry idempotently** over *silently recreate* or
*silently delete*.

| Failure | Durable state at that moment | Recovery | Never |
|---|---|---|---|
| pawn generation returns null / throws | episode `Planned`; custody `Deployed` for named members | abort: `Closed(NeverPlaced)`, custody reverted, headcount returned, one log line naming the request and the Defs | spawn a placeholder; retry forever |
| invalid race/xenotype/kind | as above | fall back along the kind chain; if none, abort as above | write a pawn with a default body |
| gear generation fails | as above | vanilla throws from `GeneratePawn` ⇒ same as generation failure | hand-build a loadout |
| the map is gone before spawn | `Planned`, pawns created and bound | abort; **bound-but-unspawned pawns are passed to the world (Decide)** and tags stripped; nothing is discarded | discard |
| partial group placed | some members `Present`, some `Created`/`Planned` | the placed members make the episode `Open`; the others become `NeverPlaced` (custody reverted, headcount returned), logged once | pretend the group is complete |
| provenance binding cannot be written | pawn generated, binding failed | do **not** spawn; drop the unreferenced pawn (nothing holds it) and abort | spawn an unbound pawn |
| episode saved halfway (`Planned`) | `Planned` | load pass: resolvable pointers ⇒ `Present`; none ⇒ `NeverPlaced` | regenerate |
| reconcile threw **before or inside the commit** (stages 1–5) | `Open`, **nothing committed**: a throw inside the commit restores the snapshot and the flag was never set | retry with backoff; the plan is recomputed from observation; after the bound `Quarantined` | partial apply; double-apply |
| a post-commit stage threw (release, follow-up, publish: stages 6–8) | `Closed(Reconciled)`; the unfinished stage's marker is unset | the finish-pending pass resumes that stage only; a publish failure republishes keyed events | reapply a consequence |
| the spatial write inside the commit fails | the commit throws and is restored | retried | skip it silently (the existing facade would) |
| role or cohesion verification fails for every attempt | candidate pawns dropped; episode `Planned` | placement aborted: member `NeverPlaced`, custody reverted, one log line (role, kind, failed clause) | spawn a contradicting pawn; relax the role; loop forever |
| the truthful age catch-up fails or leaves the person unfit | binding intact; `agedThroughTick` unchanged | the placement aborts `NeverPlaced` with a diagnostic; retried later; the person is neither mutated nor deleted | place a pawn whose age is not current |
| the contractor actor is missing from the store | an episode referencing it | `Quarantined(ActorMissing)`; **detach** (strip tags, pawns untouched) | delete pawns |
| a bound pawn is missing on load | member `Present` with a null pointer | outcome `Lost`, status `Lost`, history "vanished", one warning | regenerate or mark `Returned` |
| a held person cannot be classified | `OutOfCustody` | stays held, logged at most once per day | abstract them |
| an unsupported custody appears in 3.1 (arrest, recruit, kidnap, caravan) | member observed `HeldBy…`/`InCaravan` | **`Quarantined(UnsupportedCustody)`**: pawn untouched, person blocked from abstraction, a dev diagnostic | fake capture support |

---

## 18. Performance

### 18.1 The rule

> When no contractor is physical and no person is vanilla-held, Phase 3 costs **nothing**: no job, no per-tick
> work, no allocation, no scan. (Today's idle cost, one static null check per frame in
> `WorldComponentUpdate`, is unchanged.)

### 18.2 Budgets (targets to be *measured*, not claimed)

| Situation | Budget | Mechanism |
|---|---|---|
| nobody physical, nobody held | **0** | no scheduler job exists |
| an Open episode (≤ 8 members) | ≤ 0.05 ms average per tick, spread | one `episode.watch` job per episode, every 250 ticks, ≤ 8 classifications (~15 hash/contains operations each) |
| held people (≤ 20) | negligible | one global `custody.watch`, every 2,500 ticks, **only while ≥ 1 person is held** |
| signals | O(1) | prefix test, runtime index lookup, enqueue |
| reconcile | < 1 ms typical | no scans; every read is of a bound pawn |
| materialize ≤ 8 pawns | vanilla generation cost (≈ ms per pawn) | bounded group size; larger groups (not in Phase 3) would spread generation across ticks |
| registry (stored named people, *R* ≤ ≈ 150) | a vanilla cost multiplied by *R* | `IsReservedByAnyQuest` runs per non-mothballed world pawn per tick; **measure** *R* × *W* (§ 7.4) |
| save size | vanilla pawn saves (≈ 10–40 KB each) | the soft cap of ≈ 150 retained pawns bounds it (a *performance* policy that never breaks the identity of an encountered person, [§ 4.5.4](#454-retention-and-the-performance-cap)); our own data ≈ 0.5 KB per episode, ≈ 50 B per binding |
| role and cohesion verification (3.1/3.2) | reads of one candidate and ≤ 7 teammates; **bounded** K attempts, each at most vanilla's own 120 | ≈ ms per pawn; S25 measures it on a heavy mod list; if it spikes a group is created one pawn per tick |
| the atomic commit | O(touched set): ≤ 8 characters + three small objects: a snapshot and (only on failure) a restore | < 0.2 ms; no scan |
| truthful aging (lazy, 3.1) | one catch-up per materialized stored pawn; O(years) birthday iterations | negligible; the *periodic* variant would be ≤ 150 calls per game-year, only while a pawn is stored |
| encounter evidence | ≤ 8 `PlayLog` / `BattleLog` lookups per reconcile | never per tick |
| composition apportionment | O(≤ 8), pure | negligible |

### 18.3 Forbidden

Scanning every pawn, every map or all world pawns per tick · scanning 130 contractors per frame · a global custody
search per frame · a poll over anything that is not a member of an Open episode or a held person · a per-tick aging job for
stored pawns · scanning the play log per tick · re-verifying a bound pawn's role, cohesion or social history after binding.

### 18.4 The Phase 3 soak (extends [PERFORMANCE § 3](PERFORMANCE.md))

150 retained (stored) pawns · 5 concurrent episodes · 20 held people · a large world-pawn population (≥ 3,000). Compare
TPS with and without The Network **on the same save**; read the vanilla `ProfilerBlock`s (`AlivePawnsTick`,
`MothballUpdate`, `WorldPawnGCTick`) with and without the registry; measure one `AccumulatePawnGCData` pass. The
numbers are recorded in the PR, not asserted here.

---

## 19. Mod compatibility

The owner runs a large mod list. The design uses **capabilities and ordinary Def/API truth only**; it contains no
logic keyed to any named mod. Those mods are owner QA environments, never dependencies.

| Area | Approach | Graceful failure |
|---|---|---|
| races, xenotypes, body sizes | choose a `PawnKindDef` by capability (humanlike, tool-using, usable for the org's template family); read `RaceProps`; never assume a body | no valid kind ⇒ the episode is not created (abort, log the Defs) |
| apparel, weapons | vanilla generation only | n/a |
| health systems | read only `Dead`, `Downed`, `health.summaryHealth`, `hediffSet.hediffs`, `HediffUtility.IsPermanent`; never write a hediff except through vanilla APIs in normalization (S12) | unknown hediffs are left alone |
| factions | the temporary faction by capability (§ 13.2) | no suitable `FactionDef` ⇒ not created |
| transport systems | an unrecognised holder ⇒ `ObservedKind.Unknown` ⇒ the person stays `Present`/held | never "returned" |
| death / resurrection mechanics | observe `Dead`, tolerate resurrection (§ 10.4); races with `corpseDef == null` die without a corpse | tolerated |
| caravan behaviour | `GetCaravan()` is the vanilla class; subclasses work | n/a |
| world-pawn managers | a mod that discards or relocates pawns ⇒ `Gone` ⇒ `Lost` with evidence | one warning, no regeneration |
| roles on modded races | kind selection filters to humanlike tool-users whose kind can satisfy the role; a race with no skills cannot hold a skill-bearing role; the post-generation verification ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)) is the authority, since a mod can alter any generated trait or skill | no valid kind or a failed verification ⇒ a contained abort |
| ideology and xenotype (cohesion) | `FixedIdeo` only when Ideology is active; the same organization's existing members set the ideology; no xenotype logic of our own | n/a |
| aging mods and genes | the catch-up goes through the pawn's own `BiologicalTicksPerTick`, so a slow-aging xenotype ages slowly: *truthfully* | n/a |
| mods that add birthday or growth effects | they run inside vanilla's `BirthdayBiological` path (candidate M1), exactly as for any world pawn | n/a |

A generated physical contractor must survive an unusual Def environment: every Def reference is resolved with
`GetNamedSilentFail`/capability queries, every failure is a contained, logged, safe abort. **Open:** modded-race name
and generation behaviour (O-7, spike S23).

---

## 20. Prepare-for-removal

The existing preparation (`RemovalPreparer`) unbinds sites, invalidates searches, voids contracts and strips
`TheNetwork.*` tags **from world objects only**. Phase 3 extends it; the principle is **never delete a pawn and never
strand a reservation or a comp.**

| Step | Rule |
|---|---|
| 1. Open and Planned episodes | run a **settle**: observe each member; apply terminal observations as in § 15 (no letters); any non-terminal member becomes `OutOfCustody(Unknown)`; episodes end `Closed(Detached)`; **no physical consequence is invented** |
| 2. The registry | clear the runtime registry; end/clean the hidden quest through vanilla API (**OPEN S9r/S6**: the exact call); the previously reserved pawns become ordinary `Free` world pawns, which vanilla GC and redress treat as it treats any pawn. This is *accepted*: nothing is deleted |
| 3. Tags | strip every `TheNetwork.*` tag from **pawns** too (the current code strips them from world objects only) |
| 4. Temporary factions | untouched: vanilla removes them as soon as nothing uses them |
| 5. Comps and hediffs | none exist (rejected in § 7.2), so nothing can break a save |
| 6. Result | `preparedForRemoval = true`; watch jobs stop (`Tick` returns early); the summary reports episodes settled, pawns released, tags stripped |

Roles, composition, concretization, cohesion and aging add **no Def, comp, hediff or tag**: they are plain saved fields on
Network records, so nothing in them can break a save when the mod is removed. (Phase 3.3's right-click provider, if
built, is a *type in our assembly* that vanilla discovers by reflection: it simply disappears with the assembly.)

`Resume` re-validates (`NetValidator Full`), re-tags bound pawns and rebuilds the registry; episodes stay `Closed`.
**Without preparation:** the Network-owned quest part's class is missing, vanilla logs about two errors and drops it,
the pawns become `Free`, the tags remain as inert strings, and the pawns on maps keep their vanilla Lords. If the
vanilla-only registry variant wins S9r, an *unprepared* removal would leave pawns reserved forever; that is why the
default stays the Network-owned part (§ 7.4).

---

## 21. Runtime QA strategy

Phase 2.9 built a runner that is **safe to press in a real colony** because it never spawns, spends, creates or
sends. Phase 3 needs real pawns and maps. Those two requirements cannot share a suite. The design therefore has
**two tiers**, and the second is explicit, separate and destructive by construction.

```
   ┌──────────────── TIER S: SAFE  (Quick smoke · Full safe regression · Live scan) ───────────────┐
   │ real production services over SANDBOX ports; the live game is only READ                       │
   │ + RT-PHYS-*  the ABSTRACT half of the lifecycle over a FAKE PhysicalWorldPort (no RimWorld)   │
   │ never references the real physical adapter · source-scan enforced · safe on a real colony     │
   └─────────────────────────────────────────────────────────────────────────────────────────────┘
   ┌──────── TIER P: PHYSICAL  (separate menu · session-armed · own test map by default) ──────────┐
   │ real pawns, real maps, real world objects, real factions                                      │
   │ RT-PHYX-*  every created entity tagged TheNetwork.Test.<runId> · blast-radius proof           │
   │ never part of Quick smoke or Full safe regression · never guesses if a save is disposable     │
   └─────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 21.1 Tier S: the abstract half (safe, headless, and in-game over a fake port)

The lifecycle's *logic* (authority, episodes, reconciliation, custody rules, exactly-once, save round-trip) needs no
RimWorld pawn. It is tested through a **`PhysicalWorldPort`** (the same ports pattern as comms, payment and the
catalog) whose sandbox implementation is a **scriptable fake**: it hands out pawn *tokens*, and the test scripts what
`Observe` returns (spawned, downed, dead, held, world-free, gone, …). Production code calls only the port, so the
real adapter and the fake exercise the same services.

| ID (suggested) | Invariant it proves | Headless | In-game (safe, sandbox) |
|---|---|---|---|
| RT-PHYS-001 | one person materializes **exactly once** (custody `Deployed`, one open membership, one binding) | ✔ | ✔ |
| RT-PHYS-002 | the **same person cannot materialize twice** simultaneously (second plan refused: P3-INV-001) | ✔ | ✔ |
| RT-PHYS-003 | a normal physical exit reconciles **exactly once** under duplicate wake-ups (signal + watch + load + dev) | ✔ | ✔ |
| RT-PHYS-004 | **rematerialization preserves identity**: the same binding (token) is reused, never regenerated (P3-INV-006) | ✔ | ✔ |
| RT-PHYS-005 | a physical wound produces the **correct durable recovery truth** (`woundedUntilTick` or an `AddWounded` bucket) once | ✔ | ✔ |
| RT-PHYS-006 | **physical death prevents abstract availability and abstract resurrection**, even when abstract jobs keep running (P3-INV-004) | ✔ | ✔ |
| RT-PHYS-007 | **save/load**: stores and bindings round-trip through the real Scribe; the same token resolves; nothing is generated (P3-INV-008) | ✔ | — (no save automation) |
| RT-PHYS-008 | **custody blocks legal abstraction**: a held/unknown/absent pawn is never `Returned` (P3-INV-005, -010) | ✔ | ✔ |
| RT-PHYS-009 | a **group member's death** changes the group's truth and nobody else's; tier conservation holds (P3-INV-007) | ✔ | ✔ |
| RT-PHYS-010 | **map removal cannot silently erase** a person: members are observed and end `Returned`/`Missing`/`Lost` from state | ✔ | ✔ |
| RT-PHYS-011 | **the authority gate is complete**: a static test enumerates every abstract writer in [§ 2.3](#23-the-writer-inventory-every-abstract-site-that-must-honour-authority) and fails if one bypasses it | ✔ | — |
| RT-PHYS-012 | an **unsupported custody** observed in 3.1 ⇒ `Quarantined`, pawn untouched, person blocked (no faked capture) | ✔ | ✔ |
| RT-PHYS-013 | **reconcile that throws** leaves no half-applied state; the retry applies once | ✔ | ✔ |
| RT-PHYS-014 | **publish that throws** republishes only; consequences are not reapplied | ✔ | ✔ |
| RT-PHYS-015 | a **`Planned` episode found at load** resolves by evidence (resolvable ⇒ `Present`, none ⇒ `NeverPlaced`), never by regeneration | ✔ | — |
| RT-PHYS-016 | **operation exclusivity**: a person in an episode cannot be checked out for an operation, and vice-versa (ADR-039 extended) | ✔ | ✔ |
| RT-PHYS-017 | **spatial**: the person's anchor is frozen while physical and written once at close (P3-INV-009) | ✔ | ✔ |
| RT-PHYS-018 | **prepare-for-removal (settle)** leaves no episode `Open`, no tag, no reservation; nothing deleted | ✔ | ✔ |
| RT-PHYS-019 | **validator** reports (and does not repair) custody/episode inconsistencies | ✔ | ✔ |
| RT-PHYS-020 | **role verdict and the smallest correction** (pure functions over fake candidates): a candidate incapable of the role's work is rejected; a skill-only shortfall is corrected through the base level (aptitudes respected) and re-verified; nothing else is ever modified (P3-INV-017) | ✔ | ✔ |
| RT-PHYS-021 | **established truth beats randomness**: the projection request honours every durable statement (name snapshot, role, the organization's composition); anything not established stays unset (P3-INV-018) | ✔ | ✔ |
| RT-PHYS-022 | **fame invariance** (a property test): changing `FameBand`, the reputation score or visibility changes no projection request, role choice or mission composition (P3-INV-019) | ✔ | ✔ |
| RT-PHYS-023 | **concretization by size**: a five-person crew's placed seats persist and are reused (the same record, never a stranger); a 40-person company's rank-and-file stay ephemeral and leave no roster; named seats never exceed the caps; an encountered member is never released under the cap (P3-INV-020) | ✔ | ✔ |
| RT-PHYS-024 | **cohesion is initial-only**: the screen is a pure function applied at first projection; a source scan proves no Network code writes the relations, opinions, thoughts, memories or traits of a bound pawn (P3-INV-021) | ✔ | — |
| RT-PHYS-025 | **truthful aging contract** (over the fake port): the catch-up requested equals the full elapsed interval since `agedThroughTick`, uncapped, for 1, 10 and 70 game-years; a rarely met person ages exactly as a frequently met one; `BirthAbsTicks` is never written (P3-INV-022) | ✔ | ✔ |
| RT-PHYS-026 | **commit fault-injection sweep**: a throw injected after every Applier step leaves the deep fingerprint unchanged and the flag false; the fault-free run applies once; a re-run is a no-op ([§ 15.7](#157-how-atomicity-is-demonstrated-a-phase-30-deliverable); P3-INV-023) | ✔ | ✔ |
| RT-PHYS-027 | **commit purity and parity**: the Applier references no bus, scheduler, vanilla or random source (source scan); for the same `CasualtyReport` it yields durable state identical to the abstract casualty path (P3-INV-024) | ✔ | — |
| RT-PHYS-028 | **finish-pending after interruption**: a save/load between commit and release, release and follow-up, follow-up and publish resumes each stage exactly once; a publish that throws never reapplies a consequence (P3-INV-025) | ✔ | — (no save automation) |

These **28** IDs extend the Phase 2.9 stable-ID discipline (never renumbered, never reused; a new behaviour gets a new
ID) and the suite plugs into `RuntimeTestPlans.FullSafe` unchanged. **Full Safe Regression remains safe on a real
colony: read-only live state, mutable scenarios only in the sandbox** (P3-INV-013, enforced by the existing source scan,
extended to forbid the real physical adapter and any pawn-creating API in `RuntimeTests/Suites/`).

### 21.2 Tier P: the physical integration suite (armed per session, dedicated test map by default)

The previous revision gated this suite partly on a heuristic ("a bounded sanity check on colonist count and wealth":
*a low-wealth, low-colonist save is probably a test colony*). **That is withdrawn.** A legitimate early-game colony looks
exactly like that, and the Network must **not try to infer whether the player's save matters**. The guard is now made of
*explicit human assertions* and *facts the Network can actually check*, never a guess.

| Element | Design |
|---|---|
| Name and menu | Dev Mode → **"The Network (PHYSICAL TESTS: disposable environment only)"**, a separate category; every action label starts with `⚠ PHYSICAL`; **not** reachable from Quick smoke or Full safe regression |
| **Where: a dedicated test map, by default** | the suite **creates and owns** a generated test map on a hidden `MapParent` (tagged, removed by the suite). Tests run **there** whenever they can. No existing pawn, map, faction, world object or colonist is touched; the player's colony is not involved |
| **The session arm** | before any action is enabled, a modal lists in plain words what the suite will create and mutate and requires **typing a fixed phrase**. This sets a **runtime-only** flag that is **cleared on every load, on quit, and after each run**; menu items are inert until it is set. It is **never persisted** |
| **No persisted marker** | the suite writes **no** "this save is disposable" flag (the Phase 2.9 rule: nothing about testing is saved). None has been identified as necessary |
| **Home-colony scenarios (an even stronger gate)** | only a scenario that *fundamentally* needs a home colony (an arrest or recruit by player pawns on a home map, a caravan formed from the colony, a visit to a real colony) may use a colony map, and only after a **second, separate typed confirmation that names the home map and states "this save will be modified"**, restated at the start of each such scenario. The first arm alone never enables them. Each such scenario records *why the test map cannot do it* |
| Facts it checks (not guesses) | Dev Mode on · the Network runtime is running · no `Planned` or `Open` episode exists · the suite's own cleanup has no leftover tagged state. Each refusal is reported by name |
| Tagging | every entity the suite creates (pawns, world objects, factions, items, the test map) carries `TheNetwork.Test.<runId>` |
| Blast-radius proof | the physical analogue of RT-INFRA-001: counts of tagged vs untagged pawns, world objects and factions before and after; **untagged state must be unchanged**, tagged state must equal the script |
| Cleanup | removes only tagged entities, and only when the run ends cleanly or on an explicit cleanup action |
| On failure | the failed scenario is **preserved** (map, pawns) and a report is written; nothing is auto-deleted |
| Source layout | its own folder (candidate `Diagnostics/RuntimePhysicalTests/`); its own scan: spawning APIs are allowed *there and nowhere else* (P3-INV-013, P3-INV-028) |
| Sentinel | a `PhysicalSentinel` (pawn counts by tag/faction, world-pawn counts, factions, world objects), the pawn-level counterpart of `ColonySentinel` (which deliberately never scans pawns) |

Headless `Runner.*` tests cover the guard itself: menu items inert without the arm · the arm cleared on load and after
a run · refusal when the test map cannot be created · a home-colony scenario refused without the second gate · no
persisted field · the source scan.

| ID (suggested) | Scenario (real RimWorld) | Slice |
|---|---|---|
| RT-PHYX-001 | generate + bind + spawn exactly one named pawn on the test map; tags and binding agree | 3.1 |
| RT-PHYX-002 | the visit Lord runs, the pawn exits through the edge, the episode reconciles `Returned` once | 3.1 |
| RT-PHYX-003 | downed, then recovers; observed by the poll, recovery truth correct | 3.1 |
| RT-PHYX-004 | killed (dev damage); death recorded once; the abstract layer cannot resurrect | 3.1 |
| RT-PHYX-005 | the test map is removed with the pawn still on it; the person is observed, not erased | 3.1 |
| RT-PHYX-006 | **rematerialization**: same `Pawn` object (same `thingIDNumber`), name, permanent injury kept, **truthful age** | 3.1 |
| RT-PHYX-007 | registry: a stored pawn is not redressed in N forced generations; `Suspended`; not GC'd across several GC passes | 3.1 (S9r) |
| RT-PHYX-008 | the temporary faction is created, hostile/neutral as seeded, removed after the episode; pawn faction nulled harmlessly | 3.1 (S10) |
| RT-PHYX-009 | unsupported custody (dev arrest) ⇒ `Quarantined`, pawn untouched | 3.1 |
| RT-PHYX-010 | save/load matrix (owner-assisted, with a checklist; there is **no** save-reload automation) | 3.1 (S24) |
| RT-PHYX-011 | **role-constrained creation on real pawns**: for N seeds and every role the created pawn satisfies the role's constraints (violence/Shooting/doctoring capability, role-skill floor), including on modded races; a failure is a contained abort, never a contradicting pawn | 3.1 (S25) |
| RT-PHYX-012 | **truthful aging**: store a pawn, advance game time by N years (dev time-skip), materialize: chronological age is N years older, biological age advanced by the full interval, birthday effects consistent, no errors | 3.1 (S12) |
| RT-PHYX-013 | **cohesion probe**: N generated crews per band: pairwise opinion statistics; the screen rejects as designed; no relation write after binding | 3.2 (S26) |
| RT-PHYX-014 | **concretization**: a crew of five appears twice and the same five pawns appear; a company detachment leaves no roster | 3.2 (S27) |
| RT-PHYX-020+ | arrest/recruit/kidnap/caravan/rescue-site custody, group of five, anonymous members, held-person watch | 3.2 |
| RT-PHYX-030+ | handoff scenarios: pay, decline, rob, abandon, contractor killed (design direction only) | 3.3 (S28–S30) |

### 21.3 What Phase 3 adds to the Phase 2.9 infrastructure

A fake `PhysicalWorldPort` in the sandbox (it hands out pawn *tokens*, records the catch-up and creation requests it is
given, and lets a test script what `Observe` returns); the 28-case `RT-PHYS-*` suite; extensions to the safe-suite source
scan (no real adapter, no pawn-creating API, and now **no bus, scheduler or vanilla reference inside the commit's
Applier**); the separate physical tier with its session arm, guard, sentinel and scan; headless `Runner.*` tests for the
guard and the blast-radius proof; the **fault-injection sweep** that reuses the existing `LiveFingerprint`; and the
`LiveFingerprint` itself, which already walks every persisted field, so it covers `EpisodeStore` and the new character
fields automatically (a test asserts that).

---

## 22. Phase 3 vertical slice

### 22.1 The recommendation: **3.1, the controlled physical episode**

The smallest implementation that proves the whole lifecycle without dragging in capture, caravans, resurrection,
multiple organizations, transport pods or twelve encounter types:

```
 ONE existing Solo contractor (embodied KnownCharacter, custody Unmaterialized, operational role established at first use)
   ─► Dev action (physical tier: session-armed, on the suite's OWN test map): "Materialize … as a visitor"   [cause = Dev, flagged]
   ─► Plan: gate checks · episode Planned · custody Deployed · temporary faction created
   ─► Create the pawn ONCE as a role-constrained projection: ForceGenerateNewPawn · request fields + validators ·
      VERIFY (and at most minimally correct role skills) while it is still unbound · BIND (member Created) ·
      spawn at the edge · tag · visit Lord
   ─► the person is physically present; vanilla AI does vanilla things; the pawn can be wounded, downed, killed
   ─► ONE of: exits through the edge · is killed · its map is removed · (unsupported custody ⇒ Quarantined)
   ─► Reconcile: observe → decide → plan → validate → atomic commit → flag last → release → follow-up → publish
      (exactly once, however many wake-ups)
   ─► the contractor is abstract again if legally allowed (Stored: pawn retained, reserved, normalized, agedThroughTick set)
   ─► LATER: materialize the same contractor again ⇒ the SAME Pawn object, same name, TRUTHFULLY OLDER (full, uncapped
      catch-up), permanent injuries kept   ◄── identity continuity proven
```

**Why a Solo.** One actor, one character, one pawn: no roster math, no detachment accounting, no composition, no
cohesion. Group conservation (P3-INV-007) is proven abstractly in 3.0 (RT-PHYS-009) and physically in 3.2, where roles
become compositions and concretization and cohesion appear.

**Why a dev trigger.** The only *content* trigger that exists today (a Troubled operation's Last Known Location) needs
site-holder pawns (S11), captive handling and operation suspension, which are 3.2. Inventing a gameplay trigger here
would smuggle design into an engineering milestone. The slice is intentionally a *lifecycle proof*, then the owner
reviews it before any player-facing content.

### 22.2 Exact boundaries

| In 3.1 | Out of 3.1 (designed in this document, **not** implemented, **not** faked) |
|---|---|
| a Solo, one retained named pawn | groups, anonymous members, detachments, **organization / mission composition, progressive concretization, team cohesion** (3.2) |
| the visit Lord on the physical tier's **own generated test map** (a home-colony scenario only behind the second, stronger gate, [§ 21.2](#212-tier-p-the-physical-integration-suite-armed-per-session-dedicated-test-map-by-default)) | rescue sites and any `SitePart.things` holder |
| outcomes: exits, wounded/downed then recovers, killed, map removed | arrest, recruit, enslave, kidnap, caravan, pod/shuttle, world-pawn custody **beyond a safe Quarantine** |
| the registry reservation (S9r), store-time normalization and the **truthful aging catch-up** (S12) | resurrection handling beyond the opportunistic check |
| the temporary faction (S10) | leases, **notable-asset grants**, sponsorship, **delivery in person (3.3)**, ambient visits |
| **role-constrained first creation** of the Solo: validators, authoritative verification, the smallest skill correction, abort on failure (S25) | a skill sheet or any persisted detail beyond the role |
| the authority gate, episode store, `PawnRef`, characters' new fields, validator, compaction, prepare-for-removal settle | any content trigger, any letter beyond vanilla's, any player UI beyond a read-only Episode Monitor |
| `RT-PHYS-020…022` and the 3.1 `RT-PHYX-001…012` | the Phase 4 equipment seams (Lease, Notable Asset) are *defined* but only input (a) exists |

**Fail safe, never fake.** If during 3.1 the player arrests, recruits or kidnaps the visitor, or a caravan takes them,
the member is observed `HeldBy…`/`InCaravan`, the episode becomes **`Quarantined(UnsupportedCustody)`**, the pawn is
left exactly as vanilla has it, the person is blocked from abstraction, and a dev diagnostic says what happened. Capture
**support** is 3.2; capture **safety** is 3.1.

### 22.3 Preconditions (gates)

3.0 merged and owner-reviewed · S9r (registry) passed *or* its fallback ladder decided · S12 (normalization **and
truthful aging**) · S14 (visit Lord) · S10 (temporary faction) · S23 (creation pins and modded races) · **S25 (role-constrained
creation)** · S21 (observation completeness) · S22 (the physical-tier guard: session arm and dedicated map). 3.0
needs **no** spike.

---

## 23. Suggested subphases

**Four** subphases, each ending in something the owner can review and test, with the riskiest unknowns behind explicit
gates. The fourth (3.3) is *design direction only* here. Not more: every split below has its own proof, and a further
split would only add review overhead.

| Subphase | Content | Proof | Owner gate |
|---|---|---|---|
| **3.0 Authority and episode foundation** (no RimWorld pawn) | `EpisodeStore`, `PawnRef` (with `agedThroughTick`), the character fields (`pawn`, `episode`, `heldBy`, `opRole`, `firstEncounterTick`), `AuthorityGate` and its call sites, **the reconciliation planner, validator and the atomic Applier**, the physical-apply split of the casualty/succession/ending paths, the `PhysicalWorldPort` + scriptable fake, the validator, compaction, prepare-for-removal settle, `RT-PHYS-001…019` and **025–028**, the save-version bump + no-op migration | headless suite (incl. the fault-injection sweep and the parity test) + soaks + the **safe** runtime tier in the owner's real colony. **Zero new risk to a real save.** | review the abstract core before any pawn exists |
| **3.1 The controlled physical episode** (the slice) | the real `PhysicalWorldPort` adapter, **role-constrained projection for a Solo**, **truthful aging catch-up**, binding, tags, `SignalBridge` routes, the visit Lord, the temporary faction, the registry quest, store-time normalization, the **physical test tier with its session arm and own test map**, `RT-PHYS-020…022`, `RT-PHYX-001…012`, a read-only Episode Monitor | the physical tier on the suite's own test map; the owner's save/load checklist | review real pawns before any content |
| **3.2 Custody, rescue and groups** | held-person observation (arrest, recruit, enslave, kidnap, caravan, pod), the custody watch, **group materialization, organization role composition and mission composition, anonymous vs concretized people, promotion, team cohesion**, the **rescue** episode for a Troubled operation (site holder, `OpStatus.Physical`, `OnPhysicalResolved`), the Last Known Location with survivors/captives, the events, `RT-PHYS-023…024`, `RT-PHYX-013…014`, `RT-PHYX-020+` | physical tier + owner play | **first player-visible content** |
| **3.3 Procurement fulfillment / physical handoff** (**design direction only**, [§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)) | delivery-mode selection and capability, per-contract orbital charter, freight vs personal carry, the colony handoff and rendezvous episodes, the explicit handoff transaction, the robbery / betrayal consequence hook, future-rivalry seams | decided when 3.2 has merged and been reviewed | owner review before any implementation |

No player-as-contractor board, no NPC-issued market, no full rival simulation in any of them.

**Where each amendment lands.** *Not needed by 3.0* (they need real pawns or groups): persisted composition, cohesion, the
concretization policy and the role verification. *Needed by 3.0 as persisted shape*: `opRole` and `firstEncounterTick`
on `KnownCharacter` and `agedThroughTick` on the binding (so the one format bump carries every shape this document
freezes, O-11), plus the atomic Applier. *Pure policy functions land with the code they serve:* role verdict and
fame-invariance with the first real projection (3.1), concretization and cohesion with groups (3.2).

### 23.1 Re-scoped out of Phase 3 (needs an owner decision)

[IMPLEMENTATION_PHASES § 6](IMPLEMENTATION_PHASES.md#6-phase-3-abstract--physical-lifecycle) lists scope the design above does not
carry. Recommended disposition, **for the owner to confirm**:

| Original Phase 3 item | Recommendation |
|---|---|
| Sponsorship with equipment and **leases** | **Out** (Phase 4 compensation). The `leases` slot is *one of two* equipment seams ([§ 11.2](#112-the-future-equipment-seam-not-built-a-lease-is-not-a-notable-asset)). |
| **Notable-asset / equipment grants** | **Out** (Phase 4 compensation). A separate seam from leases ([§ 11.2](#112-the-future-equipment-seam-not-built-a-lease-is-not-a-notable-asset)). |
| **In-person delivery** (walk-in hand-over) | **Amended (this pass): recommended as Phase 3.3**, design direction only ([§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)); **not** in 3.0–3.2. The owner-validated drop-pod delivery stays. |
| **Contract inheritance and continuation** | **Out** (a contract-lifecycle feature; not required by the physical lifecycle). |
| **Payment-timing variants** (full / half upfront, on delivery, trust-based) | **Out**: a future **Direct Contract** term; current Fixer-mediated brokerage and deposit are unchanged ([§ 27.8](#278-payment-timing-a-future-direct-contract-seam)). |
| **Rival interception**, rival simulation | **Out** (a later competition / social phase); 3.3 keeps only compatible seams ([§ 27.9](#279-future-rival-interception-seam-not-implemented)). |
| **Contract board, player-as-contractor, NPC-issued market** | **Out** of all of Phase 3. |
| **Reputation / fame / capability separation** | **Out** of Phase 3 (a later focused phase); documentation terminology only ([§ 6.10](#610-professional-reputation-fame-and-capability)). |
| **Consequence Engine v1** | **Reduced** to the rescue rule in 3.2; the rest stays. |
| **Promotion** of generic pawns | **In**, in 3.2, under the progressive-concretization policy ([§ 4.5](#45-progressive-concretization)). |
| Events `Contractor.Rescued`, `KnownCharacter.CapturedByPlayer/.Defected/.Lost`, `Player.BetrayedContractor` | **In** as 3.2 needs them. |
| **Deployment Monitor** dev window | **In**, read-only, renamed Episode Monitor, in 3.1. |
| Runtime scenarios | **In**, as the two-tier design above. |
| Ambient visits | **Deferred**, unchanged. |

The Phase 0 spike list (S9, S10, S11, S12, S14, S17) is kept and revised ([§ 25](#25-open-questions-and-spikes)).

---

## 24. Risks

New risks are added to [RISKS](RISKS.md) as R-28 to R-34 (design review) and **R-35 to R-41 (amendment pass)**; existing R-01, R-02,
R-11, R-13, R-15 point here.

| ID | Risk | Mitigation | Proven in |
|---|---|---|---|
| R-28 | **An authority leak:** an abstract writer keeps simulating a physical person (the "healthy Halvard / dead Halvard" bug) | one gate ([§ 3.3](#33-operational-rules)); the writer inventory ([§ 2.3](#23-the-writer-inventory-every-abstract-site-that-must-honour-authority)); `RT-PHYS-011` static enumeration; the validator | 3.0 |
| R-29 | **Exactly-once fails** under duplicate, late or missing wake-ups, or a throw mid-reconcile | plan → commit → flag → publish; set-once outcomes; `consequencesApplied` set last; `RT-PHYS-003/013/014` | 3.0 |
| R-30 | **Registry reservation** unworkable or too costly (*R* × *W* per tick); S9r fails | the small reserved set; the fallback ladder; measurement in the soak | 3.1 (S9r) |
| R-31 | **Observation gaps** (downed, caravan join, resurrection, map-removal pawns, dropped signals) lose a person | bounded polls; positive-evidence-only `Returned`; the load pass; Quarantine | 3.1/3.2 (S21) |
| R-32 | **The physical test tier damages a real colony** | separate menu, a typed **session-only arm**, a dedicated test map by default, a stronger second gate for any home-colony scenario, **no inference of "disposable"**, tagged blast radius, no cleanup of untagged state | 3.1 (S22) |
| R-33 | **Pawn creation on a heavily modded list** fails, is slow, spams relations, or yields a wrong race | `ForceGenerateNewPawn`, `CanGeneratePawnRelations = false`, capability kind selection, contained abort | 3.1 (S23) |
| R-34 | **An unprepared removal** strands reserved/suspended pawns (only if the vanilla-only registry is chosen) | default Network-owned part (self-heals); Prepare clears the registry | 3.1 (S9r, S6) |
| R-35 | **A first projection contradicts established truth** (a "marksman" with Shooting 1, a medic who cannot doctor), or the role machinery over-constrains and generation fails or spikes (vanilla drops validators after 100 tries) | request fields + validators as an optimization, **authoritative verification**, the smallest skill-only correction, abort on failure, bounded K, measured | 3.1 (S25) |
| R-36 | **Identity vs retention:** progressive concretization grows the retained-pawn count past the soft cap, or the cap tempts the Network to replace a person the player met | concretization bounded by the existing named caps; the cap is a performance policy; an encountered member of a living organization is never released (the cap is exceeded instead) | 3.2 (S27) |
| R-37 | **The reconciliation commit crosses services and is half-applied** (the existing casualty, succession and ending paths interleave publication, scheduler calls and fault-swallowing facades) | pure plan, validation, a snapshot-guarded Applier with **no foreign effects**, flag last, idempotent post-commit stages with markers, the fault-injection sweep | 3.0 |
| R-38 | **Truthful aging has side effects:** a decades-long gap yields chronic conditions or an unfit person; the catch-up API misbehaves on a non-ticking pawn; the abstract record disagrees with the pawn | full uncapped catch-up through the vanilla mothball path (S12); refuse an unfit placement with a diagnostic; O-12 owns the abstract consequence | 3.1 (S12) |
| R-39 | **Team cohesion is infeasible or over-trusted** (opinion of an unspawned candidate; friction lists are mod-dependent) or drifts into sanitizing real social history | a derived band; screening only at first generation; never writing relations; a best-effort fallback the owner decides | 3.2 (S26) |
| R-40 | **Reputation, fame and capability stay conflated** and leak into projection (fame treated as skill; equipment gated on visibility) | P3-INV-019 + RT-PHYS-022; terminology corrections now; the separation in a later focused phase | design now; later phase |
| R-41 | **Handoff exploits (3.3):** cargo duplication, ownership ambiguity between contractor and player, a "reform caravan" loophole, a double charge | cargo stays under vanilla possession until **one** explicit, exactly-once transaction; physical reality wins; the existing money ledgers; S28–S30 | 3.3 |
| R-19 (existing) | the audit is build-specific (`1.6.9676.17735`); a 1.6.x update can move internals | every cited API is listed ([Appendix A](#appendix-a-rimworld-16-api-audit)); the physical tier re-checks them | every release |

---

## 25. Open questions and spikes

Do **not** read an OPEN item as a decision. Each names the narrowest experiment.

| ID | Open question | Why it is open | Narrowest experiment / pass criteria | Blocks |
|---|---|---|---|---|
| **S9r** | Does a hidden raw quest (Network-owned part **or** vanilla `QuestPart_ReservePawns` + a vanilla root) give GC protection, redress exclusion, suspension, kill/discard hooks and clean removal at acceptable cost? Which root def is inert? What is the exact removal/cleanup call? | static reading cannot prove it; the per-tick cost is *R* × *W* | N stored pawns; run 200 forced generations incl. `WorldPawnFactionDoesntMatter`; several GC passes; save/load; mod removal; time `GetSituation` at *R* = 150, *W* = 3,000. Pass: none redressed/GC'd, `Suspended`, removal ≤ 3 errors, cost within budget | 3.1 |
| S10 | Creation of a hidden temporary faction outside `QuestGen`: UI visibility, required `leader`, letters, goodwill seeding, automatic removal and nulling | the lifecycle is verified in code; the UI and edge cases are not | create → visit → leave → auto-remove → recreate; no errors; goodwill mirrored | 3.1 |
| S11 | Pawns in `SitePart.things` through a vanilla GenStep **without** `DownedRefugee`'s forced downing; site destroyed first ⇒ pawns to world | the two vanilla steps force a state | a Network-seeded rescue site; save/load before generation; destruction | 3.2 |
| **S12** | Store-time normalization **and truthful aging**: which vanilla APIs heal temporary hediffs and reset needs; does `AgeTickMothballed(elapsed)` (candidate M1, [§ 6.4](#64-truthful-aging-of-a-retained-pawn)) run safely on a *non-ticking* pawn for multi-decade gaps (every birthday, no letter, consistent life stage, no exception); the cost of lazy vs periodic catch-up; modded hediffs that block mothballing | not statically provable | a pawn with mixed hediffs; store → advance 1, 10 and 70 game-years → materialize; chronological and biological age both correct; birthday effects present; no errors | 3.1 |
| S14 | The visit Lord (`LordJob_VisitColony` with a duration): exit, wounded-guest toil, hostility flip, behaviour on a map with no colony | verified structurally, not behaviourally | a visit on a test map and a home map; attack mid-visit | 3.1 |
| S17 | Tag hygiene: no vanilla path parses our tags; copies by other mods | `Pawn_DuplicateTracker` copies none (read); runtime check with Anomaly | duplicate a tagged pawn | 3.1 |
| **S21** | Observation completeness: polls catch downed, caravan join, held transitions, map removal, kidnapped-then-recruited; signals dropped at the cap | the gaps are known; the cadence is a choice | scripted scenarios in the physical tier; no person lost | 3.1/3.2 |
| **S22** | The physical-tier guard: create and remove a **dedicated test map** on a hidden `MapParent`; the **session-only arm** (cleared on load and after a run, never persisted); which scenarios *truly* need a home colony and what the second gate looks like | the Network must not infer whether a save matters (a low-wealth save may be a real early colony) | build the arm flow and the test-map lifecycle; assert the arm is cleared on load; assert no persisted field; a home-colony scenario refuses without the second confirmation | 3.1 |
| **S23** | First-creation pins and modded races: name mapping, gender/age pins, kind fallback, `CanGeneratePawnRelations = false`, generation time | mod-list dependent | generate N pawns across a heavy list; no relation spam; contained failures | 3.1 |
| S24 | The save/load matrix ([§ 16.1](#161-save-at-every-point)) on real saves | no save automation exists | owner-assisted checklist, one row per scenario | 3.1 |
| **S25** | **Role-constrained creation** ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)): how often validators reach their 100-try cap; the cost of K attempts on a heavy mod list; the role → existing-kind mapping; modded races that cannot satisfy a role; whether `Dispose` of a rejected candidate leaves residue in vanilla caches (ideo membership, faction) | mod-dependent; the validator tail is a vanilla fall-through | generate N pawns per role across a heavy list with and without modded races; assert every returned pawn satisfies its role; measure attempts and time; check for residue after dropping rejected candidates | 3.1 |
| **S26** | **Team cohesion** ([§ 6.9](#69-team-cohesion)): is `OpinionOf` meaningful for an unspawned candidate (situational thoughts may need a map); how often generated crews are hostile; which traits and ideologies cause friction | not statically provable | generate N crews per band; record pairwise opinions both ways; test the screen; check that `FixedIdeo` removes ideological friction | 3.2 |
| **S27** | **Encounter evidence** ([§ 4.5.3](#453-encounter-evidence-observed-at-reconciliation-never-scanned)): are `PlayLog.AnyEntryConcerns`, `BattleLog.AnyEntryConcerns` and the GC-reason check cheap and reliable for ≤ 8 members, in a heavily modded game; what "player-visible map" is in code | the methods are public; their reliability under mods is unknown | scripted encounters (a conversation, a fight, a capture); assert each evidence flag; time the lookups | 3.2 |
| **S28** | **A right-click command on a contractor representative without Harmony** (3.3): `FloatMenuMakerMap` builds its provider list by reflection over every non-abstract `FloatMenuOptionProvider` subclass (`FloatMenuMakerMap.cs:12–23`), so a subclass in our assembly needs no Def and no patch | verified structurally, not behaviourally | a provider that adds one option on a tagged pawn; cheap `TargetPawnValid`; absent after mod removal | 3.3 |
| **S29** | **Physical cargo at a handoff** (3.3): the representation of contractor-held cargo (faction-owned items in carrier pawns' inventories, a pack animal, a container), and what vanilla does with it when the map is removed or the carriers die | not statically provable | a scripted handoff on the test map: pay, decline, rob, abandon; assert exactly-once transfer and no duplication | 3.3 |
| **S30** | **A rendezvous site** (3.3): a temporary `MapParent` for a meetup; caravan arrival and departure; retention and removal of the map; the player leaving without completing | needs real maps | a rendezvous on the test map; the player's caravan arrives, transacts, declines, leaves | 3.3 |
| O-2 | The **concretization** thresholds by organization size, the promotion thresholds, the retained-pawn cap, and whether a promoted *held* person also joins the full `knownMembers` list ([§ 4.5](#45-progressive-concretization)) | tuning | the soak | 3.2 |
| O-3 | Equipment tier → kind/loadout selection; the `condition` step on gear loss | mod-dependent | S23 + playtest | 3.1 |
| O-6 | Resurrection detection cadence for dead characters that keep a `PawnRef` | no hook | opportunistic + bounded sweep; measure | 3.2 |
| O-7 | Name and generation behaviour for modded races | mod-dependent | S23 | 3.1 |
| O-14 | A long-suspended pregnancy; Ideology/Royalty titles on generated pawns | edge | S23 | 3.2 |
| O-8 | The capability band → role-skill-floor table, and whether a composition seat carries a **grade** (the elite-bodyguard case, [§ 6.10](#610-professional-reputation-fame-and-capability)) | tuning; the model question is deferred | S25 + playtest | 3.1 / later |
| O-9 | Whether an *observed physical loss* needs a per-role `vacant` count, or the abstract apportionment suffices ([§ 6.7.2](#672-pinned-seats-and-apportionment)); whether the abstract resolver should ever become role-aware | only shown to matter by a visible contradiction | the soak and playtest | 3.2 |
| O-10 | The **publish dedupe** mechanism: a deterministic event key rejected by History, or a pending-event list ([§ 15.6](#156-failure-semantics-of-the-commit-service-by-service)) | a cosmetic defect either way, but it should be decided | 3.0 design review | 3.0 |
| O-11 | The format-bump policy across released subphases ([§ 16.5](#165-migration-and-version-implications-the-number-is-not-chosen-here)) | a release decision | the owner | 3.0 |
| O-12 | What truthful aging means for the **abstract** record (a contractor who is 70 and chronically unfit: retire, remain Active but un-materializable, or die of age) | a content / lifecycle decision | the owner | 3.1 / later |
| O-15 | Freight capability model, the charter-fee economics (a pass-through sink or contractor income), and handoff time windows ([§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)) | design | 3.3 design review | 3.3 |
| O-17 | The separation of professional reputation, visibility and regional scope: the later focused phase's design and migration ([§ 6.10](#610-professional-reputation-fame-and-capability)) | design | a focused phase before compensation work | later |
| **Harmony** | Is Harmony truly avoidable for every required event? | **Yes for 3.1 and 3.2** (§ 14.2); the three hook gaps are polls | if S21 fails: document the one method, specify a postfix, **do not implement** | — |

---

## 26. Acceptance criteria

### 26.1 This design pass (docs only)

README and the QA/status documents record the owner's runtime validation truthfully · this document and the API audit
exist and cite the code · every unknown is marked OPEN with a spike · no production code, no DLL, no Harmony, no new
save field, no save-version change · the invariants, the subphasing and the slice boundaries are explicit · ADR-048 and
ADR-049 and risks R-28 to R-34 are recorded.

**The amendment pass** adds: all ten amendments are folded into this one document and the other docs agree (no
competing statement survives; [Appendix F](#appendix-f-amendment-log) lists what changed and what became invalid) ·
the pawn-generation, aging, opinion and float-menu APIs are audited from the decompiled source ·
P3-INV-017 … 028, RT-PHYS-020 … 028, S25 … S30 and O-8 … O-17 are recorded · the current `FameBand` / `PublicReputation`
conflation is identified as *implemented truth* versus *future design* · **S20 is still NOT RUN, the Phase 2.9 owner
runtime pass is preserved unchanged, and no code, DLL, Harmony, save field or save version changed.**

### 26.2 Phase 3 as a whole (definition of done)

1. P3-INV-001 … 028 (those that apply to the implemented subphases) hold in the headless suite, the safe runtime tier and the physical tier.
2. A person is never advanced by two layers; the gate is complete (RT-PHYS-011).
3. Every episode ends in a terminal outcome per member, exactly once; no scenario reconciles twice or never.
4. Save/load at every row of [§ 16.1](#161-save-at-every-point) passes the owner's checklist; load never generates, spawns or destroys.
5. A dead person never returns; a held person is never abstracted; a named person never gets a second pawn.
6. Idle cost is **zero**; the Phase 3 soak meets the budgets or the PR states the measured numbers and why.
7. Prepare-for-removal and an unprepared removal leave a consistent game; no pawn is deleted.
8. **Full Safe Regression is unchanged in safety**; the physical tier cannot be pressed by accident.
9. No Harmony; no named-mod logic; no Phase 4 behaviour.
10. The owner has run the physical tier and the 3.1 checklist, and the 3.2 content, in a real game.

### 26.3 Per subphase

| Subphase | Done when |
|---|---|
| 3.0 | the 23 `RT-PHYS` cases of this stage (001–019, 025–028) pass headless, **including the fault-injection sweep (a throw after every Applier step restores the exact fingerprint) and the parity test with the abstract casualty path**; the safe in-game tier is green in the owner's colony with **no change to the colony** (the fingerprint covers the new store); the save bumps once and old saves load unchanged; the validator, compaction and prepare-for-removal settle are tested; mutation checks (an abstract writer bypassing the gate; a reconcile that flags first; a publication, scheduler or vanilla call inside the Applier; a restore that omits the id allocator; a signal handler that reconciles inline; `Dead` overwritten) are each caught by a named test |
| 3.1 | the slice in § 22 runs on the physical tier's own test map with the blast-radius proof green and the session arm behaving as specified; the rematerialization shows the same `Pawn`, **truthfully older**; `RT-PHYS-020…022` and `RT-PHYX-001…012` pass; S9r, S10, S12, S14, S21, S22, S23, S24, S25 are recorded as PASS/PARTIAL/FAIL with their consequences |
| 3.2 | the held-person matrix and the rescue scenario pass in the physical tier; a Troubled operation with a rescue episode never also resolves abstractly; a five-person crew's members are the same people on a second visit while a company keeps no roster (`RT-PHYS-023`, `RT-PHYX-014`); the cohesion probe is recorded (S26, `RT-PHYS-024`); S27 is recorded; the soak numbers are attached |
| 3.3 | **design direction only**: accepted by the owner; nothing is implemented, and S28–S30 are not run until a 3.3 stage is approved |

---

## 27. Phase 3.3: procurement fulfillment and physical handoff (design direction)

> **Status: design direction only.** Nothing in this section is implemented, specified to the level of code, tuned, or
> scheduled. It exists so that 3.0 to 3.2 do not make it impossible, and so that the owner can approve or amend its
> shape *before* any 3.3 work starts. It does **not** change current Procurement, the economy or any tuning, and it
> builds no player-as-contractor board, NPC-issued market, vehicle simulation or rival.

### 27.1 Why this is Phase 3-family

Procurement fulfillment by physical handoff is the **first natural gameplay consumer of the completed physical
lifecycle**: an Episode whose purpose is `Delivery`, with the same authority, identity, composition, concretization and
reconciliation machinery as a visit or a rescue. The first design pass recommended "in-person delivery: out of 3.0 to
3.2, a later subphase or Phase 4". That is **amended**: it becomes a named subphase, **3.3**, *after* 3.2's owner gate, so
it can be reviewed in its own right and so its seams are visible now. Leases, notable-asset grants, payment-timing
variants and rivalry stay out.

### 27.2 What exists today (verified)

| Fact | Where |
|---|---|
| Delivery is **vanilla drop pods to a player home map**: `IDelivery.Plan(preferredMapId, seed)` finds a landing without side effects; `Deliver(plan, payload, seed)` creates the committed payload only when the pods launch | `Ports.cs:258–270`, `DeliveryAdapter` |
| The **balance is charged only once a drop plan exists**; "money never moves for goods that cannot land" | [IMPLEMENTATION_PHASES § 5](IMPLEMENTATION_PHASES.md#5-phase-2-contractors-abstract--procurement) |
| The Phase-2-safe payment default: **the contractor holds the goods** (`AwaitingPayment`, 7-day grace) or hands over what the deposit covered. This is already the abstract form of "cargo stays with the contractor until payment" | same |
| Drop-pod delivery of a large order is **owner-observed**: a legacy contract delivered 10,000 / 10,000 Plasteel by vanilla pods after a payment hold and recovery | [RUNTIME_TESTING § 15.4](RUNTIME_TESTING.md) (an observation, **not** formal spike S20) |
| A durable orbital capability exists as a mobility mode and the derived `SpacerCapable` Tag, **never derived from fame** | [CAREERS § 8](CAREERS.md) |

So 3.3 adds modes *beside* the owner-validated orbital path; it replaces nothing.

### 27.3 The three delivery modes

| Mode | What happens | Status |
|---|---|---|
| **1. Orbital / drop pod** | the existing `IDelivery` | exists; unchanged |
| **2. Colony handoff** | the contractor's representatives physically arrive at the player's colony (a `Delivery` episode, [§ 6.7](#67-organization-and-mission-composition): a Negotiator or representative plus Security); the cargo is physically present **under the contractor's possession**; the player completes an explicit transaction | design |
| **3. Rendezvous** | the player travels to a physical meetup site; the contractor party is there with the cargo; the transaction is explicit | design |

The mode is a **contract term** offered at quote/accept time and **constrained by capability, logistics and
technology** (never by fame). Illustrations:

* A **high-tech contractor with native orbital logistics** can use orbital delivery naturally; the other modes stay
  optional.
* A **low-budget ground contractor** cannot, and asks the player to choose: **A.** "Bring it to my colony" (colony
  handoff) or **B.** "We'll meet you at a rendezvous", and may *optionally* be paid for orbital delivery (§ 27.5).

### 27.4 Personnel mobility is not freight capability

Two different capabilities, modelled separately:

| Axis | Meaning | Today |
|---|---|---|
| **Personnel mobility** | how a *person or party* gets somewhere | the existing `mobility` (mode, range), `SpatialState` |
| **Cargo / freight capability** | how much, and by what means, *goods* move: hired hauler, pack transport, contracted freight, cargo shuttle, orbital freight, an abstract logistics train | **new, derived** from form, equipment, funds and technology (like composition: established at first use, not persisted for everyone) |

**A Solo does not carry 10,000 Plasteel in his backpack.** A Solo plausibly *procures* it through contracted freight (a
hired hauler, a logistics train), and that transport **stays abstract until the handoff**: no persistent trucks,
ships or vehicle simulation. At the handoff the physical things are the *representatives* (pawns through the
lifecycle) and the *cargo as real items under their possession* (S29 decides where those items physically live).

### 27.5 The optional "Additional Funds for Orbital Delivery" tick

| Rule | Detail |
|---|---|
| a **per-contract term** | a checkbox on the quote/accept step, **not** a contractor upgrade |
| **no surcharge for native capability** | if the contractor already has suitable orbital logistics, the option is absent or shown as included |
| **a temporary charter otherwise** | ticking it *buys the capability for this contract only* and **raises the total contract payment**; it does **not** change the contractor's `mobility` or any durable capability |
| purpose | speed and reliability, less physical drama |
| money | a typed line inside the **existing** quote → deposit/balance → ledger machinery (so exactly-once, refund-on-void and clawback apply as for any contract money); whether the fee is contractor income or a pass-through sink is **OPEN O-15** |

### 27.6 The physical handoff transaction

```
 Scheduled ─► Staged  (representatives and cargo present; cargo under the CONTRACTOR's possession)
                 │
                 └─► AwaitingTransaction ─► Settled(Paid | Declined | Robbed | Abandoned | ContractorLost | Void)
```

1. **Cargo ownership stays with the contractor / contract until the transaction.** In vanilla terms the cargo is
   *faction-owned items carried by the contractor party* (S29). The player's "form caravan / job complete" flows can only
   take the **player's** possessions, so **no special lock is needed**: the goods were never the player's.
2. **The transaction is deliberate and explicit**: most likely a right-click command on a contractor representative.
   In 1.6 this needs **no Harmony and no Def**: `FloatMenuMakerMap` builds its provider list by reflection over every
   non-abstract `FloatMenuOptionProvider` subclass (`FloatMenuMakerMap.cs:12–23`), and `GetOptionsFor(Pawn, …)` is
   public (S28). One confirmation shows the contract, the balance due and the cargo.
3. **One atomic Network commit.** Payment (existing payment port and ledger), ownership transfer (the real items handed
   to the player's side) and the contract's delivery state are *planned and validated* as in [§ 15](#15-reconciliation-algorithm).
   The real-world side effects are ordered like today's delivery: **plan first** (representative present, cargo present,
   the player able to receive), **charge**, **hand over**; a failure after the charge takes the existing refund path.
   `Settled` is reached **exactly once** (P3-INV-026).
4. **A terminal handoff state is required** before the contract's normal job-complete behaviour exists for that cargo.
   "Enter the map, immediately reform the caravan, magically take the contractual goods" fails by construction.
5. **Physical reality wins** (P3-INV-027). The player may choose violence or robbery: the cargo can be taken normally;
   contractor deaths, injuries and captures **reconcile through the ordinary episode machinery**; relationship, history
   and any consequence belong to later content and services (`Player.BetrayedContractor` is already a candidate event).
   There is **no invulnerability and no ownership lock**.
6. Outcomes (hooks only): **Paid** → delivered · **Declined** → the contractor leaves with the goods, per the existing
   `AwaitingPayment` semantics · **Robbed** → a contract failure with a distinct cause · **Abandoned** (the player never
   arrives or leaves; a time-out) → the existing grace · **ContractorLost** → the existing failure paths.
7. After the transaction the contractor may leave through the visit Lord's exit; using colony comms or rest is future
   content, not designed here.

### 27.7 The rendezvous

A physical meetup place: a temporary site/map (S30) at a location derived from the contractor's spatial anchor and
route (the Network already knows where contractors are, [SPATIAL](SPATIAL.md)); a time window and an expiry (the site
pattern already uses vanilla timeouts); the player travels there as a vanilla caravan; the same handoff machine runs on
the site map. If the player leaves without a terminal state the handoff is `Abandoned`. Because the rendezvous is a
*real place*, the ordinary world (weather, other factions) applies, which is exactly what makes future rival
interception natural (§ 27.9).

### 27.8 Payment timing: a future Direct Contract seam

Current **Fixer-mediated contracts keep the current brokerage and deposit model**; nothing changes. A contractor
demanding unusual payment timing, **full payment upfront, half upfront, payment after delivery, or trust-based delayed
payment**, is most appropriate for **future Direct Contract relationships**. The design seam is a contract *term*
(`paymentTiming`, default = the Fixer model) read by the money machinery; "on delivery" is what the handoff
transaction naturally supports, and "trust-based delayed" needs Obligations (Phase 5, as the already-deferred debt
option does). **Not implemented, not tuned.**

### 27.9 Future rival interception seam (not implemented)

> **Spatial overlap creates opportunity, not automatic encounter.**

A rival NPC may interfere only if it has **all** of: *spatial opportunity*, *plausible knowledge*, *motive*,
*availability* and *capability*. Possible objectives (a list, not a design): intercept a player caravan, follow the
player to a rendezvous, attack the contractor, seize cargo, sabotage a handoff, demand the player abandon the
contractor, offer a buyout, warn or intimidate, **or do nothing**. Constraints: **no omniscient actors**, **no
invisible "rival detection radius"** exposed to the player, **no random teleport ambush**, and direct or private
contracts naturally leak less than public, Fixer-brokered work.

What Phase 3.3 must keep **compatible**: (a) Network truth about *where and when* a handoff happens (the episode's
place, the schedule, the spatial anchors and routes) so overlap can be reasoned about; (b) the Knowledge model is the
*gate* (who could know about this handoff follows from who brokered it and the contractor's discretion doctrine);
(c) interference reuses Episode purposes and the same reconciliation; (d) any rival evaluation is **event-driven at
scheduling time, bounded, never a per-tick scan**. All of it belongs to later competition, social and rivalry phases.

### 27.10 Reuse and additions

| Reuses | Adds (design only) |
|---|---|
| the Episode, the authority gate, identity, role and mission composition, concretization (a representative who returns is the same person), atomic reconciliation, `IDelivery` (orbital), the payment port and ledgers, `SpatialState`, the site/timeout pattern, Knowledge | a delivery-mode contract term, a derived freight capability, a typed charter-fee line, the `Delivery` episode purpose and its handoff states, the right-click provider, the rendezvous site, events (`Delivery.HandoffSettled`, `Player.BetrayedContractor`) |

### 27.11 Gates

3.2 merged and owner-reviewed · S28 (a right-click command without Harmony), S29 (physical cargo), S30 (a rendezvous
site) · the O-15 decisions · **owner review before any implementation.** Not before.

---

## Appendix A: RimWorld 1.6 API audit

Method: every row was read in the decompiled `Assembly-CSharp 1.6.9676.17735` (file and approximate line in the
second column). Nothing is from memory. *Safe w/o Harmony* is **Yes** when a public/protected member, a signal, a
component callback or a bounded poll provides it. Risk is after the recommended mitigation.

| # | Requirement | RimWorld 1.6 API / class / callback | Observed semantics | Safe w/o Harmony? | Save/load behaviour | Risk | Recommendation |
|---|---|---|---|---|---|---|---|
| A1 | A pawn's identity | `Thing.thingIDNumber`, `Thing.ThingID` (= `def.defName + number`), `ThingIDMaker.GiveIDTo`, `Thing.ExposeData` `"id"` (`Thing.cs:19,392,1225`) | unique int per save; recreated, duplicated or replaced pawns get new IDs; **no global ID→pawn lookup** outside the load directory | Yes | saved as `id`; stable for the same object | Low | a *binding attribute*, checked by pointer; never identity |
| A2 | Persist a pawn reference | `Scribe_References.Look<T>(ref x, label, saveDestroyedThings)` (`Scribe_References.cs`); unresolved ⇒ warning (`LoadedObjectDirectory.cs:130`) | **default writes `null` for a destroyed thing**; a dead pawn is `Destroyed` | Yes | the pawn is deep-saved by exactly one owner; the reference is non-owning | Med | `saveDestroyedThings: true`; `null` + prior existence = evidence of loss |
| A3 | Read a pawn's state | `Pawn.Spawned`, `.Dead`, `.Downed` (`health.Downed`), `.Discarded`, `.Destroyed`, `.IsPrisoner*`, `.IsSlave*`, `.HostFaction`, `.guest`, `.Corpse`, `.MapHeld`, `.ParentHolder`, `.InContainerEnclosed`, `.SpawnedOrAnyParentSpawned` (`Pawn.cs:231,518–648,878–880`) | public read-only | Yes | saved with the pawn | Low | the `Observe` port reads only these |
| A4 | Create a pawn | `PawnGenerator.GeneratePawn(PawnGenerationRequest)`; `ForceGenerateNewPawn`, `CanGeneratePawnRelations`, `FixedBirthName`, `FixedLastName`, `FixedGender`, `FixedBiologicalAge`, `ForcedTraits`, `ForcedXenotype`, `ForceNoGear` (`PawnGenerationRequest.cs`) | **may return an existing world pawn** (redress) unless `ForceGenerateNewPawn`; a generated pawn is not put in `WorldPawns` (only failure paths pass it with `Discard`) | Yes | an unspawned, unreferenced generated pawn is **not saved** | **High** | force a new pawn; bind before spawn; relations off |
| A5 | Redress hazard | `PawnGenerator.IsValidCandidateToRedress`, `ChanceToRedressAnyWorldPawn`, `RedressPawn` (`PawnGenerator.cs:368,1146,259`) | candidates: `Free`, same race, same faction (or `WorldPawnFactionDoesntMatter`); chance `min(0.02 + 0.001 × FreeCount, 0.8)`; **mutates** the pawn | n/a (a hazard) | n/a | **High** | never leave a retained Network pawn `Free` |
| A6 | Spawn | `GenSpawn.Spawn`, `Pawn.SpawnSetup` (`Pawn.cs:1358`) | removes the pawn from `WorldPawns`; **discards** a pawn spawned in an invalid state; replaces a dead pawn with a corpse | Yes | n/a | Med | verify `Spawned` after spawn; contained abort |
| A7 | World pawns | `WorldPawns.PassToWorld(pawn, mode)`, `RemovePawn`, `GetSituation`, public `ForcefullyKeptPawns` (`WorldPawns.cs:200,236,267,71`) | `Decide`/`KeepForever`/`Discard`; `KeepForever` pawns remain `Free`; passing a spawned pawn errors | Yes | `pawnsAlive`, `pawnsMothballed` saved `Deep`; `pawnsDead` with `saveDestroyedThings`; pawns with a null def are dropped on load with an error | Med | `Decide` only; **never `Discard`** |
| A8 | Faction rewrite on pass | `Pawn.Notify_PassedToWorld` (`Pawn.cs:1851–1882`) | a `Free` humanlike pawn with a null, player or Ancients faction gets a **random** non-colony faction | n/a | n/a | Med | reserve first, then pass; membership is the Network's |
| A9 | World-pawn GC | `WorldPawnGC.GetCriticalPawnReason`, public `AccumulatePawnGCDataImmediate` (`WorldPawnGC.cs:174`); every 15,000 ticks, incremental | kept for: `Colonist` (`EverBeenColonistOrTameAnimal`), `Spawned`, `CorpseExists`, `InPlayLog`/`InBattleLog`, `InActiveTale`, `Kidnapped`, `CaravanMember`, `TransportPod`, `FactionLeader`, **`ForceKept`**, **`ReservedByQuest`**; relations and memories of kept pawns are kept | Yes | n/a | Med | rely on it; never discard ourselves |
| A10 | Mothballing | `WorldPawns.ShouldMothball`, `DefPreventingMothball`, `DoMothballProcessing` (`:365–465`) | a non-permanent hediff prevents mothballing; mothballed pawns tick in bulk every 15,000 ticks | Yes | n/a | Low | store-time normalization (S12) |
| A11 | Suspension | `Pawn.Suspended` (`Pawn.cs:1112`); `TickInterval`, `TickMothballed` (`:1618–1750`); `Need.IsFrozen` (`Need.cs:63`) | `ReservedByQuest` ⇒ suspended ⇒ **no health, needs, jobs or biological aging** | Yes | n/a | Med | truthful, uncapped catch-up before any observation ([§ 6.4](#64-truthful-aging-of-a-retained-pawn)) |
| A12 | Reserve a pawn | `Quest.QuestReserves(Pawn)`, `QuestPart.QuestPartReserves(Pawn)`, `QuestManager.IsReservedByAnyQuest`, `QuestUtility.IsReservedByQuestOrQuestBeingGenerated` (`QuestUtility.cs:493`), vanilla `QuestPart_ReservePawns` | iterates active quests × parts; `Historical` quests do not reserve; cost ∝ list length × world pawns per tick | Yes | quests saved by vanilla; **a quest with a null root is dropped on load** (`QuestManager.cs:181`) | **High** | S9r: Network-owned part (default) vs vanilla part |
| A13 | Quest hooks | `QuestManager.Notify_PawnKilled` (Ongoing quests only), `Notify_PawnDiscarded` (all), `Notify_FactionRemoved` (`:239,163,261`) | called from `Pawn.Kill` / `Pawn.Discard` / faction removal | Yes | n/a | Low | secondary detection paths |
| A14 | Signals | `QuestUtility.SendQuestTargetSignals`, `SignalManager.RegisterReceiver/SendSignal` (≤ 3,000 per frame) (`SignalManager.cs`) | a global broadcast of `<tag>.<Signal>`; receivers are **not** persisted; excess signals are dropped | Yes | tags saved with the Thing (`Thing.questTags`, `:41,1291`) | Med | wake-ups only; re-register at start-up |
| A15 | Death | `Pawn.Kill` (`Pawn.cs:2088`), `Pawn.Destroy` (`:2341`), `Thing.Kill/Destroy` (`Thing.cs:1038–1099`) | corpse only if spawned, in a caravan or in a container; the pawn becomes `Destroyed` and is passed to the world as dead; **`Destroyed` and `Killed` signals fire mid-kill**, `QuestManager`/`FactionManager` after | Yes | a dead pawn is `Destroyed` ⇒ default reference saves `null` | **High** | persist death at once; handler only enqueues |
| A16 | Exit the map | `Pawn.DeSpawn` (`:2400`), `Pawn.ExitMap` (`:2505`) | `ExitMap` passes the pawn to the world, then sends `LeftMap` | Yes | n/a | Low | `LeftMap` is the exit evidence |
| A17 | Map removal | `MapDeiniter.Deinit/PassPawnsToWorld/CleanUpAndPassToWorld`; `MapParent.PostMapGenerate/Notify_MyMapAboutToBeRemoved/Notify_MyMapRemoved`; `WorldObjectComp.PostMyMapRemoved` | **all** map pawns are passed to the world; `LeftMap` is sent **only** for colonists and player-hosted pawns; a hostile parent faction **kidnaps** colonists | Yes | n/a | **High** | the site comp + `MapRemoved` signal, then observe each pawn |
| A18 | Kidnapping | `KidnappedPawnsTracker.Kidnap/RemoveKidnappedPawn`, `Faction.kidnapped` (`KidnappedPawnsTracker.cs`) | a list of `Reference`s (destroyed pawns removed on save); sends `Kidnapped`; **MTB ≈ 30 days the captor recruits the pawn** | Yes | saved by reference in the `Faction` | Med | observe; expect a later faction change |
| A19 | Caravans | `Caravan.pawns` (`ThingOwner<Pawn>`), `AddPawn`, `Notify_MemberDied`, `PostRemove`; `CaravanUtility.GetCaravan/IsCaravanMember` (`Caravan.cs`) | pawns saved `Deep` in the caravan; a member death adds a corpse; **no signal on `AddPawn`** | Yes (poll) | caravan owns the pawn | Med | poll `GetCaravan()` |
| A20 | Transport pods | `ActiveTransporterInfo`, `TravellingTransporters`, `PawnUtility.IsTravelingInTransportPodWorldObject` (`PawnUtility.cs:92`) | pawns held in a world object's `ThingOwner` | Yes | saved in the world object | Low | classify `InTransport` |
| A21 | Prisoner / guest / slave | `Pawn_GuestTracker` (`GuestStatus` Guest/Prisoner/Slave, `HostFaction`, `joinStatus`, `Released`), `ChangedHostFaction` (`Pawn_GuestTracker.cs:553`) | the status lives in the pawn | Yes | saved with the pawn | Med | classify; signals as wake-ups |
| A22 | Arrest, rescue, recruit, release, enslave | `Arrested` (`JobDriver_TakeToBed.cs:102`), `Rescued` (`:186`), `Recruited` (`InteractionWorker_RecruitAttempt.cs:197`), `Released` (`JobDriver_ReleasePrisoner.cs:40`), `Enslaved` (`InteractionWorker_EnslaveAttempt.cs:50`), `Banished` | tagged signals | Yes | n/a | Low | wake-ups; state is truth |
| A23 | Temporary faction | `FactionGenerator.NewGeneratedFaction(WithRelations)`, public `Faction.temporary`, `hidden`, `FactionManager.Add`; private `Remove`, `FactionCanBeRemoved` (`FactionManager.cs:93,109,398`) | removed when **nothing is spawned under it**, no caravan holds it, no world object is its own, no quest reserves it; **every** pawn's faction becomes null | Yes | n/a | Med | **one per episode**; S10 |
| A24 | Faction relations | `Faction.SetRelation`, `TryAffectGoodwillWith`, `GoodwillWith`; `canSendHostilityLetter: !temporary` (`Faction.cs`) | temporary factions suppress hostility letters | Yes | n/a | Low | seed and mirror, never live |
| A25 | Visit AI | `LordMaker.MakeNewLord`, `LordJob_VisitColony(Faction, IntVec3, int?)`, `LordJob_TravelAndExit`, `Lord.Notify_PawnLost(PawnLostCondition)`, `Lord.ownedPawns` (`Reference`) | handles dangerous weather, wounded guests, exit; lords end when pawns leave | Yes | the Lord is saved with the map | Med | vanilla; S14 |
| A26 | Entry cell | `RCellFinder.TryFindRandomPawnEntryCell`, `TryFindRandomSpotJustOutsideColony`, `CellFinder.TryFindRandomEdgeCellWith` | public static | Yes | n/a | Low | |
| A27 | Site pawn holder | `SitePart.things` (`ThingOwner`, `Deep`), `SitePart.PostDestroy`, `GenStep_DownedRefugee`, `GenStep_PrisonerWillingToJoin`, `ThingOwner.ClearAndDestroyContentsOrPassToWorld` | steps consume `things[0]`; **`DownedRefugee` damages the pawn until downed**; destruction passes pawns to the world | Yes | pawns saved in the site part | **High** | S11; Network-seeded rescue state |
| A28 | Resurrection | `ResurrectionUtility.TryResurrect` (`:29`) | the **same** `Pawn`; requires `!Discarded`; re-spawns if its corpse was spawned; **no signal** | n/a | n/a | Med | observe; never initiate |
| A29 | Hediff permanence | `HediffUtility.IsPermanent`, `HediffComp_GetsPermanent`, `Hediff_MissingPart`; null-def hediffs dropped (`HediffSet.cs:263`) | classification of permanent vs temporary | Yes | hediffs of a removed def are dropped with an error | Low | identity-defining vs noise |
| A30 | Gear | `Pawn.DropAndForbidEverything`, `Pawn.Strip`, `PawnKindDef.destroyGearOnDrop` | vanilla loot rules | Yes | n/a | Low | no Network rule |
| A31 | Provenance by comp | `ThingWithComps.InitializeComps` (from `def.comps`, `:193`), `PostExposeData` (`:237`) | comps exist only if the **Def** lists them | n/a | a removed comp class leaves ignored saved values | Med | **rejected** |
| A32 | Tag copying | `QuestPart_ReplaceLostLeaderReferences` (copies `questTags`), `Pawn_DuplicateTracker` (copies none, per the code read) | | Yes | | Low | handlers compare bindings (S17) |
| A33 | Map-level callback | `MapComponent.MapRemoved/MapGenerated` | auto-instantiated per map; a missing class on removal is an error per map | Yes | | Low | **not used**; the site comp covers it |
| A34 | Teleport | `Pawn.teleporting`, `WorldPawnSituation.Teleporting` | set during mod/vanilla teleports; no signal | Yes (poll) | | Low | observe `Spawned` elsewhere |
| A35 | Other world-pawn situations | `WorldPawnSituation` FactionLeader, ForSale, Borrowed, StartingPawnLeftBehind | | Yes | | Low | classify `WorldOther` ⇒ `Pending` |
| A36 | Constrain a generated pawn | `PawnGenerationRequest`: `MustBeCapableOfViolence`, `ForcedTraits`, `ProhibitedTraits`, `ValidatorPreGear`, `ValidatorPostGear`, `FixedIdeo`, `ForcedXenotype`, `AllowedXenotypes`, `BiologicalAgeRange`, `FixedBiologicalAge`, `OnlyUseForcedBackstories`, `ForceNoBackstory` (`PawnGenerationRequest.cs:31–149`) | public properties; `MustBeCapableOfViolence` is enforced by discarding the candidate (`PawnGenerator.cs:967`); validators run before gear (`:1009`) and after gear (`:1023`) | Yes | the request is transient | Med | use as an optimization; **always re-verify** the returned pawn |
| A37 | The generation retry loop | `PawnGenerator.GenerateNewPawnInternal` (`:681–724`) | up to 120 tries; scenario requirements ignored from try 70; **validators ignored from try 100** (each with a `Log.Error`); `null` after 120 | Yes | n/a | **High** | a validator is never a guarantee ([§ 6.8](#68-role-constrained-creation-validate-then-the-smallest-correction)) |
| A38 | Kind-level constraints, and why roles map to existing kinds | `PawnKindDef.skills` (`List<SkillRange>`), `requiredWorkTags`, `minBestSkillLevel`, `minTotalSkillLevels`, `forcedTraits`, `disallowedTraits(WithDegree)`, `weaponTags`, `weaponMoney`, `apparelTags`, `apparelMoney`, `techHediffsMoney`, `itemQuality` (`PawnKindDef.cs:33–291`); `Scribe_Defs.Look(ref kindDef, "kindDef")` (`Pawn.cs:4571`) | enforced inside generation (`PawnGenerator.cs:973–1002`); **the kind is saved by def name** | Yes | a runtime-created kind would not resolve at load | **High** | choose among **existing** kinds only; never create kinds |
| A39 | Skills and passions | `PawnGenerator.GenerateSkills`, `FinalLevelOfSkill` (`:1846–2015`); `SkillRecord.Level`, `GetLevel(includeAptitudes)`, `Aptitude`, `passion`, `TotallyDisabled`, `Notify_SkillDisablesChanged` (`SkillRecord.cs:15,56–66,85,334,408`) | **the `Level` getter returns base + aptitudes, the setter writes the base level clamped to 0–20**; `passion` is a public field; a kind's `skills` range re-rolls an out-of-range value | Yes | saved with the pawn | Med | correction targets the effective level through the base, raise only |
| A40 | Backstory, traits, incapabilities | `PawnBioAndNameGenerator.cs:112–115,180–186`; `Pawn_StoryTracker.Childhood/Adulthood` setters (`:47–70`); `TraitSet.GainTrait/RemoveTrait` (`:258,325`); `Pawn.WorkTagIsDisabled`, `CombinedDisabledWorkTags`, `Notify_DisabledWorkTypesChanged` (`Pawn.cs:4513–4525`) | the generator honours `kind.requiredWorkTags`; the story setters clear **only** `backstoriesCache`; trait add/remove refresh caches themselves | Yes | saved with the pawn | Med | **never correct an incapability** (it is identity); reject the candidate |
| A41 | Weapon preference and the Brawler trait | `PawnWeaponGenerator.TryGenerateWeaponFor` (`:55–92`); `TraitDefOf.Brawler` uses in `FloatMenuOptionProvider_Equip.cs:55`, `Alert_BrawlerHasRangedWeapon.cs:17`, `Building_OutfitStand.cs:748` | the weapon comes from the kind's `weaponTags` / `weaponMoney`; none if violence is disabled; no ranged weapon if Shooting is disabled; **no Brawler check in the generator**: vanilla only refuses to equip and raises an alert | Yes | n/a | Med | role → kind mapping (O-3); a Marksman prohibits Brawler |
| A42 | Disposing of a rejected candidate | `PawnGenerator.DiscardGeneratedPawn` (**private**, `:1109`) → `WorldPawns.PassToWorld(pawn, Discard)`; `GeneratePawn` does not register a returned pawn in `WorldPawns`; `Faction.Notify_PawnJoined` only informs ideology membership (`Faction.cs:890–900`) | vanilla discards its own rejects; a candidate the Network rejects after `GeneratePawn` returned is an unreferenced, unspawned object | Yes | an unspawned, unreferenced pawn is not saved | Med | S25 checks for residue |
| A43 | Relations at generation | `GeneratePawnRelations`, gated by `CanGeneratePawnRelations` (`PawnGenerator.cs:824`) | no relations are generated when it is false | Yes | n/a | Low | `false` (already [§ 6.2](#62-anonymous-ephemeral-pawns)) |
| A44 | Opinion and social fights | `Pawn_RelationsTracker.OpinionOf` (`:592–629`); `Pawn_InteractionsTracker.CheckSocialFightStart`, `SocialFightPossible`, `SocialFightChance` (`:355–486`) | opinion = relation offsets + `thoughts.TotalOpinionOffset`, clamped ±100; fights are **runtime** events after an insult, scaled by capacities, hediffs, opinion, traits, age gap and genes | Yes | thoughts and memories are saved with the pawn | Med | initial screen only; S26 |
| A45 | Ideology at generation | `PawnGenerationRequest.FixedIdeo`; `PawnGenerator.cs:898–913` | `FixedIdeo` sets the ideology; else the faction's; else weighted random | Yes | saved with the pawn | Low | match the organization's existing ideology (Ideology only) |
| A46 | The age model | `Pawn_AgeTracker.AgeChronologicalTicks` (derived), `BirthAbsTicks`, `AgeBiologicalTicks`, `AgeTickMothballed(int)`, `BiologicalTicksPerTick`, `BirthdayBiological` (`Pawn_AgeTracker.cs:87–127,236–269,486–496,585–696`) | chronological = `TicksAbs − BirthAbsTicks` (truthful by construction); biological is an accumulator; the `AgeBiologicalTicks` setter runs **no birthday**; `AgeTickMothballed` crosses **every** birthday | Yes | `ageBiologicalTicks` and `birthAbsTicks` are saved | Med | S12; the contract of [§ 6.4](#64-truthful-aging-of-a-retained-pawn) |
| A47 | Suspension freezes aging | `Pawn.TickInterval` (`Pawn.cs:1626–1727`), `Pawn.TickMothballed` (`:1743–1749`), `Need.IsFrozen` (`Need.cs:63–68`), `WorldPawns.RemovePawn` partial catch-up (`:236–253`) | a suspended pawn skips biological aging, health, jobs and needs; vanilla catches up a *mothballed* (not suspended) pawn on removal | Yes | n/a | Med | catch-up before any observation, full and uncapped |
| A48 | Encounter evidence | `PlayLog.AnyEntryConcerns(Pawn)` (`PlayLog.cs:81`), `BattleLog.AnyEntryConcerns(Pawn)` (`BattleLog.cs:74`), `WorldPawnGC.GetCriticalPawnReason` (`WorldPawnGC.cs:174–247`) | public; vanilla's own `InPlayLog` / `InBattleLog` GC reasons | Yes | the logs are saved by vanilla | Low | S27 |
| A49 | A right-click command without Harmony | public abstract `FloatMenuOptionProvider` (`GetOptionsFor(Pawn, FloatMenuContext)`); `FloatMenuMakerMap` builds `providers` by `AllSubclassesNonAbstract()` + `Activator.CreateInstance` (`FloatMenuMakerMap.cs:12–23`) | any subclass in any loaded assembly is instantiated; no Def, no patch | Yes | nothing saved | Low | 3.3 (S28); keep `TargetPawnValid` O(1) |

---

## Appendix B: Formal invariants

| ID | Invariant | Enforced by | Proven by |
|---|---|---|---|
| **P3-INV-001** | A person has **at most one authoritative physical representation**: a character has ≤ 1 bound pawn ever and ≤ 1 open episode membership; a pawn is bound to ≤ 1 character or slot | `Plan` refuses a character with `custody ≠ Unmaterialized/Stored` or an open `episode`; write-once `PawnRef`; reverse index; validator | RT-PHYS-001, 002; validator |
| **P3-INV-002** | A physical person is **never also simulated as freely abstract** | `AuthorityGate`; the writer inventory | RT-PHYS-011, 006 |
| **P3-INV-003** | An episode **reconciles at most once**: `Closed` is terminal; member outcomes are set-once; `consequencesApplied` is the **last statement of the guarded commit** | the algorithm of § 15 (plan, validate, snapshot-guarded commit); the gate on `Closed` | RT-PHYS-003, 013, 014, 026 |
| **P3-INV-004** | **Physical death cannot be overwritten** by stale abstract health: no writer changes a `Dead` status; resurrection is observed, never initiated | `SetStatus` guard + validator | RT-PHYS-006 |
| **P3-INV-005** | **Custody prevents legal dematerialization:** authority returns to abstract only on a positive terminal observation (`WorldFree` + exit evidence) | `Terminal(obs, evidence)` | RT-PHYS-008 |
| **P3-INV-006** | **Rematerialization preserves durable identity:** the same bound pawn is reused (and is truthfully older, INV-022); never regenerated; a lost pawn makes the person `Lost` | write-once binding; `Plan` | RT-PHYS-004, RT-PHYX-006 |
| **P3-INV-007** | **Group materialization cannot clone roster members:** named members come only from the actor's own records (or the operation's `characters`), anonymous slots only from committed headcount; `materialized = returned + killed + wounded-returned + held + missing + lost` per tier | `Plan` validation; conservation check in § 15 | RT-PHYS-009 |
| **P3-INV-008** | **Save/load does not duplicate or reroll** a physical representation; load never generates, spawns or destroys | the load pass (observe, re-tag, rebuild) | RT-PHYS-007, 015; S24 |
| **P3-INV-009** | **Physical location and abstract `SpatialState` cannot both advance as independent truth:** the anchor is frozen while physical and written once at close | `Spatial` early-out; `OnPhysicalEpisodeClosed` | RT-PHYS-017 |
| **P3-INV-010** | **No physical episode disappears silently:** `Open → Closed` requires a terminal outcome per member with an observation; absence of evidence is `Missing`/`Lost` with a diagnosis, never `Returned` | § 15.3 | RT-PHYS-008, 010 |
| P3-INV-011 | The Network **never destroys or discards a pawn that has been bound or spawned**, and creates pawns only at placement; it only passes pawns to the world, and only ones it created. A candidate it rejects while still **unbound, unspawned and unreferenced** is simply dropped (S25 checks for residue) | code rule + source scan | review; scan |
| P3-INV-012 | **Provenance is the binding:** a pawn is "ours" iff a binding says so by reference equality; tags, names, factions and labels are never evidence | handlers compare bindings | RT-PHYS-001; S17 |
| P3-INV-013 | **The safe tier never reaches the physical adapter or a pawn-creating API** | source scan; the fake port | the scan |
| P3-INV-014 | **Exactly-once of money and career effects** is unchanged: a physical episode that resolves an operation reuses `careerOutcomeApplied` and the existing ledgers | reuse of the existing flags | the existing soaks + RT-PHYS-013 |
| P3-INV-015 | **Bounded work:** no per-tick scan; jobs exist only while an episode or a held person exists | scheduler discipline | idle-cost test; the soak |
| P3-INV-016 | A person in an episode is **occupied**: it cannot be checked out for an operation, and an operation's committed person cannot be planned into an episode | `Occupied` extended | RT-PHYS-016 |
| **P3-INV-017** | **Role truth survives projection:** a pawn is bound to a person with an Operational Role only if its generated state satisfies the role's necessary constraints; a failing candidate is rejected, minimally corrected (role skills only) or the placement aborts. A contradicting pawn is never bound | the verification step of § 6.8 (authoritative; a pure verdict) | RT-PHYS-020; RT-PHYX-011 |
| **P3-INV-018** | **Established truth beats randomness:** a first projection honours every durable statement the Network has made about the person or organization; anything never established is vanilla-random and is **not** persisted merely because a pawn now exists | the request builder reads durable facts only; no skill sheet is stored | RT-PHYS-021 |
| **P3-INV-019** | **Fame is not capability:** no projection, role, composition, equipment-kind or cohesion decision reads `FameBand`, the reputation score or visibility | the projection inputs of § 6.5; a source scan of the projection folder | RT-PHYS-022 |
| **P3-INV-020** | **Concretization is bounded and monotone:** named seats never exceed the existing named-people caps; a person the player has **encountered** is never silently replaced by another human in the same seat; rank-and-file of a large organization never accumulate a roster; an encountered member of a living organization is never released for a performance cap | the policy of § 4.5 | RT-PHYS-023 |
| **P3-INV-021** | **Cohesion constrains first projection only:** after binding, no Network code writes a pawn's relations, opinions, thoughts, memories or traits | the screen is a pure function applied before binding; a source scan | RT-PHYS-024 |
| **P3-INV-022** | **Truthful aging:** chronological age is never altered (`BirthAbsTicks` is never written); biological age is brought current by the **full** elapsed interval since `agedThroughTick`, uncapped, before any observation | § 6.4; the catch-up request | RT-PHYS-025; RT-PHYX-006, 012 |
| **P3-INV-023** | **Atomic durable commit:** the Network-durable effects of one episode's reconciliation are all-or-nothing: a throw at any step restores the exact prior state with the flag unset; `consequencesApplied` is set last | validation before mutation; the Applier with snapshot and restore | RT-PHYS-013, 026 |
| **P3-INV-024** | **Commit purity:** the commit contains no publication, scheduler, vanilla or random effect and calls no fault-swallowing facade; those run after the flag | a source scan of the Applier; the classification of § 15.6 | RT-PHYS-027 |
| **P3-INV-025** | **Post-commit stages are idempotent and self-finishing, and publication never replays a consequence** | stage markers; the finish-pending pass; keyed events | RT-PHYS-014, 028 |
| **P3-INV-026** | *(3.3, design)* **A handoff is one explicit transaction:** contractual cargo stays under the contractor's / contract's possession until a terminal handoff state; the payment and the transfer commit exactly once; "form caravan" cannot take cargo that was never the player's | vanilla possession + one exactly-once transaction | design; S29 |
| **P3-INV-027** | *(3.3, design)* **No ownership locks, no invulnerability:** physical reality wins; robbery, violence and abandonment are legal outcomes the Network records | no bespoke locks | design |
| **P3-INV-028** | **The physical test tier is armed per session and never inferred:** the arm is runtime-only, cleared on load and after a run; the Network never decides that a save is disposable; spawning APIs exist only in the physical tier's folder | the guard and a source scan | the headless guard tests |

---

## Appendix C: Interaction classification

**NO CHANGE** · **SMALL INTEGRATION** · **NEW AUTHORITY RULE** · **MAJOR RISK**.

| System | Class | What changes |
|---|---|---|
| Procurement | SMALL INTEGRATION (3.0–3.2); **MAJOR RISK in 3.3, design only** | candidate selection and refusal reasons inherit the gate; 3.3 would add delivery modes and a physical handoff on top of the existing ledgers ([§ 27](#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)) |
| Operations | **NEW AUTHORITY RULE** | `OpStatus.Physical`; a rescue episode cancels `operation.troubled` and resolves through `OnPhysicalResolved`; `Occupied` includes episodes |
| Spatial | **NEW AUTHORITY RULE** | frozen while physical; one write at close (§ 12) |
| Field Log | NO CHANGE | (a physical beat may come later) |
| Career | SMALL INTEGRATION | an open episode counts as a job; `CommitOutcome` only for operation-linked episodes, once, from FOLLOW-UP. The equipment rung's reliance on the fame band is **noted, not changed** ([§ 6.10](#610-professional-reputation-fame-and-capability)) |
| Equipment | **NEW AUTHORITY RULE** | G1–G10; no mirror; **two** separate Phase 4 seams (Lease, Notable Asset) |
| Reputation / fame | NO CHANGE in Phase 3 | physical events publish ordinary events; Phase 3 only **forbids projection from reading fame**; the separation of reputation, visibility and capability is a later focused phase |
| Relations | SMALL INTEGRATION | seed and mirror the temporary faction's goodwill; player-hostility events |
| Knowledge | NO CHANGE | |
| History | SMALL INTEGRATION | new event keys, published after commit |
| Consequences | SMALL INTEGRATION (3.2: rescue) | the Last Known Location gains survivors in 3.2 |
| Recovery | **NEW AUTHORITY RULE** | physical → abstract recovery mapping; recovery runs once |
| Organization roster | **NEW AUTHORITY RULE** | headcount conservation; returned/lost applied once; a role template and concretized seats (3.2) |
| `KnownCharacter` | **MAJOR RISK** | the identity binding, custody, operational role and encounter are the riskiest new truth |
| Runtime test infrastructure | SMALL INTEGRATION + a **new tier** | fake port, `RT-PHYS` (28 cases), the session-armed physical tier on its own test map (§ 21) |
| Compaction | SMALL INTEGRATION | Closed episodes; never a character with a pawn or held |
| Save migration | SMALL INTEGRATION (consequential) | the first Phase 3 build bumps once; the downgrade hazard is why; later-subphase policy is O-11 |
| Prepare-for-removal | **NEW AUTHORITY RULE** (+ MAJOR RISK) | settle, clear the registry, strip pawn tags; never delete (§ 20) |
| Operational Roles / composition | SMALL INTEGRATION | read once at first projection and at mission selection; no effect on the abstract resolver (§ 6.6–6.7) |
| Team cohesion | SMALL INTEGRATION | a derived band and a first-generation screen; never writes a bound pawn (§ 6.9) |
| Progressive concretization | **NEW AUTHORITY RULE** | when an abstract seat becomes a named, bound person; bounded by the existing caps (§ 4.5) |
| The reconciliation commit | **NEW AUTHORITY RULE** (+ MAJOR RISK) | the Applier: a bounded mutation layer with no foreign effects; the existing casualty / succession / ending paths are *split*, not called (§ 15.6) |
| Rivals / Knowledge | NO CHANGE | seams only; nothing implemented (§ 27.9) |

---

## Appendix D: Glossary

| Term | Meaning |
|---|---|
| **Actor** | a `NetworkActor` (a contractor or organization); never a person, never a pawn |
| **Person** | one human-scale individual: *named* (`KnownCharacter`) or *anonymous* (an episode slot) |
| **Pawn** | the RimWorld object; a *binding*, not an identity |
| **Episode** | one physical appearance of some of an actor's people, owning presence facts only |
| **Member** | a person in an episode, with a binding and a set-once outcome |
| **Authority** | which layer advances a person: Abstract, Physical, Vanilla-held |
| **Custody** | the persisted `CustodyState`: who controls the pawn, if one exists |
| **Projection** | creating a believable pawn from capability, once |
| **Retention** | keeping a named person's one pawn between episodes (reserved, suspended) |
| **Registry** | the hidden quest reservation that makes a stored pawn non-`Free` |
| **Binding** | the persisted pawn pointer + `thingIDNumber`, write-once per character |
| **Observation** | a read-only classification of a bound pawn (`ObservedKind`) |
| **Reconciliation** | observe → decide → plan → commit → flag → release → publish, once |
| **Quarantine** | an episode the Network cannot safely reconcile; its members stay blocked, pawns untouched |
| **Operational Role** | a durable *semantic function* (Marksman, Medic, Heavy, …) that bounds a first projection; **not** a class, perk or bonus; distinct from the organizational `CharacterRole` |
| **Role composition** | the **organization composition**: a persisted role template (what an organization broadly contains); the **mission composition** is the per-episode subset a purpose needs |
| **Seat** | one role position of a composition; *abstract* until a living `KnownCharacter` is pinned to it (*concretized*) |
| **Concretization (crystallization)** | an abstract seat or episode slot becoming a named, bound person, by size policy or by encounter evidence; bounded and monotone |
| **Encounter evidence** | cheap, observed-at-reconcile facts (E0–E4) that a person mattered: presence on a player-visible map, play/battle-log entries, a vanilla stake, a material outcome, being named |
| **Cohesion (band)** | a derived, never stored semantic of an organization's internal stability; constrains only the first generation of a member |
| **Professional reputation / Fame (visibility) / Capability** | what the work market thinks of a *track record* / how widely one is *known* / what one can actually *do*; correlated, not equivalent ([§ 6.10](#610-professional-reputation-fame-and-capability)) |
| **Truthful aging** | chronological age derived from the clock; biological age brought fully current before observation, never capped |
| **Applier / ReconciliationPlan** | the bounded mutation layer and the immutable, validated plan it executes inside one snapshot-guarded commit |
| **Finish-pending pass** | the load-time and watch-time pass that resumes an unfinished post-commit stage (release, follow-up, publish) from its marker |
| **Lease / Notable Asset** | the two future durable equipment relationships: *lent, ownership external* vs *permanently granted, ownership transferred* |
| **Handoff / Freight / Rendezvous** | (3.3, design) the explicit physical transaction that transfers contractual cargo / the cargo-moving capability, distinct from personnel mobility / a physical meetup site |

---

## Appendix E: What the audit changed from the Phase 0 design

Phase 0 ([ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md)) is **confirmed in its core**: the identity
tiers, one pawn per character, never discarding, reconciliation from state, the registry quest, per-organization
factions, the failure philosophy. These items changed or are new:

| # | Phase 0 said | The code says (evidence) | Consequence |
|---|---|---|---|
| E1 | ([RIMWORLD_INTEGRATION § 2.19](RIMWORLD_INTEGRATION.md)) normal pawn death does **not** send `Killed`; observe death through `Despawned` plus reconciliation | `Pawn.Kill` ends `if (!base.Destroyed) base.Kill(...)`; `Thing.Kill → Destroy(KillFinalize)`; `Thing.Destroy` sends `Destroyed` and `Killed` (`Pawn.cs:2088…`, `Thing.cs:1043–1099`) | death has a signal, but it fires **mid-kill**: handlers only enqueue |
| E2 | at collapse "set the faction back to null" | `Notify_PassedToWorld` rewrites a `Free` null/player-faction humanlike pawn to a **random** faction (`Pawn.cs:1851`) | reserve first, then pass; membership is the Network's |
| E3 | `PawnRef` = pointer + `thingIDNumber` | a reference to a destroyed (dead) pawn saves **`null`** unless `saveDestroyedThings` (`Scribe_References.cs`) | `saveDestroyedThings: true`; persist death at the time |
| E4 | wrap `GeneratePawn` in `Rand.PushState`; commit by binding | `GeneratePawn` can **return an existing world pawn** unless `ForceGenerateNewPawn` (`PawnGenerator.cs:199`) | mandatory `ForceGenerateNewPawn`; relations off |
| E5 | `KeepForever` is "safe from GC but still `Free`"; factionless storage "rarely" redressed | redress chance is `min(0.02 + 0.001 × Free, 0.8)`; null-faction pawns match `faction: null` requests (drifters, `GenStep_Monolith.cs:84`); `PrisonerWillingToJoinQuestUtility` uses `WorldPawnFactionDoesntMatter` (`PawnGenerator.cs:1146`) | the registry (or a non-null unrequested faction) is **required**, not optional; quantified |
| E6 | one temporary faction per organization, reused | vanilla removes a temporary faction as soon as nothing is spawned under it and nulls every pawn's faction (`FactionManager.cs:109–140,398`) | **one per episode**; goodwill mirrored |
| E7 | map removal passes pawns to the world; `MapRemoved` triggers reconciliation | `PassPawnsToWorld` sends `LeftMap` only for colonists/hosted pawns (`MapDeiniter.cs:175–179`) | the site comp / `MapRemoved` is the trigger; observe every pawn |
| E8 | the registry quest costs "O(1)" | `IsReservedByAnyQuest` is quests × parts × `List.Contains`, per non-mothballed world pawn per tick | *R* is a cost parameter; measure *R* × *W* |
| E9 | only `KeepForever` as a non-quest GC guard | `WorldPawns.ForcefullyKeptPawns` is a **public** saved `HashSet` | a GC-only guard exists without a quest (not redress protection) |
| E10 | a Network `QuestScriptDef` root and part are required | vanilla has `QuestPart_ReservePawns`; only a non-null root is required | a vanilla-only registry is possible; compared in S9r (self-healing trade-off) |
| E11 | a kidnapped pawn is a custody state | vanilla **recruits kidnapped pawns into the captor with MTB ≈ 30 days** (`KidnappedPawnsTracker.cs`) | custody watch expects a faction change |
| E12 | `Downed` observed via signals | there is **no** downed/incapacitated signal | poll |
| E13 | corpses are vanilla's; nothing to track | a pawn that dies as a world pawn leaves **no corpse** and may be GC'd (`Pawn.Kill`, `WorldPawnGC`) | death is persisted when it happens |
| E14 | `Pawn.SpawnSetup` just spawns | it can **discard** a pawn that spawns in an invalid state (`Pawn.cs:1358`) | verify after spawn; contained abort |
| E15 | `Deployment`, `DeploymentStore`, `LeaseStore` | the code already has the reserved `deployments`/`leases` slots, `EntityKind.Deployment/Lease`, an unwritten `CustodyState`, and `OpStatus.Physical` | reuse them: no layout shift |
| E16 | Phase 3 scope: deployments, leases, sponsorship, delivery, inheritance, Consequence Engine v1, monitor | too much for one phase and partly Phase 4 | **three subphases** (the amendment pass adds a fourth, 3.3, as design direction, § 27); items re-scoped for owner decision (§ 23.1) |
| E17 | one test tier | Full Safe Regression must never spawn | two tiers (§ 21, ADR-049) |
| E18 | *(first Phase 3 pass)* a request validator can enforce a role | `GenerateNewPawnInternal` **ignores validators from the 100th try** (`PawnGenerator.cs:694–698`) | validators are an optimization; the authoritative check is a post-generation verification |
| E19 | roles could be expressed as custom `PawnKindDef`s | `Pawn.kindDef` is saved **by def name** (`Pawn.cs:4571`); a runtime kind does not survive a save | map roles to **existing** kinds; never create kinds |
| E20 | *(first Phase 3 pass)* "elapsed biological age catch-up capped per materialization" | chronological age is derived from the clock; biological age freezes while suspended; `AgeTickMothballed` is vanilla's bulk path and crosses every birthday | truthful, uncapped catch-up (candidates M1 / M2, S12) |
| E21 | *(first Phase 3 pass)* the spatial anchor can be written through the existing facade inside the commit | the facade catches every fault and returns a default (`SpatialService.cs:336–360`) | a direct assignment in the Applier; a reporting entry |
| E22 | *(first Phase 3 pass)* the commit is "ONE synchronous block of primitive assignments (cannot fail half-way)" | `ApplyCasualties`, `RunSuccession` and `EndActor` interleave inline publication, scheduler calls and cross-service calls (`ContractorService.cs:794–974`) | plan / validate / a snapshot-guarded Applier / idempotent post-commit stages |
| E23 | a contractor representative would need Harmony to take a right-click command | `FloatMenuMakerMap` discovers `FloatMenuOptionProvider` subclasses by reflection (`FloatMenuMakerMap.cs:12–23`) | no Harmony (3.3, S28) |

---

## Appendix F: Amendment log

The ten amendments of the amendment pass, where each landed, and **which statement of the first design pass
(`5f16a71`) it invalidated**. Everything *not* listed here, including the authority model, the three identities, the
Episode as a presence-only store, observation-based reconciliation with "not spawned is never evidence", monotonic
death, `PawnRef` as a binding, the registry reservation, the API-audit evidence and its corrections (Appendix E), the
event-driven performance rule, and the Phase 2.9 owner-validation record, **is unchanged**.

| # | Amendment | Where it landed | Statements it invalidated | New OPEN items |
|---|---|---|---|---|
| 1 | **Operational Roles** | § 6.5–6.6, § 6.8, § 5.3, App. A36–A41, B 017–018 | § 6.5 (first pass): "experience band, `skill` … never writes skills afterwards" is refined: a role's skill floor may be enforced by a **pre-bind** minimal correction; nothing is ever written to a *bound* pawn | S25, O-8 |
| 2 | **Role composition** (organization and mission) | § 6.7, § 5.1, § 5.3, § 4.5 | § 5.1 (first pass): "anonymous slots by tier" as the only way to fill a crew; an episode slot now carries a seat role | O-9 |
| 3 | **Progressive concretization** | § 4.2–4.5, decisions 3, G2, N15, § 18.2 | Decision 3, § 4.2, § 4.3 and the title of § 6.2 (first pass): "anonymous members are ephemeral". Now: ephemeral for *large* organizations only; small organizations concretize | O-2, S27, R-36 |
| 4 | **Team cohesion** | § 6.9, § 17, § 19, App. A43–A45, B 021 | none (new). `CanGeneratePawnRelations = false` (§ 6.2) keeps its meaning and becomes the first cohesion safeguard | S26, R-39 |
| 5 | **Reputation vs fame vs capability** | § 6.5, § 6.10, App. C, CAREERS § 2, DATA_MODEL § 3, B 019 | none in the design; the **terminology** of CAREERS § 1–2 is clarified (the single score is a professional-record score shown as "fame"); the code is **not** changed | O-17, R-40 |
| 6 | **Reconciliation atomicity** | § 15.2, § 15.5–15.7, § 8.1, § 17, App. B 023–025 | § 15.2 (first pass): "ONE synchronous block of primitive assignments (cannot fail half-way)"; § 15's intro "plan → durable commit → flag → publish"; § 15.5's "extracted into a physical-apply entry point"; § 12.2's use of the spatial facade; ADR-048 rule 6 wording | O-10, R-37 |
| 7 | **Truthful aging** | § 6.4, § 5.3 (`agedThroughTick`), § 16.1, App. A46–A47, B 022 | § 6.4 (first pass): "advance biological age by the elapsed ticks, **capped per event**" | S12 (extended), O-12, R-38 |
| 8 | **Generalized equipment seam** | § 11.2, G7–G10, § 11.3, § 23.1 | § 11.2 (first pass): "the reserved `leases` slot … durable explicit records" as *the* exact-equipment seam | none |
| 9 | **Phase 3.3** | § 23, § 27, § 22, decision 12, § 23.1 | § 23.1 (first pass): "In-person delivery: Out of 3.0–3.2 … a later subphase after 3.2, or Phase 4"; "three subphases" | S28–S30, O-15, R-41 |
| 10 | **Physical test guard** | § 21.2, § 22.2, S22, ADR-049, R-32, App. B 028 | § 21.2 (first pass): "the colony does not look real (a bounded sanity check on colonist count and wealth)"; § 22.2: "the home map of a **disposable** save (the guard refuses a real colony)" | S22 (revised) |

**Status of the evidence.** The amendments add API-audit rows A36–A49 from the decompiled 1.6.9676.17735 assembly. **No
runtime spike was run**; S12, S22, S25–S30 are *planned*. The owner's Phase 2.9 runtime validation is unchanged, and
formal Phase 2.5 **S20 is still NOT RUN**.
