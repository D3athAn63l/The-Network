# The Network — Technical Architecture

> Canonical technical reference. Phase 0 output.
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
14. [Assumptions pending master-brief review](#14-assumptions-pending-master-brief-review)

---

## 1. Purpose and scope

This document describes the long-term technical structure of The Network. The architecture is
designed for the complete mod, including the approved long-term directions (reputation through
history, rumors, bidding, refusal, organizational morale, gossip, contract inheritance,
favors/debts, introductions, black contracts, evidence and witnesses, sanctions, geographic
knowledge, learning, retirement transformation, fragmentation and mergers, legends, and chain
reactions). Implementation is still delivered in small vertical phases.

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
| Success / failure | Operations + Abstract Resolver, or physical play via Physical Adapters | `Operation` outcome, `Deployment` reconciliation |
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
│   ActorStore ──── capabilities:              IntelService ─────────► OpportunityService        │
│     ContractorProfile · IntelSourceProfile   ContractService ◄─────► BiddingService            │
│     IssuerProfile · SponsorProfile ·         WillingnessModel (refusal / pricing inputs)       │
│     IntroducerProfile                        OperationService ─────► AbstractResolver          │
│   CharacterStore (Known Characters)          OrganizationService (upkeep, morale, succession) │
│   RelationStore · ObligationLedger ·         ReputationModel (derived, cached)                 │
│     ContactBook                              ConsequenceEngine (chain reactions, inheritance)  │
│   KnowledgeStore                             GossipService (Phase 5)                           │
│   IntelStore · OpportunityStore                                                                │
│   ContractStore · OperationStore             HISTORY                                           │
│   DeploymentStore · LeaseStore                 HistoryLedger (tiered records) · SummaryStore   │
│   BeliefStore (Phase 5)                        LegendArchive · Awareness (facts vs knowledge)  │
│                                                                                                │
│  INTEGRATION (the only code that touches live RimWorld objects) ─────────────────────────────── │
│   ItemCatalog (session cache of ThingDefs)   SiteAdapter (vanilla Site + injected WorldObjectComp)│
│   CustodyService (pawn binding/reservation)  EncounterFactionAdapter (temporary factions)      │
│   DeliveryAdapter (drop pods; shuttle opt.)  PaymentAdapter (silver in/out)                    │
│   SignalBridge (SignalManager receiver)      CompatRegistry (DLC / mod adapters, gating)       │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
          │ vanilla APIs only (no Harmony in Phases 1–3)
          ▼
   RimWorld: WorldPawns · FactionManager · WorldObjects/Site · QuestManager (registry quest, Ph.3)
             SignalManager · LetterStack · TradeUtility · DropPodUtility · DefDatabase · Scribe
```

Arrows point in the direction of calls. Services never call Presentation. Domain never calls
Integration directly: domain code asks through narrow adapter interfaces
(`ISiteAdapter`, `ICustody`, `IPayment`, `IDelivery`) so that domain logic and state machines
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
    rebuilds runtime indexes only and emits nothing.
  - The **first `WorldComponentTick`** runs `EnsureStarted()`. For a new game, or a save that
    has just gained the mod, this bootstraps the pseudo-actors and the seed. On a load it runs
    reconciliation (validation, reference repair, custody checks) and registers the signal
    receiver. Starting lazily avoids depending on world-generation ordering and needs no
    GameComponent.
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
  organizations, brokers, individuals, RimWorld factions (as proxies), the player, and the
  Network's own institutions. Model what each actor can do through **capability components**,
  not inheritance.
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

### 6.7 Characters and custody

- **Responsibility.** Track the individuals that matter as **Known Characters**: leaders,
  lieutenants, and anyone the player met or who did something notable. Bind each one to at most
  one real `Pawn`, and only when physically needed. Enforce the no-duplication invariants.
- **Persistent.** `CharacterStore` (`KnownCharacter` records, including custody state and
  `PawnRef`), `DeploymentStore` and `LeaseStore`.
- **Runtime cache.** A reverse map from `Pawn` to `CharacterId`, and the set of pawns currently
  reserved.
- **Public surface.** `Custody.Materialize(characterId, purpose)`,
  `Custody.BeginDeployment(...)`, `Custody.Reconcile(deploymentId)`,
  `Characters.Promote(...)`.
- **Emits.** `KnownCharacterPromoted`, `KnownCharacterKilled`, `KnownCharacterCaptured`,
  `KnownCharacterRescued`, `KnownCharacterDefected`, `KnownCharacterLost`,
  `DeploymentReconciled`.
- **Consumes.** Signals via `SignalBridge`, registry-quest notifications (Phase 3), and
  `OperationResolved` (abstract fates).
- **Detail.** [ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md).

### 6.8 Organizations (contractor behaviour)

- **Responsibility.** Run the group-level state of contractor organizations: roster headcounts
  by tier, wounded recovery, equipment profile, doctrine drift, organizational morale,
  recruitment, succession, career age and retirement pressure.
- **Persistent.** Stored in `ContractorProfile` on the actor.
- **Runtime cache.** Derived strength rating and morale descriptor, both flagged dirty.
- **Public surface.** `Orgs.EffectiveStrength(actor)`, `Orgs.MoraleState(actor)`,
  `Orgs.Checkout(actor, request)` / `Orgs.Return(...)`, and `Orgs.RunUpkeep(actor)`
  (a scheduler job).
- **Emits.** `OrganizationMoraleShifted`, `LeaderSucceeded`, `OrganizationRetired`, and
  `OrganizationFragmented` / `OrganizationMerged` (Phase 6).
- **Consumes.** `OperationResolved`, `ContractorCasualties`, `ContractCompleted`,
  `ContractFailed`, `KnownCharacterKilled`, `LeaderKilled`, `PaymentDefaulted`, `SponsorshipChanged`.
- **Lifecycle.** Per-organization upkeep runs about once per in-game day, staggered by seed.
- **Detail.** [SIMULATION § 4–5](SIMULATION.md#4-organizations-upkeep-morale-doctrine).

### 6.9 Knowledge (learning and geographic knowledge)

- **Responsibility.** Let actors get better at things they have actually done. Knowledge is
  held as compact per-actor experience over topic keys (item, faction def, threat type,
  archetype, region, planet layer).
- **Persistent.** `KnowledgeStore`: `actorId → KnowledgeBook { topicKey → (exp, lastTouchedTick) }`,
  capped at 64 topics per actor, with the lowest-value topics evicted.
- **Runtime cache.** None needed. Books are small.
- **Public surface.** `Knowledge.Proficiency(actor, topic)` returns a value in 0..1 with lazy
  decay. `Knowledge.Transfer(from, to, fraction)` supports retirement, succession and mergers.
- **Consumes.** `OperationResolved`, `OpportunityClaimed` and `IntelResolved` (the topics touched).
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
  so that reputation has inertia and hysteresis and does not flicker.
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
  loot.
- **Persistent.** `IntelStore`: `IntelRequest` and `Lead`.
- **Public surface.** Commands `SubmitIntel(topic, source)` and `CancelIntel(id)`; read models.
- **Dependencies.** ItemCatalog (topic validation), PaymentAdapter (fee), OpportunityService
  (generation), Actors (the source), Knowledge (source proficiency).
- **Emits.** `IntelRequested`, `IntelSearchProgressed` (optional flavour), `IntelResolved`,
  `IntelNoLead`, `IntelCancelled`, `IntelInvalidated`.
- **Consumes.** `ReferenceInvalidated` (the topic def is gone).
- **Detail.** [STATE_MACHINES § 1–2](STATE_MACHINES.md#1-intel-request).

### 6.14 Opportunities (world truth)

- **Responsibility.** Generate and own world truth: what actually exists, where it is, how much
  of it, who guards it, who else is after it, and when it expires. Opportunities are created by
  Intel, by failures (rescue and recovery), by world events and by consequences.
- **Persistent.** `OpportunityStore`: `Opportunity { archetype, payload, location, threat,
  competitors, expiry, lifecycle, materialization, lineage }`.
- **Public surface.** `Opportunities.Generate(request)`, `Opportunities.Materialize(id)`,
  and site callbacks through the SiteAdapter.
- **Emits.** `OpportunityGenerated`, `OpportunityMaterialized`, `OpportunityEngaged`,
  `OpportunityClaimed`, `OpportunityExpired`, `OpportunityLostToCompetitor`,
  `OpportunityDestroyed`, `OpportunityInvalidated`.
- **Future.** Archetype catalog growth (trader holds it, owner holds it, salvage, orbital wreck,
  mineable deposit, rumor-only), competitors racing the player, and black-market leads.

### 6.15 Contracts (including offers and procurement)

- **Responsibility.** Agreements between an issuer and a contractor. One shared lifecycle is
  composed from parts: Parties, Terms, Confidentiality, Objectives, Offers, Assignment,
  Progress, Outcome and Lineage. Procurement, recovery, rescue, hunt, escort, transport and
  black work differ in **objectives and kind rules**, not in class hierarchy.
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
  tradeability, equipment traits and rarity signals. It never infers what the player needs.
- **Persistent.** None in the save. Per-item overrides (Auto / Allowed / Blocked) are kept in
  `ModSettings` by defName.
- **Detail.** [COMPATIBILITY § 2](COMPATIBILITY.md#2-item-catalog).

### 6.19 Physical adapters (Integration)

| Adapter | Responsibility | Vanilla APIs used |
|---|---|---|
| `SiteAdapter` | Build vanilla `Site`s for opportunities (vanilla `SitePartDef`s such as `ItemStash` plus a threat part), start the timeout, bind the injected `WorldObjectComp_NetworkSite`, and forward its callbacks | `SiteMaker.MakeSite`, `SitePart.things`, `TimeoutComp`, `WorldObjectComp` |
| `CustodyService` | Pawn binding, registry reservation (Phase 3), deployment ledger, reconciliation | `WorldPawns`, `QuestManager` (registry quest), `PawnGenerator` under `Rand.PushState` |
| `EncounterFactionAdapter` | Temporary per-organization factions for physical presence (Phase 3) | `FactionGenerator`, `Faction.temporary`, `FactionManager` |
| `DeliveryAdapter` | Hand goods to the player: drop pods (Core), walk-in (Phase 3), shuttle (Royalty, optional) | `DropPodUtility.DropThingsNear`, `TransportShipMaker` (optional) |
| `PaymentAdapter` | Take silver from and pay silver to the player; represent debt when the player cannot pay | `TradeUtility.ColonyHasEnoughSilver`, `TradeUtility.LaunchSilver`, drop pods |
| `SignalBridge` | Receive the `TheNetwork.*` quest-tag signals (pawn, thing and world-object lifecycle) and route them to services | `SignalManager.RegisterReceiver`, `QuestUtility.AddQuestTag` |

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

- **Responsibility.** Logging, timing, validators, dev actions and the simulation harness.
- **Detail.** [DEBUGGING](DEBUGGING.md).

---

## 7. Key flows

### 7.1 Phase 1: Intel to site to history

```
UI: player picks ThingDef from ItemCatalog → Commands.SubmitIntel(topic)
  └─ IntelService: validate topic (catalog verdict, DefRef resolves) → PaymentAdapter.Charge(fee)
       └─ create IntelRequest{Submitted, seed} → Events.Publish(IntelRequested)
       └─ Scheduler.Schedule("intel.resolve", dueTick = now + seededDuration)
…time passes (no per-tick work)…
Scheduler fires "intel.resolve"
  └─ IntelService.Resolve: re-validate topic → seeded lead roll
       ├─ no lead → IntelRequest{ResolvedNoLead} → Publish(IntelNoLead) → letter
       └─ lead → OpportunityService.Generate(archetype=GuardedCache, payload=DefRef×amount)
             → Opportunity{Revealed} + Lead{reported ranges} → SiteAdapter.Materialize
                → vanilla Site (ItemStash things + threat part) + TimeoutComp + comp binding
             → Publish(IntelResolved, OpportunityMaterialized) → letter with look target
Player caravan / pods reach site → vanilla map generation places the stash
  └─ comp.PostMapGenerate → Opportunity{Engaged} → Publish(OpportunityEngaged)
Player leaves with the goods (caravan) → comp.PostCaravanFormed → tally the target def in the caravan
Map removed → comp.PostMyMapRemoved → Opportunity{Claimed | Abandoned} → Publish(...)
  └─ History consumer writes a Notable record, and the player's summary is updated
Timeout without a visit → comp.PostDestroy → Opportunity{Expired} → Publish(OpportunityExpired)
```

### 7.2 Phase 2: procurement (abstract)

```
Commands.PostContract(kind=Procurement, objective=Acquire(DefRef, qty), deliverTo=home map)
  └─ ContractService: Posted → Bidding (window jobs) → WillingnessModel per eligible contractor
       → Offers (or recorded refusals with reasons) → player AcceptOffer → Awarded
       → PaymentAdapter.Charge(deposit) → OperationService.Start → checkpoints scheduled
Checkpoint "engage" → AbstractResolver (frozen inputs + seed) → committed outcome
  └─ Publish(OperationResolved, ContractorCasualties?) → Orgs / History / Relations / Morale
Checkpoint "deliver" → DeliveryAdapter (drop pods) → Publish(DeliveryCompleted)
  └─ ContractService: Completed | PartiallyCompleted → PaymentAdapter.Charge(balance)
Failure → ContractFailed(cause) → ConsequenceEngine may create a recovery or rescue Opportunity
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
Event ContractorRescued { rescuer: Player(A1), rescued: Org "Red Hand"(A17),
                          characters: [K42 "Dead Red"], opportunity: O311, place: T(2341) }
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
- **Every scheduler job and event consumer runs inside try/catch.** A failing job is logged
  once per `(kind, target)`, then removed or rescheduled with backoff. Up to three attempts are
  made before the target entity is quarantined.
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
| 1 | Can Intel work without knowing what the player needs? | **Yes.** Intel takes a *topic* from a neutral catalog. The world decides what exists (or that nothing does). The catalog never ranks by need. | [STATE_MACHINES § 1](STATE_MACHINES.md#1-intel-request), [COMPATIBILITY § 2](COMPATIBILITY.md#2-item-catalog) |
| 2 | Can procurement exist independently of Intel? | **Yes.** Procurement is a Contract with an `Acquire` objective. It needs no Lead. The contractor's own knowledge stands in for Intel. | [STATE_MACHINES § 4](STATE_MACHINES.md#4-procurement-contract-specialization) |
| 3 | Can contractor groups persist for decades cheaply? | **Yes.** Organizations are records with headcounts. At most a few Known Characters per org hold real pawns. Those pawns are quest-reserved and therefore suspended, and normalized at storage so that vanilla mothballs them. Upkeep runs daily and is staggered. History is tiered and capped. | [PERFORMANCE](PERFORMANCE.md) |
| 4 | Can contractors die permanently? | **Yes.** Deaths are committed once, whether by the resolver or by physical reconciliation. A dead Known Character is never re-materialized. | [ABSTRACT_PHYSICAL_LIFECYCLE § 3](ABSTRACT_PHYSICAL_LIFECYCLE.md#3-invariants) |
| 5 | Can organizations survive leader death? | **Yes.** The leader is a role pointing at a Known Character. Succession picks or promotes a successor. The organization's identity is its `ActorId`, not its leader. | [SIMULATION § 4.5](SIMULATION.md#45-leadership-and-succession) |
| 6 | Can history alter future behaviour? | **Yes.** Summaries, relation edges, obligations, morale and knowledge feed willingness, pricing, bidding and gossip. | [§ 8](#8-how-history-changes-future-behaviour) |
| 7 | Can one Network event affect several systems without tight coupling? | **Yes.** There is one publish and ordered consumers, and consumers do not know each other. | [EVENTS_AND_HISTORY § 1](EVENTS_AND_HISTORY.md#1-network-events) |
| 8 | Can external mods be removed safely? | **Yes, with defined degradation.** DefRefs are strings, and a miss invalidates the affected activity with a refund or an explanation. Vanilla-level errors for missing Things inside sites cannot be avoided, but they do not cascade. | [COMPATIBILITY § 3](COMPATIBILITY.md#3-external-def-safety) |
| 9 | Can new modded ThingDefs appear automatically? | **Yes.** The catalog is rebuilt every session from DefDatabase with conservative heuristics. | [COMPATIBILITY § 2](COMPATIBILITY.md#2-item-catalog) |
| 10 | Can a contractor move abstract → physical → abstract without duplication? | **Yes, by invariants.** One pawn per character, a deployment ledger, reconciliation driven by pawn state, and the Network never discards pawns. This is verified by Phase 3 spikes. | [ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md) |
| 11 | Can the player become a contractor later without replacing their faction? | **Yes.** The player is a `PlayerProxy` actor bound to `Faction.OfPlayer`. Registration adds a `ContractorProfile` component. | [DATA_MODEL § 4](DATA_MODEL.md#4-actors) |
| 12 | Can competing contractors exist? | **Yes.** Contracts have offers, not a single owner. Opportunities have a `competitors` list, and actors race through Operations. | [DATA_MODEL § 8–9](DATA_MODEL.md#8-intel-leads-and-opportunities) |
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

## 14. Assumptions pending master-brief review

The master brief was not available. These points were inferred from the Phase 0 brief and
should be confirmed or corrected **before Phase 1 implementation starts**:

1. **Intel fee and payment medium.** Silver taken through the vanilla comms/beacon path
   (`TradeUtility.LaunchSilver`) is assumed. Fee scale and whether a comms console is required
   are open.
2. **Who performs Intel in Phase 1.** A single Network institution actor, provisionally "the
   Exchange", acts as broker. Named brokers arrive in a later phase.
3. **Search duration.** Assumed to be days (seeded, for example 2–6 days) and scaled by rarity.
4. **Opportunity archetypes for Phase 1.** Assumed: guarded cache (vanilla `ItemStash` plus a
   threat part) and "no credible lead". The brief may prescribe others.
5. **Tenebrite specifics.** `BOR_Tenebrite` from Beyond Our Reach is treated as an ordinary
   modded ThingDef with no special code. Any Tenebrite-specific content (for example
   "Tenebral forces") would be a compat adapter.
6. **Whether leads are materialized immediately or on "pursue".** The state machine supports
   both. Phase 1 materializes immediately.
7. **UI tab set.** Intel, Procurement, Contracts, Contractors, History, taken from the Phase 0 brief.
8. **Contractor population scale.** Assumed 12–40 active NPC organizations, each with 5–40
   members in headcount.
9. **Player contractor registration mechanics.** Where the player registers and what they are
   eligible for.
10. **Sponsorship semantics.** Assumed: a sponsor provides equipment or funding in exchange for
    priority or a revenue share. Equipment is leased, as tracked equipment leases.
11. **Economy constants.** Deposit percentages, insurance premiums, penalty rules.
12. **Naming and tone.** Organization and character name generation, epithet vocabulary.
