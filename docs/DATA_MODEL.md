# Data Model

> The shared domain model. Field lists define the **persistent shape**. Names are indicative;
> the *concepts, identities and relationships* are binding for implementation phases.
> Notation: `field: Type = default // note`. `?` means optional or nullable. `[]` means list.
> Everything here is persisted under `NetworkWorldComponent` unless marked *(runtime)*.

## Contents

1. [Identifier strategy](#1-identifier-strategy)
2. [External references](#2-external-references)
3. [Root state](#3-root-state)
4. [Actors](#4-actors)
5. [Known Characters](#5-known-characters)
6. [Contractor organizations (ContractorProfile)](#6-contractor-organizations-contractorprofile)
7. [Knowledge](#7-knowledge)
8. [Intel, Leads and Opportunities](#8-intel-leads-and-opportunities)
9. [Contracts](#9-contracts)
10. [Operations](#10-operations)
11. [Deployments and equipment leases](#11-deployments-and-equipment-leases)
12. [Relationships, obligations, contacts](#12-relationships-obligations-contacts)
13. [History, summaries, legends](#13-history-summaries-legends)
14. [Beliefs and rumors (Phase 5 shape)](#14-beliefs-and-rumors-phase-5-shape)
15. [Scheduler jobs and event journal](#15-scheduler-jobs-and-event-journal)
16. [Money](#16-money)
17. [Persistence conventions](#17-persistence-conventions)

---

## 1. Identifier strategy

### 1.1 Network IDs

- One persisted counter, `nextId: int`, starts at 1. Every Network entity takes its ID from this
  counter, whatever its kind. IDs are therefore **unique across kinds** inside a save.
- IDs are wrapped in **typed readonly structs** so they cannot be mixed up at compile time:

| Type | Debug prefix | Entity |
|---|---|---|
| `ActorId` | `A` | NetworkActor (org, individual, faction proxy, player proxy, institution) |
| `CharacterId` | `K` | KnownCharacter |
| `IntelRequestId` | `I` | IntelRequest |
| `LeadId` | `L` | Lead |
| `OpportunityId` | `O` | Opportunity |
| `ContractId` | `C` | Contract |
| `OfferId` | `B` | Offer (bid) |
| `OperationId` | `P` | Operation |
| `DeploymentId` | `D` | Deployment |
| `LeaseId` | `E` | EquipmentLease |
| `ObligationId` | `F` | Obligation (favor or debt) |
| `BeliefId` | `R` | Belief / rumor (Phase 5) |
| `HistoryRecordId` | `H` | HistoryRecord |
| `LegendId` | `G` | Legend |

- `0` means *none*. Every ID struct has `IsValid => value > 0` and prints as `A17`, `C301`, and
  so on in logs and dev tools.
- **Event sequence numbers** use a separate `nextEventSeq: long`, because events are far more
  numerous and are ordered. Scheduler jobs use `nextJobSeq: long` as an ordering tie-break.
  Neither needs to be globally unique with entity IDs.
- IDs are **never reused, never renumbered, never derived from RimWorld objects**. Successors,
  merges and splits always get new IDs, and lineage links connect them.
- **Generic references** use `EntityRef { kind: EntityKind(byte), id: int }`. They are used in
  event subjects, history participants and belief subjects. Because IDs are unique across
  kinds, `kind` is a validation and readability aid, not a disambiguator.

**Rejected:**

- Vanilla `UniqueIDsManager`: it is closed, and adding counters would need patching.
- GUIDs: they bloat saves and are unreadable in logs.
- Object references as identity: they are fragile across save and load, and break when an
  entity is compacted.
- Per-kind counters: they give no cross-kind uniqueness and make more fields to migrate.

`int` capacity (about 2.1 billion) is effectively unlimited. Expected lifetime allocation is in
the tens of thousands.

### 1.2 Why identity survives fragmentation, succession and mergers

An actor's identity is its `ActorId`. It is not its name, its leader or its faction. A
successor is a **new** actor with `lineage.predecessors = [old]`. An absorbed actor keeps its ID
with `status = Absorbed` and `lineage.absorbedInto = X`. Every historical reference still
resolves, to a live actor, a tombstone or a legend. Nothing is ever repointed.

---

## 2. External references

Everything outside The Network is referenced through **typed reference structs**. Each has a
defined persistence form and a defined recovery when the target is missing. Resolution is always
silent (it never goes through `Scribe_Defs`, which logs an error per missing def, see
[RIMWORLD_INTEGRATION § 2.13](RIMWORLD_INTEGRATION.md#213-scribe-and-iexposable--reuse-with-rules)), and each miss
is reported once by the validator.

| Ref type | Persisted as | Snapshot kept | Resolved via | When the target is missing |
|---|---|---|---|---|
| `DefRef<T>` (ThingDef, PawnKindDef, FactionDef, SitePartDef, …) | `defName` string | `label`, `packageId` of source mod (at capture time) | `DefDatabase<T>.GetNamedSilentFail`, cached | Owning entity decides: Intel is Invalidated, Contract is Voided (refund), Opportunity is Invalidated, Lease is written off, PawnKind falls back along a chain. History keeps the snapshot label, so "3 Tenebrite (Beyond Our Reach, removed)" still reads correctly. |
| `FactionRef` | `loadID` int | `name`, `def` defName, `wasPlayer` | runtime map `loadID → Faction`, rebuilt on load and on faction add | Proxy actor becomes `Destroyed(FactionVanished)`. Contracts issued by it are Voided. Encounter-faction links are cleared. History keeps the snapshot. |
| `WorldObjectRef` | `ID` int | `def` defName, `label`, last `TileRef` | map `ID → WorldObject` (rebuilt on load; updated through our comp and signals) | The site vanished outside our callbacks: the Opportunity becomes `Vanished`, which is handled like Destroyed. |
| `PawnRef` | `Scribe_References` pointer **plus** `thingIDNumber` int | `NameSnapshot`, gender, `kindDef` defName, faction snapshot | vanilla cross-ref resolution at load | The pointer is null after load, or the pawn is `Discarded`/`Destroyed` while not dead: the character becomes `Lost` (record only; never re-materialized). One *warning* per pawn from vanilla is acceptable and expected only when another mod discarded our pawn. |
| `TileRef` | `PlanetTile` (via `Scribe_Values`, supported by `ParseHelper`) **plus** `layerDef` defName | region key | `Find.WorldGrid.PlanetLayers` contains the layer id, and the tile id is in range | The layer is gone (for example Odyssey was removed and orbit tiles no longer exist): location-dependent entities are Invalidated. Region-keyed knowledge stays as data. |
| `MapRef` | the `WorldObjectRef` of its `MapParent` | — | `mapParent.Map` | Treated as "map gone": deliveries reroute ([STATE_MACHINES § 4](STATE_MACHINES.md#4-procurement-contract-specialization)). |
| `IdeoRef` (Ideology, later) | `Ideo.id` int | name | `Find.IdeoManager` | Dropped from derived features only. |
| `QuestRef` (registry quest, Phase 3) | `Quest.id` int | — | `Find.QuestManager` | Recreate the registry quest and re-reserve pawns. |

**Rules**

1. **No `Scribe_Defs` and no `Scribe_References` to factions or world objects in Network data.**
   Pawns are the only exception (`PawnRef`), because pawns can live in many holders and only
   vanilla's cross-reference resolver finds them reliably.
2. **Snapshots are captured once, when the reference is created.** They are never refreshed from
   live objects, because history needs *what it was*.
3. **Resolution caches store misses as well as hits.** A missing def is looked up once per
   session.
4. **Validation happens in the load pipeline and at checkpoints** (resolution, payment,
   delivery), never per tick.

---

## 3. Root state

```
NetworkWorldComponent : WorldComponent
  saveVersion: int                      // NetworkSaveVersion; see SAVE_AND_MIGRATION
  createdWithModVersion: string
  lastSavedWithModVersion: string
  networkSeed: int                      // fixed at bootstrap
  bootstrapped: bool
  ids: IdAllocator { nextId: int, nextEventSeq: long, nextJobSeq: long }
  // stores, saved in this fixed order (each under its own XML node):
  actors: ActorStore
  characters: CharacterStore
  knowledge: KnowledgeStore
  relations: RelationStore
  obligations: ObligationLedger
  contacts: ContactBook
  intel: IntelStore
  opportunities: OpportunityStore
  contracts: ContractStore
  operations: OperationStore
  deployments: DeploymentStore
  leases: LeaseStore
  history: HistoryLedger
  summaries: SummaryStore
  legends: LegendArchive
  beliefs: BeliefStore                  // empty until Phase 5; node present from Phase 1
  consequences: ConsequenceState        // rule cooldowns and budgets
  scheduler: SchedulerState
  journal: EventJournal
  diagnostics: DiagnosticsState         // quarantine list, invalidation notes, one-time warnings
```

Stores that have no content yet are still saved as empty nodes. This keeps the layout fixed and
makes migrations predictable.

---

## 4. Actors

```
NetworkActor
  id: ActorId
  kind: ActorKind              // Organization | Individual | FactionProxy | PlayerProxy | Institution
  status: ActorStatus          // Active | Dormant | Retired | Dissolved | Absorbed | Destroyed | Tombstone
  name: NameSnapshot           // display name; orgs get generated names
  seed: int                    // derived: Hash(networkSeed, id)
  foundedTick: int
  endedTick: int = -1
  endReasonKey: string?
  lineage: Lineage
    predecessors: ActorId[]
    successors: ActorId[]
    absorbedInto: ActorId?
    splitFrom: ActorId?
  bindings: ActorBindings
    faction: FactionRef?       // FactionProxy / PlayerProxy; origin faction for orgs lives in ContractorProfile
    embodies: CharacterId?     // Individual actors (for example a retired leader turned broker)
  homeRegion: RegionKey?
  components: ActorComponent[] // capabilities (polymorphic, persisted type names frozen)
  reputation: PublicReputation // see EVENTS_AND_HISTORY § 6
  flags: ActorFlags            // Quarantined, PlayerVisible, Legendary, …
```

### 4.1 Actor kinds

| Kind | Examples | Created when |
|---|---|---|
| `Organization` | NPC contractor crews, mercenary companies, salvage outfits | Bootstrap population. Recruitment of new orgs by the population manager. Successors from splits and mergers. |
| `Individual` | Retired contractor turned broker or Intel contact, a notable freelancer | Retirement transformation. Special content. |
| `FactionProxy` | A vanilla or modded RimWorld faction taking part in the Network (as an issuer, a target or a relationship holder) | **Lazily**, the first time a faction needs an identity in Network data |
| `PlayerProxy` | The player's colony | Bootstrap (exactly one; rebinds if `Faction.OfPlayer` changes) |
| `Institution` | "The Exchange" (Phase 1 broker), later market boards | Bootstrap |

### 4.2 Capability components

Components are **data**. Services interpret them. "Can this actor do X *right now*?" is
answered by a service (`Willingness`, `Bidding`) that combines components, status, commitments
and relationships.

| Component | Grants | Key fields |
|---|---|---|
| `ContractorProfile` | can accept contracts, can procure, can deploy | see [§ 6](#6-contractor-organizations-contractorprofile) |
| `IntelSourceProfile` | can provide Intel | `quality: float`, `coverage: RegionKey[]`, `topicStrengths` (reads KnowledgeBook), `feeScale: float`, `reliability: float`, `discretion: float` |
| `IssuerProfile` | can issue contracts | `budgetBand: int`, `preferredKinds: string[]`, `legitimacy: float` (0 = criminal, 1 = lawful), `paysOnTime: float` |
| `SponsorProfile` | can sponsor | `sponsored: ActorId[]`, `terms` per sponsee (revenue share, priority), `leases: LeaseId[]` |
| `SponsoredProfile` | receives sponsorship | `sponsor: ActorId`, `sinceTick`, `obligationsTo: ObligationId[]` |
| `IntroducerProfile` | can provide introductions | `introducibleActors: ActorId[]` (bounded), `introductionCooldownUntil` |
| `TraderProfile` (later) | can sell or buy goods | stock policy, tags |
| `RivalryProfile` (Phase 5) | long-running rivalries | `rivals: ActorId[]`, grudge records |

A capability that has been **lost** (for example a broker stops brokering) is removed. The
change is recorded in history. The component is not left in a dead state.

**The player** is a `PlayerProxy` actor. When the player registers as a contractor (Phase 4),
a `ContractorProfile` is added to the same actor. There is no new faction and no replacement.

---

## 5. Known Characters

Individuals who matter. Generic members are **headcount only** (see § 6).

```
KnownCharacter
  id: CharacterId
  name: NameSnapshot           // first / nick / last as captured; stable for history
  gender: Gender
  bioAgeYearsAtCapture: float
  kindDef: DefRef<PawnKindDef>
  xenotype: DefRef<XenotypeDef>?          // Biotech, optional
  role: CharacterRole          // Leader | Lieutenant | Specialist | Member | Freelancer | Retired
  org: ActorId?                // current organization
  formerOrgs: ActorId[]
  embodiedBy: ActorId?         // if an Individual actor embodies this character
  status: CharacterStatus      // Active | Wounded | Captured | Missing | Dead | Retired | Defected | Lost
  custody: CustodyState        // Unmaterialized | Stored | Deployed | OutOfCustody | Released | Lost
  pawn: PawnRef?               // bound at most once, never rebound to a different pawn
  boundTick: int = -1
  deploymentId: DeploymentId?  // non-null only while Deployed
  captor: FactionRef? / captorActor: ActorId?
  notability: float            // drives promotion, retention, legend candidacy
  traitsSnapshot: string[]     // defNames of notable traits, for narrative (strings, not DefRefs)
  woundedUntilTick: int = -1   // abstract recovery after store-time normalization (lifecycle § 4.3)
  flags: CharacterFlags        // NeverRematerialize, PlayerMet, NamedInLetter, …
  lastSeenByPlayerTick: int = -1
  diedTick: int = -1
  deathCauseKey: string?
  legend: LegendId?
```

When a generic member becomes a Known Character, and how custody works, is defined in
[ABSTRACT_PHYSICAL_LIFECYCLE § 2](ABSTRACT_PHYSICAL_LIFECYCLE.md#2-identity-tiers).

---

## 6. Contractor organizations (ContractorProfile)

```
ContractorProfile : ActorComponent
  templateKey: string                // content template (Def) the org was generated from; soft reference
  origin: FactionRef?                // may vanish; the org survives
  originSnapshot: string             // "a pirate band", "an outlander union splinter", …
  roster: Roster
    leader: CharacterId?
    knownMembers: CharacterId[]      // capped (default 6)
    tiers: TierCount[]               // { tier: Veteran|Regular|Recruit, healthy: int, wounded: int }
    woundedRecovery: RecoveryBucket[]// { tier, count, dueTick } — aggregated, ≤ 8 buckets
    committed: TierCount[]           // headcount checked out to operations and deployments
    capacity: int                    // soft max headcount
  equipment: EquipmentProfile
    tier: int (1..5)                 // abstract kit quality
    specialties: string[]            // "breaching", "medical", "vacuum" …
    leases: LeaseId[]                // sponsored or tracked real items (see § 11)
    condition: float (0..1)          // abstract wear, restored by upkeep and money
  doctrine: Doctrine                 // stable personality, drifts slowly from history
    caution, greed, loyalty, discretion, professionalism, ambition, cruelty: float (0..1)
  morale: OrgMorale                  // group state, NOT pawn mood
    cohesion, confidence, fatigue: float (0..1)
    lastShockTick: int
    descriptor: MoraleDescriptor     // persisted with hysteresis: Confident | Steady | Cautious | Shaken | Reckless | Exhausted | Desperate
  commitments: OperationId[]
  funds: int                         // abstract treasury (silver-equivalent)
  careerStage: CareerStage           // Rising | Established | Veteran | Declining
  retirementPressure: float
  nextUpkeepTick: int                // mirrored by a scheduler job; kept for validation
```

- **Strength** is derived and not persisted. It is computed from tiers, healthy counts,
  equipment tier and condition, and morale, and cached until the org is marked dirty.
- **Wounded** are tracked as aggregated recovery buckets, not as per-person injuries.
- **Sponsored equipment** is abstract (`tier`, `specialties`) *plus* explicit leases for items
  that matter (see § 11).
- **Source faction disappears.** `origin` resolves to missing. The org continues independently
  and history records `OriginFactionLost`.

---

## 7. Knowledge

```
KnowledgeStore: Dictionary<ActorId, KnowledgeBook>
KnowledgeBook
  entries: KnowledgeEntry[]  (cap 64; evict lowest (exp × recency))
KnowledgeEntry
  topic: TopicKey            // string, namespaced
  exp: float                 // accumulated experience
  lastTick: int              // lazy decay on read: exp × decay(now − lastTick)
```

**Topic keys** are strings, so they persist even when the referenced content is gone:

| Topic key form | Example |
|---|---|
| `thing:<defName>` | `thing:BOR_Tenebrite` |
| `factiondef:<defName>` | `factiondef:Pirate` |
| `faction:<loadID>` | `faction:12` |
| `arch:<archetypeKey>` | `arch:GuardedCache` |
| `threat:<tag>` | `threat:Mechanoid`, `threat:Insectoid` |
| `region:<regionKey>` | `region:S0:R07` |
| `layer:<layerDefName>` | `layer:Orbit` (Odyssey) |

**Region keys (geographic knowledge).** A compact, stable quantization of a tile. It is the
layer id, plus the tile's latitude band (6 bands), plus its longitude sector (8 sectors). That
gives roughly 48 regions per layer. It is computed from `WorldGrid.LongLatOf` once, when a
`TileRef` is captured. The key is stored as a string, for example `S0:R23`. There is no
information map, and nothing is computed per tick.

---

## 8. Intel, Leads and Opportunities

These are three separate things: the player's **interest**, the **reported perception** and the
**world truth**.

```
IntelRequest                                  // the player's interest
  id: IntelRequestId
  requester: ActorId                          // player proxy (later: NPC requesters too)
  source: ActorId                             // broker or Intel source actor
  topic: IntelTopic
    kind: Item | Actor | Region | Character   // Phase 1: Item only
    thing: DefRef<ThingDef>?
    quantityHint: int?                        // optional; never a guarantee
  fee: MoneyRecord
  state: IntelState                           // see STATE_MACHINES § 1
  submittedTick, dueTick, resolvedTick: int
  seed: int, rerollNonce: int
  outcome: IntelOutcome?                      // committed at resolution
    kind: Lead | NoCredibleLead
    leads: LeadId[]
    reasonKey: string?                        // for no-lead / invalidated
  confidentiality: Confidentiality            // Phase 1: Private (player and source only)

Lead                                          // perception (what the source reports)
  id: LeadId
  intel: IntelRequestId?
  opportunity: OpportunityId
  reportedBy: ActorId
  reportedTick: int
  reliability: float (0..1)                   // committed; how close reports are to the truth
  reported: LeadReport                        // what the player sees
    archetypeKey: string?                     // may be withheld or wrong (Phase 5 distortion)
    amountRange: IntRange?
    threatBand: ThreatBand?                   // Negligible | Light | Moderate | Heavy | Extreme | Unknown
    location: TileRef?                        // may be approximate before materialization
    competitionHint: string?
    expiresAroundTick: int?
  state: LeadState                            // Active | Pursued | Stale | Closed

Opportunity                                   // world truth
  id: OpportunityId
  archetypeKey: string                        // "GuardedCache", later "TraderHolds", "OrbitalWreck", …
  origin: OpportunityOrigin                   // IntelResolution | ConsequenceRule | WorldEvent | Debug
  originRef: EntityRef?                       // IntelRequest / HistoryRecord / Contract …
  lineage: { parentOpportunity?, rootContract?, depth: int }
  payload: OpportunityPayload[]               // polymorphic, small
      ItemPayload { thing: DefRef<ThingDef>, stuff: DefRef<ThingDef>?, count: int, qualityBand?: int }
      CharacterPayload { character: CharacterId, condition }        // rescue (Phase 3)
      ActorPayload { actor: ActorId, role }                         // hunt / meet (later)
  location: TileRef
  threat: ThreatSpec { points: float, factionUsed: FactionRef?, profileKey: string }
  competitors: CompetitorEntry[]              // { actor, operation?, etaTick } (Phase 4+)
  expiresTick: int
  state: OpportunityState                     // see STATE_MACHINES § 2
  site: WorldObjectRef?                       // when materialized
  engagement: { firstEngagedTick, playerClaimedCounts: ItemTally[], claimedBy: ActorId? }
  seed: int
  committedAtTick: int                        // truth fixed here; never recomputed
```

- **When cargo is fixed.** `payload` (def, count and stuff) is committed when the opportunity is
  generated. Real Things are created when the site is materialized, and placed by vanilla map
  generation from `SitePart.things`. See [SIMULATION § 6](SIMULATION.md#6-determinism-and-rng).
- **When Intel truth is fixed.** At `IntelRequest` resolution. Save and load before resolution
  does not reroll, because the seed and the frozen inputs are persisted.

---

## 9. Contracts

A contract is a **composition**. The core holds identity, kind, status and lineage. Everything
else is a part with its own small shape. A null part means that aspect does not apply; it is
not a sign of the "nullable god object" problem, because parts are cohesive groups.

```
Contract
  id: ContractId
  kindKey: string                          // "Procurement", "Recovery", "Rescue", "Hunt", … (soft ref to NetworkContractKindDef)
  status: ContractStatus                   // see STATE_MACHINES § 3
  createdTick, postedTick, awardedTick, closedTick: int
  deadlineTick: int?
  seed: int
  parties: Parties
    issuer: ActorId                        // who posts (visible)
    principal: ActorId?                    // who truly wants it (may be hidden → black contracts)
    beneficiary: ActorId?                  // who receives the result
    contractor: ActorId?                   // assigned (null before award)
    target: EntityRef?                     // victim / subject of hostile work
    interested: ActorId[]                  // watchers (competitors, patrons)
  terms: Terms                             // see § 16 Money
    price: int, deposit: int, insurance: Insurance?, penalties: PenaltyRule[]
    paymentSchedule: PaymentStep[]         // OnAward(deposit), OnDelivery(balance), …
    refundPolicyKey: string
  confidentiality: Confidentiality
    visibility: Public | Listed | Private | Confidential | Black
    issuerDisclosed: bool
    principalDisclosed: bool
    discretionRequired: float (0..1)
    exposure: ExposureState                // Unexposed | Suspected | Exposed(by, tick)
  objectives: ContractObjective[]          // polymorphic, small
      AcquireObjective { thing: DefRef<ThingDef>, stuff?, count, minQualityBand? }
      DeliverObjective { destination: DeliveryTarget }
      ReachOpportunityObjective { opportunity: OpportunityId }
      RescueObjective { character: CharacterId }              // Phase 3
      EliminateObjective / EscortObjective / StealObjective …  // later phases
  progress: ObjectiveProgress[]            // parallel to objectives: { state, amountDone, note }
  offers: OfferId[]                        // bids received (see Offer)
  refusals: Refusal[]                      // { actor, reasonKeys[], tick } capped at 12
  acceptedOffer: OfferId?
  operations: OperationId[]                // one or more attempts (inheritance / retries)
  ledger: MoneyRecord[]                    // paid, refunded, owed
  outcome: ContractOutcome?                // committed at close
    result: Fulfilled | PartiallyFulfilled | Failed | Cancelled | Expired | Voided
    causeKey: string                       // "ContractorWipedOut", "PlayerDefaulted", "DefMissing", …
    delivered: ItemTally[]
    historyRecord: HistoryRecordId?
  lineage: ContractLineage
    parent: ContractId?                    // continuation of
    root: ContractId                       // first contract of this story line
    inheritedFrom: ContractId?             // obligation taken over from another contractor
    spawnedBy: EntityRef?                  // HistoryRecord / Opportunity that caused it
    children: ContractId[]
    depth: int
  flags: ContractFlags                     // Quarantined, PlayerIssued, PlayerContractor, …

Offer
  id: OfferId
  contract: ContractId
  bidder: ActorId
  price: int, deposit: int
  etaTicks: int
  insurance: Insurance?
  riskTolerance: float                     // what the bidder is willing to face
  conditions: string[]                     // "no mechanoid targets", "cash up front", "favor cashed"
  basis: OfferBasis                        // why: relationship, knowledge, doctrine snapshot (for UI and debug)
  state: OfferState                        // Proposed | Accepted | Declined | Withdrawn | Expired | Superseded
  expiresTick: int
```

Design consequences:

- **Bidding is structural from day one.** Phase 2 may produce a single offer per contract, but
  it still flows through `offers[]` and `acceptedOffer`. No contract assumes a pre-assigned
  owner.
- **Inheritance and continuation never reopen a terminal contract.** A new contract links
  `parent`, `root` and `inheritedFrom`. Branching is therefore explicit and auditable.
- **Black and confidential work** needs no new classes: `principal` differs from `issuer`,
  visibility is Black, and `exposure` changes over time through events
  ([EVENTS_AND_HISTORY § 7](EVENTS_AND_HISTORY.md#7-facts-awareness-rumors-and-witnesses)).
- **Location and site relationships** live in objectives (`ReachOpportunityObjective`) and in
  `DeliverObjective.destination`, not in contract core fields.

`NetworkContractKindDef` (an XML Def owned by The Network, added when contracts are
implemented) holds per-kind rules and tuning: allowed objectives, base risk profile, default
terms policy, refusal sensitivities and narrative keys. Contracts persist `kindKey` as a string.
If a kind def disappears in a future version, the migration maps it or the contract is Voided
with a refund.

---

## 10. Operations

Execution is kept separate from agreement. A contract can have several operations: retries, an
inheritance by another contractor, or a rescue sub-operation.

```
Operation
  id: OperationId
  contract: ContractId
  contractor: ActorId
  kindKey: string                       // Procure | Recover | Rescue | Escort | …
  phase: OpPhase                        // Preparing | Transit | Engaged | Returning | Delivering | Done
  status: OpStatus                      // Running | Delayed | Troubled(Missing|Captured|Stranded) | Physical | Resolved | Aborted
  forces: TierCount[]                   // checked out from roster
  characters: CharacterId[]             // known characters taking part
  leases: LeaseId[]
  target: OperationTarget               // opportunity / tile / abstract source ("market", "trader network")
  startedTick: int
  checkpoints: Checkpoint[]             // { key, dueTick, done } — scheduler mirrors
  seed: int, rerollNonce: int
  frozenInputs: ResolverInputs?         // snapshot taken at engagement start (see SIMULATION § 3)
  outcome: OperationOutcome?            // committed once
    band: Triumph | Success | CostlySuccess | Partial | Failure | Disaster
    securedAmount: int
    casualties / wounded / captured / missing: TierCount[]
    characterFates: CharacterFate[]     // { character, fate }
    delayTicks: int
    extraLoot: ItemTally[]
    knowledgeGains: TopicGain[]
  physical: DeploymentId?               // when the operation went physical
```

---

## 11. Deployments and equipment leases

```
Deployment                                   // one physical appearance of an org's people
  id: DeploymentId
  org: ActorId
  operation: OperationId?
  purposeKey: string                         // "Delivery", "RescueTarget", "SiteCompetitor", "JointOp" …
  encounterFaction: FactionRef?              // temporary faction used (Phase 3)
  anchor: WorldObjectRef? / map: MapRef?
  entries: DeploymentEntry[]
      { pawn: PawnRef, character: CharacterId?, tier: Tier, leases: LeaseId[],
        fate: Pending | Returned | Killed | CapturedByPlayer | CapturedByOther | Defected |
              Missing | Lost | StillDeployed,
        reconciledTick: int }
  state: Planned | Materialized | Active | Reconciling | Closed
  openedTick, closedTick: int

EquipmentLease                               // a specific tracked item (sponsored or notable)
  id: LeaseId
  owner: ActorId                             // sponsor or org
  holder: ActorId                            // org using it
  thing: DefRef<ThingDef>, stuff: DefRef<ThingDef>?, quality: QualityCategory?, count: int
  condition: float (0..1)                    // abstract; updated from real HP% after physical use
  state: Held | Deployed | Returned | Lost | WrittenOff
  physicalThing: ThingRefSnapshot?           // thingIDNumber + label while deployed (no pointer)
  originTick: int
```

The lifecycle rules are in [ABSTRACT_PHYSICAL_LIFECYCLE](ABSTRACT_PHYSICAL_LIFECYCLE.md).

---

## 12. Relationships, obligations, contacts

```
RelationStore: Dictionary<long, RelationEdge>   // key = ((long)from << 32) | (uint)to — DIRECTED
RelationEdge
  from, to: ActorId
  standing: float (-100..100)          // like / dislike
  trust: float (0..1)                  // reliability expectation
  familiarity: float (0..1)
  updatedTick: int                     // lazy decay anchor
  counters: { jobsTogether, successes, failures, betrayals, rescues, defaults, jointOps }: int
  salient: HistoryRecordId[]           // ≤ 3 most defining shared records
  flags: Rival | Ally | Blacklisted | IntroducedBy(… see contacts) | Sponsor | Sponsee
```

- Default (unknown) pairs are **not stored**. `Get` returns a neutral, computed default.
- Decay toward neutral is applied **on read and on write**, using `updatedTick`. There is no
  periodic sweep.

```
Obligation                              // favors and debts; non-silver unless kind says so
  id: ObligationId
  debtor, creditor: ActorId
  kindKey: string                       // "favor.rescue", "favor.general", "debt.silver", "favor.introduction" …
  magnitude: int                        // 1..5 abstract size (or silver amount for debt.silver)
  createdTick, expiresTick: int (-1 = never)
  origin: HistoryRecordId?
  state: Open | Called | Honored | Defaulted | Forgiven | Expired

ContactBook                             // what the player knows
  entries: Contact[]
      { actor: ActorId, sinceTick, howKey: "Listed" | "Introduced" | "MetInField" | "Rumor",
        introducedBy: ActorId?, trustNote: float, hidden: bool }
```

The Network available to the player **is** the ContactBook. It grows through listings,
introductions and encounters.

---

## 13. History, summaries, legends

The detailed rules are in [EVENTS_AND_HISTORY](EVENTS_AND_HISTORY.md). These are the shapes:

```
HistoryRecord
  id: HistoryRecordId
  tick: int
  typeKey: string                        // e.g. "Contract.Failed", "Rescue", "Betrayal"
  schema: byte
  importance: Notable | Major | Legendary   // Minor events never become records
  participants: Participation[]          // { entity: EntityRef, roleKey: string }
  place: TileRef?  regionKey: string?
  subjectDef: DefRef<ThingDef>?
  magnitudes: RecordMagnitudes           // small fixed set: value, count, casualties, duration
  outcomeKey: string?
  causedBy: HistoryRecordId?             // causal chain for storytelling
  sourceEventSeq: long
  awareness: Awareness                   // facts vs knowledge (see EVENTS_AND_HISTORY § 7)
  narrativeSeed: int

ActorRecordSummary                        // incremental counters; systems read THESE
  actor: ActorId
  lifetime: DeedCounters                 // contractsDone/failed by kind, rescuesDone, timesRescued,
                                          // clientsAbandoned, betrayals, fastCompletions, discreetSuccesses,
                                          // exposures, casualtiesTaken, casualtiesInflicted, impossibleRecoveries …
  recent: DecayedCounters                // same keys, exponentially decayed (half-life ~1 year)
  extremes: { bestRecord, worstRecord, mostDangerousSuccess }: HistoryRecordId?
  firstTick, lastTick: int

CharacterRecordSummary                    // same idea for Known Characters (smaller)

Legend
  id: LegendId
  subject: EntityRef                     // actor or character
  name: string, epithets: string[]
  lifespanTicks: (from, to)
  fateKey: string
  deeds: LegendDeed[]                    // ≤ 7 frozen deed snapshots (text keys + args + tick)
  associates: { entity: EntityRef, name: string, relationKey }[]  (≤ 6)
  promotedTick: int
```

---

## 14. Beliefs and rumors (Phase 5 shape)

The store is reserved now so that its slot in the root layout is fixed.

```
Belief
  id: BeliefId
  holder: ActorId                     // who believes
  subject: EntityRef                  // who or what it is about
  claimKey: string                    // "BetrayedEmployer", "SurvivedImpossibleRecovery", "Rich", …
  magnitude: float
  truth: HistoryRecordId?             // null = fabricated
  distortion: None | Partial | Exaggerated | Obsolete | False | Malicious
  sourceKind: Witness | Participant | Rumor | Deduction | Introduction
  source: ActorId?
  confidence: float (0..1)
  acquiredTick: int, decayAnchorTick: int
```

Bounds: 24 beliefs per holder, lowest confidence × recency evicted. Only **non-public**
knowledge creates beliefs. Public records are known implicitly.

---

## 15. Scheduler jobs and event journal

```
ScheduledJob { seq: long, dueTick: int, kind: string, target: int, arg: int, createdTick: int }
EventJournal { entries: NetworkEvent[] (bounded), droppedCount: long }
NetworkEvent (header) { seq: long, tick: int, typeKey: string, schema: byte,
                        importance: Ephemeral|Minor|Notable|Major|Legendary,
                        subjects: EntityRef[], place: TileRef?, flags }
             + typed payload fields per event class
```

See [SIMULATION § 1](SIMULATION.md#1-scheduler) and [EVENTS_AND_HISTORY § 1](EVENTS_AND_HISTORY.md#1-network-events).

---

## 16. Money

```
MoneyRecord { tick: int, amount: int, direction: PlayerPaid | PlayerRefunded | PlayerOwes | ContractorOwes,
              medium: Silver | Favor(ObligationId) | Debt(ObligationId), noteKey: string }
Insurance { coverage: float (0..1), premium: int, coversKeys: string[] }   // e.g. "cargo", "casualty"
PaymentStep { when: OnAward | OnMilestone(key) | OnDelivery | OnClose, amount: int, state: Due|Paid|Defaulted|Waived }
```

- Silver is the medium. The `PaymentAdapter` moves real silver, and the contract ledger
  **records** each movement. A record is written in the same synchronous step as the movement,
  so neither can happen without the other.
- Deposits are **not** held as real silver. They are recorded as paid, and refunds create
  silver through drop pods.

---

## 17. Persistence conventions

1. **Values**: `Scribe_Values.Look(ref x, "x", default)` with sensible defaults. A missing node
   means "added in a later version": take the default and let migration fill in the value.
2. **Lists of records**: saved through the tolerant list helper `NetScribe.LookList`. It loads
   each element inside try/catch and routes failures to quarantine. Its feasibility is
   verified by Spike S5
   ([RIMWORLD_INTEGRATION § 5](RIMWORLD_INTEGRATION.md#5-runtime-spikes)). The fallback is
   `Scribe_Collections` plus post-load validation.
3. **Dictionaries** are saved as lists of records carrying their key, and rebuilt on load.
   `Scribe_Collections` dictionaries are brittle when keys fail to load.
4. **Polymorphism** is used only for: actor components, objectives, payloads, event payloads
   and belief subtypes. Their full type names are frozen
   ([SAVE_AND_MIGRATION § 3](SAVE_AND_MIGRATION.md#3-persisted-type-names)).
5. **Enums** persist as their names (the Scribe default). New enum members are appended, and
   renames need a migration.
6. **No derived data is persisted** unless it deliberately has inertia (reputation epithets,
   morale descriptor). Anything persisted that way is documented as such.
7. **Snapshot strings** (names, labels) are stored raw, not translated keys. Translation is
   applied only to *narrative templates*.
