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

## The decisions on one page

1. **One authority at a time, per person.** A person is *Abstract* (the Network record is truth), *Physical*
   (the real Pawn is truth for the facts the projection owns), or *Vanilla-held* (a vanilla system holds the pawn:
   prisoner, colonist, kidnapped, in a caravan). Nothing advances a person in two layers. One central question,
   `CanSimulateAbstractly(person)`, gates every abstract writer ([§ 3](#3-authority-model), [Appendix C](#appendix-c-interaction-classification)).
2. **Three identities, never conflated.** *Actor* (`ActorId`: the contractor or organization) ≠ *Person*
   (`CharacterId` for a named person, an anonymous *slot* for rank-and-file) ≠ *Pawn* (a RimWorld object whose
   `thingIDNumber` is a binding, **never** identity) ([§ 4](#4-identity-model)).
3. **A named person has at most one pawn, for life.** First materialization creates it; it is bound once and
   never regenerated or rerolled. If it is lost, the person is `Lost`. **Anonymous members are ephemeral**: a
   fresh pawn per episode, released to vanilla afterwards, with only headcount deltas persisted
   ([§ 6](#6-projection-rules)).
4. **A new durable concept is justified: the Physical Episode** (candidate name). It owns *presence* facts only
   (who is out there, where, since when, what has been seen, whether reconciliation has been applied) and no
   consequences of its own: consequences are applied through the **existing** casualty, career, spatial and event
   services ([§ 5](#5-materialization-model)).
5. **Provenance is a Network-persisted binding** (`CharacterId`/episode slot ↔ pawn pointer + `thingIDNumber`),
   with **quest tags** only as a signal-routing aid and a **registry reservation** only for retained pawns that
   would otherwise be `Free` ([§ 7](#7-physical-provenance)). No Hediff, no ThingComp, no name matching.
6. **Reconciliation is plan → durable commit → flag → publish,** idempotent, driven by *observed* pawn state, and
   a signal is only a wake-up ([§ 15](#15-reconciliation-algorithm)). "Not spawned" is never evidence of
   anything ([§ 9](#9-custody-model)).
7. **Death is monotonic.** A physical death is final; resurrection by another mod is observed, never initiated,
   and never makes the person `Active` again ([§ 10](#10-death-and-injury)).
8. **Only identity-defining truth survives abstraction.** Permanent physical truth stays on the retained pawn (it
   is real); temporary injury becomes the existing abstract recovery; gear is real loot and never mirrored into
   the abstract equipment tier ([§ 10](#10-death-and-injury), [§ 11](#11-gear-semantics)).
9. **No Harmony is required for the recommended slice.** Every needed event has a vanilla signal, a component
   callback or a bounded poll; the real gaps (downed, resurrection, caravan join) are polls, documented
   ([§ 14](#14-event-detection)).
10. **Event-driven, near-zero idle.** No scan of pawns, maps or world pawns per tick; jobs exist only while an
    episode or a vanilla-held person exists ([§ 18](#18-performance)).
11. **Physical tests are a separate, explicit, disposable-environment tier.** The safe suites never spawn a pawn
    ([§ 21](#21-runtime-qa-strategy), ADR-049).
12. **Three subphases, two owner review gates before any player-facing content.** 3.0 abstract foundation
    (headless + safe suites over a fake physical port), 3.1 the controlled physical episode (first real pawn),
    3.2 custody and rescue (first player-visible content). Leases, sponsorship, in-person delivery and ambient
    visits are **re-scoped out** of Phase 3 for owner decision ([§ 22](#22-phase-3-vertical-slice),
    [§ 23](#23-suggested-subphases)).

## Contents

1. [Goals and non-goals](#1-goals-and-non-goals)
2. [Existing architecture audit](#2-existing-architecture-audit)
3. [Authority model](#3-authority-model)
4. [Identity model](#4-identity-model)
5. [Materialization model](#5-materialization-model)
6. [Projection rules](#6-projection-rules)
7. [Physical provenance](#7-physical-provenance)
8. [Lifecycle state machine](#8-lifecycle-state-machine)
9. [Custody model](#9-custody-model)
10. [Death and injury](#10-death-and-injury)
11. [Gear semantics](#11-gear-semantics)
12. [Spatial integration](#12-spatial-integration)
13. [Faction and AI model](#13-faction-and-ai-model)
14. [Event detection](#14-event-detection)
15. [Reconciliation algorithm](#15-reconciliation-algorithm)
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

Appendices: [A. RimWorld 1.6 API audit](#appendix-a-rimworld-16-api-audit) ·
[B. Formal invariants](#appendix-b-formal-invariants) ·
[C. Interaction classification](#appendix-c-interaction-classification) ·
[D. Glossary](#appendix-d-glossary) ·
[E. What the audit changed from the Phase 0 design](#appendix-e-what-the-audit-changed-from-the-phase-0-design)

---

## 1. Goals and non-goals

**Goal.** A persistent abstract actor can temporarily become physically present in real RimWorld; what then
happens physically is authoritative; and that reality returns to The Network **exactly once**, with no clones, no
resurrection bugs, no lost custody, no duplicated consequences, no save corruption and no hidden second
authority. *Physical Halvard may now acquire legs. He may not acquire clones.*

| # | Goal |
|---|---|
| G1 | One authority per person at any time, enforced in one place, provable by invariant and test |
| G2 | A named person keeps one pawn for life; an anonymous member is a projection that leaves no persistent trace beyond headcount |
| G3 | Physical outcomes (death, wounds, capture, defection, loss) reconcile into existing Network truth through existing services, once |
| G4 | Save/load at any moment of a physical episode neither duplicates, rerolls, abstracts, resurrects nor loses anyone |
| G5 | Custody beyond the map (prisoner, kidnapped, caravan, world pawn) is tracked and never mistaken for "gone home" |
| G6 | Zero Harmony; event-driven; near-zero cost when nobody is physical |
| G7 | Heavily modded games (races, xenotypes, gear, health systems, factions, transports, resurrection mechanics) degrade gracefully through capabilities, never through named-mod code |
| G8 | Destructive physical QA exists, but as a separate explicit tier that cannot be pressed by accident |

**Non-goals (frozen for Phase 3).**

| # | Non-goal | Why |
|---|---|---|
| N1 | Physical contractor caravans for ordinary background movement | `SpatialState` is the one geographic truth; background movement stays abstract |
| N2 | Exact off-map inventory, bullet counts, apparel history, vehicle ownership, hidden implants | Projection, not secret inventory simulation ([§ 6](#6-projection-rules)) |
| N3 | A mirror of every Hediff in an abstract health simulator | Only identity-defining truth survives; temporary injury maps to the existing recovery model ([§ 10](#10-death-and-injury)) |
| N4 | One RimWorld faction per contractor, or altering world diplomacy for AI convenience | ADR-006; [§ 13](#13-faction-and-ai-model) |
| N5 | A contractor tactical-AI framework | Vanilla `Lord`/`LordJob`/`Duty` do the work |
| N6 | Phase 4 compensation, sponsorship, leases, the contract board, the player-as-contractor | A **seam** is defined ([§ 11](#11-gear-semantics)); nothing is built |
| N7 | Ambient contractor visits as content | Deferred; the machinery makes them technically possible |
| N8 | Mod-specific logic in core rules (Grandmaster21, RegenNanites, Beyond Our Reach, Isekai, …) | Capabilities and ordinary Def/API truth only; those mods are owner QA environments |
| N9 | Scanning all pawns, all maps or all world pawns on a timer | [§ 18](#18-performance) |
| N10 | Resurrection as a feature | Observed and tolerated, never initiated ([§ 10](#10-death-and-injury)) |
| N11 | Fixing the dev-tool or UI surface beyond a read-only monitor | A read-only Episode Monitor only ([DEBUGGING § 7](DEBUGGING.md)) |

---

## 2. Existing architecture audit

Audited from the merged code, not from the design documents. Everything in this section is a fact about
`main` `6d0352d`.

### 2.1 What exists, and what Phase 3 needs from it

| Concept | Where | Phase 3 relevance |
|---|---|---|
| `NetworkActor` (`ActorKind` Organization, Individual, FactionProxy, PlayerProxy, Institution; `ActorStatus`) | `Domain/Actors/NetworkActor.cs` | The *actor* is a contractor or organization. It is **not** a person and never has a pawn. |
| `ActorBindings.embodies` / `KnownCharacter.embodiedBy` | `NetworkActor.cs`, `ContractorService.cs:222`, `ActorService.cs:166` | A **Solo** contractor is an `Individual` actor that *embodies* exactly one `KnownCharacter`. The person is the character, not the actor. |
| `KnownCharacter` (`role`, `org`, `embodiedBy`, `status`, `custody`, `notability`, `woundedUntilTick`, `diedTick`, `deathCauseKey`, `NameSnapshot`) | `NetworkActor.cs:237` | The persisted *person* record. It has **no pawn binding, no gender, no age, no appearance**: exactly the minimum a Phase 3 binding must add. |
| `CustodyState` (Unmaterialized, Stored, Deployed, OutOfCustody, Released, Lost) | `NetworkActor.cs:222` | Persisted on every character since Phase 2 and **never written**: every saved value is `0`. Its meanings can be fixed now at zero migration cost ([§ 8](#8-lifecycle-state-machine)). |
| `CharacterStatus` (Active, Wounded, Captured, Missing, Dead, Retired, Defected, Lost) | `NetworkActor.cs:210` | Abstract *story* status. Orthogonal to custody ("who controls the pawn"). |
| `OrganizationProfile` (leader, ≤ 2 lieutenants, ≤ 6 `knownMembers`, `tiers` headcount, `woundedRecovery` buckets, `committed`) | `Persist/ContractorComponents.cs:537` | Named people are records; everyone else is a *count by tier*. A crew is `knownMembers` + headcounts. There is no roster of anonymous individuals, and Phase 3 must not create one. |
| `ContractorSimulation` (`equipment` tier/condition/specialties, `mobility`, `spatial`, `career`, `commitments`, `funds`, `skill`, runtime `cachedStrength`) | `ContractorComponents.cs:381` | Capability to *project* from. `commitments` is `List<OperationId>` and drives job capacity. |
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
 ContractorActor A12  "Horizon Tide Crew"                       (ActorId — never a pawn)
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
people are `Character → Pawn` pairs and anonymous slots. An *Individual* (Solo) actor embodies one character; the
actor, the character and the pawn are still three distinct things.

### 4.2 The tiers (confirmed, with one change)

| Tier | Representation | Persists | Pawn |
|---|---|---|---|
| **T0 anonymous member** | a count in `OrganizationProfile.tiers`; during an episode an **episode slot** | headcount only | ephemeral: a new pawn per episode, released to vanilla at the end |
| **T1 Known Character (record)** | `KnownCharacter`, `custody = Unmaterialized` | ≈ 0.4 KB | none |
| **T2 Known Character (bound)** | `KnownCharacter` + `PawnRef`, `custody ∈ {Stored, Deployed, OutOfCustody}` | the record + a real pawn in the save | **one, for life** |

The one change from Phase 0: a *named* person becomes T2 **when it is first materialized**, not only when it is
"met". Reason: the moment the Network puts a named person into the world, vanilla can start referencing the pawn
(relations, play log, battle log, tales, a quest). From that instant a regenerated twin would be a clone, so the
pawn must be the person.

### 4.3 The questions, answered

| Question | Answer |
|---|---|
| Does every physical person need a persistent `CharacterId`? | **No.** Only people the Network already names (leaders, lieutenants, notable members, every Solo) or that are promoted. A rank-and-file pawn has an *episode slot* and vanishes from Network state when the episode closes. |
| Do anonymous members gain a durable record only when materialized? | **Only if they matter at reconciliation** (promotion, below). Being materialized is not a reason. |
| When is a generated pawn a persistent `KnownCharacter`? | At reconciliation, if any of: **captured, recruited, rescued or enslaved by the player**; **named in a Network letter or event**; **killed or downed a colonist** (or was downed by one and survived); **vanilla now holds a stake in it** (it is in the PlayLog/BattleLog with colonists, has a colonist relation, or `EverBeenColonistOrTameAnimal`); or it is the most notable on its side. Promotion is deterministic and once. |
| How does a returning pawn remain recognizably the same person? | **By being the same `Pawn`.** A named person's pawn is retained (reserved, suspended) and reused. Nothing is regenerated to "look like" them. |
| What is the minimum persisted identity? | `CharacterId` (exists) + `NameSnapshot` (exists) + a `PawnRef` (new) + `custody` (exists) + the episode link (new). No appearance, gender, age or traits are stored: the pawn *is* them. |
| Should pawn IDs be durable Network identity? | **No.** `Thing.ThingID` is `def.defName + thingIDNumber`, saved as `id`, unique within one save, stable across save/load for as long as that *object* exists, and different for any recreated, duplicated or replaced pawn (`Thing.cs:392`, `ThingIDMaker.cs`). It is a **binding attribute**: valid for the life of one pawn object, checked by *pointer identity*, never an identity. |
| What if a pawn becomes important through interaction? | Promotion above. A generic pawn the player arrests becomes a Known Character (T2, `OutOfCustody`) at reconciliation, so the Network never forgets a prisoner it created. |
| How is organization membership tied back to physical people? | `KnownCharacter.org` for named people; `Episode.actor` + slot `tier` for anonymous ones; the headcount `committed → returned/lost` in `OrganizationProfile`. **Never** inferred from `pawn.Faction` or name. |

### 4.4 What this deliberately does not do

No persisted roster of anonymous individuals; no appearance snapshot; no regeneration from a seed to recreate a
person (generation depends on the mod list, ADR-013); no cap on *physical visits*, only on retained pawns
(a soft cap of ≈ 150, a **performance policy, not an identity rule**: under pressure only safe, dormant,
low-notability people are *released* — `neverRematerialize`, the record stays — and the cap may be exceeded rather
than break continuity, exactly as Phase 0 specified).

---

## 5. Materialization model

### 5.1 What is materialized

| Actor form | What becomes physical | What stays abstract |
|---|---|---|
| **Solo** (Individual, embodies one character) | the one character → **one retained pawn** | the actor's funds, career, relations, reputation (always), and everything while the person is not physical |
| **Duo / crew / team** (small organization) | the **people the purpose needs**: named members first (leader only when the purpose demands it), then anonymous slots by tier, each as **its own pawn** | the rest of the headcount, the org's morale, funds, doctrine, career, spatial body |
| **Company / larger organization** | a **detachment only**, bounded by purpose (a rescue: exactly the people the operation already committed; a visit: a small fixed party) | the main body, which keeps its own `SpatialState` and upkeep, like a concurrent job's `detached` plan |
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
| `consequencesApplied: bool` | set **inside** the commit, last; the exactly-once flag |
| `publishedTick: int` | set **after** events publish; a retry after a throw during publish republishes, it never reapplies |
| `attempts: int`, `lastError: string` | bounded reconcile retries, then `Quarantined` |
| `where: {tile: TileRef, mapId: int}` | context only; **not** the actor's location |
| `faction: FactionRef?` | the temporary encounter faction, if any |
| `seed: int` | seeds the *first* creation of anonymous pawns |
| `members: List<EpisodeMember>` | the people (≈ 60 B each) |

**`EpisodeMember`**

| Field | Meaning |
|---|---|
| `character: CharacterId` | none for an anonymous slot |
| `slot: int`, `tier: Tier` | the anonymous slot's index and tier |
| `pawn: PawnRef` | the binding ([§ 7](#7-physical-provenance)) |
| `state` | `Planned → Created → Present → Done` |
| `outcome` | `Pending` until `Done`, then **set once**: `Returned`, `Killed`, `HeldByPlayer`, `JoinedPlayer`, `Kidnapped`, `HeldByOther`, `Missing`, `Lost`, `NeverPlaced` |
| `observed: ObservedKind`, `observedTick` | last classification and when (diagnostics and the long-open warning) |

**`KnownCharacter` additions:** `pawn: PawnRef` (null when no pawn exists), `episode: EpisodeId` (the exclusive
open membership), `heldBy: HeldKind` + `heldSinceTick` (what vanilla holds them as). Nothing else: not gender, not
age, not appearance, not traits.

**`PawnRef`:** the pawn **pointer** (saved with `saveDestroyedThings: true`), `thingIDNumber`, the pawn's `def`
name (a sanity check), `boundTick`. Write-once per character.

Estimated cost: ≈ 0.5 KB per episode plus 60 B per member plus ≈ 40 B per bound character: negligible beside the
pawns, which vanilla saves in whatever holder owns them. The full minimum-new-persisted-truth list and the
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

| Request setting | Value | Why (verified) |
|---|---|---|
| `ForceGenerateNewPawn` | **`true`** | Without it `GeneratePawn` may **return an existing `Free` world pawn** (redress) instead of creating one (`PawnGenerator.cs:199–222`). The chance is `min(0.02 + 0.01 × FreePawnCount/10, 0.8)` per generation (`:1146`): in a long game with many world pawns it approaches certainty. A materialized "anonymous mercenary" must never be somebody else's pawn (or a retained Network pawn that lost its reservation). |
| `CanGeneratePawnRelations` | `false` | avoids creating relatives as new world pawns with relations to colonists |
| `AllowDead`, `AllowDowned` | `false` | healthy people only |
| `Faction` | the episode's temporary faction ([§ 13](#13-faction-and-ai-model)) | AI, hostility and UI need one |
| kind | chosen by capability from the org's template family, falling back along a chain, then to vanilla defaults | no named mods; **OPEN O-3**: the equipment-tier → kind/loadout mapping |
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
- race/kind/xenotype: from the org template by capability.

The pins are **inputs, not stored**. The pawn is bound at once ([§ 7](#7-physical-provenance)) and from then on *it* is the
person: scars, bionics, genes, addictions, skills, relations, tales and every mod's pawn-level state live in the real
pawn, which is why a heavily modded game is better served by keeping the pawn than by describing it.

*Every later* materialization reuses the retained pawn: unreserve it, apply catch-up (below), place it. It is **never
regenerated and never rerolled**. If the pointer is null or the pawn is `Discarded`, the person is `Lost`
([§ 17](#17-failure-recovery)).

### 6.4 Catch-up for a retained pawn (a suspended pawn does not age)

A registry-reserved pawn is `Suspended`: `Pawn.TickInterval` skips health, needs, jobs and **aging**, and
`TickMothballed` skips aging too (`Pawn.cs:1618–1750`). So at materialization the Network applies, and only these:

| Catch-up | Rule |
|---|---|
| age | advance biological age by the elapsed ticks, **capped per event** so a decade-long save does not push the pawn through several life stages at once (vanilla birthdays then run normally after spawn) |
| needs | reset to comfortable; the organization fed them off-map |
| health | already normalized at storage ([§ 10](#10-death-and-injury)); materialization only checks `woundedUntilTick` |
| gear | exactly as the pawn left it |
| faction | set to the episode's temporary faction **after** unreserving (see the `PassToWorld` faction rule in [§ 13](#13-faction-and-ai-model)) |

The exact APIs are **OPEN (spike S12)**.

### 6.5 Projection inputs (capability → effect)

| Abstract capability | Physical effect |
|---|---|
| equipment tier / condition / specialties | which kind or loadout class is requested (O-3). Not an item list. |
| experience band, `skill` | influences kind choice (and, if the kind allows, skill ranges); never writes skills afterwards |
| mobility | the **arrival mode** (walk-in vs pod/shuttle), not a pawn property |
| doctrine | the `LordJob` chosen where a choice exists (e.g. visit vs defend) |
| fame, career, funds | nothing physical |
| wounded state | wounded people are not projected (they stay home), except a rescue target, which is seeded explicitly through vanilla damage APIs |
| form / size | how many people, bounded by the purpose |
| derived Tags (`WellEquipped`, `EliteCombat`, …) | descriptive only; no bonuses |

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

1. **Binding is written before the pawn is spawned.** `generate → bind (member.state = Created) → spawn → Present`.
   A throw after generation therefore cannot orphan a pawn that nothing records ([§ 17](#17-failure-recovery)).
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

Two small machines and one derived rule. The design deliberately keeps the *durable* machine tiny: reconciliation
is a synchronous, atomic, idempotent step, so "Reconciling" never needs to be a persisted state.

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
| missing body part, permanent injury, scar, bionic/added part, chronic condition (`HediffUtility.IsPermanent`, `chronic`), addiction, genes, xenotype, skills, traits, age | **identity-defining / long-term** | **the retained pawn** (real) | not mirrored; survives because the pawn survives |
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

### 11.2 The Phase 4 seam (not built)

Phase 4 will let the player give or lend equipment that should later appear physically. The design keeps that
possible without building it: projection takes its loadout from a single place that today has one input
(**(a)** the kind's own generation) and later gets a second (**(b)** durable explicit records in the reserved
`leases` slot: "this person/organization holds item X, quality Q, condition C"). Projection rule for (b): *ensure
the pawn carries a real item matching the record*; on return, the record is updated from the real item's state
(`HitPoints / MaxHitPoints`, still present or not), exactly the lease semantics of the Phase 0 design
([DATA_MODEL § 11](DATA_MODEL.md#11-deployments-and-equipment-leases)). **Phase 3 implements only (a) and the
extension point.**

### 11.3 Edge answers

| Case | Answer |
|---|---|
| Player kills and loots a contractor | real loot; the death is reconciled |
| Contractor leaves carrying colony property | real; the Network observes nothing; theft consequences are content, deferred |
| Gear destroyed | G4 |
| `PawnKindDef.destroyGearOnDrop` kinds | vanilla behaviour (the gear is destroyed on drop); no Network rule |
| Modded weapons/apparel | vanilla generation; no special case |
| Quality / stuff / biocoding / royalty locks | untouched; they belong to the real items |

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
route, never travel time, never invented.

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

It must be **idempotent and exactly-once**, in the same discipline that made the Phase 2.75 career commit safe:
*plan → durable commit → flag → publish*, never *flag first, mutate later*.

### 15.1 Wake-ups (none of them is trusted to say what happened)

A tagged signal (death, left map, map removed, arrested, kidnapped, …) · the Open-episode **watch job** (250 ticks) ·
the site comp's map callbacks · the **load pass** · a purpose-specific end (a visit's duration elapsed) · a dev
action. Every wake-up calls the same `Reconcile(episode)`.

### 15.2 The steps

```
Reconcile(episode):
  0 GATE      if episode.state == Closed: return                 // idempotent: a duplicate wake-up is a no-op
  1 OBSERVE   for each member without an outcome:                 // pure read, mutates nothing in vanilla or the Network
                obs = port.Observe(member.pawn)                   // ObservedKind + facts (§ 9.3), via the PhysicalWorldPort
                member.observed/observedTick = obs               // diagnostics only; never a consequence
  2 DECIDE    outcome(member) = Terminal(obs, exit evidence) or Pending      // § 15.3
              if any member is Pending: return                     // the episode stays Open (long-open warning at 30 days; never auto-closed)
              if an unsupported/contradictory observation: Quarantine and return
  3 PLAN      build ReconciliationPlan on COPIES (may throw; nothing is applied yet):
                CasualtyReport {killed, wounded, captured, missing, fates} · custody/status/heldBy per character ·
                wound days (§ 10.5) · promotions · headcount returns · spatial anchor · pawn release actions
              VALIDATE: every member belongs to this episode · character.episode == episode.id · custody == Deployed ·
                the actor exists · per-tier conservation (§ 5.1) · no two members share a pawn
  4 COMMIT    ONE synchronous block of primitive assignments (cannot fail half-way), in this order:
                abstract fates and status through the existing services · custody/heldBy/pawn bindings · headcount ·
                SpatialState anchor · member.outcome (set-once) · episode.state = Closed ·
                episode.consequencesApplied = true                               // LAST statement of the commit
  5 RELEASE   vanilla-side effects after the commit: strip episode tags · anonymous pawns: PassToWorld (Decide) ·
              named pawns: normalize (§ 10.6), RESERVE first, then pass to the world (§ 7.4) · held/dead: leave to vanilla
  6 PUBLISH   events and letters; episode.publishedTick = now
```

| If it throws in … | State left behind | Recovery |
|---|---|---|
| 1–3 | nothing changed (`attempts++`, `lastError`) | retried by the watch with backoff; after the bound ⇒ `Quarantined` |
| 4 | the plan was built on copies and committed as primitive assignments, so there is no half-applied commit; if it nevertheless threw before the flag, nothing is flagged | retried; the plan is recomputed from observation |
| 5 | consequences already applied (`Closed`) | the watch finds a Closed episode whose members still carry the episode tag and finishes the release; **tags are the idempotent marker** |
| 6 | consequences applied; `publishedTick` unset | republish only; **never** reapply |

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
| a save/load lands between commit and release | `Closed` is persisted with `consequencesApplied`; the load pass finishes the release from the tags |
| contract/operation reaches a terminal state while an episode is open | the operation is `OpStatus.Physical`; it waits for the episode's `OnPhysicalResolved` (§ 15.5); its own deadline job is cancelled |
| reconcile is called by a dev action twice | the gate |

### 15.5 Reuse of the existing services (no parallel rules)

- **Fates and casualties.** The per-person branches of `ContractorService.ApplyCasualties` (`SetStatus`, death
  fields, events, leader loss, morale shock by loss share, succession, `EndActor`) are *extracted* into a
  physical-apply entry point that the abstract resolver also uses. The *per-operation* bookkeeping (`opsCompleted++`,
  skill gain, morale on success) stays with the operation's own resolution and is **not** repeated for a physical
  episode unless it resolves an operation.
- **Careers.** `CareerService.CommitOutcome` is called only for an operation-linked episode, only at the
  authoritative end, and is guarded by the existing `careerOutcomeApplied` flag: P3-INV-014.
- **A rescue replaces `TroubledDeadline`'s dice, not its branches.** When the episode resolves a Troubled operation,
  `OperationService` gains `OnPhysicalResolved(op, found)` that runs the existing *found* branch
  (`ReturnForces`, `OnRecovered`) or *written-off* branch (`CommitOutcome(op, true)`, `Finish`, `OnWrittenOff`).
- **Spatial.** `Spatial.OnPhysicalEpisodeClosed(actor, tile)` (§ 12.2).
- **History and letters.** New event keys (`Episode.Opened/Closed`, `Character.CapturedByPlayer`, …) go through the
  existing bus, published *after* the commit; letters are a `LetterConsumer` concern as today.

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
| `Stored` (between episodes) | `WorldPawns` (`pawnsAlive` or `pawnsMothballed`, saved `Deep`) | `custody = Stored`, binding | the runtime registry is rebuilt from the characters store in `FinalizeInit` (§ 16.3) |

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
| `KnownCharacter.pawn`, `.episode`, `.heldBy`, `.heldSinceTick` | the characters store | absent ⇒ defaults (no pawn, no episode, not held), which is **correct**: nobody has ever been materialized, and every saved `custody` is `0 = Unmaterialized` |
| a new entity-id kind | `EntityKind.Deployment` renamed `Episode` (value 9), added to `NetworkState.MaxEntityId` and its test | n/a |
| (3.1) the registry quest | created lazily by vanilla's `QuestManager`; **not** a Network store | n/a |

Not persisted, by rule: the runner, sessions, sandboxes, the wake-up queue, the runtime index, the runtime registry,
anything about the physical test tier.

### 16.5 Migration and version implications (the number is **not** chosen here)

- Phase 3 will require **one format bump** (a "next version" migration `…ToN+1PhysicalLifecycle`). It performs no
  data change (the new fields default correctly) but *marks* the save as written by a Phase 3 build.
- **Why bump if nothing migrates?** An older build treats `deployments` as a `ReservedStore` and would **silently
  drop** episode data at its next save, orphaning pawns the Network had bound. The existing newer-version guard
  (`diagnostics.downgradedFrom`, "saved by a newer Network: best-effort") exists for exactly this; only a bump
  triggers it.
- **Validator.** New findings are **report-and-quarantine**, never repair-by-guessing: a character `Deployed` with no
  open episode; an episode member whose character does not point back; two members sharing a pawn; an `Open` episode
  older than the long-open threshold; a `Stored` character with no pawn; a `Dead` character that is not `Released`.
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
| reconcile threw | `Open` (nothing committed) or `Closed` with release/publish pending | retry with backoff; after the bound `Quarantined`; release/publish retried from their markers | double-apply |
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
| save size | vanilla pawn saves (≈ 10–40 KB each) | the soft cap of ≈ 150 retained pawns bounds it; our own data ≈ 0.5 KB per episode, ≈ 40 B per binding |

### 18.3 Forbidden

Scanning every pawn, every map or all world pawns per tick · scanning 130 contractors per frame · a global custody
search per frame · a poll over anything that is not a member of an Open episode or a held person.

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
   ┌──────── TIER P: PHYSICAL  (separate menu · explicit · disposable save/map ONLY) ──────────────┐
   │ real pawns, real maps, real world objects, real factions                                      │
   │ RT-PHYX-*  every created entity tagged TheNetwork.Test.<runId> · blast-radius proof           │
   │ never part of Quick smoke or Full safe regression · refuses on a colony that looks real       │
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

These IDs extend the Phase 2.9 stable-ID discipline (never renumbered, never reused; a new behaviour gets a new
ID) and the suite plugs into `RuntimeTestPlans.FullSafe` unchanged. **Full Safe Regression remains safe on a real
colony: read-only live state, mutable scenarios only in the sandbox** (P3-INV-013, enforced by the existing source scan,
extended to forbid the real physical adapter and any pawn-creating API in `RuntimeTests/Suites/`).

### 21.2 Tier P: the physical integration suite (disposable dev map and save only)

| Element | Design |
|---|---|
| Name and menu | Dev Mode → **"The Network (PHYSICAL TESTS: disposable save only)"**, a separate category; every action label starts with `⚠ PHYSICAL`; **not** reachable from Quick smoke or Full safe regression |
| Confirmation | a modal window that lists, in plain words, what the suite will create and mutate in *this* save, and requires **typing a fixed phrase** (not a button) |
| Environment guard (all must hold, each reported on refusal) | Dev Mode on · the live Network has **no live player contract or operation** and **no open episode** · the colony does not look real (a bounded sanity check on colonist count and wealth; thresholds **OPEN**, spike S22) · optionally, a save marked by the owner as disposable. **How to detect "this save is disposable" robustly is OPEN (S22).** No persisted test flag is added if it can be avoided (the Phase 2.9 rule: nothing about testing is saved) |
| Where | by default a **dedicated generated test map** on a hidden `MapParent` the suite creates; the home map of a *disposable* save only for scenarios that need a colony (S14); never an existing pawn |
| Tagging | every entity the suite creates (pawns, world objects, factions, items) carries `TheNetwork.Test.<runId>` |
| Blast-radius proof | the physical analogue of RT-INFRA-001: counts of tagged vs untagged pawns, world objects and factions before and after; **untagged state must be unchanged**, tagged state must equal the script |
| Cleanup | removes only tagged entities, and only when the run ends cleanly or on an explicit cleanup action |
| On failure | the failed scenario is **preserved** (map, pawns) and a report is written; nothing is auto-deleted |
| Source layout | its own folder (candidate `Diagnostics/RuntimePhysicalTests/`); its own scan: spawning APIs are allowed *there and nowhere else* |
| Sentinel | a `PhysicalSentinel` (pawn counts by tag/faction, world-pawn counts, factions, world objects), the pawn-level counterpart of `ColonySentinel` (which deliberately never scans pawns) |

| ID (suggested) | Scenario (real RimWorld) | Slice |
|---|---|---|
| RT-PHYX-001 | generate + bind + spawn exactly one named pawn on the test map; tags and binding agree | 3.1 |
| RT-PHYX-002 | the visit Lord runs, the pawn exits through the edge, the episode reconciles `Returned` once | 3.1 |
| RT-PHYX-003 | downed, then recovers; observed by the poll, recovery truth correct | 3.1 |
| RT-PHYX-004 | killed (dev damage); death recorded once; the abstract layer cannot resurrect | 3.1 |
| RT-PHYX-005 | the test map is removed with the pawn still on it; the person is observed, not erased | 3.1 |
| RT-PHYX-006 | **rematerialization**: same `Pawn` object (same `thingIDNumber`), name, age + catch-up, permanent injury kept | 3.1 |
| RT-PHYX-007 | registry: a stored pawn is not redressed in N forced generations; `Suspended`; not GC'd across several GC passes | 3.1 (S9r) |
| RT-PHYX-008 | the temporary faction is created, hostile/neutral as seeded, removed after the episode; pawn faction nulled harmlessly | 3.1 (S10) |
| RT-PHYX-009 | unsupported custody (dev arrest) ⇒ `Quarantined`, pawn untouched | 3.1 |
| RT-PHYX-010 | save/load matrix (owner-assisted, with a checklist; there is **no** save-reload automation) | 3.1 (S24) |
| RT-PHYX-020+ | arrest/recruit/kidnap/caravan/rescue-site custody, group of five, anonymous members, held-person watch | 3.2 |

### 21.3 What Phase 3 adds to the Phase 2.9 infrastructure

A fake `PhysicalWorldPort` in the sandbox; the `RT-PHYS-*` suite; extensions to the safe-suite source scan; the
separate physical tier with its guard, sentinel and scan; headless `Runner.*` tests for the guard and the blast-radius
proof; and the `LiveFingerprint`, which already walks every persisted field, so it covers `EpisodeStore` and the new
character fields automatically (a test asserts that).

---

## 22. Phase 3 vertical slice

### 22.1 The recommendation: **3.1, the controlled physical episode**

The smallest implementation that proves the whole lifecycle without dragging in capture, caravans, resurrection,
multiple organizations, transport pods or twelve encounter types:

```
 ONE existing Solo contractor (embodied KnownCharacter, custody Unmaterialized)
   ─► Dev action (disposable physical tier): "Materialize … as a visitor"           [cause = Dev, flagged]
   ─► Plan: gate checks · episode Planned · custody Deployed · temporary faction created
   ─► Create the pawn ONCE (ForceGenerateNewPawn) · BIND (member Created) · spawn at the edge · tag · visit Lord
   ─► the person is physically present; vanilla AI does vanilla things; the pawn can be wounded, downed, killed
   ─► ONE of: exits through the edge · is killed · its map is removed · (unsupported custody ⇒ Quarantined)
   ─► Reconcile: observe → plan → commit → flag → release → publish   (exactly once, however many wake-ups)
   ─► the contractor is abstract again if legally allowed (Stored: pawn retained, reserved, normalized)
   ─► LATER: materialize the same contractor again ⇒ the SAME Pawn object, same name, age + catch-up,
      permanent injuries kept   ◄── identity continuity proven
```

**Why a Solo.** One actor, one character, one pawn: no roster math, no detachment accounting. Group conservation
(P3-INV-007) is proven abstractly in 3.0 (RT-PHYS-009) and physically in 3.2.

**Why a dev trigger.** The only *content* trigger that exists today (a Troubled operation's Last Known Location) needs
site-holder pawns (S11), captive handling and operation suspension, which are 3.2. Inventing a gameplay trigger here
would smuggle design into an engineering milestone. The slice is intentionally a *lifecycle proof*, then the owner
reviews it before any player-facing content.

### 22.2 Exact boundaries

| In 3.1 | Out of 3.1 (designed in this document, **not** implemented, **not** faked) |
|---|---|
| a Solo, one retained named pawn | groups, anonymous members, detachments |
| the visit Lord on a generated test map, or on the home map of a **disposable** save (the physical-tier guard refuses a real colony) | rescue sites and any `SitePart.things` holder |
| outcomes: exits, wounded/downed then recovers, killed, map removed | arrest, recruit, enslave, kidnap, caravan, pod/shuttle, world-pawn custody **beyond a safe Quarantine** |
| the registry reservation (S9r) and store-time normalization (S12) | resurrection handling beyond the opportunistic check |
| the temporary faction (S10) | leases, sponsorship, delivery in person, ambient visits |
| the authority gate, episode store, `PawnRef`, characters' new fields, validator, compaction, prepare-for-removal settle | any content trigger, any letter beyond vanilla's, any player UI beyond a read-only Episode Monitor |
| `RT-PHYS-*` (safe) and the 3.1 `RT-PHYX-*` | the Phase 4 equipment seam is *defined* but only input (a) exists |

**Fail safe, never fake.** If during 3.1 the player arrests, recruits or kidnaps the visitor, or a caravan takes them,
the member is observed `HeldBy…`/`InCaravan`, the episode becomes **`Quarantined(UnsupportedCustody)`**, the pawn is
left exactly as vanilla has it, the person is blocked from abstraction, and a dev diagnostic says what happened. Capture
**support** is 3.2; capture **safety** is 3.1.

### 22.3 Preconditions (gates)

3.0 merged and owner-reviewed · S9r (registry) passed *or* its fallback ladder decided · S12 (normalization) · S14 (visit
Lord) · S10 (temporary faction) · S23 (creation pins and modded races) · S21 (observation completeness) · S22 (the
physical-tier guard). 3.0 needs **no** spike.

---

## 23. Suggested subphases

The minimum sane breakdown after auditing complexity: **three**, each ending in something the owner can review and
test, with the riskiest unknowns behind explicit gates. Not more: every split below has its own proof, and a further
split would only add review overhead.

| Subphase | Content | Proof | Owner gate |
|---|---|---|---|
| **3.0 Authority and episode foundation** (no RimWorld pawn) | `EpisodeStore`, `PawnRef`, the character fields, `AuthorityGate` and its call sites, the physical-apply extraction, reconciliation, the `PhysicalWorldPort` + scriptable fake, the validator, compaction, prepare-for-removal settle, `RT-PHYS-001…019`, save version bump + no-op migration | headless suite + soaks + the **safe** runtime tier in the owner's real colony. **Zero new risk to a real save.** | review the abstract core before any pawn exists |
| **3.1 The controlled physical episode** (the slice) | the real `PhysicalWorldPort` adapter, projection, binding, tags, `SignalBridge` routes, the visit Lord, the temporary faction, the registry quest, store-time normalization/catch-up, the **physical test tier**, `RT-PHYX-001…010`, a read-only Episode Monitor | the physical tier on a disposable save; the owner's save/load checklist | review real pawns before any content |
| **3.2 Custody and rescue** | held-person observation (arrest, recruit, enslave, kidnap, caravan, pod), the custody watch, promotion of anonymous pawns, **anonymous members and groups**, the **rescue** episode for a Troubled operation (site holder, `OpStatus.Physical`, `OnPhysicalResolved`), the Last Known Location with survivors/captives, the events, `RT-PHYX-020+` | physical tier + owner play | **first player-visible content** |

### 23.1 Re-scoped out of Phase 3 (needs an owner decision)

[IMPLEMENTATION_PHASES § 6](IMPLEMENTATION_PHASES.md#6-phase-3-abstract--physical-lifecycle) lists scope the design above does not
carry. Recommended disposition, **for the owner to confirm**:

| Original Phase 3 item | Recommendation |
|---|---|
| Sponsorship with equipment and **leases** | **Out** (Phase 4 compensation). The `leases` slot and the gear seam ([§ 11.2](#112-the-phase-4-seam-not-built)) are reserved. |
| **In-person delivery** (walk-in hand-over) | **Out** of 3.0–3.2. The owner-validated drop-pod delivery stays. A later subphase after 3.2, or Phase 4, once visits are proven. |
| **Contract inheritance and continuation** | **Out** (a contract-lifecycle feature; not required by the physical lifecycle). |
| **Consequence Engine v1** | **Reduced** to the rescue rule in 3.2; the rest stays. |
| **Promotion** of generic pawns | **In**, minimally, in 3.2 (capture/recruit/rescue). |
| Events `Contractor.Rescued`, `KnownCharacter.CapturedByPlayer/.Defected/.Lost`, `Player.BetrayedContractor` | **In** as 3.2 needs them. |
| **Deployment Monitor** dev window | **In**, read-only, renamed Episode Monitor, in 3.1. |
| Runtime scenarios | **In**, as the two-tier design above. |
| Ambient visits | **Deferred**, unchanged. |

The Phase 0 spike list (S9, S10, S11, S12, S14, S17) is kept and revised ([§ 25](#25-open-questions-and-spikes)).

---

## 24. Risks

New risks are added to [RISKS](RISKS.md) as R-28 to R-34; existing R-01, R-02, R-11, R-13, R-15 point here.

| ID | Risk | Mitigation | Proven in |
|---|---|---|---|
| R-28 | **An authority leak:** an abstract writer keeps simulating a physical person (the "healthy Halvard / dead Halvard" bug) | one gate ([§ 3.3](#33-operational-rules)); the writer inventory ([§ 2.3](#23-the-writer-inventory-every-abstract-site-that-must-honour-authority)); `RT-PHYS-011` static enumeration; the validator | 3.0 |
| R-29 | **Exactly-once fails** under duplicate, late or missing wake-ups, or a throw mid-reconcile | plan → commit → flag → publish; set-once outcomes; `consequencesApplied` set last; `RT-PHYS-003/013/014` | 3.0 |
| R-30 | **Registry reservation** unworkable or too costly (*R* × *W* per tick); S9r fails | the small reserved set; the fallback ladder; measurement in the soak | 3.1 (S9r) |
| R-31 | **Observation gaps** (downed, caravan join, resurrection, map-removal pawns, dropped signals) lose a person | bounded polls; positive-evidence-only `Returned`; the load pass; Quarantine | 3.1/3.2 (S21) |
| R-32 | **The physical test tier damages a real colony** | separate menu, typed confirmation, environment guard, tagged blast radius, no cleanup of untagged state | 3.1 (S22) |
| R-33 | **Pawn creation on a heavily modded list** fails, is slow, spams relations, or yields a wrong race | `ForceGenerateNewPawn`, `CanGeneratePawnRelations = false`, capability kind selection, contained abort | 3.1 (S23) |
| R-34 | **An unprepared removal** strands reserved/suspended pawns (only if the vanilla-only registry is chosen) | default Network-owned part (self-heals); Prepare clears the registry | 3.1 (S9r, S6) |
| R-19 (existing) | the audit is build-specific (`1.6.9676.17735`); a 1.6.x update can move internals | every cited API is listed ([Appendix A](#appendix-a-rimworld-16-api-audit)); the physical tier re-checks them | every release |

---

## 25. Open questions and spikes

Do **not** read an OPEN item as a decision. Each names the narrowest experiment.

| ID | Open question | Why it is open | Narrowest experiment / pass criteria | Blocks |
|---|---|---|---|---|
| **S9r** | Does a hidden raw quest (Network-owned part **or** vanilla `QuestPart_ReservePawns` + a vanilla root) give GC protection, redress exclusion, suspension, kill/discard hooks and clean removal at acceptable cost? Which root def is inert? What is the exact removal/cleanup call? | static reading cannot prove it; the per-tick cost is *R* × *W* | N stored pawns; run 200 forced generations incl. `WorldPawnFactionDoesntMatter`; several GC passes; save/load; mod removal; time `GetSituation` at *R* = 150, *W* = 3,000. Pass: none redressed/GC'd, `Suspended`, removal ≤ 3 errors, cost within budget | 3.1 |
| S10 | Creation of a hidden temporary faction outside `QuestGen`: UI visibility, required `leader`, letters, goodwill seeding, automatic removal and nulling | the lifecycle is verified in code; the UI and edge cases are not | create → visit → leave → auto-remove → recreate; no errors; goodwill mirrored | 3.1 |
| S11 | Pawns in `SitePart.things` through a vanilla GenStep **without** `DownedRefugee`'s forced downing; site destroyed first ⇒ pawns to world | the two vanilla steps force a state | a Network-seeded rescue site; save/load before generation; destruction | 3.2 |
| S12 | Store-time normalization and catch-up: which vanilla APIs heal temporary hediffs, advance age safely, reset needs; behaviour of modded hediffs that block mothballing | not statically provable | a pawn with mixed hediffs; store → materialize; no hediff errors; correct life stage | 3.1 |
| S14 | The visit Lord (`LordJob_VisitColony` with a duration): exit, wounded-guest toil, hostility flip, behaviour on a map with no colony | verified structurally, not behaviourally | a visit on a test map and a home map; attack mid-visit | 3.1 |
| S17 | Tag hygiene: no vanilla path parses our tags; copies by other mods | `Pawn_DuplicateTracker` copies none (read); runtime check with Anomaly | duplicate a tagged pawn | 3.1 |
| **S21** | Observation completeness: polls catch downed, caravan join, held transitions, map removal, kidnapped-then-recruited; signals dropped at the cap | the gaps are known; the cadence is a choice | scripted scenarios in the physical tier; no person lost | 3.1/3.2 |
| **S22** | The physical-tier environment guard: how to detect a disposable save without persisting a flag | no verified API for "this save is a test save" | try Quicktest detection, save-name and colony heuristics; refuse on the owner's real colony | 3.1 |
| **S23** | First-creation pins and modded races: name mapping, gender/age pins, kind fallback, `CanGeneratePawnRelations = false`, generation time | mod-list dependent | generate N pawns across a heavy list; no relation spam; contained failures | 3.1 |
| S24 | The save/load matrix ([§ 16.1](#161-save-at-every-point)) on real saves | no save automation exists | owner-assisted checklist, one row per scenario | 3.1 |
| O-2 | The promotion thresholds and the retained-pawn cap | tuning | the soak | 3.2 |
| O-3 | Equipment tier → kind/loadout selection; the `condition` step on gear loss | mod-dependent | S23 + playtest | 3.1 |
| O-6 | Resurrection detection cadence for dead characters that keep a `PawnRef` | no hook | opportunistic + bounded sweep; measure | 3.2 |
| O-7 | Name and generation behaviour for modded races | mod-dependent | S23 | 3.1 |
| O-14 | A long-suspended pregnancy; Ideology/Royalty titles on generated pawns | edge | S23 | 3.2 |
| **Harmony** | Is Harmony truly avoidable for every required event? | **Yes for 3.1 and 3.2** (§ 14.2); the three hook gaps are polls | if S21 fails: document the one method, specify a postfix, **do not implement** | — |

---

## 26. Acceptance criteria

### 26.1 This design pass (docs only)

README and the QA/status documents record the owner's runtime validation truthfully · this document and the API audit
exist and cite the code · every unknown is marked OPEN with a spike · no production code, no DLL, no Harmony, no new
save field, no save-version change · the invariants, the subphasing and the slice boundaries are explicit · ADR-048 and
ADR-049 and risks R-28 to R-34 are recorded.

### 26.2 Phase 3 as a whole (definition of done)

1. P3-INV-001 … 016 hold in the headless suite, the safe runtime tier and the physical tier.
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
| 3.0 | all 19 `RT-PHYS` cases pass headless; the safe in-game tier is green in the owner's colony with **no change to the colony** (the fingerprint covers the new store); the save bumps once and old saves load unchanged; the validator, compaction and prepare-for-removal settle are tested; mutation checks (an abstract writer bypassing the gate; a reconcile that flags first; a signal handler that reconciles inline; `Dead` overwritten) are each caught by a named test |
| 3.1 | the slice in § 22 runs on a disposable save with the blast-radius proof green; the rematerialization shows the same `Pawn`; S9r, S10, S12, S14, S21, S22, S23, S24 are recorded as PASS/PARTIAL/FAIL with their consequences |
| 3.2 | the held-person matrix and the rescue scenario pass in the physical tier; a Troubled operation with a rescue episode never also resolves abstractly; the soak numbers are attached |

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
| A11 | Suspension | `Pawn.Suspended` (`Pawn.cs:1112`); `TickInterval`, `TickMothballed` (`:1618–1750`) | `ReservedByQuest` ⇒ suspended ⇒ **no health, needs, jobs or aging** | Yes | n/a | Med | catch-up at materialization (§ 6.4) |
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

---

## Appendix B: Formal invariants

| ID | Invariant | Enforced by | Proven by |
|---|---|---|---|
| **P3-INV-001** | A person has **at most one authoritative physical representation**: a character has ≤ 1 bound pawn ever and ≤ 1 open episode membership; a pawn is bound to ≤ 1 character or slot | `Plan` refuses a character with `custody ≠ Unmaterialized/Stored` or an open `episode`; write-once `PawnRef`; reverse index; validator | RT-PHYS-001, 002; validator |
| **P3-INV-002** | A physical person is **never also simulated as freely abstract** | `AuthorityGate`; the writer inventory | RT-PHYS-011, 006 |
| **P3-INV-003** | An episode **reconciles at most once**: `Closed` is terminal; member outcomes are set-once; `consequencesApplied` is set last, inside the commit | the algorithm of § 15; the gate on `Closed` | RT-PHYS-003, 013, 014 |
| **P3-INV-004** | **Physical death cannot be overwritten** by stale abstract health: no writer changes a `Dead` status; resurrection is observed, never initiated | `SetStatus` guard + validator | RT-PHYS-006 |
| **P3-INV-005** | **Custody prevents legal dematerialization:** authority returns to abstract only on a positive terminal observation (`WorldFree` + exit evidence) | `Terminal(obs, evidence)` | RT-PHYS-008 |
| **P3-INV-006** | **Rematerialization preserves durable identity:** the same bound pawn is reused; never regenerated; a lost pawn makes the person `Lost` | write-once binding; `Plan` | RT-PHYS-004, RT-PHYX-006 |
| **P3-INV-007** | **Group materialization cannot clone roster members:** named members come only from the actor's own records (or the operation's `characters`), anonymous slots only from committed headcount; `materialized = returned + killed + wounded-returned + held + missing + lost` per tier | `Plan` validation; conservation check in § 15 | RT-PHYS-009 |
| **P3-INV-008** | **Save/load does not duplicate or reroll** a physical representation; load never generates, spawns or destroys | the load pass (observe, re-tag, rebuild) | RT-PHYS-007, 015; S24 |
| **P3-INV-009** | **Physical location and abstract `SpatialState` cannot both advance as independent truth:** the anchor is frozen while physical and written once at close | `Spatial` early-out; `OnPhysicalEpisodeClosed` | RT-PHYS-017 |
| **P3-INV-010** | **No physical episode disappears silently:** `Open → Closed` requires a terminal outcome per member with an observation; absence of evidence is `Missing`/`Lost` with a diagnosis, never `Returned` | § 15.3 | RT-PHYS-008, 010 |
| P3-INV-011 | The Network **never destroys or discards a pawn** and creates pawns only at placement; it only passes pawns to the world, and only ones it created | code rule + source scan | review; scan |
| P3-INV-012 | **Provenance is the binding:** a pawn is "ours" iff a binding says so by reference equality; tags, names, factions and labels are never evidence | handlers compare bindings | RT-PHYS-001; S17 |
| P3-INV-013 | **The safe tier never reaches the physical adapter or a pawn-creating API** | source scan; the fake port | the scan |
| P3-INV-014 | **Exactly-once of money and career effects** is unchanged: a physical episode that resolves an operation reuses `careerOutcomeApplied` and the existing ledgers | reuse of the existing flags | the existing soaks + RT-PHYS-013 |
| P3-INV-015 | **Bounded work:** no per-tick scan; jobs exist only while an episode or a held person exists | scheduler discipline | idle-cost test; the soak |
| P3-INV-016 | A person in an episode is **occupied**: it cannot be checked out for an operation, and an operation's committed person cannot be planned into an episode | `Occupied` extended | RT-PHYS-016 |

---

## Appendix C: Interaction classification

**NO CHANGE** · **SMALL INTEGRATION** · **NEW AUTHORITY RULE** · **MAJOR RISK**.

| System | Class | What changes |
|---|---|---|
| Procurement | SMALL INTEGRATION | candidate selection and refusal reasons inherit the gate; nothing else |
| Operations | **NEW AUTHORITY RULE** | `OpStatus.Physical`; a rescue episode cancels `operation.troubled` and resolves through `OnPhysicalResolved`; `Occupied` includes episodes |
| Spatial | **NEW AUTHORITY RULE** | frozen while physical; one write at close (§ 12) |
| Field Log | NO CHANGE | (a physical beat may come later) |
| Career | SMALL INTEGRATION | an open episode counts as a job; `CommitOutcome` only for operation-linked episodes, once |
| Equipment | **NEW AUTHORITY RULE** | G1–G6; no mirror; a seam for Phase 4 |
| Reputation | NO CHANGE | physical events publish ordinary events; no new rules |
| Relations | SMALL INTEGRATION | seed and mirror the temporary faction's goodwill; player-hostility events |
| Knowledge | NO CHANGE | |
| History | SMALL INTEGRATION | new event keys, published after commit |
| Consequences | SMALL INTEGRATION (3.2: rescue) | the Last Known Location gains survivors in 3.2 |
| Recovery | **NEW AUTHORITY RULE** | physical → abstract recovery mapping; recovery runs once |
| Organization roster | **NEW AUTHORITY RULE** | headcount conservation; returned/lost applied once |
| `KnownCharacter` | **MAJOR RISK** | the identity binding and custody are the riskiest new truth |
| Runtime test infrastructure | SMALL INTEGRATION + a **new tier** | fake port, `RT-PHYS`, the physical tier (§ 21) |
| Compaction | SMALL INTEGRATION | Closed episodes; never a character with a pawn or held |
| Save migration | SMALL INTEGRATION (consequential) | one bump; the downgrade hazard is why |
| Prepare-for-removal | **NEW AUTHORITY RULE** (+ MAJOR RISK) | settle, clear the registry, strip pawn tags; never delete (§ 20) |

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
| E16 | Phase 3 scope: deployments, leases, sponsorship, delivery, inheritance, Consequence Engine v1, monitor | too much for one phase and partly Phase 4 | **three subphases**; items re-scoped for owner decision (§ 23.1) |
| E17 | one test tier | Full Safe Regression must never spawn | two tiers (§ 21, ADR-049) |
