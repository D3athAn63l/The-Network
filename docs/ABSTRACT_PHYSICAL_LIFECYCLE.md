# Abstract ↔ Physical Lifecycle

> **Superseded in part by [PHYSICAL_LIFECYCLE](PHYSICAL_LIFECYCLE.md) (the Phase 3 design review).** This is the Phase 0
> design, written before the code existed and before the 1.6.9676 assemblies were audited for it. Its core is
> **confirmed** (identity tiers, one pawn per character, never discarding, reconciliation from state, the registry quest).
> Where the two documents differ, **PHYSICAL_LIFECYCLE is normative**; the corrections, with evidence, are listed in its
> [Appendix E](PHYSICAL_LIFECYCLE.md#appendix-e-what-the-audit-changed-from-the-phase-0-design). In particular: **§ 5.3**
> generates pawns without `ForceGenerateNewPawn` (`GeneratePawn` can return an existing world pawn); **§ 7** omits that
> normal pawn death sends `Killed` (mid-kill); **§ 9** sets a collapsed pawn's faction "back to null" (vanilla's
> `Notify_PassedToWorld` rewrites a `Free` pawn's faction); **§ 10** reuses one temporary faction per organization
> (vanilla removes it with the episode; the review uses one per episode); and the **`Deployment`** concept and name are
> replaced by the Episode. Read this file for the rationale and the edge-case catalogue; do not implement from it without PHYSICAL_LIFECYCLE.

> The highest-risk area of the mod. It covers how an off-map contractor organization, which is
> a record with headcounts, becomes real pawns on a map, and how the results return to the
> abstract record without duplication, resurrection or leaks.
> Related: [STATE_MACHINES § 7–8](STATE_MACHINES.md#7-character-custody),
> [RIMWORLD_INTEGRATION § 2](RIMWORLD_INTEGRATION.md#2-vanilla-systems-assessed),
> [RISKS R-01/R-02/R-11](RISKS.md), [DECISIONS ADR-013/014/006](DECISIONS.md).

## Contents

1. [When the Network becomes physical](#1-when-the-network-becomes-physical)
2. [Identity tiers](#2-identity-tiers)
3. [Invariants](#3-invariants)
4. [Custody mechanism: the Network registry quest](#4-custody-mechanism-the-network-registry-quest)
5. [Materialization procedure](#5-materialization-procedure)
6. [Save and load while physical](#6-save-and-load-while-physical)
7. [Observation during physical play](#7-observation-during-physical-play)
8. [Reconciliation: deciding fates](#8-reconciliation-deciding-fates)
9. [Collapse back to abstraction](#9-collapse-back-to-abstraction)
10. [Encounter factions](#10-encounter-factions)
11. [Edge-case catalogue](#11-edge-case-catalogue)
12. [Spikes that must pass before Phase 3 builds on this](#12-spikes-that-must-pass-before-phase-3-builds-on-this)

---

## 1. When the Network becomes physical

The Network is abstract by default. Pawns exist only when the player can see or touch them.

| Scenario | Phase | Anchor | How pawns arrive |
|---|---|---|---|
| Contractor delivers goods in person | 3 | a player home map | walk-in from the map edge with a vanilla visitor-style Lord; or drop pods |
| Stranded, captured or missing contractors to rescue | 3 | a Network opportunity site | pawns placed in `SitePart.things` and spawned by vanilla map generation, like vanilla `DownedRefugee` and `PrisonerWillingToJoin` |
| Failed-expedition site (survivors, corpses, left-behind gear) | 3 | opportunity site | the same holder mechanism; corpses and gear as Things |
| Competitor crew at the same site as the player | 4+ | opportunity site | map generation plus a Lord |
| Player contractor handing over to an NPC client, or a joint operation | 4 / 6 | varies | walk-in or site |
| Meeting a broker or leader in person | 5+ | player map or site | walk-in |
| Ambient visit (passing through, stopover, resupply, trade; deferred direction) | 3+ technically, content later | player home map | the same deployment machinery with a new purpose; presentation from the contractor's mobility |

Everything else (bidding, travel, off-map combat, recovery, recruitment, succession) stays
abstract.

## 2. Identity tiers

| Tier | Representation | Persistence cost | Examples |
|---|---|---|---|
| **T0: Anonymous member** | a count in `Roster.tiers` | about 0 | the thirty rank-and-file of an org |
| **T1: Known Character (record)** | `KnownCharacter` with no pawn (`custody = Unmaterialized`) | about 400 B | the leader nobody has met, a lieutenant named in a letter, a veteran who became notable off-map, the person behind every Solo contractor and every Fixer |
| **T2: Known Character (bound pawn)** | `KnownCharacter` + `PawnRef` to a real `Pawn` | a full pawn in the save (about 10–40 KB) | anyone the player has physically met who still matters |

**Promotion from T0 to T1** happens when any of these is true:

1. The member becomes **leader** (succession) or **lieutenant** (the org needs one: each org
   keeps 1 leader and at most 2 lieutenants as Known Characters).
2. An abstract resolution produces a **notable individual fate**: sole survivor, captured,
   missing, or a heroic deed recorded as Major or higher.
3. A **consequence rule needs a named subject** (the raider leader who escaped, the captured
   medic to rescue).

**Promotion from a generic physical pawn to a Known Character** (at reconciliation) happens when
any of these is true:

1. The player **captured, recruited, rescued or enslaved** them.
2. They were **named in a letter**, or were the subject of a Network event.
3. They **killed or downed a colonist**, or were downed by one and survived (the player will
   remember them).
4. Vanilla now considers them important: they are in the PlayLog or BattleLog with colonists,
   or have a relation to a colonist. We check `PawnUtility`/`relations` at reconciliation.
5. They performed a notable action from the encounter's heuristics, for example the most kills
   on their side.

**Binding (T1 → T2)** happens only through `Custody.Materialize`. It is **permanent**: a
character is bound to one pawn for life, and never rebound to a different pawn.

Caps: at most 6 Known Characters per org (leader + 2 lieutenants + 3 notable). There is also a
global **soft** cap of about 150 bound pawns. The cap is a **performance policy, not an identity
invariant**. Under capacity pressure, only **safe, dormant, low-notability** T2 characters are
**retired to T1-with-release**: the record stays and the pawn is released to vanilla. Such a
character is marked `neverRematerialize`, and if needed later they are written out of the story
("left the trade").

A character is **protected from release** while any of these holds:

- current organization leader (or a lieutenant in line to succeed);
- part of an active contract, operation or opportunity, or deployed;
- captured, missing or stranded with an unresolved story state;
- an active rival, or the subject of an active relationship thread (open obligation, salient
  grudge, an ongoing player relationship);
- a major character encountered recently;
- referenced by a pending follow-up (a consequence job, a lead, a rescue);
- depended on by another vanilla system right now (quest reservation by another quest, a Lord,
  a caravan, a relation to a colonist).

If no unprotected candidate exists, the Network **temporarily exceeds the soft cap** rather than
break story continuity, and the validator reports the overshoot (it is not an error).

## 3. Invariants

| # | Invariant | Enforced by |
|---|---|---|
| I-1 | A `CharacterId` is bound to **at most one Pawn, ever**. | `Custody.Bind` refuses if `pawn` is already set. There is no API to rebind. |
| I-2 | A `Pawn` is bound to **at most one CharacterId**. | reverse map `Pawn → CharacterId` checked in `Bind`; validator on load |
| I-3 | Materializing a character that already has a live bound pawn **reuses** that pawn. | `Materialize` returns the existing pawn when `custody ∈ {Stored}` |
| I-4 | A character can be in **at most one active Deployment**. | `BeginDeployment` refuses if `custody = Deployed`; the planner excludes deployed characters |
| I-5 | A **dead** character is never materialized or deployed again. Resurrection is observed, never initiated. | `status = Dead` is a terminal guard in Custody. Vanilla resurrection of the actual pawn is detected by reconciliation (§ 11). |
| I-6 | Headcount checked out to a deployment is **not available** to other operations until reconciled. | `Roster.committed` |
| I-7 | Every materialized generic pawn belongs to exactly one `DeploymentEntry`. | created only inside `BeginDeployment` |
| I-8 | **The Network never destroys or discards pawns.** It only (a) reserves, (b) releases, (c) passes to world with `Decide`. | code rule, enforced in review; the only `PassToWorld` call site is in Custody |
| I-9 | Deaths and captures are applied to the org **exactly once**, when the deployment entry's fate first leaves `Pending`. | fate is set-once; `Deployment.Reconciled` publishes the delta |
| I-10 | Signal handlers act only on the **bound** pawn object. A pawn that merely carries a `TheNetwork.*` quest tag (for example a copy made by another mod) is ignored and has the tag stripped. | handler compares the `SUBJECT` pawn with the binding maps |

## 4. Custody mechanism: the Network registry quest

### 4.1 The problem

Vanilla RimWorld treats an unspawned pawn as a **world pawn**. Three vanilla behaviours make a
plain world pawn unsafe as a persistent Known Character:

1. **Garbage collection.** `WorldPawnGC` discards pawns that have no "critical reason"
   (`WorldPawnGC.GetCriticalPawnReason`). A pawn referenced *only* by our WorldComponent has no
   such reason and would be discarded. Our `PawnRef` would then fail to resolve on the next load.
2. **Redress (reuse).** `PawnGenerator` reuses existing world pawns in the `Free` situation for
   new raids, visitors and traders (`PawnGenerator.GetValidCandidatesToRedress`,
   `ChanceToRedressAnyWorldPawn`). **A pawn pinned with `PawnDiscardDecideMode.KeepForever` is
   still `Free`.** It is safe from GC but can still be pulled into a random raid of its faction.
3. **Ticking cost.** Unspawned world pawns tick, and pawns with non-permanent hediffs are not
   mothballed (`WorldPawns.ShouldMothball`). Keeping many injured contractor pawns as plain
   world pawns costs TPS.

### 4.2 The solution

The Network creates **one hidden, permanent, Network-owned Quest**, the "registry quest". It
contains one `QuestPart_NetworkRegistry` whose `QuestPartReserves(Pawn)` returns true for every
pawn in Network custody (`Stored` or `Deployed`). The lookup is O(1) against a HashSet.

Vanilla consequences, verified statically in the 1.6 assemblies:

| Vanilla code path | Effect of reservation |
|---|---|
| `WorldPawns.GetSituation` → `WorldPawnSituation.ReservedByQuest` | the pawn is no longer `Free` |
| `PawnGenerator.GetValidCandidatesToRedress` (only `Free`, plus `FactionLeader` for leader kinds) | **not reused** for raids or visitors |
| `WorldPawnGC.GetCriticalPawnReason` → `"ReservedByQuest"` | **never garbage-collected** |
| `Pawn.Suspended` (true when `ReservedByQuest`) | off-map **needs, health, aging and jobs are skipped** (`Pawn.Tick` checks `Suspended`). Healthy pawns are also **mothballed** by vanilla (`WorldPawns.DoMothballProcessing`, every 15,000 ticks), so they leave the per-tick loop entirely. See § 4.3 for keeping stored pawns mothball-eligible. |
| `QuestUtility.IsReservedByQuestOrQuestBeingGenerated` | other quests will not pick the pawn (for example as a lodger or a kidnap target) |
| Health ticking skipped while suspended | no random hediffs accrue while stored. The per-def `HediffGiver.allowOnQuestReservedPawns` flag defaults to **true**, so it is not what protects them; suspension is. |
| `QuestManager.Notify_PawnKilled` (Ongoing quests) → `QuestPart.Notify_PawnKilled` | **death notification anywhere**, including off-map |
| `QuestManager.Notify_PawnDiscarded` → `QuestPart.Notify_PawnDiscarded` | a pawn discarded by vanilla or another mod is noticed immediately, and the character becomes `Lost` |
| `QuestPart.Notify_FactionRemoved` | temporary encounter faction removal is noticed |
| `QuestPart.ReplacePawnReferences` | a vanilla pawn replacement (for example a faction leader replacement) is noticed |

**Requirements from vanilla's quest handling** (these determine how the quest must be built):

- `QuestManager.ExposeData` **drops quests whose `root` is null on load** (with an error), and
  `StorytellerComp_RandomEpicQuest` dereferences `root.IsEpic`. The registry quest therefore
  needs a **Network-owned `QuestScriptDef`** as `root`: `TheNetwork_Registry`, with no
  generation nodes, `isRootSpecial`-style flags off, and never offered by the storyteller.
- It is created with `Quest.MakeRaw()`, `hidden = true`, `SetInitiallyAccepted()` (so it is
  `Ongoing` and receives kill notifications) and `Find.QuestManager.Add`. It never ends.
- `MainTabWindow_Quests` hides `hidden` quests unless dev "show all" is on.

**Mod removal:** the registry quest's root def and part class are missing, so vanilla logs about
two errors and drops the quest. Previously reserved pawns become ordinary `Free` world pawns,
which GC may discard over time. This is acceptable: the game stays consistent.

**Spike S9 must confirm this whole section at runtime before Phase 3 depends on it.** The
fallback, if the registry quest proves unworkable, is `KeepForever` plus a **factionless**
storage state for stored pawns. Redress requires a faction match unless the request sets
`WorldPawnFactionDoesntMatter`, so it rarely applies to factionless pawns. That combination
still ticks injured pawns and still leaves a small redress window, and those residual risks
would be documented in [RISKS](RISKS.md). A Harmony patch is the last resort
([RIMWORLD_INTEGRATION § 3.3](RIMWORLD_INTEGRATION.md#33-contingency-patches-analysed-not-adopted)).

### 4.3 Consequences of suspension (frozen pawns)

**Store-time normalization.** A suspended pawn never heals. Vanilla will not mothball a world
pawn that has non-permanent hediffs (`WorldPawns.ShouldMothball` / `DefPreventingMothball`), so
a stored pawn that is still injured would stay in the per-tick world-pawn loop forever. It would
be cheap, because it is suspended, but not free: `Pawn.Suspended` itself queries the
world-pawn situation each tick. When a Known Character enters `Stored`, the Network therefore:

1. converts its current **non-permanent injuries** into abstract recovery:
   `KnownCharacter.woundedUntilTick` is derived from total injury severity, and status becomes
   `Wounded`;
2. heals those injuries on the pawn through vanilla hediff APIs. Permanent injuries, scars,
   missing parts, implants, chronic conditions and addictions are left untouched, because they
   are part of the character;
3. leaves any modded hediff that still blocks mothballing alone. The pawn is then merely
   suspended, and the Phase 3 soak test measures this residual cost.

A character in `Wounded` status cannot be deployed before `woundedUntilTick`. The exception is
scenarios that need them, such as rescue targets, which re-apply seeded injuries explicitly
(§ 5.5).

Stored pawns do not age or satisfy needs either. When a stored pawn is materialized, **catch-up**
is applied:

- **Healing:** already done at store time (above). Materialization only checks
  `woundedUntilTick`. (Spike S12 confirms the exact hediff APIs for both steps.)
- **Age:** biological age is advanced by the elapsed ticks, capped per event to avoid pushing
  the pawn through several life stages at once. This matters for decade-long saves. Vanilla
  birthdays and life-stage effects run once through normal ticking after spawn.
- **Needs:** reset to comfortable defaults. The org fed them off-map.
- **Equipment:** reconciled with the org's current equipment profile and leases (§ 5.4).

## 5. Materialization procedure

`Custody.BeginDeployment(org, purpose, anchor, composition) → DeploymentId`

### 5.1 Plan

1. Validate that the org is Active, not Exhausted (unless the purpose requires it, as for
   rescue targets), and that its forces are available (`Roster.healthy − committed`).
2. Choose the **composition**: Known Characters, selected by purpose (leader for meetings,
   captured characters for rescues), plus a generic count by tier.
3. **Check out** the headcount and characters (`Roster.committed += …`; each character becomes
   `Deployed`). Create the `Deployment` in the `Planned` state.

### 5.2 Faction

Resolve the **encounter faction** through `EncounterFactionAdapter`, described in § 10. All
materialized pawns get this faction. Organization membership is **never** inferred from
`pawn.Faction`. It lives in the `DeploymentEntry` and `KnownCharacter.org`.

### 5.3 Pawns

For each Known Character:

- `custody = Stored` → take the existing pawn (I-3), apply catch-up (§ 4.3), `SetFaction(encounterFaction)`.
- `custody = Unmaterialized` → generate:
  - `PawnGenerator.GeneratePawn(request)` under `Rand.PushState(Hash(character.seed, "materialize"))`.
  - Kind from `character.kindDef`, falling back to the org template's preferred kinds, then to
    vanilla defaults.
  - Apply the name snapshot (`pawn.Name = NameTriple(...)`) so the pawn matches the record. The
    name is part of the identity that has already appeared in history and letters.
  - Gender and age are from the snapshot where the request allows it.
  - Then `Custody.Bind(character, pawn)` (I-1, I-2) and reserve the pawn in the registry.

For each generic slot:

- Generate a pawn the same way, from the org template's kind for that tier.
- Seed: `Hash(deployment.seed, slotIndex)`.
- **Not** bound to any character. It is reserved in the registry as `deployment-scoped` until
  reconciliation, so it cannot be GC'd or redressed in between (for example while waiting in a
  `SitePart.things` holder before map generation).

For every materialized pawn, add quest tags `TheNetwork.Dep.<deploymentId>` and, for known
characters, `TheNetwork.Char.<characterId>` (`QuestUtility.AddQuestTag`).

### 5.4 Equipment and leases

- **Generic gear** comes from vanilla kind generation. Its quality is nudged by
  `EquipmentProfile.tier` through the request's gear budget.
- **Leased items** (sponsored or notable, `EquipmentLease`) become real Things:
  `ThingMaker.MakeThing(def, stuff)` with quality and `HitPoints = condition × MaxHitPoints`.
  They are equipped on or given to a designated pawn and tagged `TheNetwork.Lease.<leaseId>`.
  Lease state becomes `Deployed`, and `physicalThing` = `thingIDNumber` plus label.
- **Missing lease def** (a mod was removed): the lease becomes `WrittenOff`, the pawn uses
  generic gear, and one info note is recorded.

### 5.5 Injuries

- Known Characters carry their own real hediffs, frozen while stored.
- Generic pawns are drawn from **healthy** headcount only. The wounded stay home. For the
  "stranded or captured" rescue scenarios, the scenario applies injuries explicitly through
  vanilla damage APIs, seeded.

### 5.6 Placement by anchor type

| Anchor | Mechanism | Holder before spawn | Notes |
|---|---|---|---|
| Opportunity site (map not yet generated) | add the pawns to a `SitePart.things` ThingOwner, as vanilla `GenStep_DownedRefugee` and `GenStep_PrisonerWillingToJoin` do, with a Network GenStep or the vanilla step, depending on Spike S11 | the site part | if the site is destroyed first, `SitePart.PostDestroy` calls `ClearAndDestroyContentsOrPassToWorld`, which passes the pawns to world. They are still reserved, reconciliation sees "never spawned", and fate becomes `Returned` |
| Player map (delivery, visit) | `PawnsArrivalModeDef` edge walk-in plus a vanilla Lord (a visitor or trader LordJob, selected by Spike S14) | none | the Lord owns the pawns while spawned. On leaving, `Pawn.ExitMap` sends them to the world (still reserved) |
| Player map (drop) | `DropPodUtility.DropThingsNear` with the pawns | the drop pod | transporter handling is vanilla |
| Existing generated site map | direct `GenSpawn.Spawn` near the edge plus a Lord | none | `Pawn.SpawnSetup` removes the pawn from world pawns automatically |

The deployment moves to `Materialized`, and becomes `Active` once the first entry spawns.

## 6. Save and load while physical

All state is persistent, and vanilla already saves the pawns wherever they are:

| Where the pawn is at save time | Who saves it | Our reference |
|---|---|---|
| on a map | the map | `PawnRef` (cross-ref) |
| in a `SitePart.things` holder | the site | `PawnRef` |
| in a caravan (player's, for example an escort) | the caravan | `PawnRef` |
| in a drop pod or travelling transporter | the world object | `PawnRef` |
| a world pawn (stored or returned) | `WorldPawns` | `PawnRef` |

On load, cross-refs resolve pointers. The first-tick reconciliation then walks every
**non-Closed deployment** and every **bound character** and checks:

- A null pointer after load means the pawn was discarded while we did not reserve it. That only
  happens if another mod interfered. The entry becomes `Lost` and the character becomes `Lost`
  (history note "vanished"). One aggregated warning is logged.
- Registry reservations are re-established from the `CharacterStore` and `DeploymentStore`.
  The quest part's HashSet is runtime-only and rebuilt from our stores. It is never persisted in
  the quest, so the stores are the single source of truth.
- Quest tags are checked (re-added if they are missing).
- `SignalBridge` is re-registered with `SignalManager`. Receivers are not persisted by vanilla.

Saving *during* a Network synchronous step cannot happen: saves run between ticks
([EVENTS_AND_HISTORY § 1.4](EVENTS_AND_HISTORY.md#14-no-double-application-after-save-and-load)).

## 7. Observation during physical play

These sources are used together. None is trusted alone.

| Source | What it tells us | Harmony? |
|---|---|---|
| Quest-tag signals via `SignalManager` (`TheNetwork.Dep.*`, `.Char.*`, `.Lease.*`, and site `TheNetwork.Opp.*`) | `Despawned`, `LeftMap`, `Recruited`, `Arrested`, `Rescued`, `Released`, `Kidnapped`, `Banished`, `Enslaved`, `ChangedFaction*`, `BecameMutant`, `TookDamageFromPlayer`, `Destroyed`; on sites: `MapGenerated`, `MapRemoved`, `Destroyed` | no |
| Registry quest part | `Notify_PawnKilled` (death anywhere), `Notify_PawnDiscarded`, `Notify_FactionRemoved` | no |
| Injected site `WorldObjectComp` | `PostMapGenerate`, `PostCaravanFormed`, `PostMyMapRemoved`, `PostDestroy` | no |
| **Reconciliation** (source of truth) | the actual state of each pawn (see § 8), evaluated on deployment triggers, by the watchdog every 2,500 ticks while Active, and on load | no |

Signals are only **wake-ups**. Every signal handler calls `Reconcile(entry)`, which reads real
pawn state. Missing a signal (for example a mod that moves pawns without firing it) therefore
only delays the result until the watchdog runs. It cannot corrupt state.

**Player actions during the encounter:**

| Action | Detected by | Result |
|---|---|---|
| Player attacks the contractors | `TookDamageFromPlayer` | a `Player.Hostility` note on the deployment. At reconciliation this becomes an event: a relation hit, and possibly `Player.BetrayedContractor` if they were under contract to the player |
| Player arrests or captures one | `Arrested` / `Rescued` then prisoner status | fate `CapturedByPlayer` |
| Player recruits a prisoner or guest | `Recruited` | fate `Defected`; the character remains a Known Character with custody `OutOfCustody` |
| Player strips or loots gear | at reconciliation, lease items are not with the pawn | leases `Lost`; the sponsor is notified |
| Player trades with them (walk-in traders) | vanilla trade | not tracked, except delivered goods, which are handled by delivery logic |

## 8. Reconciliation: deciding fates

`Reconcile(entry)` reads real pawn state in this **priority order**. The first match wins, and
fate is set **once** (I-9).

| # | Observed pawn state | Fate | Abstract effect |
|---|---|---|---|
| 1 | pointer null, or `pawn.Discarded`, or (`Destroyed` and not `Dead`) | `Lost` | headcount −1 (or the character becomes Lost). Note: vanished |
| 2 | `pawn.Dead` | `Killed` | headcount −1 by tier, or `KnownCharacter.Killed` (+ `Leader.Killed`). The corpse is left to vanilla |
| 3 | `pawn.IsPrisonerOfColony` or held by the player's faction as prisoner or slave | `CapturedByPlayer` | headcount −1. The character becomes Captured by the player (promoted if generic, § 2). A rescue or ransom story may follow |
| 4 | `pawn.Faction == Faction.OfPlayer` (recruited) | `Defected` | headcount −1. Relation hit. The character becomes Defected |
| 5 | kidnapped by a third faction (`faction.kidnapped` contains the pawn) or prisoner of a non-player map | `CapturedByOther` | headcount −1. `Contractor.Captured` with the captor. A rescue follow-up may fire |
| 6 | `pawn.Spawned` or in a caravan or transporter still travelling | `StillDeployed` | wait (the watchdog continues) |
| 7 | world pawn, alive, and faction is the encounter faction or null (left the map normally or was passed to world at map removal) | `Returned` | headcount back to healthy (or wounded if injured: the injury severity maps into a recovery bucket). Known characters go back to `Stored` |
| 8 | anything else (another faction took them, another mod's holder, …) | `Missing` | headcount −1 now. The character becomes Missing and is re-checked by the audit. It can later resolve to Returned, Lost or Killed |

When every entry has a final fate, the deployment moves to **Closed**. The engine then publishes
`Deployment.Reconciled` with aggregated deltas, plus per-character events, and returns the
operation (if any) to the abstract flow.

## 9. Collapse back to abstraction

For each **Returned** entry:

- **Known Character**: re-reserve. `custody = Stored`. Apply store-time normalization (§ 4.3):
  real injuries become `woundedUntilTick`, and permanent ones stay on the pawn. Set the faction
  back to null, or keep the encounter faction if it still exists. On the next materialization,
  § 4.3 catch-up applies.
- **Generic pawn** that meets a promotion rule (§ 2): promote to a Known Character (bind and
  store).
- **Generic pawn** otherwise: **collapse**. Remove its reservation, strip the Network quest
  tags, and leave it to vanilla as an ordinary world pawn (`Decide` mode). Its tier count
  returns to the roster, and the org does not reference this pawn again. If vanilla later
  redresses it into a random raid, that is a different, anonymous person as far as the Network
  is concerned. Nothing is duplicated, because the Network no longer counts it.
  - *Why not discard?* Vanilla may still need the pawn: relations with colonists, PlayLog
    entries, tales, lords or quests (GC keep reasons). Releasing it to vanilla GC is always
    safe. Discarding it ourselves could break references (I-8).

For **leases**: if the leased Thing is with a Returned pawn, the lease becomes `Held`, with
`condition = HitPoints / MaxHitPoints` and quality unchanged. The real Thing is then
**destroyed** (Vanish), because the abstract lease represents it again. **This is the only
place the Network destroys a Thing, and it only destroys Things it created and tagged.** If the
Thing is elsewhere (on a map, carried by a colonist), the lease becomes `Lost`, the Thing stays
in the world as ordinary loot, and the sponsor gets `SponsorshipChanged`.

**Corpses**: vanilla owns them. Our record says Killed. A corpse left on a player map can be
buried, butchered or stripped with no effect on us. A resurrection of that corpse is covered in
§ 11.

## 10. Encounter factions

**Organizations are not RimWorld factions** ([DECISIONS ADR-006](DECISIONS.md)). Physical pawns
still need a faction for hostility, AI and UI.

- **Mechanism:** one **temporary faction per organization**, created on the org's first
  physical deployment through `FactionGenerator.NewGeneratedFactionWithRelations`. It uses a
  vanilla FactionDef chosen by the org template (for example an outlander-style def for
  professional crews, a pirate-style def for criminal ones), with the org's name,
  `temporary = true`, and optionally `hidden` to keep the faction tab tidy (Spike S10). Initial
  goodwill toward the player comes from the relation edge. Relations to other factions follow
  the org's origin and doctrine.
- **Reuse:** the org record keeps `encounterFaction: FactionRef`. While that faction still
  exists, it is reused, so goodwill history carries across visits.
- **Removal:** vanilla removes temporary factions when no spawned or travelling pawns and no
  world objects belong to them, and no quest reserves them (`FactionManager.FactionCanBeRemoved`).
  Our stored pawns are world pawns, which do not keep the faction alive. When vanilla removes
  it, their faction is set to null (`FactionManager.Remove`), which is harmless because
  membership is ours. `Notify_FactionRemoved` (registry part) clears the `FactionRef`, and the
  next deployment creates a new temporary faction. **Goodwill with the player is mirrored into
  our relation edge at reconciliation**, so it survives faction recreation.
- **Hostility changes mid-encounter** (the player attacks them, or goodwill drops below hostile
  in the encounter faction): vanilla AI handles combat. Reconciliation records what happened.
- **Rejected alternatives:** one permanent faction per org (the faction count grows without
  bound, permanent factions cannot be removed, and world generation assumes a fixed set); a
  shared hidden "Network" faction (every org would share one relation with the player, so
  per-org hostility would be impossible); borrowing the origin faction (hostility would leak to
  the whole parent faction, and it breaks when the origin is gone).

## 11. Edge-case catalogue

| Case | Handling |
|---|---|
| **Pawn duplication (same character instantiated twice)** | I-1/I-3/I-4. `Materialize` returns the existing pawn, and a deployed character cannot join a second deployment. The validator checks that the reverse map is one-to-one. |
| **Anomaly-style duplication or another mod copying a pawn** (the copy inherits quest tags) | I-10. Handlers compare against bound objects, strip our tags from unbound copies, and do not track the copy. |
| **Save and load while physical** | § 6. |
| **Map removal** (the site is abandoned) | `MapDeiniter` passes non-player pawns to the world (still reserved). The `MapRemoved` signal and comp `PostMyMapRemoved` trigger reconciliation, and entries resolve as Returned, Killed or Captured. Colonists left on a hostile map are kidnapped by vanilla, which is not our concern. |
| **Caravan escape** (the player flees with a contractor in the caravan, for example a rescued prisoner) | The pawn is in a caravan, which is `StillDeployed` (entry 6), and the watchdog keeps checking. When the caravan arrives home and the pawn is released to its org (rescue contract completion), the entry becomes `Returned` and the pawn is re-stored. |
| **Capture by a third-party faction** | Kidnapped-pawn tracking (the `Kidnapped` signal plus reconciliation) gives fate `CapturedByOther`, a Major event, and a rescue follow-up rule. |
| **Corpse persistence** | Vanilla owns corpses. Our record is final at death. If the corpse is destroyed, nothing changes. If the corpse holds leased items, the lease becomes Lost unless the corpse is returned with the org (not modelled). |
| **Resurrection of a dead Known Character** (serum, mech-resurrector, a shambler rising) | We never initiate it. Observed through signals (`BecameMutant`) or reconciliation, where a character with `status = Dead` has a pawn that is not `Dead`: history records "returned from death". The character does **not** rejoin the org automatically; custody is `OutOfCustody`. Its future is a content decision (a legend rumor, a haunted crew). The invariant "never materialize a dead character" still holds. |
| **Equipment left behind** | § 9 leases. Non-leased generic gear is ordinary loot. |
| **Player recruits a contractor** | Fate `Defected`. The character stays Known, custody `OutOfCustody` (not reserved; vanilla treats them as a colonist, and GC keeps them as `Colonist`). The org's relation to the player takes a hit, unless the recruitment was a rescue the org wanted. Recruiting a character under contract to the player is `Player.BetrayedContractor`. |
| **Player sells a captured contractor to a trader or releases them** | The pawn leaves the player (a signal), and reconciliation of the character (not the deployment) gives: released means back to the org, custody `Stored` if the pawn reaches the world; sold means `Missing` (with a possible slave-rescue follow-up). |
| **Contractor becomes permanently world-persistent for vanilla reasons** (relations with colonists, tales, logs) | Irrelevant to us once released. Vanilla keeps it. We hold no reference unless the pawn was promoted, and duplication is impossible because counting is ours. |
| **References from quests, lords or factions prevent safe disposal** | We never dispose (I-8). Lords end when the pawn leaves the map or the map is removed. Our registry reservation is removed at release. Other quests' reservations are theirs. |
| **Another mod discards a reserved pawn** | `Notify_PawnDiscarded` or a null pointer marks the character `Lost`. One warning. History: "vanished". |
| **Faction leader collision** (a pawn becomes the temporary faction's leader) | Temporary factions generated for encounters get their own leader pawn from `FactionGenerator`. We never set our characters as faction leaders. If vanilla picks one (for example leader replacement), `ReplacePawnReferences` and reconciliation note it. Faction-leader world pawns are `FactionLeader` situation, not `Free`, so they are still safe from redress. |
| **Mod removal while physical** | Pawns stay in vanilla (on maps, in world pawns). The registry quest is dropped, and pawns become ordinary. Leased Things remain as normal items. No errors beyond the known "missing class/def" lines. |
| **Encounter map is the player's home and the player abandons it** | Map removal handling applies. Entries resolve from pawn state, which is usually Returned (passed to world). |
| **Pawn enslaved by the player** | Fate `CapturedByPlayer` (slave variant). Later emancipation leads to Released, then back to the org or Defected, depending on the pawn's faction. |

## 12. Spikes that must pass before Phase 3 builds on this

| Spike | Question | Pass criteria |
|---|---|---|
| **S9** Registry quest | Can a raw hidden quest with a Network `QuestScriptDef` root and a reserving part live forever, survive save and load, and produce suspension, GC protection, redress exclusion, and kill and discard notifications? | 1,000 stored pawns: no GC over 30 in-game days; none reused by 200 generated raids; `Suspended == true`; kill notification for an off-map death forced by a dev action; clean save and load; mod-removal error count ≤ 3 |
| **S10** Temporary encounter factions | Creation outside QuestGen, naming, hidden flag, goodwill seeding, automatic removal, and our pawns becoming factionless on removal | create, visit, leave, remove, recreate: no errors; goodwill mirrored |
| **S11** Site-part pawn holder | Pawns in `SitePart.things` spawn at map generation with a Lord and survive save and load before generation; site destruction passes them to world | same as vanilla `DownedRefugee` behaviour; reconciliation gives Returned |
| **S12** Catch-up | Healing elapsed injuries and advancing age on a suspended pawn at materialization | pawn is valid after catch-up; no hediff errors; life stage correct |
| **S14** Walk-in visit | A vanilla LordJob for arriving, trading or delivering, then leaving; hostility flip mid-visit | pawns leave through the edge, reach world pawns and reconcile as Returned; hostility is handled by vanilla AI |
| **S17** Tag hygiene | Our quest tags on pawns and things cause no side effects in vanilla quest code, and copies are handled | there are no vanilla code paths that parse tags as `Quest<N>` (confirmed statically). A runtime check with the Anomaly duplication path, if Anomaly is present. |

If S9 fails, Phase 3 uses the documented fallback (§ 4.2), and the risk register is updated
before any content is built on top of it.
