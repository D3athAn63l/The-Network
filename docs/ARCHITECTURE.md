# The Network — Technical Architecture

> Canonical technical reference. Phase 0 output.
> Canonical product and gameplay specification: the
> [master design](../The%20Network%20%E2%80%94%20Full%20Mod%20Design%20-%20Master%20Implementation%20Brief.md).
> The master design document defines product and gameplay intent. The architecture documents define technical
> implementation. Where they conflict, the design takes precedence unless a reviewed ADR
> explicitly records a necessary deviation ([DECISIONS](DECISIONS.md)). References such as
> "master § 15" point at sections of that document.
> Companion documents: [DATA_MODEL](DATA_MODEL.md) · [EVENTS_AND_HISTORY](EVENTS_AND_HISTORY.md) ·
> [STATE_MACHINES](STATE_MACHINES.md) · [SIMULATION](SIMULATION.md) ·
> [ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md) ·
> [RIMWORLD_INTEGRATION](RIMWORLD_INTEGRATION.md) · [SAVE_AND_MIGRATION](SAVE_AND_MIGRATION.md) ·
> [COMPATIBILITY](COMPATIBILITY.md) · [PERFORMANCE](PERFORMANCE.md) · [DEBUGGING](DEBUGGING.md) ·
> [IMPLEMENTATION_PHASES](IMPLEMENTATION_PHASES.md) · [DECISIONS](DECISIONS.md) · [RISKS](RISKS.md)

## Contents

