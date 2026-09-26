# RimWorld 1.6 Integration

> Findings from inspecting the **actual RimWorld 1.6 assemblies** (`Assembly-CSharp.dll`,
> build dated 2026-07-02, decompiled with ILSpy 8.2) and the 1.6 Core/DLC XML. For each vanilla
> system: what The Network reuses, what it avoids, where coupling is dangerous, and what needs an
> adapter or a runtime spike. File references (`File.cs:line`) are relative to the decompiled
> source tree and are given so the facts can be re-checked after game updates.

## Contents

1. [Method and scope](#1-method-and-scope)
2. [Vanilla systems assessed](#2-vanilla-systems-assessed)
3. [Harmony policy](#3-harmony-policy)
4. [Summary matrix](#4-summary-matrix)
5. [Runtime spikes](#5-runtime-spikes)

---

## 1. Method and scope

- The decompiled `Assembly-CSharp` contains all DLC code (Royalty, Ideology, Biotech, Anomaly,
  Odyssey). DLC content is gated at runtime by `ModsConfig.*Active` and by Defs that exist only
  when the DLC is loaded. **Optional DLC support therefore never needs a separate assembly.**
- Reference mods (Grandmaster21, Parametric) were read for repository and persistence
  conventions only. The Network does not depend on them.
- Anything that can only be confirmed by running the game is listed as a **spike** in § 5 and is
  never assumed.

---

## 2. Vanilla systems assessed

### 2.1 WorldComponent — **REUSE (persistence root)**

- Instantiated by reflection for every non-abstract subclass (`World.FillComponents`,
  `World.cs:184`). Adding the mod to an existing save therefore creates the component
  automatically.
- Saved in `World.ExposeComponents` **after** `factionManager`, `worldPawns` and `worldObjects`
  (`World.cs:126–136`). Cross-references to pawns and factions resolve normally.
- A missing class on load (the mod was removed) produces one "could not find class" error from
  the deep-load path. The null is removed (`components.RemoveAll(null)`) and the game continues.
- `FinalizeInit(bool fromLoad)`: for **new worlds** it is called from
  `WorldGenerator.cs:67` **during world generation**, before the colony and scenario exist. On
  load it is called from `Game.LoadGame` (`Game.cs:586`). **Consequence:** the bootstrap runs
  lazily on the first `WorldComponentTick`, not in `FinalizeInit` ([ARCHITECTURE § 6.1](ARCHITECTURE.md#61-networkworldcomponent-kernel-root)).
- `WorldComponentTick` is called every tick from `World.WorldTick` (`World.cs:220`).
- A WorldComponent may implement `IThingHolder` (`World.GetChildHolders`, `World.cs:465`). This
  is **not used**: holding pawns ourselves would take them out of vanilla's pawn systems, and
  they would vanish with our mod.

### 2.2 GameComponent — **NOT USED for state**

It offers `StartedNewGame` and `LoadedGame` hooks, but a second persisted component doubles the
mod-removal errors and splits ownership. Lazy start on the first tick covers the same need. It
may be reconsidered only if a spike shows first-tick bootstrap is too late for some UI path. It
is not, because UI read models tolerate a not-yet-bootstrapped state.

### 2.3 Quest, QuestPart, QuestScriptDef, Slate, QuestGen — **LIMITED REUSE (registry quest, Phase 3)**

Facts:

- `Quest.MakeRaw()` (`Quest.cs:240`) creates a quest without QuestGen.
- **`QuestManager.ExposeData` removes quests with a null `root` on load**, logging an error
  (`QuestManager.cs:181`). `StorytellerComp_RandomEpicQuest.cs:17` dereferences `root.IsEpic`
  on every quest. A raw quest therefore **must** have a `QuestScriptDef` root.
- `hidden` quests are excluded from the Quests tab (`MainTabWindow_Quests.cs:1275`) and from
  quest letters. Hiding is supported vanilla behaviour.
- `QuestPart` extension points: `QuestPartReserves(Pawn/Faction/TransportShip)`,
  `Notify_PawnKilled`, `Notify_PawnDiscarded`, `Notify_FactionRemoved`, `ReplacePawnReferences`,
  `Notify_QuestSignalReceived`, `Cleanup` (`QuestPart.cs`).
- `QuestManager.Notify_PawnKilled` notifies only **Ongoing** quests (`QuestManager.cs:239`).
  The registry quest must be `SetInitiallyAccepted()`.
- Quest reservation drives GC protection, redress exclusion and **suspension**
  (`Pawn.Suspended`, `Pawn.cs:1112`), described in
  [ABSTRACT_PHYSICAL_LIFECYCLE § 4](ABSTRACT_PHYSICAL_LIFECYCLE.md#4-custody-mechanism-the-network-registry-quest).

Verdict:

- **Do not model Network contracts or opportunities as vanilla quests.** Vanilla quest state,
  expiry, accept/decline flow, QuestGen slate and grammar would *own* our lifecycle. Cleanup
  semantics could destroy our sites or pawns. Other mods iterate and modify quests. Our
  lifecycles (bidding, inheritance, Troubled) do not fit the vanilla quest states. Master § 87
  asks for Quest/QuestPart/Slate "where practical"; quest ownership is not a practical match,
  as [ADR-025](DECISIONS.md#adr-025--network-lifecycles-are-not-vanilla-quests) records
  (accepted after owner review). Everything else § 87 lists (Site, SitePart, WorldObject,
  Faction, Letter, caravans and transport) is used.
- **Use one hidden registry quest** purely as a vanilla-native custody anchor (Phase 3, Spike S9).
- **Optional later:** a "Quests tab mirror" presentation adapter that creates a hidden or
  visible *informational* quest for an opportunity. It would never own state. Deferred and
  optional.
- QuestGen and Slate are avoided entirely. Generation is C#, with seeded vanilla calls wrapped
  in `Rand.PushState`.

### 2.4 WorldObject lifecycle — **REUSE (vanilla objects plus an injected comp)**

- `WorldObject.ExposeData` calls `InitializeComps()` from **the def's current comp list** and
  then each comp's `PostExposeData` (`WorldObject.cs:262–305`). **A comp we add to a vanilla
  WorldObjectDef through an XML patch stores its fields inline. If our mod is removed, the def
  no longer lists the comp and those XML nodes are silently ignored: no error.** This is the
  most removal-safe hook available.
- Hooks available to a `WorldObjectComp`: `PostMapGenerate`, `PostCaravanFormed(Caravan)`
  (called from `MapParent.Notify_CaravanFormed` through `CaravanExitMapUtility.cs:66`),
  `PostMyMapRemoved`, `PostDestroy`, `CompInspectStringExtra`, `GetFloatMenuOptions(Caravan)`,
  `GetGizmos`.
- World objects send quest-tag signals `Spawned`, `Despawned`, `Destroyed`
  (`WorldObject.cs:454/489/510`). A `MapParent` also sends `MapGenerated` and `MapRemoved`
  (`MapParent.cs:58/72`).
- `WorldObject.Destroy` calls `FactionManager.Notify_WorldObjectDestroyed`, which can trigger
  removal of a temporary faction.
- `ID` is an int, unique per save. We persist it in `WorldObjectRef`.

### 2.5 Site, SitePart, SitePartDef, SiteMaker — **REUSE (vanilla defs only in Phase 1)**

- `SiteMaker.MakeSite(IEnumerable<SitePartDefWithParams>, PlanetTile, Faction, …)` works outside
  QuestGen (`SiteMaker.cs:23`).
- `SitePart.things` is a deep-saved `ThingOwner` (`SitePart.cs:16, 101`). `GenStep_ItemStash`
  places `parms.sitePart.things` onto the map when it is generated (`GenStep_ItemStash.cs`).
  The vanilla **`ItemStash` SitePartDef** (Core `Sites/Parts/ItemStash.xml`) can therefore carry
  exactly the Things the Network committed, **with no Network Def**.
- Vanilla threat parts that combine with stashes: `Outpost`, `BanditCamp`, `AmbushHidden`,
  `AmbushEdge`, `Manhunters`, `SleepingMechanoids`, `Turrets` (their tags include
  `ItemStashQuestThreat`).
- The vanilla `Site` WorldObjectDef includes `TimeoutComp`, which destroys the site when the
  timeout passes **and no map exists** (`TimeoutComp.CompTickInterval`). It also includes
  `ItemStashContentsComp`, `TimedDetectionRaids` and `EnterCooldownComp`.
- `Site.ShouldRemoveMapNow` removes the map when no player pawns remain, and usually removes the
  world object too (`Site.cs:383`).
- A missing `SitePartDef` on load makes the part null, removed with the error "Some site parts
  were null" (`Site.cs:290`). A site with zero parts would be broken, which is why Phase 1 uses
  **only vanilla SitePartDefs**.
- `SitePart.PostDestroy` calls `things.ClearAndDestroyContentsOrPassToWorld()` (`SitePart.cs:75`).
  Pawns inside are passed to the world, not destroyed.

**Decision:** Network sites are vanilla `Site` objects with vanilla parts, plus
`WorldObjectComp_NetworkSite` added to the vanilla `Site` def by an XML patch. The comp holds
only the `OpportunityId` and forwards callbacks. It stays inert on sites it is not bound to
([DECISIONS ADR-015](DECISIONS.md)). This is confirmed at runtime by Spikes S1 and S2.

### 2.6 MapParent, map generation, map removal — **REUSE via Site**

- Map generation is fully vanilla (site parts and linked GenSteps). The Network does not write
  map generation code in Phase 1.
- `MapDeiniter.Deinit` passes map pawns to the world. On a hostile-faction map, colonists left
  behind are kidnapped (`MapDeiniter.cs:142–175`). `LeftMap` signals are sent for pawns.
- There is no "about to be removed" hook available to comps. Only `SitePartWorker` has
  `Notify_SiteMapAboutToBeRemoved`, and it requires a Network SitePartDef. Claim accounting
  therefore uses `PostCaravanFormed` plus a low-frequency sample
  ([STATE_MACHINES § 2.2](STATE_MACHINES.md#22-opportunity-truth)).

### 2.7 Faction, FactionManager — **ADAPTER (proxies; temporary factions for encounters)**

- Permanent factions cannot be removed. Only `temporary` ones can (`FactionManager.cs:109, 388`).
- `FactionCanBeRemoved` requires no spawned or caravan/transporter pawns, no world objects, and
  no quest reservation (`FactionManager.cs:398–427`). **World pawns do not keep a temporary
  faction alive.** On removal, their faction is set to null (`FactionManager.cs:119–125`).
- A missing `FactionDef` on load removes the faction ("Some factions were null",
  `FactionManager.cs:85`). A `FactionRef` must therefore be ID-based and tolerate a miss.
- Vanilla creates temporary factions at runtime for quests (`QuestNode_Root_WorkSite.cs:156`,
  `QuestNode_Root_Beggars.cs:74`, and others) through
  `FactionGenerator.NewGeneratedFactionWithRelations`.
- `Faction.GetUniqueLoadID` = `"Faction_" + loadID`. `loadID` is our stable key.

**Decision:** RimWorld factions participate as `FactionProxy` actors, created lazily and keyed
by `loadID`. Contractor organizations are **not** factions. Physical encounters use one
temporary faction per org (Spike S10).

### 2.8 Caravan — **REUSE (player only)**

Player caravans visit Network sites as vanilla does. `PostCaravanFormed` tallies claimed
payload. NPC contractors **do not** travel as world-map caravans. Their movement is abstract.
A later, optional "operation marker" world object is deferred. It would need a Network
WorldObjectDef, which has a removal cost.

### 2.9 Transport pods and shuttles — **ADAPTER**

- `DropPodUtility.DropThingsNear(IntVec3, Map, IEnumerable<Thing>, …)` (Core) is the baseline
  for deliveries and refunds.
- `TradeUtility.ColonyHasEnoughSilver(map, fee)` and `TradeUtility.LaunchSilver(map, fee)`
  (`TradeUtility.cs:225/237`) are vanilla's beacon-based fee payment. They are the baseline for
  Intel fees and deposits (Spike S4).
- Shuttles: `TransportShipMaker.MakeTransportShip` needs the `Ship_Shuttle` TransportShipDef,
  which is **Royalty** content (`ThingDefOf.Shuttle` is `[MayRequireRoyalty]`). A shuttle
  delivery adapter is therefore **optional** and registered only when Royalty is active.

### 2.10 Gravships, Odyssey, planet layers — **OPTIONAL ADAPTER**

- 1.6 identifies tiles with `PlanetTile { tileId, layerId }` (`PlanetTile.cs`). It is
  `Scribe_Values`-compatible through `ParseHelper.ParsePlanetTile`. `WorldGrid.PlanetLayers` is
  a dictionary keyed by layer id. `WorldGrid.Surface` and `WorldGrid.Orbit` are available, and
  there are `OnPlanetLayerAdded` and `OnPlanetLayerRemoved` events.
- `TileFinder.TryFindNewSiteTile(…, canBeSpace, layer, …)` supports layers. Note that it mixes
  `TicksGame` into landmark selection when Odyssey is active (`TileFinder.cs:327`), which is
  covered by Spike S8.
- `Gravship`, `GravshipUtility` and `WorldComponent_GravshipController` exist in the main
  assembly. Sites expose `GravShipCanLandOn`.
- Odyssey provides SitePartDefs for orbital content (`OrbitalAncientPlatform`,
  `Opportunity_OrbitalWreck`, …).

**Decision:** all orbit, space and gravship features live in `Compat.Odyssey` behind
`ModsConfig.OdysseyActive`. They register extra archetypes and logistics capabilities.
`TileRef` stores the layer def name so a missing orbit layer invalidates cleanly
([COMPATIBILITY § 4](COMPATIBILITY.md#4-dlc-and-optional-systems)). **Naming note:** Odyssey
already uses "Opportunity" in some SitePartDef names. Network types live in the `TheNetwork`
namespace, and the UI says "lead" or "opportunity" in context.

### 2.11 Incidents — **NOT NEEDED early**

The Network's timing comes from its own scheduler. Incidents (with Network `IncidentDef`s) are
considered only when storyteller pacing *should* gate something, for example an NPC contractor
visit competing with raids. Deferred.

### 2.12 Letters and Archive — **REUSE (vanilla letter classes only)**

`LetterMaker.MakeLetter` / `LetterStack.ReceiveLetter` with vanilla `LetterDefOf` and look
targets. **No custom `Letter` subclasses**: letters persist in the LetterStack and Archive, and a
missing class would error after the mod is removed. Choices are made in the Network UI, not in
choice letters.

### 2.13 Scribe and IExposable — **REUSE with rules**

- `Scribe_Defs` on a missing def logs **one error per reference**:
  `ScribeExtractor.DefFromNode` → "Could not load reference to …" (`ScribeExtractor.cs:56`).
  **The Network stores defNames as strings** (`DefRef`).
- `Scribe_References` to an unresolvable object logs a **warning** ("Could not resolve reference
  …", `LoadedObjectDirectory.cs:130`) and yields null. It is used only for pawns (`PawnRef`).
- A deep-saved polymorphic object writes a `Class` attribute. An unknown class on load gives an
  error and null. Persisted type names are therefore frozen
  ([SAVE_AND_MIGRATION § 3](SAVE_AND_MIGRATION.md#3-persisted-type-names)).
- Unknown XML nodes are ignored silently by Scribe. This is what makes added and removed fields
  easy to handle.

### 2.14 DefDatabase, ThingDef, FactionDef — **REUSE read-only**

`DefDatabase<T>.GetNamedSilentFail` for resolution. The catalog reads `ThingDef` fields
(`category`, `thingCategories`, `tradeability`, `tradeTags`, `BaseMarketValue`, `stackLimit`,
`techLevel`, `IsStuff`, `MadeFromStuff`, `isUnfinishedThing`, `IsCorpse`, `Minifiable`,
`destroyOnDrop`, `EverHaulable`, `deepCommonality`, `thingSetMakerTags`, `modContentPack`,
recipes) once per session. `Def.modContentPack.PackageId`, `.Name`, `.IsCoreMod` and
`.IsOfficialMod` give source-mod metadata.

### 2.15 BackCompatibility — **NOT USABLE for mods**

`BackCompatibility.conversionChain` is a private static list (`BackCompatibility.cs:18`). Mods
cannot register converters without reflection or patches. **The Network runs its own
migrations** ([SAVE_AND_MIGRATION](SAVE_AND_MIGRATION.md)).

### 2.16 Tales, HistoryEvents, Archive — **NOT USED for Network history (precedent only)**

- `TaleManager` prunes tales for art and memory, and `TaleData_Pawn` snapshots name, kind,
  faction, gender and age **so a tale survives the pawn being gone**. This is the same principle
  as our snapshots and Legends, and a useful precedent. Tales need Network `TaleDef`s and are
  vanilla-owned. They are **optional later** ("art depicting Dead Red's last stand").
- Ideology `HistoryEvent`s could let precepts react to Network deeds. This would be an optional
  adapter.
- The Archive stores letters. Custom archivables are avoided (removal errors).

### 2.17 Pawn generation — **REUSE under seeded state; beware redress**

- `PawnGenerator.GeneratePawn(PawnGenerationRequest)` is wrapped in `Rand.PushState(seed)`.
  Results are committed by binding the pawn.
- **Redress:** `PawnGenerator` may **reuse** existing world pawns in `WorldPawnSituation.Free`
  (`PawnGenerator.cs:220, 1136–1150`). `KeepForever` pawns are still `Free`
  (`WorldPawns.GetSituation`, `WorldPawns.cs:267–314`). Only quest reservation and a few other
  situations exclude them. This is the core reason for the registry quest.
- Generation depends on the mod list, so we never regenerate a pawn to "recreate" a character.

### 2.18 World pawns and GC — **REUSE with reservation**

- `WorldPawns.PassToWorld(pawn, mode)` with `Decide`, `KeepForever` or `Discard`
  (`WorldPawns.cs:200`).
- GC keep reasons (`WorldPawnGC.GetCriticalPawnReason`, `WorldPawnGC.cs:174–247`): colonist,
  faction leader, kidnapped, caravan member, transport pod, **ForceKept**, spawned, corpse
  exists, PlayLog or BattleLog entry, active tale, **ReservedByQuest**, pawn source. Our
  references are **not** a keep reason.
- GC runs every 15,000 ticks as an incremental coroutine (`WorldPawnGC.cs:20`).
- Non-mothballed world pawns tick every tick (`WorldPawns.WorldPawnsTick`, which iterates
  `pawnsAlive` only). Mothballed pawns are ticked in bulk every 15,000 ticks
  (`DoMothballProcessing`). A pawn is mothballed only if it has no non-permanent hediff, apart
  from defs flagged `AlwaysAllowMothball`, missing parts that are not bleeding, and
  `allowMothballIfLowPriorityWorldPawn` defs in low-priority situations (`Free`, `ForSale`,
  `FactionLeader`, `Kidnapped`; `ReservedByQuest` is **not** low priority)
  (`WorldPawns.cs:11–17, 365–386`). This is why stored characters are normalized at storage
  ([ABSTRACT_PHYSICAL_LIFECYCLE § 4.3](ABSTRACT_PHYSICAL_LIFECYCLE.md#43-consequences-of-suspension-frozen-pawns)).
- `Pawn.SpawnSetup` removes the pawn from world pawns automatically (`Pawn.cs:1374`).
- `Pawn.Discard` refuses while the pawn is still a world pawn (`Pawn.cs:2436`).

### 2.19 SignalManager and quest tags — **REUSE (observation without Harmony)**

- `SignalManager.RegisterReceiver(ISignalReceiver)` is public (`SignalManager.cs`). It is not
  persisted, so we re-register on every load. The per-frame cap is 3,000 signals (vanilla-wide).
- `QuestUtility.AddQuestTag(obj, tag)` works on Things, Pawns and WorldObjects. Tags are
  persisted (`Thing.cs:1291`, `WorldObject.cs:276`).
- Signals relevant to us (sender files):
  - pawns: `Despawned`, `Destroyed`, `Killed` (only `Thing.Destroy(KillFinalize)`, **not** normal
    pawn death), `LeftMap`, `Recruited`, `Arrested`, `Rescued`, `Released`, `Kidnapped`,
    `Banished`, `Enslaved`, `ChangedFaction*`, `BecameMutant`, `TookDamageFromPlayer`;
  - world objects: `Spawned`, `Despawned`, `Destroyed`, `MapGenerated`, `MapRemoved`.
- **Pawn death** does not reliably send `Killed` for normal deaths: `Pawn.Kill` despawns and
  makes a corpse without `Destroy(KillFinalize)`. Death is observed through `Despawned` plus
  reconciliation on maps, and through the registry quest's `Notify_PawnKilled` anywhere.
- Vanilla never parses tags as quest IDs. It only matches strings, and sometimes **copies**
  tags to another pawn (`QuestPart_ReplaceLostLeaderReferences.cs:28–34`). Hence invariant I-10.

### 2.20 Tick scheduling — **own scheduler on WorldComponentTick**

`Find.TickManager.TicksGame` is the clock. 1.6's `TickInterval(delta)` exists for Things and
WorldObjects, not for WorldComponents. Our heap-based scheduler has an O(1) idle check
([SIMULATION § 1](SIMULATION.md#1-scheduler)).

### 2.21 Rand — **use only under PushState; never for Network decisions**

`Verse.Rand` is `MurmurHash(seed, iterations)` with global state (`Rand.cs`). It is not
persisted across loads. `PushState` and `PopState` support scoped seeding. Network decisions
use the private `NetRng` ([SIMULATION § 6](SIMULATION.md#6-determinism-and-rng)).

### 2.22 UniqueIDsManager — **NOT EXTENDED**

It has a closed set of counters. The Network keeps its own `nextId`.

### 2.23 UI: MainButtonDef, Windows, DebugActions — **REUSE**

A Network `MainButtonDef` opens `MainTabWindow_Network`. If the mod is removed, the button
disappears and nothing is left in the save. Dev tools use `[DebugAction]` (namespace
`LudeonTK`) with `allowedGameStates = Playing`. No Harmony is needed.

### 2.24 Kidnapped pawns — **OBSERVE**

`Faction.kidnapped` (`KidnappedPawnsTracker`) sends the `Kidnapped` signal. Reconciliation
checks every faction's kidnapped list when resolving `CapturedByOther`.

### 2.25 Comms Console — **REUSE (access gate)**

`Building_CommsConsole.CanUseCommsNow` (`Building_CommsConsole.cs:12`) is false while the map's
electricity is disabled by a game condition (solar flare) and otherwise follows
`CompPowerTrader.PowerOn`. Vanilla's own comms jobs gate on it (`JobDriver_UseCommsConsole.cs:17`).
The Network uses the same property for its access gate, found through
`ListerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>()` on maps where
`Map.IsPlayerHome`, so modded console subclasses count too ([DECISIONS ADR-032](DECISIONS.md)).

---

## 3. Harmony policy

### 3.1 Policy

1. **Zero Harmony patches are required for Phases 1–3.** The Network will not even declare a
   Harmony dependency until a patch is adopted.
2. A patch may be adopted only if a vanilla extension point is shown to be insufficient **by a
   failed spike**, not by convenience.
3. Every adopted patch must be documented with: exact target · why vanilla is insufficient ·
   expected call frequency · compatibility risk · fallback if another mod patches the same method
   · the phase it is required in. It is then verified at build time against the real assemblies
   (Grandmaster21's `verify-real.sh` approach: reflective target resolution plus parameter-name
   checks).
4. **No patches on hot paths** (per-tick pawn, render, pathing, stat calculation) under any
   circumstances.

### 3.2 Needs evaluated, with the vanilla alternatives chosen

| Need | Tempting patch | Vanilla alternative chosen |
|---|---|---|
| Detect pawn death anywhere | `Pawn.Kill` postfix | registry quest `Notify_PawnKilled` + `Despawned` signal + reconciliation |
| Keep contractor pawns from GC | `WorldPawnGC.GetCriticalPawnReason` postfix | quest reservation |
| Keep contractor pawns out of random raids | `PawnGenerator` redress prefix | quest reservation (not `Free`) |
| Know when the player leaves a site with loot | `Site.Notify_MyMapAboutToBeRemoved` postfix | injected `WorldObjectComp.PostCaravanFormed` + fallback sample |
| Avoid red errors for missing defs | patch `ScribeExtractor.DefFromNode` | store defNames as strings (`DefRef`) |
| React to goodwill changes during contracts | `Faction.TryAffectGoodwillWith` postfix | re-check at contract checkpoints |
| Track recruitment, arrest and kidnapping of contractors | patch each interaction | quest-tag signals + reconciliation |
| World-map inspect text and options for Network sites | `Site.GetInspectString` / `GetFloatMenuOptions` postfixes | comp `CompInspectStringExtra` / `GetFloatMenuOptions` |
| Main UI entry | patch the main button bar | `MainButtonDef` |
| Save migration hooks | patch `BackCompatibility` | own migrations in `ExposeData` / post-load |

### 3.3 Contingency patches (analysed, not adopted)

These patches are documented in advance so a failed spike does not lead to an improvised patch.

**C-1 · Redress guard (only if Spike S9 fails and the fallback is insufficient)**

| Field | Value |
|---|---|
| Target | `Verse.PawnGenerator.IsValidCandidateToRedress(Pawn, PawnGenerationRequest)` (private static), postfix: `__result &= !NetworkCustody.IsReserved(pawn)` |
| Why vanilla is insufficient | only if quest reservation cannot be used and factionless storage still leaks into `WorldPawnFactionDoesntMatter` requests |
| Call frequency | per candidate during pawn generation: raids, visitors (tens to hundreds per generation burst). Not per tick. |
| Compatibility risk | Low to medium. Other mods (Pawnmorpher, character editors) patch generation. A postfix that only narrows `true → false` is composable. |
| Fallback if another mod patches it | our postfix still applies because postfixes chain. If the method is replaced by a transpiler that removes the call, the effect is lost and a pawn might be redressed. Reconciliation then detects it (the character's pawn spawned in an unknown context) and marks it OutOfCustody. The world stays consistent. |
| Phase | 3, conditional |

**C-2 · GC keep reason (only if Spike S9 fails)**

| Field | Value |
|---|---|
| Target | `RimWorld.Planet.WorldPawnGC.GetCriticalPawnReason(Pawn)` (private), postfix: if the result is null and the pawn is Network-reserved, return `"TheNetwork"` |
| Why | only if quest reservation is unusable. `KeepForever` is the non-Harmony alternative and is preferred over this patch. |
| Call frequency | about every 15,000 ticks, per world pawn, spread across frames (incremental GC) |
| Compatibility risk | Low. World-pawn cleanup mods may replace GC. |
| Fallback | `KeepForever` pinning (vanilla API) |
| Phase | 3, conditional (expected unnecessary) |

**C-3 · Pre-removal site hook (only if claim accounting proves unacceptable in playtesting)**

| Field | Value |
|---|---|
| Target | `RimWorld.Planet.Site.Notify_MyMapAboutToBeRemoved()`, postfix: forward to our comp if bound |
| Why | an exact count of payload left on the map at removal |
| Call frequency | once per site map removal |
| Compatibility risk | Very low. The alternative is adding a Network SitePartDef whose worker gets `Notify_SiteMapAboutToBeRemoved` without Harmony. **Preferred over the patch** if exact accounting becomes necessary, at the cost of a Network Def on sites. |
| Fallback | sampling (current design) |
| Phase | 1 only if playtesting demands it; expected to be unnecessary |

No other contingency is anticipated through Phase 6. Black contracts, witnesses, rumors and
bidding are domain logic plus vanilla observation.

---

## 4. Summary matrix

| System | Reuse | Dangerous coupling if misused | Save/load | Assumes vanilla quest ownership | Adapter | Harmony |
|---|---|---|---|---|---|---|
| WorldComponent | ✔ root | — | ✔ (missing class → 1 error) | — | — | ✘ |
| GameComponent | ✘ | a second removal error | ✔ | — | — | ✘ |
| Quest / QuestPart | registry only (Ph.3) | modelling contracts as quests: lifecycle ownership, cleanup, UI | root **must** be non-null | **yes** (state, cleanup) | CustodyService | ✘ |
| QuestGen / Slate | ✘ | grammar/slate coupling | — | yes | — | ✘ |
| WorldObject + comp | ✔ | custom WorldObjectDef/class (removal errors) | ✔ (comp data ignored when removed) | no | SiteAdapter | ✘ |
| Site / SitePart | ✔ vanilla defs | custom SitePartDefs (null parts on removal) | ✔ | no | SiteAdapter | ✘ |
| Faction / FactionManager | proxies + temporary | per-org permanent factions | IDs + snapshots | temporary factions reserved by quests | EncounterFactionAdapter | ✘ |
| Caravan | player only | NPC world caravans | ✔ | no | — | ✘ |
| Drop pods | ✔ | — | ✔ | no | DeliveryAdapter | ✘ |
| Shuttles | optional (Royalty) | hard dependency on Royalty | ✔ | shuttles have quest-reservation semantics | Compat.Royalty | ✘ |
| Gravship / layers | optional (Odyssey) | hard dependency; persisting orbit tiles without a layer check | `TileRef` with layer def | no | Compat.Odyssey | ✘ |
| Letters | ✔ vanilla classes | custom letter classes | ✔ | no | LetterPresenter | ✘ |
| Scribe | ✔ with rules | `Scribe_Defs` on external defs | — | — | NetScribe helpers | ✘ |
| BackCompatibility | ✘ | — | — | — | own migrations | ✘ |
| Tales / HistoryEvent | later, optional | making history depend on them | — | — | Compat | ✘ |
| PawnGenerator | ✔ seeded | regenerating a character | commit on bind | — | CustodyService | ✘ |
| WorldPawns / GC | ✔ with reservation | relying on our references to keep pawns | ✔ | — | CustodyService | ✘ |
| SignalManager | ✔ | trusting signals alone | re-register on load | tags are quest-style | SignalBridge | ✘ |
| Rand | under PushState only | using it for Network decisions | not persisted | — | NetRng | ✘ |
| Comms Console | ✔ access gate (`CanUseCommsNow`) | requiring a specific def (modded consoles are subclasses) | — | no | CommsAccessAdapter | ✘ |
| ModSettings | ✔ global cast and preferences | writing runtime history into settings | own `NetworkSettingsVersion` | no | NetworkSettings | ✘ |

---

## 5. Runtime spikes

Assumptions that cannot be confirmed statically. **Each must pass before the phase that depends
on it builds content on top.** A spike is a small, throwaway-or-kept dev build plus a written
result in `docs/spikes/Sx-<name>.md` (created when run).

| ID | Phase | Question | Pass criteria |
|---|---|---|---|
| **S1** | 1 | Programmatic vanilla `Site` (ItemStash with Network-made Things + threat part) created outside QuestGen: map generation places the stash; timeout destroys it when unvisited; leaving removes the map | stash items present on the map; no errors; timeout and removal behave like vanilla item-stash quests |
| **S2** | 1 | `WorldObjectComp_NetworkSite` injected into the vanilla `Site` def by XML patch: all callbacks fire; data persists; **removing the mod leaves the site working with no errors** | callbacks logged; save/load ok; mod removed: 0 errors from the comp |
| **S3** | 1 | `SignalBridge` registration from a WorldComponent across new game, load and reload-in-session; signals from tagged world objects arrive | signals received after each path; no duplicate registration errors |
| **S4** | 1 | Fee payment through `ColonyHasEnoughSilver` / `LaunchSilver` (beacons); behaviour without beacons; UX message; the Comms Console gate (`Building_CommsConsole.CanUseCommsNow` on player home maps) with power loss and solar-flare conditions | fee taken correctly; clear refusal reason; refund by drop pod works; commands refused without a usable console and accepted again when it returns, with nothing in progress lost |
| **S5** | 1 | Tolerant per-element list loading (`NetScribe.LookList`) by iterating child XML nodes under Scribe | a corrupted element is quarantined; siblings load; no Scribe state corruption |
| **S6** | 1 | Remove The Network from a save with an active site, pending Intel and history, then re-add it | Phase 1: exactly 1 error (component class; nothing from the comp). Phase 3+: 1 prepared, ≤ 3 unprepared. Re-adding the mod bootstraps cleanly. |
| **S7** | 1 | Remove an item mod (for example BOR) while Intel is Searching and a stash with its items exists | Intel is Invalidated with a refund; vanilla errors limited to the missing-def Things in the stash; the Network does not add errors |
| **S8** | 1 | Determinism: resolve Intel, reload before the due tick, let it resolve again; repeat with Odyssey on | identical tile, amount, archetype and threat both times |
| **S9** | 3 | Registry quest custody (see [ABSTRACT_PHYSICAL_LIFECYCLE § 12](ABSTRACT_PHYSICAL_LIFECYCLE.md#12-spikes-that-must-pass-before-phase-3-builds-on-this)) | as listed there |
| **S10** | 3 | Temporary encounter factions | as listed there |
| **S11** | 3 | Pawns in the `SitePart.things` holder | as listed there |
| **S12** | 3 | Catch-up healing and aging of suspended pawns | as listed there |
| **S13** | 2 | Drop-pod delivery: destination map missing, roofed or crowded; incoming transporters blocking map removal | delivery lands or reroutes; no stuck state |
| **S14** | 3 | Walk-in delivery or visit Lord; hostility flip mid-visit | as listed there |
| **S15** | 6+ | Odyssey: orbital site creation, gravship landing on a Network site, save without Odyssey afterwards | orbit opportunities Invalidated cleanly without Odyssey |
| **S16** | 2+ | Royalty shuttle delivery adapter | shuttle arrives and leaves; no quest-reservation conflicts |
| **S17** | 3 | Tag hygiene with duplication or copy paths | copies ignored; tags stripped |
| **S18** | 1 | Performance smoke: the dev harness simulates 10,000 scheduler jobs and 5,000 history records; save-size growth | per-tick idle cost unmeasurable; job cost within budget; save size within the [EVENTS_AND_HISTORY § 11](EVENTS_AND_HISTORY.md#11-save-size-budget) targets |
| **S19** | 1 | **Loot without extermination.** Generate a guarded Network opportunity; enter; recover only part of the target payload; leave by caravan (and separately by pods) while some defenders are alive. Does the vanilla `ItemStash` + threat-part composition (and its site comps: `TimedDetectionRaids`, `EnterCooldownComp`, timeout, `Site.ShouldRemoveMapNow`) let the player leave, and does anything require all enemies dead? | the caravan or pods can leave; recovered items stay recovered; the opportunity resolves Claimed with a partial `recoveredBand`; history says "partial recovery"; no duplicated loot on re-entry or a second departure; the site cleans up as vanilla does. **If it fails**, record the result and switch Phase 1 to another vanilla composition or a minimal Network site part (ADR-015's upgrade path); Phase 0 is not redesigned. |