1. [Purpose and scope](#1-purpose-and-scope)
2. [The core loop, in system terms](#2-the-core-loop-in-system-terms)
3. [System diagram](#3-system-diagram)
4. [Layers, modules and dependency rules](#4-layers-modules-and-dependency-rules)
5. [Cross-cutting foundations](#5-cross-cutting-foundations)
6. [Subsystem catalog](#6-subsystem-catalog)
7. [Key flows](#7-key-flows)
8. [How history changes future behaviour](#8-how-history-changes-future-behaviour)
9. [UI boundary](#9-ui-boundary)
10. [What is stable now and what stays replaceable](#10-what-is-stable-now-and-what-stays-replaceable)
11. [Where abstraction pays and where it does not](#11-where-abstraction-pays-and-where-it-does-not)
12. [Threading, exceptions and failure containment](#12-threading-exceptions-and-failure-containment)
13. [Self-review against the full design](#13-self-review-against-the-full-design)
14. [Master-design reconciliation](#14-master-design-reconciliation)

---

## 1. Purpose and scope

This document describes the long-term technical structure of The Network. The architecture is
designed for the complete mod described by the master design, including the approved long-term
directions (reputation through history, rumors, bidding, refusal, organizational morale, gossip,
contract inheritance, favors/debts, introductions, black contracts, evidence and witnesses,
sanctions, geographic knowledge, learning, retirement transformation, fragmentation and
mergers, legends, and chain reactions). Implementation is still delivered in small vertical phases.

The architecture has to:

- keep persistent structures stable enough that later phases **add** to them rather than rewrite them;
- keep formulas, heuristics, content and UI **replaceable**;
- use vanilla RimWorld 1.6 extension points and avoid Harmony;
- keep ordinary play free of measurable TPS cost;
- survive long saves, mod removal and version upgrades.

## 2. The core loop, in system terms

| Loop step | Owning subsystem(s) | Persistent artefact |
|---|---|---|
| Player intent | Presentation → Commands → Intel / Contracts | `IntelRequest`, `Contract` (issuer = player) |
| World reaction | Scheduler → Intel / Bidding / Willingness | Offers, refusals, search progress |
| Opportunity | Opportunities (world truth) + Leads (reported perception) | `Opportunity`, `Lead` |
| Success / failure | Operations + Abstract Resolver, or physical play via Physical Adapters | `Operation` outcome, physical `Episode` reconciliation |
| Consequences | Event Bus → consumers; Consequence Engine | Relation edges, obligations, morale, follow-up opportunities |
| Remembered history | History Ledger + Summaries + Legends | `HistoryRecord`, `ActorRecordSummary`, `Legend` |
| Changed future behaviour | Willingness, pricing, resolver, gossip, reputation all read summaries and edges | derived caches (rebuilt, never rescanned) |
| New stories | Consequence Engine + Opportunity generation | new `Opportunity` / `Contract` with lineage links |

## 3. System diagram

```
                        ┌──────────────────────────────────────────────────────────────┐
                        │                        PRESENTATION                          │
                        │  MainTabWindow_Network (Intel | Procurement | Contracts |    │
                        │  Contractors | History)  ·  LetterPresenter  ·  Inspect text │
                        │        reads ▼ ReadModels            writes ▼ NetworkCommands│
                        └───────────────┬──────────────────────────────┬───────────────┘
                                        │ (pure queries)               │ (validated commands)
┌───────────────────────────────────────▼──────────────────────────────▼────────────────────────┐
│ NetworkWorldComponent  (single persistence root · WorldComponent · tick entry)                │
│                                                                                                │
│  KERNEL ──────────────────────────────────────────────────────────────────────────────────── │
│   IdAllocator · RefResolver (DefRef / FactionRef / PawnRef / WorldObjectRef / TileRef)         │
│   NetScheduler (due-tick job queue, budgeted) · NetworkEventBus (+ bounded EventJournal)       │
│   NetRng (seed derivation) · Migrations (NetworkSaveVersion) · Diagnostics (log/timing/valid.) │
│                                                                                                │
│  DOMAIN STATE (stores, persisted)            DOMAIN SERVICES (stateless logic over stores)     │
│   CastSnapshot (world copy of global cast)   IntelService ─────────► OpportunityService        │
│   ActorStore ──── capabilities:              ContractService ◄─────► BiddingService            │
│     ContractorProfile · FixerProfile ·       QuoteAssembly (contractor + Fixer terms)          │
│     ContractorSimulation ·                   WillingnessModel (refusal / pricing inputs)       │
│     OrganizationProfile · IssuerProfile ·    OperationService ─────► AbstractResolver          │
│     IntelSourceProfile · SponsorProfile ·    ContractorSimService (NPC upkeep, succession)     │
│     IntroducerProfile                                                                          │
│   CharacterStore (Known Characters)          CastImport (settings → world, once)               │
│   RelationStore · ObligationLedger ·         ReputationModel (derived, cached)                 │
│     ContactBook                              ConsequenceEngine (chain reactions, inheritance)  │
│   KnowledgeStore                             GossipService (Phase 5)                           │
│   IntelStore · OpportunityStore                                                                │
│   ContractStore · OperationStore             HISTORY                                           │
│   EpisodeStore · LeaseStore                    HistoryLedger (tiered records) · SummaryStore   │
│   BeliefStore (Phase 5)                        LegendArchive · Awareness (facts vs knowledge)  │
│                                                                                                │
│  INTEGRATION (the only code that touches live RimWorld objects) ─────────────────────────────── │
│   ItemCatalog (session cache of ThingDefs)   SiteAdapter (vanilla Site + injected WorldObjectComp)│
│   CustodyService (pawn binding/reservation)  EncounterFactionAdapter (temporary factions)      │
│   DeliveryAdapter (drop pods; shuttle opt.)  PaymentAdapter (silver in/out)                    │
│   SignalBridge (SignalManager receiver)      CompatRegistry (DLC / mod adapters, gating)       │
│   CommsAccessAdapter (usable Comms Console)  ColonyReader (player execution state, read-only)  │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
          │ vanilla APIs only (no Harmony in Phases 1–3)
          ▼
   RimWorld: WorldPawns · FactionManager · WorldObjects/Site · QuestManager (registry quest, Ph.3)
             SignalManager · LetterStack · TradeUtility · DropPodUtility · DefDatabase · Scribe
             Building_CommsConsole · ModSettings (NetworkSettings: global cast, read-only at runtime)
```

Arrows point in the direction of calls. Services never call Presentation. Domain never calls
Integration directly: domain code asks through narrow adapter interfaces
(`ISiteAdapter`, `ICustody`, `IPayment`, `IDelivery`, and from Phase 2.5 `ISpatialWorld` for the
world graph, [SPATIAL § 4](SPATIAL.md#4-the-world-graph-port-and-adapter)) so that domain logic and state machines
can be tested headless ([DEBUGGING § Headless tests](DEBUGGING.md#6-headless-tests)).

## 4. Layers, modules and dependency rules

| Layer | Namespace (planned) | May depend on | Must not depend on |
|---|---|---|---|
| Kernel | `TheNetwork.Kernel` | Verse (Scribe, Log), System | Domain, Integration, Presentation |
| Domain | `TheNetwork.Domain.*` | Kernel; adapter **interfaces** | Integration implementations, UI, `Find.*` (except through Kernel clock) |
| History | `TheNetwork.History` | Kernel, Domain read APIs | Integration, UI |
| Integration | `TheNetwork.Integration.*` | Kernel, Domain, RimWorld/Verse | Presentation |
| Presentation | `TheNetwork.UI` | Kernel, ReadModels, Commands | Stores directly (read-only through ReadModels) |
| Compat | `TheNetwork.Compat.*` | everything above; DLC types behind `ModsConfig` checks | — |

Rules:

1. **Only the Integration layer touches live RimWorld objects** (`Pawn`, `Map`, `Site`, `Faction`).
   Domain state holds typed references ([DATA_MODEL § 2](DATA_MODEL.md#2-external-references)),
   never raw object pointers, except `PawnRef`, which wraps a `Scribe_References` pointer and
   exists for exactly this purpose.
2. **All mutation goes through services or commands.** UI code never edits a store collection.
3. **Services are stateless.** Their persistent state lives in stores and their runtime caches
   are rebuildable. Tests can therefore construct a `NetworkState` and run services on it
   directly.
4. **The clock is injected.** Domain code reads `Clock.Now` (the kernel's wrapper around
   `Find.TickManager.TicksGame`). It never reads `Find.TickManager` directly.
5. **No domain logic in `ExposeData`** beyond load-time defaulting. Validation and repair run
   in the post-load pipeline ([SAVE_AND_MIGRATION § 5](SAVE_AND_MIGRATION.md#5-load-pipeline)).

## 5. Cross-cutting foundations

| Foundation | Summary | Detail |
|---|---|---|
| Identity | One persisted `int` counter and typed ID structs (`ActorId`, `ContractId`, …). IDs are never reused, never renumbered and never derived from RimWorld objects. | [DATA_MODEL § 1](DATA_MODEL.md#1-identifier-strategy) |
| External references | `DefRef` (defName string), `FactionRef` (loadID), `WorldObjectRef` (ID), `PawnRef` (`Scribe_References` plus a snapshot), `TileRef` (`PlanetTile` plus layer def name). Every reference resolves silently, and a miss has a defined recovery. | [DATA_MODEL § 2](DATA_MODEL.md#2-external-references) |
| Scheduling | A persisted min-heap of `(dueTick, seq)` jobs. The per-tick cost is one integer comparison, and a budget caps how many jobs run in one tick. | [SIMULATION § 1](SIMULATION.md#1-scheduler) |
| Events | Typed `NetworkEvent`s are dispatched synchronously in a fixed consumer order. A bounded journal is kept. Deferred reactions are persisted as scheduler jobs. | [EVENTS_AND_HISTORY § 1](EVENTS_AND_HISTORY.md#1-network-events) |
| Determinism | Each entity has its own seed. A private PRNG drives the resolver. Random results are committed and persisted at the moment they are decided. | [SIMULATION § 6](SIMULATION.md#6-determinism-and-rng) |
| Versioning | One integer `NetworkSaveVersion` with ordered migrations, per-entity validation and quarantine. | [SAVE_AND_MIGRATION](SAVE_AND_MIGRATION.md) |
| Diagnostics | Logging tiers under the `[TheNetwork]` prefix, per-job-kind timing, validators and dev actions. | [DEBUGGING](DEBUGGING.md) |

---

## 6. Subsystem catalog

Each entry lists: **Responsibility · Persistent state · Runtime cache · Public surface ·
Dependencies · Emits · Consumes · Lifecycle · Future extensions**.
Event names here are shorthand (`ContractFailed`). The canonical, persisted type keys are dotted
(`Contract.Failed`) and are defined in [EVENTS_AND_HISTORY § 2](EVENTS_AND_HISTORY.md#2-event-catalog).
Method names are illustrative. The responsibilities and boundaries are binding.

### 6.1 NetworkWorldComponent (Kernel root)

- **Responsibility.** Single persistence root and tick entry point. Owns `NetworkState` (all
  stores) and `NetworkRuntime` (services, caches, adapters). Runs the load pipeline and bootstrap.
- **Persistent.** `saveVersion`, `createdWithModVersion`, `lastSavedWithModVersion`,
  `networkSeed`, `bootstrapped`, `IdAllocator` counters, and every store in a fixed order
  ([SAVE_AND_MIGRATION § 2](SAVE_AND_MIGRATION.md#2-save-layout)).
- **Runtime cache.** `NetworkRuntime`, rebuilt in `FinalizeInit`. A static
  `NetworkWorldComponent.Instance` is set in the constructor and cleared when the world is
  replaced. This is the same pattern Parametric's `OverloadGameComponent` uses.
- **Public surface.** `Network.Current` (null-safe accessor), `Network.Commands`,
  `Network.Read`, `Network.Events`, `Network.Scheduler`.
- **Dependencies.** RimWorld `WorldComponent` lifecycle.
- **Emits.** `NetworkBootstrapped`, `NetworkLoaded`.
- **Consumes.** None.
- **Lifecycle.**
  - The constructor runs during world generation, and also when the mod is added to an
    existing save. It must stay trivial: no `Find.*`, no Def lookups.
  - `ExposeData` saves or loads the stores.
  - `FinalizeInit(fromLoad)` is called for new worlds **during world generation**
    (`WorldGenerator.cs:67`), before the colony exists, and on load from `Game.LoadGame`. It
    rebuilds runtime indexes only and emits nothing. A world that has not bootstrapped derives
    its `networkSeed` from the world's seed here, before any runtime service is built, so every
    service sees the real seed; a bootstrapped save keeps its persisted seed.
  - **Start-up** (`EnsureStarted()`) runs once per session on first use: normally the first
    `WorldComponentTick`, or an earlier site callback, signal or command. For a new game, or a
    save that has just gained the mod, it bootstraps the pseudo-actors and the cast snapshot. On
    a load it runs reconciliation (validation, reference repair, custody checks) and registers
    the signal receiver. Starting lazily avoids depending on world-generation ordering and needs
    no GameComponent.
  - **Start-up fails closed.** The session state is `NotStarted → Running` or `Failed`
    (runtime only). If start-up throws, the error is logged once with the failing stage and the
    Network is inactive for the rest of the session: the scheduler does not run, site callbacks
    and signals are ignored, commands return `NetworkStartupFailed`, and the Network tab shows
    that it is inactive (reading still works). Nothing is deleted or rewritten; `bootstrapped`
    is set only after bootstrap has completed, and every bootstrap step is idempotent, so the
    next load simply tries again.
  - `WorldComponentTick` checks `Clock.Now < scheduler.NextDueTick` and returns immediately
    when nothing is due.
- **Future.** None expected. New subsystems register stores and handlers; the root does not grow.

### 6.2 Identity and references (Kernel)

- **Responsibility.** Allocate Network IDs. Resolve external references with silent-fail
  semantics, and report each kind of miss once.
- **Persistent.** `nextId` (int) and `nextEventSeq` (long). References are persisted inside the
  entities that own them.
- **Runtime cache.** Def resolution cache (`defName → Def`, including misses). Faction
  `loadID → Faction`. World-object `ID → WorldObject`. Reverse maps from `Pawn` to
  `CharacterId` and from `Pawn` to `DeploymentId`. All are rebuilt on load and patched
  incrementally.
- **Public surface.** `Ids.Next<TId>()`, `Refs.Resolve(DefRef<ThingDef>)`,
  `Refs.TryResolve(FactionRef, out Faction)`, `Refs.Validate(...)`.
- **Emits.** `ReferenceInvalidated` (external target missing; consumed by the owning subsystem
  to cancel or degrade).
- **Future.** Nothing structural.

### 6.3 Scheduler (Kernel)

- **Responsibility.** Run deferred and periodic work at a due tick with a per-tick budget,
  staggered across entities.
- **Persistent.** Job list `(seq, dueTick, kind, targetId, arg, createdTick)`.
- **Runtime cache.** Binary heap and a handler table keyed by job kind.
- **Public surface.** `Schedule(kind, dueTick, target, arg)`, `Cancel(kind, target)`,
  `Reschedule(...)`.
- **Consumes / Emits.** Neither directly. Handlers belong to services.
- **Detail.** [SIMULATION § 1](SIMULATION.md#1-scheduler).

### 6.4 Event Bus and Journal (Kernel)

- **Responsibility.** Record each meaningful thing once and fan it out to every interested
  subsystem in a deterministic order, without those subsystems knowing about each other.
- **Persistent.** A bounded `EventJournal`: the most recent N events (default 256) and every
  event younger than 15 in-game days, used for diagnostics and the "recent activity" UI. There
  is **no** replay log.
- **Runtime cache.** A consumer table `typeKey → ordered handlers`, and the re-entrancy FIFO.
- **Public surface.** `Events.Publish(NetworkEvent)`.
- **Detail.** [EVENTS_AND_HISTORY § 1](EVENTS_AND_HISTORY.md#1-network-events).

### 6.5 History (Ledger, Summaries, Legends)

- **Responsibility.** Remember what happened, at bounded cost. Maintain incremental summaries
  that other systems read, so nothing ever rescans the ledger. Promote exceptional actors and
  characters to Legends.
- **Persistent.** `HistoryLedger` (tiered `HistoryRecord`s), `SummaryStore`
  (`ActorRecordSummary` and `CharacterRecordSummary`), and `LegendArchive`.
- **Runtime cache.** Index `participant → record ids`, and reputation caches with dirty flags.
- **Public surface.** Read-only queries: `Summaries.For(actor)`,
  `History.RecentFor(entity, n)`, `Legends.All`.
- **Emits.** `LegendPromoted`.
- **Consumes.** Every event marked `importance ≥ Minor` (Minor events update summaries only).
- **Lifecycle.** A retention sweep runs once per in-game quarter, staggered
  ([EVENTS_AND_HISTORY § 4](EVENTS_AND_HISTORY.md#4-retention-pruning-and-aggregation)).
- **Future.** Awareness-aware (perceived) summaries, narrative text generation and
  legend-driven content.

### 6.6 Actors (registry and capabilities)

- **Responsibility.** Give every participant in the Network a stable identity: NPC contractor
  organizations, Solo contractors, Fixers and brokers, other individuals, RimWorld factions (as
  proxies), the player, and the Network's own institutions. Model what each actor can do through
  **capability components**, not inheritance.
- **Persistent.** `ActorStore`: `NetworkActor { id, kind, status, name, lineage, bindings,
  components[] }` ([DATA_MODEL § 4](DATA_MODEL.md#4-actors)).
- **Runtime cache.** Lists by capability (for example "active contractors") and a map from
  faction to proxy actor.
- **Public surface.** `Actors.Get(id)`, `Actors.WithCapability<T>()`,
  `Actors.ProxyFor(Faction)` (created lazily), and `Actors.Player`.
- **Emits.** `ActorCreated`, `ActorStatusChanged`, `ActorRetired`, `ActorDissolved`,
  `ActorAbsorbed`, `ActorSucceeded`.
- **Consumes.** `ReferenceInvalidated` (a proxied faction vanished).
- **Lifecycle.** Actors are never deleted. Ended actors are compacted into tombstones
  ([STATE_MACHINES § 9](STATE_MACHINES.md#9-actor-lifecycle)).
- **Future.** New capabilities (Trader, Recruiter, Settlement founder) are added as new
  component types, with no change to the actor core.

#### 6.6.1 Contractor actors: capability is not simulation

Three separate responsibilities ([DATA_MODEL § 6](DATA_MODEL.md#6-contractor-actors-capability-npc-simulation-organization)):

| Component | Meaning | On |
|---|---|---|
| `ContractorProfile` | **provides contractor services**: bids on, accepts and performs supported contract kinds; public identity and registration, specialties, eligibility, where its capability is read from. It does not grant issuing contracts. | Organization, Individual (Solo), PlayerProxy |
| `ContractorSimulation` | **is simulated off-map as an NPC contractor**: equipment tier and condition, doctrine, morale, funds, career, retirement pressure, **mobility**, upkeep | NPC Organization, NPC Solo |
| `OrganizationProfile` | **has an organization's structure**: roster headcount, leader and lieutenants, wounded recovery buckets, recruitment, succession | NPC Organization only |

Issuing work is a separate capability, `IssuerProfile` (draft and post contracts, invite
contractors, select bids, act as client, pay), and **contractors may hold it too**: most
established organizations carry both, while a poor Solo may only take work.

```
Desperate Solo      Individual   + ContractorProfile + ContractorSimulation
Dead Red            Organization + ContractorProfile + IssuerProfile + ContractorSimulation + OrganizationProfile
Rich institution    Institution  + IssuerProfile
Player (start)      PlayerProxy  + IssuerProfile
Player (Phase 4+)   PlayerProxy  + IssuerProfile + ContractorProfile
```

The player never gets an abstract roster, wounds, equipment tier or morale. When a decision needs
the player's execution state (who is available, injured or equipped; what is carried), it is
read from the real colony through `ColonyReader` in the Integration layer. There is one truth
about the colony, and it is RimWorld's. **Operational capability** (experience tier, strength)
and **public fame** (`PublicReputation`) are separate axes for every contractor, and **mobility**
(`MobilityProfile`: ground, long range, rapid transport, heavy lift, orbital) is a third,
independent axis that the resolver and later presentation adapters read
([DATA_MODEL § 6.2](DATA_MODEL.md#62-contractorsimulation-npc-contractors-only)).

#### 6.6.2 Fixers and brokers

Fixers are persistent, first-class actors, normally `Individual`s embodying a Known Character,
with a `FixerProfile` (brokering) and usually an `IntelSourceProfile` (Intel service)
([DATA_MODEL § 4.3](DATA_MODEL.md#43-fixers-and-brokers-fixerprofile)). Not every Intel source is
a Fixer: faction contacts, known contractors and the Exchange institution provide Intel through
`IntelSourceProfile` alone.

A Fixer shapes, through replaceable policies and never fixed formulas: Intel fees, search speed
and reliability, specialties and geographic reach, access to contractors and markets, brokerage
markup, procurement payment and deposit terms, the insurance offered and its premium and
coverage, quote validity and urgency handling, and what happens when a contractor fails before
work starts. Fixers take part in history, relationships, reputation, knowledge and contacts like
any actor. The player perceives them through descriptors ("cheap but unreliable", "expensive,
but gets results quickly", "a legendary fixer with absurd contacts"), not percentages.
Phase 1 uses Fixers as Intel sources; Phase 2 makes them the procurement broker.

#### 6.6.3 Global Network cast (ModSettings) and world snapshots

The recurring cast lives in `ModSettings` (`NetworkSettings.roster`: contractor and Fixer
templates with stable template IDs), so the same Dead Red can appear in many saves. The settings
hold **who may exist in new worlds**; each world holds **what happened to them in that colony**
([DATA_MODEL § 18](DATA_MODEL.md#18-global-network-cast-modsettings-cross-save)).

- On bootstrap a world **snapshots** the enabled templates into its own `WorldCastSnapshot` and
  instantiates actors from that snapshot with world-local `ActorId`s; the template ID is kept as
  provenance only. A contractor always gets `ContractorProfile`, and gets `IssuerProfile` only
  when its template's explicit `canIssueWork` flag is set.
- Runtime outcomes are **never** written back to `ModSettings`. Global edits, renames and
  regeneration affect future worlds only, and never silently rename, replace or resurrect an
  actor in an existing save.
- Regenerating the generated cast preserves custom entries. The settings data has its own
  version and migrations ([SAVE_AND_MIGRATION § 11](SAVE_AND_MIGRATION.md#11-global-cast-settings-networksettingsversion)).
- The default cast is **about 100 contractor identities** (a setting): Solos, duos, tiny crews,
  teams, companies and specialists, mostly obscure, with a small famous upper tier. They are
  lightweight records, not pawns and not per-tick simulations. **Legendary means famous, never
  protected**: legendary actors fail, get captured, retire and die.

### 6.7 Characters and custody

- **Responsibility.** Track the individuals that matter as **Known Characters**: leaders,
  lieutenants, and anyone the player met or who did something notable. Bind each one to at most
  one real `Pawn`, and only when physically needed. Enforce the no-duplication invariants.
- **Status.** Phase 2 implemented the *records* (`KnownCharacter`, with a persisted `custody` that is never written).
  **Phase 3 is a design only** ([PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md), [ADR-048](DECISIONS.md)); nothing below is
  implemented. Identity is `Actor ≠ Person ≠ Pawn`; a named person keeps one pawn for life; rank-and-file of a *large*
  organization are ephemeral episode slots while a *small* recurring organization concretizes its placed seats into named,
  bound people ([PHYSICAL_LIFECYCLE § 4.5](PHYSICAL_LIFECYCLE.md#45-progressive-concretization)); *presence alone* promotes
  nobody in a large organization (a material outcome or a named story is required). A first projection never
  contradicts established truth: Operational Roles, role composition, team cohesion and truthful aging
  ([ADR-050](DECISIONS.md)); a role or composition derives from **immutable origin facts**, never from when the player first
  looked ([PHYSICAL_LIFECYCLE § 6.6.5](PHYSICAL_LIFECYCLE.md#665-identity-comes-from-immutable-origin-facts-never-from-when-the-player-first-looks)).
- **Persistent (design).** `CharacterStore` (`KnownCharacter` records, plus `pawn`, `episode`, `heldBy`, `opRole`,
  `firstEncounterTick`; `PawnRef` carries `agedThroughTick`), the `EpisodeStore` in the already-reserved `deployments` slot
  (the Phase 0 `DeploymentStore`, renamed; it also carries the explicit per-stage markers, the publication outbox and its
  cursor), `OrganizationProfile.composition` (a small role template, *stored* lazily in 3.2 but a pure function of immutable
  origin facts, so storing it never changes what it is), and the reserved `LeaseStore` (one of **two** Phase 4 equipment seams: a *Notable Asset* is owned by the person,
  not stored in `leases`).
- **Runtime cache.** `thingIDNumber → (episode, member)`, `CharacterId → member`, and the registry of stored pawns
  (all rebuilt from the stores in `FinalizeInit`).
- **Public surface (candidate names).** `AuthorityGate.CanSimulateAbstractly(person)`, `Episodes.Plan/Materialize`,
  `Episodes.Reconcile(episode)` (a pure `ReconciliationPlan`, validation, a snapshot-guarded `Applier`, then the
  post-commit stages release, follow-up and publish, each with its own durable marker), `Characters.Promote(...)`, pure policy functions (role verdict and correction, composition
  apportionment, concretization policy, cohesion screen); the real RimWorld work sits behind a `PhysicalWorldPort` with a
  scriptable fake for the safe test tier.
- **Emits.** `Episode.Opened/Closed`, `KnownCharacterPromoted`, `…Killed`, `…CapturedByPlayer`, `…Defected`, `…Lost`,
  `Contractor.Rescued` (published after the commit from a durable outbox, one event at a time under a persisted cursor).
- **Consumes.** Tagged signals via `SignalBridge` (wake-ups only), the site comp's map callbacks, the registry quest
  part's kill/discard notifications, and `OperationResolved`.
- **Detail.** [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md) (normative); the Phase 0 text
  [ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md) is superseded where they differ.

### 6.8 Organizations (contractor behaviour)

- **Responsibility.** Run the abstract off-map state of **NPC contractors**: for every NPC
  contractor (Solo or organization) the equipment profile, doctrine drift, morale, funds, career
  age and retirement pressure; for organizations also roster headcounts by tier, wounded
  recovery, recruitment and succession. It never runs for the player (§ 6.6.1).
- **Persistent.** `ContractorSimulation` and, for organizations, `OrganizationProfile` on the
  actor.
- **Runtime cache.** Derived strength rating and morale descriptor, both flagged dirty.
- **Public surface.** `Orgs.EffectiveStrength(actor)`, `Orgs.MoraleState(actor)`,
  `Orgs.Checkout(actor, request)` / `Orgs.Return(...)`, and `Orgs.RunUpkeep(actor)`
  (a scheduler job).
- **Emits.** `OrganizationMoraleShifted`, `LeaderSucceeded`, `OrganizationRetired`, and
  `OrganizationFragmented` / `OrganizationMerged` (Phase 6).
- **Consumes.** `OperationResolved`, `ContractorCasualties`, `ContractCompleted`,
  `ContractFailed`, `KnownCharacterKilled`, `LeaderKilled`, `PaymentDefaulted`, `SponsorshipChanged`.
- **Lifecycle.** Per-contractor upkeep runs about once per in-game day, staggered by seed.
- **Detail.** [SIMULATION § 4–5](SIMULATION.md#4-organizations-upkeep-morale-doctrine).

#### 6.8.1 Careers (Phase 2.75, ADR-046)

- **Responsibility.** The spine that lets work change a contractor: reputation from finished jobs,
  exact contractor money, equipment advancement, and derived `CareerNeed` and Tags. **It extends the
  existing state and adds no second reputation, wealth, skill or equipment system** ([CAREERS](CAREERS.md)).
- **Persistent (all existing homes).** `PublicReputation.score` (the fame band is derived),
  `ContractorSimulation.funds`, `EquipmentProfile.tier`, and one new small `ContractorSimulation.career`
  (`CareerRecord`: cumulative counters, advancement cooldown). `Operation.careerEligible` /
  `careerOutcomeApplied`; `MoneyRecord.contractorSilver` on contract ledgers.
- **Public surface.** `CareerService` (`DomainContext.Career`): `CommitOutcome(op)`, `MoveFunds` /
  `Credit` / `ClawBack` / `ClawBackAll` (the one saturating money path; `ClawBackAll` is a technical invalidation's full reversal of what the current contractor holds), `AddReputation`, `RunAdvancement`, `BlockedBy`,
  `CurrentNeed`, `Tags`, `Validate`. Every number lives in `CareerPolicy`.
- **Hooks (no new scheduler job, nothing per tick).** `OperationService.Finish` / post-outcome `Abort` /
  written-off Troubled apply the career result once (planned as a pure delta, committed as one small durable step, flagged only after that commit, then announced; a missing contractor or simulation is a failed commit, never an applied one); `UpkeepService.UpkeepJob` runs the advancement
  check; `ProcurementService` mirrors contractor-owned money at the ledger commit point.
- **Derived, never stored.** `CareerNeed` and Tags (`CareerTags`). Nothing that decides outcomes
  (resolver, pricing, willingness, upkeep) reads them; a source scan in the test run holds that.
- **Emits.** `Contractor.FameChanged` (a band was crossed), `Contractor.Advanced` (equipment advanced).
- **Detail.** [CAREERS](CAREERS.md), [SIMULATION § 4.7](SIMULATION.md#47-careers-phase-275).

### 6.9 Knowledge (learning and geographic knowledge)

- **Responsibility.** Let actors get better at things they have actually done. Knowledge is
  held as compact per-actor experience over topic keys (item, faction def, threat type,
  archetype, region, planet layer).
- **Persistent.** `KnowledgeStore`: `actorId → KnowledgeBook { topicKey → (exp, lastTouchedTick) }`,
  capped at 64 topics per actor, with the lowest-value topics evicted.
- **Runtime cache.** None needed. Books are small.
- **Public surface.** `Knowledge.Proficiency(actor, topic)` returns a value in 0..1 with lazy
  decay. `Knowledge.Transfer(from, to, fraction)` supports retirement, succession and mergers.
- **Consumes.** `OperationResolved`, `OpportunityClaimed` and `IntelLeadDelivered` (the topics touched).
- **Emits.** `ActorLearnedTopic` when a threshold is crossed (Minor).
- **Detail.** Topic keys and region quantization are in [DATA_MODEL § 7](DATA_MODEL.md#7-knowledge).

### 6.10 Relationships, obligations and contacts

- **Responsibility.** Hold directed relationships between actors (standing, trust,
  familiarity, counters, salient memories), typed non-silver obligations (favors and debts),
  and the player's **ContactBook** (which actors the player knows and how they met).
- **Persistent.** `RelationStore` (sparse directed edges), `ObligationLedger`, and `ContactBook`.
- **Runtime cache.** Adjacency lists per actor.
- **Public surface.** `Relations.Get(from, to)` (applies decay lazily), `Relations.Apply(delta, cause)`,
  `Obligations.Open(...)`, `Obligations.Call(...)`, `Contacts.Introduce(...)`.
- **Emits.** `RelationThresholdCrossed` (for example became rival or became trusted),
  `ObligationCreated`, `ObligationSettled`, `ContactIntroduced`.
- **Consumes.** All interaction events: contract outcomes, rescues, betrayals, payment defaults,
  joint operations.
- **Future.** Blacklist flags, sanctions and rival or ally links (Phase 5).

### 6.11 Reputation (derived)

- **Responsibility.** Turn record summaries and awareness into player-facing **epithets**
  ("reliable", "reckless", "rescuers", "oathbreakers") and into numeric reputation inputs for
  willingness and pricing.
- **Persistent.** `PublicReputation { epithets[], lastComputedTick }` on the actor. It is stored
  so that reputation has inertia and hysteresis and does not flicker. **Phase 2.75 implements the
  numeric core:** `PublicReputation { fame, score }`, where the `FameBand` is derived from the score
  (§ 6.8.1, [CAREERS § 3](CAREERS.md#3-numeric-reputation)); epithets remain future work.
- **Runtime cache.** Per-observer perceived-reputation cache (Phase 5).
- **Public surface.** `Reputation.Public(actor)`, `Reputation.As(observer, subject)`.
- **Consumes.** Summary-changed notifications from History, which set dirty flags and never
  trigger a rescan.
- **Emits.** `EpithetGained`, `EpithetLost`.
- **Detail.** [EVENTS_AND_HISTORY § 6](EVENTS_AND_HISTORY.md#6-reputation-through-history).

### 6.12 Awareness and beliefs (rumors, witnesses, secrecy)

- **Responsibility.** Separate **what happened** (facts, held in history records) from **who
  knows it and how well** (awareness scopes and per-actor beliefs). Rumors are beliefs that may
  be distorted or fabricated.
- **Persistent.** `Awareness` on each record: scope plus a small explicit knower set. From
  Phase 5, `BeliefStore` (per-actor, bounded).
- **Phase 1–4 behaviour.** Every record has `Awareness.Public` unless its contract is
  confidential. That keeps the code paths real while the rumor system itself waits for Phase 5.
- **Detail.** [EVENTS_AND_HISTORY § 7](EVENTS_AND_HISTORY.md#7-facts-awareness-rumors-and-witnesses).

### 6.13 Intel

- **Responsibility.** Model the player's **interest** ("find information about X"), the search
  process, and its result. The result is one or more `Lead`s, which are the *reported* view of
  an `Opportunity`, or an explicit "no credible lead". Intel never assumes that the result is
  loot, and it is never guaranteed to be accurate (master § 3.2, § 12).
- **Topic only, never a quantity.** The player picks an item (a `ThingDef` from the catalog). The
  request carries no quantity, target amount, minimum or stack count, and no price or duration
  rule reads one. How much exists (17, 600, none, an already-looted cache) is decided by the
  opportunity generator (§ 6.14). Quantity is a Procurement concept (`AcquireObjective.count`).
- **Contact / source.** The player chooses who to ask (master § 8–9): a **Fixer** (§ 6.6.2),
  a faction contact, or the generic information network (the institution "the Exchange") from
  Phase 1; known contractors from Phase 2. Any actor with an `IntelSourceProfile` can be a source.
  Source quality follows master § 13 (tech level, faction type, goodwill, specialization,
  geography, source-mod relationship, previous reliability) and is shown only as narrative
  descriptors that the player learns from how that source's leads turned out.
- **Access.** Every outgoing Network action requires a usable vanilla Comms Console (§ 9).
- **Money and time depend on the source.** There is no global Intel fee or duration. The fee, the
  round duration (source speed, specialties, knowledge, geography, target difficulty and rarity,
  relationship, reputation, seeded variance) and the reliability all come from the chosen source's
  profile and policies. Fees are kept for rounds that ran, even when nothing credible is found,
  refunded in full for the running round on technical invalidation, and partly refunded on
  cancellation as the source's policy says ([STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)).
- **One request, many leads.** A search runs in rounds and can deliver several leads over time.
  After a lead the player may pursue it, ignore it, abandon it, keep the search going or end it;
  pursuing Lead A does not stop the search, so Lead B can arrive while the player is at A's site.
  Whether and at what cost a search continues is the source's continuation policy, not a global
  rule.
- **Quality intent** (optional, not Phase 1). For quality-bearing items the request may carry a
  broad preference (any, approximate, minimum; master § 79). The lead's reported quality is
  perception and may be wrong.
- **Truth vs report.** At resolution the source's reliability commits a hidden *divergence*
  class (accurate, partial, outdated, bad, misinformation, trap, jackpot, complication; master
  § 12). The opportunity is generated as world truth and the lead reports it through that
  divergence ([DATA_MODEL § 8](DATA_MODEL.md#8-intel-leads-and-opportunities)).
- **Persistent.** `IntelStore`: `IntelRequest` and `Lead`.
- **Public surface.** Commands `SubmitIntel(topic, source)`, `ContinueIntel(request)`,
  `EndIntel(request)` and `CancelIntel(request)` (names indicative; they are exactly the player
  transitions of [STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)); read models.
- **Dependencies.** ItemCatalog (topic validation), PaymentAdapter (fee), OpportunityService
  (generation), Actors (the source), Knowledge (source proficiency).
- **Emits.** `IntelRequested`, `IntelSearchProgressed` (optional flavour), `IntelLeadDelivered`,
  `IntelNoLead` (a round with nothing), `IntelSearchContinued`, `IntelConcluded`,
  `IntelCancelled`, `IntelInvalidated`.
- **Consumes.** `ReferenceInvalidated` (the topic def is gone).
- **Detail.** [STATE_MACHINES § 1–2](STATE_MACHINES.md#1-intel-request).

### 6.14 Opportunities (world truth)

- **Responsibility.** Generate and own world truth: what actually exists, where it is, how much
  of it, who guards it, who else is after it, and when it expires. Opportunities are created by
  Intel, by failures (rescue and recovery), by world events and by consequences.
- **Persistent.** `OpportunityStore`: `Opportunity { archetype, sourceContext, payload (target and
  extra cargo), location, threat, competitors, expiry, lifecycle, materialization, lineage }`.
- **Public surface.** `Opportunities.Generate(request)`, `Opportunities.Materialize(id)`,
  and site callbacks through the SiteAdapter.
- **Emits.** `OpportunityGenerated`, `OpportunityMaterialized`, `OpportunityEngaged`,
  `OpportunityClaimed`, `OpportunityExpired`, `OpportunityLostToCompetitor`,
  `OpportunityDestroyed`, `OpportunityInvalidated`.
- **Generation answers the master § 52 questions in order:** what the item is (catalog entry),
  where it could plausibly exist and who might hold it (**source/context resolution**, § 6.14.1),
  what scale was found (quantity), how reliable the report is (Intel divergence), who else might
  know (competitors), what danger makes sense (threat), and what else is there (extra cargo,
  master § 18). **Quantity** is decided here from market value, stack limit, category, world
  wealth, threat level, archetype, holder strength and balance caps (master § 10). It never
  comes from the request.
- **Acquisition, not extermination.** An opportunity's objective is getting the goods. Nothing
  in its lifecycle requires the defenders to be dead: taking part of the payload and leaving is
  a Claimed outcome (master § 17; Spike S19).
- **Future.** Archetype catalog growth (trader holds it, owner holds it, salvage, orbital wreck,
  mineable deposit, rumor-only), competitors racing the player, and black-market leads.

#### 6.14.1 Opportunity source and context resolution

A responsibility **inside `OpportunityService`** (called `SourceResolver` here; the name is not
binding). It picks plausible world context for an opportunity. It is not a subsystem of its own.

- **Rule: source mod is contextual evidence, not ownership** (master § 14, § 57). Same mod is
  not automatically an enemy, not automatically the owner, and never a dependency. Nothing is
  hardcoded per item ("if `BOR_Tenebrite` then the Tenebral faction" does not exist anywhere);
  the reasoning is generic: *this item comes from package X, an active faction from package X
  exists and fits this opportunity, so it is more plausible*.
- **Signals** (all cheap reads at generation time): the target ThingDef and its catalog entry
  (category, value, rarity signals, tech level); source package; the live faction list with each
  faction's def, **def source package**, tech level, hostility to the player, defeated and hidden
  state, humanlike / mechanoid / other, and trade capability where inferable; distance and
  nearby settlements or world objects; the archetype; the requesting source's knowledge; and
  optional hints registered by compatibility adapters ([COMPATIBILITY § 6](COMPATIBILITY.md#6-compatibility-adapter-architecture)).
- **Candidates** it can produce: same-source-mod faction · faction using similar technology ·
  suitable hostile faction by tech level · neutral trader or owner · pirate or raider possession ·
  mechanoid possession · ancient site or ruin · salvage site · abandoned or unguarded cache ·
  **no credible source**.
- **Selection.** Candidates are filtered (world presence, not defeated, hidden factions only
  where fitting, combat capability, faction generation rules, player relationship) and scored.
  The master § 15 hierarchy is the default prior: same-mod faction with a plausible relationship
  to the item, then similar technology, hostile by tech level, neutral trader, ancient ruin or
  cache, mechanoid site, pirates, generic abandoned location, and finally no credible lead. The
  choice is a seeded weighted draw, not always the top entry, and "no credible source" is a real
  result: **the generator does not have to succeed.** When no suitable same-source actor exists,
  it falls back down the hierarchy.
- **Stance is kept, never forced.** A same-mod faction that is friendly or neutral is a
  candidate *owner or trader*, not a guard of a raidable site: its stance toward the player is
  never changed to fit an archetype. If the phase has no archetype that can express the chosen
  context (Phase 1 has only guarded or unguarded caches), the resolver moves to the next
  candidate.
- **Output.** A committed `Opportunity.sourceContext` (candidate kind, holder, the evidence
  reason keys) that the threat, the lead text and history all read ([DATA_MODEL § 8](DATA_MODEL.md#8-intel-leads-and-opportunities)).
- **Cost.** A session index `packageId → FactionDefs` plus one pass over live factions (tens)
  when an opportunity is generated, which is rare. Nothing is cached across faction changes.
- **Phase 1** implements a simplified resolver that keeps this hierarchy and these rules
  ([IMPLEMENTATION_PHASES § 4.2](IMPLEMENTATION_PHASES.md#42-smallest-slice-that-proves-the-chain)).

### 6.15 Contracts (including offers and procurement)

- **Responsibility.** Agreements between an issuer and a contractor, optionally brokered by a
  Fixer: **issuer → Fixer → reachable contractors**. The issuer (any actor with `IssuerProfile`,
  contractors included) owns the demand and the objective; the Fixer mediates reach, access,
  bids, negotiation, brokerage, deposits, insurance, replacement and anonymity, and never replaces
  the issuer. One shared lifecycle is
  composed from parts: Parties, Terms, Confidentiality, Objectives, Offers, Assignment,
  Progress, Outcome and Lineage. Procurement, recovery, rescue, hunt, investigation, escort,
  salvage, transport, acquisition and black work differ in **objectives and kind rules**, not in
  class hierarchy.
- **Procurement** names the exact item and the exact quantity (master § 19). It is hired as an
  **Open** contract (any eligible contractor may bid), a **Direct** contract (one invited known
  group), or a **Premium / sponsored** contract (either, plus the player's contributions of
  silver, equipment, medicine or logistics, master § 22).
- **Fixer-mediated quotes.** Procurement is normally brokered by a Fixer. **Contractor pricing and
  Fixer pricing are separate actor-driven contributions assembled into one client-facing quote**:
  the contractor's bid (acquisition, risk, capability and danger premiums, logistics, urgency,
  profit) plus the Fixer's terms (brokerage fee, market and contractor access, coordination
  markup, contingency, deposit policy, insurance offer, quote validity, replacement policy). The
  player sees one price; the quote keeps which actor contributed which component
  ([DATA_MODEL § 9](DATA_MODEL.md#9-contracts)). No arithmetic is fixed in Phase 0.
- **Deposits.** The deposit (share and schedule from the Fixer's deposit policy; master § 21
  suggests half) is committed cost and is **normally lost** when the contractor fails in the
  world. A contractor that disappears **before work starts** is mediated by the Fixer's policy
  and the terms (replacement, successor, refund, credit, forfeit, insurance, renegotiation), not
  by a global rule. Technical invalidation still refunds in full, and insurance, when the Fixer
  offers it, recovers part ([STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules)).
- **Persistent.** `ContractStore`.
- **Public surface.** Commands `DraftContract`, `PostContract`, `AcceptOffer`, `CancelContract`,
  `RespondToRenegotiation`. Services `Bidding.CollectOffers`, `Contracts.Transition(...)`.
- **Emits.** `ContractPosted`, `OfferReceived`, `ContractorRefused`, `ContractAwarded`,
  `ContractActivated`, `ContractDelayed`, `RenegotiationRequested`, `ContractCompleted`,
  `ContractPartiallyCompleted`, `ContractFailed`, `ContractCancelled`, `ContractVoided`,
  `ContractContinued`, `PaymentReceived`, `PaymentDefaulted`.
- **Consumes.** `OperationResolved`, `DeliveryCompleted`, `ReferenceInvalidated`,
  `ActorStatusChanged` (the contractor dissolved), and faction relation checks at checkpoints.
- **Detail.** [DATA_MODEL § 9](DATA_MODEL.md#9-contracts), [STATE_MACHINES § 3–5](STATE_MACHINES.md#3-contract-generic).

### 6.16 Operations and the abstract resolver

- **Responsibility.** Execute contracts off-map in a cheap, deterministic way, with partial
  outcomes, casualties, capture and delay. When the player becomes involved, hand over to the
  physical layer.
- **Persistent.** `OperationStore`: `Operation { contract, contractor, committed forces, leases,
  phase, checkpoints, seed, frozen inputs, committed outcome, physicalization }`.
- **Public surface.** `Operations.Start(contract)`, and scheduler checkpoint handlers.
- **Emits.** `OperationStarted`, `OperationCheckpoint` (Minor), `OperationResolved`,
  `ContractorCasualties`, `ContractorCaptured`, `ContractorMissing`, `ContractorStranded`,
  `CargoLost`, `CargoStolen`.
- **Career result (Phase 2.75).** An operation started by a 2.75 build is `careerEligible`; its result
  is applied once at the lifecycle's end (§ 6.8.1). An operation from an older save never is.
- **Detail.** [SIMULATION § 3](SIMULATION.md#3-abstract-resolver).

### 6.17 Consequence Engine (chain reactions, inheritance, follow-ups)

- **Responsibility.** Turn events into **new opportunities and contracts** through data-driven
  rules, not scripts. Examples: a failed recovery produces a rescue lead; a stolen cargo
  produces a hunt; an abandoned contract is reposted or inherited.
- **Persistent.** Per-rule cooldowns and budget counters. Pending follow-ups are scheduler jobs.
- **Public surface.** Internal only (it is an event consumer).
- **Consumes.** `ContractFailed`, `OperationResolved`, `ContractorCaptured`, `CargoStolen`,
  `OpportunityLostToCompetitor`, `LeaderKilled`, and others.
- **Emits.** `FollowUpOpportunityCreated` and `ContractContinued`.
- **Guardrails.** A global follow-up budget, a per-lineage depth cap (default 4) and cooldowns
  prevent runaway chains. Rules are keyed by event type and use seeded chances.

### 6.18 Item Catalog

- **Responsibility.** A cached, classified view of every item ThingDef, built once per session:
  eligibility verdict with reason codes, source mod, market data, categories, craftability,
  tradeability, equipment traits and rarity signals. It never infers what the player needs and
  never reads research, stockpiles or demand (master § 53).
- **Overrides actually override.** Only true technical impossibilities (things that cannot exist
  as a standalone possessed item) are non-overridable. Everything that is merely unusual is a
  heuristic, and the player's `Allowed` override rescues it. The final safety net is at runtime:
  if creating a def's Things ever fails, the def is unusable for the session and the affected
  activity is invalidated with a refund and a diagnostic reason.
- **Persistent.** None in the save. Per-item overrides (Auto / Allowed / Blocked) are kept in
  `ModSettings` by defName.
- **Detail.** [COMPATIBILITY § 2](COMPATIBILITY.md#2-item-catalog).

### 6.19 Physical adapters (Integration)

| Adapter | Responsibility | Vanilla APIs used |
|---|---|---|
| `SiteAdapter` | Build vanilla `Site`s for opportunities (vanilla `SitePartDef`s such as `ItemStash` plus a threat part), start the timeout, bind the injected `WorldObjectComp_NetworkSite`, and forward its callbacks | `SiteMaker.MakeSite`, `SitePart.things`, `TimeoutComp`, `WorldObjectComp` |
| `CustodyService` / `PhysicalWorldPort` adapter (Phase 3, design) | The RimWorld half behind the port: create (`ForceGenerateNewPawn`, role-constrained and verified before binding), age catch-up, spawn, tag, observe (`ObservedKind`), release, and the registry reservation. The Episode ledger and reconciliation are **Domain**, tested over a fake port | `PawnGenerator`, `GenSpawn`, `WorldPawns`, `QuestManager` (registry quest), `Pawn`/`Faction`/`Caravan` state reads, `LordMaker` |
| `EncounterFactionAdapter` | One temporary hidden faction per **episode** (vanilla removes it with the episode) (Phase 3, design) | `FactionGenerator`, `Faction.temporary`, `FactionManager` |
| `DeliveryAdapter` | Hand goods to the player: drop pods (Core; the only mode today), colony handoff and rendezvous (Phase 3.3, **design direction only**), shuttle (Royalty, optional) | `DropPodUtility.DropThingsNear`, `TransportShipMaker` (optional) |
| `PaymentAdapter` | Take silver from and pay silver to the player; represent debt when the player cannot pay | `TradeUtility.ColonyHasEnoughSilver`, `TradeUtility.LaunchSilver`, drop pods |
| `SignalBridge` | Receive the `TheNetwork.*` quest-tag signals (pawn, thing and world-object lifecycle) and route them to services | `SignalManager.RegisterReceiver`, `QuestUtility.AddQuestTag` |
| `CommsAccessAdapter` | Answer "can the player communicate now?": a spawned `Building_CommsConsole` (any subclass, so modded consoles count) on a player home map whose `CanUseCommsNow` is true (powered, no electricity-disabling condition) | `Map.IsPlayerHome`, `ListerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>()`, `Building_CommsConsole.CanUseCommsNow` |
| `ColonyReader` | Read-only view of the player's real execution state when the player acts as a contractor or a decision needs it (colonists, injuries, gear, inventories, transport); never stored as abstract state | vanilla pawn, map and caravan APIs |

The API facts behind each adapter are in [RIMWORLD_INTEGRATION](RIMWORLD_INTEGRATION.md).

### 6.20 Compatibility layer

- **Responsibility.** DLC gating (`ModsConfig.*Active`), optional mod adapters, defName fallback
  chains, and missing-reference policies.
- **Persistent.** None, apart from references stored as strings inside entities.
- **Detail.** [COMPATIBILITY](COMPATIBILITY.md).

### 6.21 Presentation

- **Responsibility.** The Network main tab (Intel, Procurement, Contracts, Contractors, History),
  letters, world-site inspect strings and dialogs. All data comes from ReadModels, and every
  change goes out through Commands.
- **Persistent.** None. UI preferences such as the last tab and filters live in `ModSettings`.
- **Detail.** [§ 9](#9-ui-boundary).

### 6.22 Diagnostics

- **Responsibility.** Logging, timing, validators, dev actions and the simulation harness, and (Phase 2.9)
  the in-game **runtime regression runner**.
- **Detail.** [DEBUGGING](DEBUGGING.md), [RUNTIME_TESTING](RUNTIME_TESTING.md).

#### 6.22.1 Runtime regression tests (Phase 2.9, [ADR-047](DECISIONS.md))

Developer infrastructure under `Diagnostics/RuntimeTests/`, reachable only from eight Dev Mode actions and one
per-frame call. It is **not** a gameplay subsystem: nothing in the game depends on it, it stores nothing, and
it sits beside the Domain, never inside it.

```
 Dev action ─► RuntimeTestGame (Start/Cancel/Status/Report/Export/Inspect)
                  │  one static null check per frame when idle ─ NetworkWorldComponent.WorldComponentUpdate ─► PumpFrame
                  ▼
            RuntimeTestRunner ── pumped slices (8 ms), per-step override snapshot/restore, exception containment,
                  │              finite timeouts, cancel, live-state capture before/after every slice (RT-INFRA-001):
                  │              a fingerprint, or why there is none; a capture that throws FAILS (fails closed)
                  │
        ┌─────────┴───────────────────────────────┐
        ▼                                         ▼
  RuntimeTestSandbox (isolated, in memory)    GameRuntimeTestHost (read-only view of the live game)
  production services over sandbox ports:     real catalog, comms gate, payment environment inspection,
  own ids/clock/scheduler/bus/journal/        world graph, drop-pod plan, LiveInvariants.Scan,
  stores/History; SandboxComms/Payment/       LiveFingerprint (Network durable fields, by content)
  Catalog/WorldFacts/Sites/Delivery           + ColonySentinel (silver by beacon, cargo, world objects, letters)
                                              (never writes; never starts the Network; never calls NetValidator)
  + GridWorldGraph; discarded after the test
```

* **Dependency rule.** The runner and sandbox depend on the Domain, Kernel and Integration *ports* like any
  other client; **the Domain, Kernel and Persist layers never reference the runner**. The production assembly
  never references the test project.
* **Never starts or repairs the live Network.** `ProbeNetwork()` only asks whether the game has started it; a
  never-started Network is SKIP ("allow one normal tick and rerun"). A source scan forbids `EnsureStarted`,
  `StartNow`, `RunStartup`, `.Active` and `NetValidator` in `RuntimeTests`.
* **Not persisted.** The runner, sessions, results, preserved failures and sandboxes are runtime-only: no
  `Scribe`, no scheduler job (RT-INFRA-003), no save-version change (still 4).
* **Process-wide statics** a test can influence (`ProcurementDevOverrides`, `IntelDevOverrides`,
  `ServiceToggles`: 18 values) are snapshotted and restored to their *previous* values after every step.
* **Single-threaded**, driven from the main thread's update, never blocking: it fits the model of § 12.

---

### 6.23 Physical lifecycle (Phase 3, design only)

The abstract ↔ physical lifecycle is specified in [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md) after a design review
that audited the 1.6.9676 assemblies; no Phase 3 code exists. The architectural shape:

```
 DOMAIN (headless-testable)            PORT                          INTEGRATION (RimWorld)
 AuthorityGate ─ every abstract        PhysicalWorldPort             real adapter: GeneratePawn(ForceNew) · GenSpawn ·
   writer asks it                        Create (a ProjectionRequest:  tags · LordJob_VisitColony · registry quest ·
 EpisodeStore (slot "deployments")       role, teammates, age) ·      temporary faction · state reads · age catch-up
 Reconciler: observe → decide →          Spawn · Tag · Observe ·     FAKE (sandbox): scriptable tokens, used by the
   plan → validate → ATOMIC commit       Release · Reserve · Age       safe runtime tier and the headless suite
   (Applier, snapshot-guarded) →
   flag → release → follow-up → publish
   (each stage: explicit marker; publish: outbox + cursor)
 pure policy: role verdict · composition ·
   concretization · cohesion screen
 existing services apply consequences
   (casualties, career, spatial, events): their
   state halves inside the commit, their effects after it
```

Rules that bound it: one authority per person; `Actor ≠ Person ≠ Pawn`; reconciliation exactly once from observed
state **and atomic for the Network's durable data** (no publication, scheduler or vanilla effect inside the commit; each
later stage has an explicit durable marker written last, publication progress is durable per event so the event bus is never
asked to accept one twice, and a person is not abstractly simulatable again until release has completed; vanilla itself
passes an exiting pawn to the world, so the Network never passes a pawn already in `WorldPawns`, and the exit-reservation
window was closed by M1, the accepted outcome of the owner's spike S31: ADR-053); a first projection never contradicts established truth; no Harmony; no work when nobody is physical; physical tests are a
separate, session-armed tier on its own test map ([ADR-048](DECISIONS.md), [ADR-049](DECISIONS.md),
[ADR-050](DECISIONS.md)). Phase 3.3 (procurement fulfillment by physical handoff) is design direction only
([ADR-051](DECISIONS.md), [PHYSICAL_LIFECYCLE § 27](PHYSICAL_LIFECYCLE.md#27-phase-33-procurement-fulfillment-and-physical-handoff-design-direction)).

## 7. Key flows

### 7.1 Phase 1: Intel to site to history

```
UI: CommsAccessAdapter says a usable Comms Console exists (else the command is refused with a reason)
    player picks a contact (a Fixer, a faction or the Exchange) and a ThingDef from the ItemCatalog
    (no quantity) → Commands.SubmitIntel(topic, source)
  └─ IntelService: validate topic (catalog verdict, DefRef resolves) and source
       → freeze the source's SearchTerms → PaymentAdapter.Charge(fee per the source's policy)
       └─ create IntelRequest{Submitted → Searching, round 1, seed} → Events.Publish(IntelRequested)
       └─ Scheduler.Schedule("intel.round", due = now + RoundDuration(source, topic, seed))  // UI: "several days"
…time passes (no per-tick work)…
Scheduler fires "intel.round"
  └─ IntelService.RunRound: re-validate topic → seeded lead roll (source quality) → divergence class
       ├─ nothing this round → Publish(IntelNoLead); continuation policy: next round, or Concluded
       │    (a search that ends with no leads at all: letter, fee kept)
       └─ lead → OpportunityService.Generate(topic, divergence)
             → SourceResolver: candidates from item + source package + live factions + world
                (may still end in "no credible source" → no lead)
             → archetype (Phase 1: GuardedCache), holder/threat from the chosen context,
               target quantity + extra cargo decided here (never from the request)
             → Opportunity{Revealed, sourceContext} + Lead{report through the divergence, confidence}
             → SiteAdapter.Materialize → vanilla Site (ItemStash things + threat part) + TimeoutComp + comp
             → Publish(IntelLeadDelivered, OpportunityMaterialized) → letter with look target
             → continuation policy: keep searching (next round) | AwaitingDecision (player may
               ContinueIntel or EndIntel) | Concluded; pursuing this lead never stops the search
Player caravan / pods reach site → vanilla map generation places the stash
  └─ comp.PostMapGenerate → Opportunity{Engaged} → Publish(OpportunityEngaged)
Player leaves with some or all of the goods, defenders dead or not (caravan)
  → comp.PostCaravanFormed → tally the target def in the caravan
Map removed → comp.PostMyMapRemoved → Opportunity{Claimed (share recovered) | Abandoned} → Publish(...)
  └─ History consumer writes a Notable record ("recovered part of the cache"), player summary updated
Timeout without a visit → comp.PostDestroy → Opportunity{Expired} → Publish(OpportunityExpired)
```

### 7.2 Phase 2: procurement (abstract)

```
Commands.PostContract(kind=Procurement, objective=Acquire(DefRef, exact qty), deliverTo=home map,
                      broker=Fixer, hiring=Open | Direct(actor) | Premium(contributions))   // comms required
  └─ ContractService: Posted → Bidding (window jobs) → WillingnessModel per eligible contractor
       → Offers (contractor quote; or recorded refusals with reasons)
       → QuoteAssembly: the Fixer wraps each offer into one client-facing quote
         (contractor components + Fixer fee, markup, deposit terms, insurance offer, validity)
       → player accepts a quote → Awarded (terms copied from the quote)
       → PaymentAdapter.Charge(deposit) → OperationService.Start → checkpoints scheduled
Checkpoint "engage" → AbstractResolver (frozen inputs + seed) → committed outcome
  └─ Publish(OperationResolved, ContractorCasualties?) → Orgs / History / Relations / Morale
Checkpoint "deliver" → DeliveryAdapter (drop pods) → Publish(DeliveryCompleted)
  └─ ContractService: Completed | PartiallyCompleted → PaymentAdapter.Charge(balance)
Contractor disappears before work starts → the Fixer's replacement policy and the terms decide
  (replacement, successor, refund, credit, forfeit, insurance, renegotiation)
Failure → ContractFailed(cause), deposit normally lost (insurance may recover part)
  └─ ConsequenceEngine: "last known location" recovery Opportunity (Phase 2: cargo and threat;
     Phase 3 adds survivors, captives and bodies) or a rescue Opportunity
```

### 7.3 Save and load

```
Save:  WorldComponent.ExposeData → stores in fixed order → only IDs, refs, values
Load:  ExposeData (LoadingVars) → per-element tolerant list loading
       → ResolvingCrossRefs (PawnRef pointers) → PostLoadInit (defaults)
       → FinalizeInit(true): run migrations, rebuild runtime caches, no events
       → first tick: EnsureStarted → validators → ReferenceInvalidated events → re-register signals
```

## 8. How history changes future behaviour

A single fact, "the player rescued Dead Red's crew", flows through the system once and changes
several future behaviours.

```
Event ContractorRescued { rescuer: Player(A1), rescued: Org "Dead Red"(A17),
                          characters: [K42 "Mara Red"], opportunity: O311, place: T(2341) }
 ├─ History ........ Major record H901 (participants A1:rescuer, A17:rescued, K42:rescued)
 │                   Summary(A1).rescuesPerformed++   Summary(A17).timesRescued++
 ├─ Relations ...... edge A17→A1: standing +25, trust +0.2, salient += H901
 ├─ Obligations .... favor F77 { debtor A17, creditor A1, kind "favor.rescue", magnitude 3 }
 ├─ Organizations .. A17 morale: confidence +, cohesion +; K42 notability +
 ├─ Reputation ..... A1 dirty → may gain epithet "doesn't leave people behind"
 ├─ Gossip (Ph.5) .. newsworthiness high → propagation job (region, allies of A17)
 └─ Presentation ... letter
Later:
 • WillingnessModel(A17, contract from A1): standing and the open favor → accepts risky work
   and offers a discount. A refusal becomes much less likely.
 • Bidding: A17 bids first on A1's contracts, and may offer to cash in the favor.
 • Other organizations: A1's public epithet feeds their willingness and pricing, including
   the risk premium they charge.
 • Resolver: no effect. History changes *behaviour*, not dice.
 • Legends: if K42 later dies heroically, H901 is one of the deeds in K42's Legend.
```

The design rule behind this: **the event is published once; each consumer owns its own
reaction; later decisions read summaries and edges, never the raw ledger.**

## 9. UI boundary

- **ReadModels** are plain, immutable view objects: `IntelRowView`, `LeadView`,
  `ContractView`, `OfferView`, `ActorCardView`, `CharacterCardView`, `HistoryEntryView` and
  `LegendView`. They are built on demand from stores and cached per window. The cache is
  invalidated by a global `Network.StateVersion` counter that every mutation increments. Views
  never hold live `Pawn`, `Faction` or `Site` objects. They hold IDs and pre-formatted strings,
  plus look targets resolved at click time.
- **Commands** are single entry points that validate (affordability, eligibility, state),
  mutate, publish events and return a `CommandResult { ok, reasonKey }`. UI buttons are enabled
  from `Commands.CanX(...)`, which returns the same reason codes. The UI therefore cannot put
  the simulation into an invalid state.
- **Narrative presentation.** Text is generated from records and events through a
  `NarrativeFormatter`. It starts with keyed translation strings, and later can use the
  vanilla RulePack grammar. It is never stored, **except** in Legends, which keep frozen text
  snapshots so they read the same after mods change.
- **Layout independence.** The five top-level tabs are one arrangement of the read models. The
  backend does not know about tabs.
- **No randomness in UI paths.** Read models are pure, so looking at something never rerolls it
  ([SIMULATION § 6](SIMULATION.md#6-determinism-and-rng)).
- **Narrative, not numbers** (master § 70, § 82). Read models never expose hidden truth, committed
  due ticks or raw percentages: a search shows "elapsed 4.8 days · estimate: several days", a
  lead shows a confidence descriptor (Very Low … Very High), sources and contractors show
  descriptors (experience tier, doctrine, relationship, fame) built from internal numbers.
- **Settings** (master § 75) live in `ModSettings`. Tuning settings are read-only inputs to
  formulas. Turning a service off (Intel, Procurement, player registration) hides its commands
  and stops new requests; entities already in progress run to their normal end, so no save data
  is stranded. The same `ModSettings` holds the **global cast** (§ 6.6.3), which runtime code
  only reads at world import.
- **Comms Console gate** (Phase 1; owner decision). Every command that *contacts* the Network
  (submitting or continuing a search, ending or cancelling it, posting, accepting or answering a
  contract) checks `CommsAccessAdapter` in `Commands.CanX`. Without a usable console the command
  is refused with `reasonKey = "NoUsableCommsConsole"`: the button is disabled with a tooltip
  ("needs a powered comms console") and nothing is charged or changed. The Network tab can still
  be opened to read requests, leads, contracts and history, and local actions (abandoning a lead)
  still work. **Nothing in progress depends on the console**: searches, operations and deliveries
  continue, incoming results are recorded and shown normally, a search waiting for the player's
  decision simply waits, and communications resume as soon as a console is usable again. Other
  ways to communicate (portable radios, orbital links, other mods) are not designed yet.

## 10. What is stable now and what stays replaceable

| Must be stable from Phase 1 (breaking changes need a migration) | Intentionally replaceable (changes freely) |
|---|---|
| ID strategy, typed IDs, `nextId` | Resolver formulas and outcome-band tables |
| External reference types and their persistence shape | Willingness, pricing and bidding heuristics |
| Root layout and store order, `NetworkSaveVersion` | Opportunity archetype catalog and site compositions |
| Event header shape (seq, tick, typeKey, schema, importance, subjects) | Individual event payload classes (the journal is bounded) |
| `HistoryRecord` header, `Participation`, `Awareness` | Retention thresholds, caps and importance scoring |
| Actor core (id, kind, status, lineage, bindings, components list) | Capability component internals (versioned per component) |
| Character identity and custody invariants | Promotion criteria for Known Characters |
| Contract core (parties, status, lineage) and the separation of Contract from Operation | Contract kinds, objective types, terms policies |
| Intel/Lead/Opportunity separation (interest vs. perception vs. truth) | Reliability model, lead text |
| Scheduler job shape `(seq, dueTick, kind, target, arg)` | Job frequencies, budgets |
| Relation edge key (directed actor pair) | Relation dimensions beyond standing and trust |
| Persisted polymorphic type names (see [SAVE_AND_MIGRATION § 3](SAVE_AND_MIGRATION.md#3-persisted-type-names)) | UI, letters, epithet definitions, gossip rules |
| The contractor split (`ContractorProfile` / `ContractorSimulation` / `OrganizationProfile`) and `FixerProfile` | Fixer and contractor policies: fees, speed, markups, deposits, insurance, replacement |
| Global cast template IDs, template schema and `NetworkSettingsVersion`; the snapshot-at-import rule | Cast generation, name pools, fame and experience distribution |
| One Intel request, many leads (rounds) | Continuation policies, round durations |
| Quote structure (components tagged with the contributing actor) | Quote arithmetic |
| The runtime test contract: stable test IDs (`RT-*`), PASS / FAIL / WARN / SKIP, runtime-only and never persisted, isolated sandbox, read-only live scan ([ADR-047](DECISIONS.md)) | Which scenarios exist, the sandbox ports' internals, the runner's slicing and report layout |

## 11. Where abstraction pays and where it does not

**Worth it:**

- *Typed references with silent resolution.* This is the only defence against missing-mod
  error cascades.
- *Capability components for actors.* A retired contractor becoming a broker is just an added
  component.
- *Contract composition and a separate Operation.* Inheritance, bidding and continuation fall
  out of it.
- *Adapter interfaces between domain and integration.* They allow headless tests, and let
  Odyssey, Royalty and other mods plug in.
- *Event bus with ordered consumers.* It stops six systems from rediscovering the same fact.

**Deliberately *not* abstracted:**

- *No generic message broker with topics, priorities and persistence.* In-process synchronous
  dispatch is enough.
- *No plugin framework for resolvers.* There is one resolver with data tables, and it is
  replaced wholesale if needed.
- *No generic rule engine.* Consequence rules are small C# predicates plus XML-tunable weights.
- *No ORM-like persistence layer.* We use plain `IExposable` with a tolerant list helper.
- *No UI framework.* Plain RimWorld `Window` and `Widgets` over read models.
- *No abstract "Entity" base class for every record.* Typed IDs and small interfaces
  (`IHasParticipants`) are enough.

## 12. Threading, exceptions and failure containment

- **Single-threaded.** All Network code runs on the main thread inside RimWorld's tick or GUI
  call. It makes no `Task` or thread-pool use. Def reads during catalog building happen on the
  main thread.
- **Every scheduler job and event consumer runs inside try/catch**, but the two are recovered
  differently:
  - **Scheduler jobs.** A failing job is logged once per `(kind, target)`. It is retried with
    backoff (up to three attempts) **only if its kind is declared idempotent or state-guarded**
    (it re-checks the entity state before applying anything). Otherwise it is removed and the
    target entity is marked for repair or quarantined.
  - **Event consumers.** The bus is synchronous, so earlier consumers may already have applied
    effects. An event is **never re-dispatched**, not after a consumer throws and not on load:
    that would duplicate relation changes, favors, history, money or follow-ups. The failure is
    recorded (event seq, type, consumer, message) in diagnostics, the affected entity is marked
    dirty, degraded or quarantined as appropriate, and the owning subsystem's reconciliation
    repairs its own state. A single consumer may be retried only if it has an explicit
    idempotency guard, for example a persisted "applied event seq" on the state it changes
    ([EVENTS_AND_HISTORY § 1.4](EVENTS_AND_HISTORY.md#14-no-double-application-after-save-and-load)).
- **Quarantine, never nuke.** An entity that fails validation or repeatedly throws is marked
  `Quarantined` with a reason. It is excluded from simulation and kept for diagnostics
  ([SAVE_AND_MIGRATION § 7](SAVE_AND_MIGRATION.md#7-failed-migration-and-quarantine)).
  The component never deletes unrelated data because one subsystem failed.
- **Subsystem degradation.** If a whole subsystem fails to load, it is marked `Degraded`. Its
  commands return `reasonKey = "SubsystemDegraded"` and the rest of the Network keeps running.

---

## 13. Self-review against the full design

| # | Question | Answer | Where |
|---|---|---|---|
| 1 | Can Intel work without knowing what the player needs? | **Yes.** Intel takes a *topic* from a neutral catalog, with no quantity. The world decides what exists and how much (or that nothing does). The catalog never ranks by need. | [STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request), [COMPATIBILITY § 2](COMPATIBILITY.md#2-item-catalog) |
| 2 | Can procurement exist independently of Intel? | **Yes.** Procurement is a Contract with an `Acquire` objective. It needs no Lead. The contractor's own knowledge stands in for Intel. | [STATE_MACHINES § 4](STATE_MACHINES.md#4-procurement-contract-specialization) |
| 3 | Can contractor groups persist for decades cheaply? | **Yes.** Organizations are records with headcounts. At most a few Known Characters per org hold real pawns. Those pawns are quest-reserved and therefore suspended, and normalized at storage so that vanilla mothballs them. Upkeep runs daily and is staggered. History is tiered and capped. | [PERFORMANCE](PERFORMANCE.md) |
| 4 | Can contractors die permanently? | **Yes.** Deaths are committed once, whether by the resolver or by physical reconciliation. A dead Known Character is never re-materialized. | [ABSTRACT_PHYSICAL_LIFECYCLE § 3](ABSTRACT_PHYSICAL_LIFECYCLE.md#3-invariants) |
| 5 | Can organizations survive leader death? | **Yes.** The leader is a role pointing at a Known Character. Succession picks or promotes a successor. The organization's identity is its `ActorId`, not its leader. | [SIMULATION § 4.5](SIMULATION.md#45-leadership-and-succession) |
| 6 | Can history alter future behaviour? | **Yes.** Summaries, relation edges, obligations, morale and knowledge feed willingness, pricing, bidding and gossip. | [§ 8](#8-how-history-changes-future-behaviour) |
| 7 | Can one Network event affect several systems without tight coupling? | **Yes.** There is one publish and ordered consumers, and consumers do not know each other. | [EVENTS_AND_HISTORY § 1](EVENTS_AND_HISTORY.md#1-network-events) |
| 8 | Can external mods be removed safely? | **Yes, with defined degradation.** DefRefs are strings, and a miss invalidates the affected activity with a refund or an explanation. Vanilla-level errors for missing Things inside sites cannot be avoided, but they do not cascade. | [COMPATIBILITY § 3](COMPATIBILITY.md#3-external-def-safety) |
| 9 | Can new modded ThingDefs appear automatically? | **Yes.** The catalog is rebuilt every session from DefDatabase with conservative heuristics. | [COMPATIBILITY § 2](COMPATIBILITY.md#2-item-catalog) |
| 10 | Can a contractor move abstract → physical → abstract without duplication? | **Yes, by invariants.** One pawn per character, a deployment ledger, reconciliation driven by pawn state, and the Network never discards pawns. This is verified by Phase 3 spikes. | [ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md) |
| 11 | Can the player become a contractor later without replacing their faction? | **Yes.** The player is a `PlayerProxy` actor bound to `Faction.OfPlayer`. Registration adds a `ContractorProfile` component only: no abstract roster or simulation; execution state is the real colony. | [DATA_MODEL § 4](DATA_MODEL.md#4-actors) |
| 12 | Can competing contractors exist? | **Yes.** Contracts have offers, not a single owner, and are non-exclusive where the kind allows. Opportunities have a `competitors` list, and actors race through Operations. | [DATA_MODEL § 8–9](DATA_MODEL.md#8-intel-leads-and-opportunities) |
| 13 | Can contracts branch after failure? | **Yes.** Terminal contracts are immutable, and continuations are new contracts linked by lineage. Failures feed the Consequence Engine. | [STATE_MACHINES § 3](STATE_MACHINES.md#3-contract-generic) |
| 14 | Can retirement preserve meaningful knowledge? | **Yes.** `Knowledge.Transfer` goes to the successor or the transformed individual actor, and a Legend snapshot preserves the story. | [SIMULATION § 4.6](SIMULATION.md#46-retirement-transformation-fragmentation-mergers) |
| 15 | Can rumors, bidding, favors, secrecy, witnesses, sanctions, mergers and legends be added without rewriting the core save model? | **Yes.** Awareness is on every record from Phase 1. Obligations, contacts and lineage exist in the model. Confidentiality is part of the contract core. BeliefStore is a new store that plugs into the root in a fixed slot. | [DATA_MODEL](DATA_MODEL.md) |
| 16 | Can Odyssey remain optional? | **Yes.** DLC types are only touched behind `ModsConfig.OdysseyActive`. `TileRef` records the layer def. Orbital archetypes register only when Odyssey is active. | [COMPATIBILITY § 4](COMPATIBILITY.md#4-dlc-and-optional-systems) |
| 17 | Can the system scale while staying invisible to TPS? | **Yes.** Idle cost is one integer comparison per tick. Everything else is scheduled and budgeted. | [PERFORMANCE](PERFORMANCE.md) |
| 18 | Can future versions migrate old saves? | **Yes.** There is an integer version, ordered migrations, legacy-field reads and per-entity quarantine. | [SAVE_AND_MIGRATION](SAVE_AND_MIGRATION.md) |
| 19 | Are Harmony patches minimized? | **Yes.** There are zero patches in Phases 1–3. Contingency patches are pre-analysed and not adopted. | [RIMWORLD_INTEGRATION § 3](RIMWORLD_INTEGRATION.md#3-harmony-policy) |
| 20 | Does the architecture produce stories rather than service transactions? | **Yes, if content follows through.** Every transaction produces history, relationships remember it, failures branch, and characters can become legends. The main risk is under-investment in narrative text (tracked as R-17). | [RISKS](RISKS.md) |

No answer was "not really". Two answers depend on runtime spikes (Q10 on pawn custody and
Q8 on the error volume when a mod is removed). Both are scheduled as early experiments before
the dependent phase is built.

## 14. Master-design reconciliation

The first Phase 0 pass was written before the [master design](../The%20Network%20%E2%80%94%20Full%20Mod%20Design%20-%20Master%20Implementation%20Brief.md) was on `main`, and listed
twelve assumptions to check against it. The master design has since been added, and this
revision reconciles the whole architecture against it. The design controls gameplay intent;
numbers it deliberately leaves unspecified stay tuning values. Nothing below was invented to
fill a gap: anything the design does not answer is marked open. A final owner review then
settled the remaining open questions (§ 14.3); those decisions are reflected in the rows below.

### 14.1 The former assumptions

| # | Former assumption | Master design | Status | Decision now, and where it lives |
|---|---|---|---|---|
| 1 | Intel fee and payment medium | § 9 (a modest fee, paid on submitting), § 71 (650 silver), § 83 (500 silver) | **Resolved** (owner) | Silver, paid at submission and kept for rounds that ran. **No global fee:** it depends on the chosen Fixer or contact (§ 6.13). A usable Comms Console is **required** (§ 9). Beacon payment mechanics are technical (Spike S4). |
| 2 | Who performs Intel in Phase 1 | § 8, § 9 step 3, § 13, § 71 (the contact is a faction) | **Resolved — architecture corrected** (owner) | The player chooses a contact: **Fixers** (persistent first-class actors, § 6.6.2), faction contacts and the Exchange from Phase 1; known contractors from Phase 2. |
| 3 | Search duration | § 9, § 71 ("Unknown / several days"), § 83 (five days) | **Resolved** (owner) | Several days per round, **by source**: speed, specialties, knowledge, geography, target difficulty, relationship, reputation and seeded variance. No frozen formula. The UI shows the elapsed time and a vague estimate, never the due tick (§ 9). |
| 4 | Opportunity archetypes for Phase 1 | § 16 ("not every archetype must exist initially"), § 15, § 12 | **Resolved** | `GuardedCache` (guarded or unguarded) and "no credible lead", both chosen through the source resolver, plus the subset of § 12 divergence classes a cache can express ([IMPLEMENTATION_PHASES § 4.2](IMPLEMENTATION_PHASES.md#42-smallest-slice-that-proves-the-chain)). |
| 5 | Tenebrite specifics | § 14, § 56–57, § 85 | **Resolved — architecture corrected** | No Tenebrite- or Beyond Our Reach-specific code, and no BOR requirement. A "Tenebral" association emerges from generic source-mod evidence (§ 6.14.1). Compatibility adapters may add hints only. |
| 6 | Materialize leads immediately or on "pursue" | § 11 (distance, operational window; pursue / ignore / abandon / keep waiting) | **Resolved** (owner) | Phase 1 materializes at once, and the vanilla timeout is the operational window. Ignore leaves the lead to expire; abandon closes it. **One request can deliver many leads**, and the same search continues after a lead; its cost is the source's continuation policy ([STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)). |
| 7 | UI tab set | § 70 | **Resolved** | Intel, Procurement, Contracts, Contractors, History. |
| 8 | Contractor population scale | § 75 (a "contractor population scale" setting) | **Resolved** (owner) | About **100 contractor identities** by default (a setting), from Solos to companies, mostly obscure, with a small famous upper tier; a recurring global cast in `ModSettings`, snapshotted per world (§ 6.6.3). Exact ratios are tuning. |
| 9 | Player contractor registration | § 35, § 39, § 40, § 75 | **Resolved**; UI placement open | Registration makes the colony an organization **without replacing its faction**. The player picks a name, a basic public profile and later an emblem; reputation starts at Unknown; a setting enables it ([DATA_MODEL § 4.2](DATA_MODEL.md#42-capability-components)). **Open:** where it sits in the UI (Contracts tab by default). |
| 10 | Sponsorship semantics | § 22, § 30, § 31, § 41 | **Resolved — architecture corrected** | Sponsorship is an investment (silver, weapons, armor, medicine, transport, technology, supplies) that raises capability and the relationship and never guarantees success. The assumed revenue share and priority terms are not in the design and were removed. Given gear is tracked and can reappear physically. Factions may lend the player gear and expect it back. |
| 11 | Economy constants | § 20–21, § 23, § 62, § 77–78 | **Resolved** (owner); numbers are tuning | Procurement quotes are **Fixer-mediated**: contractor and Fixer contribute separate components to one client-facing quote; the Fixer's policies set the deposit (master § 21 suggests half), insurance and replacement terms; the deposit is normally lost on failure; insurance recovers part of it, never all; procurement costs more than a market purchase and scales steeply with rarity; sanity caps guard against broken values. Exact arithmetic, percentages, premiums and penalties remain tuning. |
| 12 | Naming and tone | § 1, § 25, § 28–29, § 43, § 63, § 66, § 81–82 | **Resolved** (owner) | The design's tone and vocabularies, plus a lightweight compositional name model: word pool A + word pool B + optional suffix (*Dead* + *Red*); Solos use pawn-style names; names are editable and never identity ([DATA_MODEL § 18.4](DATA_MODEL.md#184-name-generation)). Pool contents are Phase 1 content. |

### 14.2 Contradictions found and corrected

Required by the review:

1. **Intel accepted a quantity** (`IntelTopic.quantityHint`). Removed. Intel is topic-only, and the
   generator decides what exists (master § 8, § 10). Procurement keeps the exact quantity.
2. **No source/context resolution; "Tenebral forces" needed a compatibility adapter.** Added
   § 6.14.1: source mod is contextual evidence, not ownership, with the master § 15 fallback
   hierarchy (master § 14–15, § 57).
3. **A wiped-out or dissolved contractor refunded the deposit.** Now the deposit is normally lost
   on in-world failure; fraud, cancellation, technical invalidation and insurance are separate
   cases ([STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules); master § 21, § 23, § 62).
4. **Catalog "hard" exclusions included heuristics** (`NotHaulable`, `NoLabel`, missing graphic).
   Moved to the overridable tier; each remaining hard exclusion states why it cannot be
   materialized ([COMPATIBILITY § 2.3](COMPATIBILITY.md#23-eligibility-heuristics-conservative); master § 6).
5. **Phase 1 did not prove loot without extermination.** Added Spike S19 and acceptance criterion
   A10 (master § 17).

Found in the full comparison:

6. **Intel had one fixed source in Phase 1.** The design has the player choose a contact,
   including factions (master § 8–9, § 13, § 71). Faction contacts are now in Phase 1.
7. **Leads were the truth plus a small amount error**, close to a guaranteed loot quest. The
   master § 12 quality classes are now a committed, hidden divergence class; leads carry a
   confidence descriptor and report other cargo (master § 3.2, § 11–12, § 18, § 85).
8. **Leaders had a lower death weight in abstract resolution**, which is plot armor (master § 45).
   Removed ([SIMULATION § 3.4](SIMULATION.md#34-known-characters-in-operations)).
9. **Successors inherited reputation "only as legacy text".** The design lets a successor inherit
   some reputation (master § 46). It now inherits a reduced, decaying reputation prior.
10. **Sponsorship assumed revenue share and priority.** Replaced by the design's investment model
    (master § 31).
11. **In Phase 2 a failed procurement had no world consequence** until Phase 3 (master § 3.4, § 24).
    Phase 2 now has one consequence rule: a "last known location" recovery opportunity built with
    the Phase 1 site machinery.
12. **Partial success and renegotiation lacked the client's choices** (master § 23). The client
    now chooses: accept partial, request continuation or renegotiate; a renegotiation can be
    paid, refused, cancelled or reduced in scope.
13. **Open-market contracts could not bring new groups into the story** (master § 64). The
    population manager may now introduce a new organization as a bidder.
14. **Vanilla quests are used only as a custody anchor**, while master § 87 says to use
    Quest/QuestPart/Slate "where practical". Recorded as
    [ADR-025](DECISIONS.md#adr-025--network-lifecycles-are-not-vanilla-quests), which the owner
    has since **accepted**: quest ownership is not a practical lifecycle match, so this is not a
    violation of § 87.
15. **Examples named "Dead Red" as a character in an organization "Red Hand".** In the design,
    Dead Red is the organization. Fixed.
16. **The exploit list of master § 78 was only partly covered.** [RISKS R-20](RISKS.md#r-20--economic-exploits) is expanded.
17. **Player-facing descriptors** for experience (§ 28), doctrine (§ 29), relationships (§ 43),
    reputation (§ 39) and fame (§ 66) were missing. They are now derived labels over internal
    numbers.

Consistent already, and kept: persistence root, typed IDs, external references, actors by
composition, the Intel/Lead/Opportunity and Contract/Operation separations, the event bus,
bounded history and summaries, facts vs awareness, determinism, abstract contractors, the
adapter boundary, the phase split, zero Harmony, and the conditional status of ADR-014, ADR-015
and ADR-023.

### 14.3 Final owner decisions (foundation pass)

The owner's final review settled every remaining design question and moved several foundations
before Phase 0 is frozen:

1. **Comms Console.** Network access requires a usable vanilla Comms Console, enforced from
   Phase 1 (§ 9). Alternatives are not designed.
2. **Fixers / brokers** are persistent, first-class actors with their own capability (§ 6.6.2).
3. **Intel fees and durations** depend on the chosen Fixer or contact; no global value, no
   frozen formula (§ 6.13).
4. **Contractor population:** about 100 identities by default, configurable; a small famous
   upper tier; operational experience separate from fame; Legendary is never protection
   (§ 6.6.3).
5. **Fixer-mediated procurement:** contractor and Fixer contribute separate components to one
   quote; the Fixer mediates deposits, insurance and a contractor who disappears before work
   starts (§ 6.15).
6. **Quality intent:** an optional broad preference for Intel (not Phase 1); strict quality
   minimums for Procurement are deferred and will be expensive, slow and refusal-prone.
7. **Name generation:** lightweight and compositional; names are never identity.
8. **ADR-025 accepted.**

And from the same review, structural fixes:

- **One Intel request, many leads**; the same search continues after a lead
  ([STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request)). The earlier rule "keep waiting means
  a new request" is gone.
- **Contractor capability is separate from NPC simulation** (§ 6.6.1): the player gets no
  abstract roster, and a Solo has no headcount model.
- **A global cast in `ModSettings`**, snapshotted into each world, never written back, with its
  own settings version (§ 6.6.3).

### 14.4 Still open

Nothing architectural. What remains is **tuning and content**: Fixer and contractor policies
(fees, speeds, markups, deposits, insurance, continuation, replacement), durations, quantities,
premiums, penalties, the fame and experience distribution of the generated cast, name pools,
and all resolver and pricing tables.

### 14.5 Reconciliation check (master design)

| # | Question | Answer | Where |
|---|---|---|---|
| 1 | Can Intel specify a quantity? | **No.** | § 6.13, [DATA_MODEL § 8](DATA_MODEL.md#8-intel-leads-and-opportunities) |
| 2 | Can Procurement specify a quantity? | **Yes**, exactly. | § 6.15, [DATA_MODEL § 9](DATA_MODEL.md#9-contracts) |
| 3 | Does source-mod metadata force ownership or hostility? | **No.** It is contextual evidence only. | § 6.14.1 |
| 4 | Can a same-source-mod faction influence opportunity generation? | **Yes**, when it is contextually appropriate. | § 6.14.1 |
| 5 | Is the deposit refunded automatically when a contractor dies catastrophically? | **No.** It is normally lost; insurance and technical invalidation are separate cases. | [STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules) |
| 6 | Can `Allowed` recover a weird but valid modded ThingDef that the heuristics dislike? | **Yes**, unless it is truly technically impossible or unsafe. | [COMPATIBILITY § 2.3](COMPATIBILITY.md#23-eligibility-heuristics-conservative) |
| 7 | Can the player take part of an Intel site's loot and retreat without killing everyone? | **Yes** by architecture; Phase 1 must prove the vanilla site allows it (S19, A10). | § 6.14, [IMPLEMENTATION_PHASES § 4.3](IMPLEMENTATION_PHASES.md#43-phase-1-acceptance-criteria) |
| 8 | Is the master design linked from the README and docs? | **Yes.** | [README](../README.md), this document's header |
| 9 | Is the "master design unavailable" risk resolved? | **Yes.** | [RISKS R-16](RISKS.md#r-16--master-design-brief-unavailable-during-phase-0-resolved) |
| 10 | Can an active major character be discarded because the bound-pawn soft cap was reached? | **No.** | [ABSTRACT_PHYSICAL_LIFECYCLE § 2](ABSTRACT_PHYSICAL_LIFECYCLE.md#2-identity-tiers) |
| 11 | Can a failed event consumer replay the whole event and double-apply earlier effects? | **No.** | § 12, [EVENTS_AND_HISTORY § 1.4](EVENTS_AND_HISTORY.md#14-no-double-application-after-save-and-load) |
| 12 | Is registry-quest custody still conditional on S9? | **Yes.** | [DECISIONS ADR-014](DECISIONS.md#adr-014--off-map-pawn-custody-via-a-hidden-registry-quest-conditional-s9) |
| 13 | Is Phase 0 still documentation-only? | **Yes.** | [README](../README.md#repository-status) |

### 14.6 Foundation check (final owner decisions)

| # | Question | Answer | Where |
|---|---|---|---|
| 1 | Can one IntelRequest deliver more than one Lead? | **Yes.** | [STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request), [DATA_MODEL § 8](DATA_MODEL.md#8-intel-leads-and-opportunities) |
| 2 | Can the player continue the same search after receiving a lead? | **Yes.** | [STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request) |
| 3 | Is the continuation fee hardcoded globally? | **No.** The source's continuation policy decides. | [STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request) |
| 4 | Does registering the player as a contractor give the colony an abstract roster? | **No.** | § 6.6.1 |
| 5 | Can an Individual Solo be a contractor without an organization roster? | **Yes.** | § 6.6.1, [DATA_MODEL § 6](DATA_MODEL.md#6-contractor-actors-capability-npc-simulation-organization) |
| 6 | Are Fixers / brokers persistent actors with their own capability? | **Yes.** | § 6.6.2, [DATA_MODEL § 4.3](DATA_MODEL.md#43-fixers-and-brokers-fixerprofile) |
| 7 | Can Fixers affect Intel fee, speed, reliability, procurement terms and insurance? | **Yes, structurally.** Formulas stay replaceable. | § 6.6.2 |
| 8 | Is the default contractor cast about 100 identities and configurable? | **Yes.** | § 6.6.3 |
| 9 | Do 100 contractor identities mean 100 full pawns or per-tick simulations? | **No.** | § 6.6.3, [PERFORMANCE § 3](PERFORMANCE.md#3-scale-assumptions-and-cost-estimates) |
| 10 | Can Legendary contractors die? | **Yes, absolutely.** | § 6.6.3, [SIMULATION § 3.4](SIMULATION.md#34-known-characters-in-operations) |
| 11 | Are operational experience and fame separate concepts? | **Yes.** | § 6.6.1 |
| 12 | Is the reusable cross-save cast stored in ModSettings? | **Yes.** | [DATA_MODEL § 18](DATA_MODEL.md#18-global-network-cast-modsettings-cross-save) |
| 13 | Is cross-save runtime history stored in ModSettings? | **No.** | [DATA_MODEL § 18.2](DATA_MODEL.md#182-world-snapshot-import-rule) |
| 14 | Does every world get its own runtime actor and history state? | **Yes.** | [DATA_MODEL § 18.2](DATA_MODEL.md#182-world-snapshot-import-rule) |
| 15 | Can changing the global roster silently rename an actor in an existing colony? | **No.** | [DATA_MODEL § 18.2](DATA_MODEL.md#182-world-snapshot-import-rule) |
| 16 | Does "Regenerate generated cast" preserve custom templates? | **Yes.** | [DATA_MODEL § 18.3](DATA_MODEL.md#183-editing-and-regenerating-the-global-roster) |
| 17 | Are global templates identified by stable IDs, not display names? | **Yes.** | [DATA_MODEL § 18.1](DATA_MODEL.md#181-settings-shape) |
| 18 | Does the ModSettings cast data have its own schema version? | **Yes.** | [SAVE_AND_MIGRATION § 11](SAVE_AND_MIGRATION.md#11-global-cast-settings-networksettingsversion) |
| 19 | Can Procurement separate the contractor quote from the Fixer's fee and terms? | **Yes.** | [DATA_MODEL § 9](DATA_MODEL.md#9-contracts) |
| 20 | Is the deposit outcome when a contractor disappears before work globally hardcoded? | **No.** Fixer policy, terms, insurance and circumstances decide. | [STATE_MACHINES § 4.2](STATE_MACHINES.md#42-money-rules) |
| 21 | Is a usable Comms Console required initially? | **Yes.** | § 9 |
| 22 | Does temporary loss of the Comms Console destroy active requests? | **No.** | § 9 |
| 23 | Can quality-bearing Intel eventually express a broad quality preference? | **Yes.** | [DATA_MODEL § 8](DATA_MODEL.md#8-intel-leads-and-opportunities) |
| 24 | Is strict quality-qualified Procurement deferred? | **Yes.** | [IMPLEMENTATION_PHASES § 11](IMPLEMENTATION_PHASES.md#11-deliberately-deferred) |
| 25 | Is organization name generation lightweight and compositional? | **Yes.** | [DATA_MODEL § 18.4](DATA_MODEL.md#184-name-generation) |
| 26 | Can users create and edit their own global contractors and Fixers, including a custom Legendary? | **Yes** (architecture). | [DATA_MODEL § 18.3](DATA_MODEL.md#183-editing-and-regenerating-the-global-roster) |
| 27 | Is ADR-025 accepted and no longer awaiting owner review? | **Yes.** | [DECISIONS ADR-025](DECISIONS.md#adr-025--network-lifecycles-are-not-vanilla-quests) |
| 28 | Is registry-quest custody still conditional on S9? | **Yes.** | [DECISIONS ADR-014](DECISIONS.md#adr-014--off-map-pawn-custody-via-a-hidden-registry-quest-conditional-s9) |
| 29 | Is the partial-loot Spike S19 still required? | **Yes.** | [RIMWORLD_INTEGRATION § 5](RIMWORLD_INTEGRATION.md#5-runtime-spikes) |
| 30 | Is Phase 0 still documentation-only? | **Yes.** | [README](../README.md#repository-status) |
