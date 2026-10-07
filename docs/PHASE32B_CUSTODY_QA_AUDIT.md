# Phase 3.2B custody QA: actual RimWorld 1.6 audit

The prison audit below supported the earlier TestCompound. The current dedicated-lab reset audit extends it using the same supplied 1.6 assembly. Previous 026–028 owner PASS and the 029 escape/FAIL remain historical; generated Encounter debris later blocked automatic setup before Plan. New explicit Create/Reset/029 requires owner runtime. Neither audit changes promotion policy or claims live reset/custody success.

## Evidence provenance

The primary evidence is the actual supplied `Assembly-CSharp.dll`, assembly version **1.6.9676.17735**, SHA-256 **`5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a`**, decompiled with the available ILSpy. Audit files are retained in `/tmp/pr13-custody-audit`; line references below identify those decompilations, not original proprietary source locations. The two read-only audit reports are `audit.md` and `transaction-escape-path-report.md` in that directory.

The separate think-tree evidence is the supplied [Humanlike.xml snapshot](https://github.com/D3athAn63l/zRim_Source_XMLs/blob/9fcca42215c247135067694cd97c1ed7a4d86a7b/1.6/Data/Core/Defs/ThinkTreeDefs/Humanlike.xml#L206), repository commit **`9fcca42215c247135067694cd97c1ed7a4d86a7b`**, lines 206–245. XML selects the think-tree branches; the actual assembly supplies the invoked code predicates. These evidence types are not interchangeable.

## Why the wilderness captive escaped

The owner reported real `CapturedBy(Faction.OfPlayer)` followed about three ticks later by the vanilla escaping message. The same anonymous Pawn remained bound/reserved initially, with Pending peers, no early Character and conserved humans. By terminal reconciliation around 500 ticks later it was WorldFree; all three slots truthfully Returned and no captured promotion occurred. Nine final promotion failures followed the single lost-custody prerequisite.

`JobGiver_PrisonerEscape.ShouldStartEscaping` admits an actual player-hosted prisoner with `guest.PrisonerIsSecure`, unless its Lord explicitly reports `PrisonerSecure`. It returns true when the current district touches an edge or a search through at most 25 regions reaches an edge through doorless/`FreePassage` regions. `TryGiveJob` emits `MessagePrisonerIsEscaping` and supplies a Goto with `exitMapOnArrival = true`. An outdoor, unenclosed wilderness captive has this ordinary open-route opportunity. Missing enclosure/open passage is the direct predicate; a missing bed is an indirect fixture defect, not a direct timer/bed test in this method.

Repository evidence gives a more specific cause than an inferred open-wilderness route. [RimWorldPhysicalWorldPort.Place](../Source/TheNetwork/Integration/Physical/RimWorldPhysicalWorldPort.cs#L278) selects an edge entry through `TryCells`/`TrySharedCells` and spawns each Pawn there. The prior source `bd59bd0`'s [029 script and arrest](https://github.com/D3athAn63l/The-Network/blob/bd59bd0a57704597feefead598721151a6e42758/Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs#L522) proceeded directly from placement to arrest; [PhysicalRun.Pump](../Source/TheNetwork/Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs#L117) continues consecutive Next steps in the same frame. The old fixture did not relocate the captive, so arrest occurred in the edge district, directly satisfying the supplied DLL's `district.TouchesMapEdge` predicate. The DLL predicate and repository placement/timing are separate evidence; the cloud did not independently inspect the owner's saved district.

`CapturedBy` establishes guest/prisoner status and notifies/removes the visitor from its Lord, but neither creates a cell nor transports it into one. `PrisonerIsSecure` is a guest-state eligibility property, not a physical enclosure validator. The correction relocates only the owned captive into a validated real cell before that one vanilla arrest.

## Actual assembly predicates and safe fixture

| Audited class/method | Source condition and consequence |
|---|---|
| `Pawn_GuestTracker.SetGuestStatus` / `CapturedBy` (`RimWorld_Pawn_GuestTracker.cs:443,561`) | Real Prisoner status, vanilla ClearMind/gear and MadePrisoner Lord notification. No requirement to convert the map to a home or add a warden. |
| `JobGiver_PrisonerEscape.TryGiveJob` / `ShouldStartEscaping` | Actual prisoner/Player HostFaction, secure guest state, Lord override, edge/open-door reachability, then actual exit job/message. Closed enclosure prevents this immediate open-route condition. |
| `Building_Door.FreePassage` / `PawnCanOpen`; `GenAI.MachinesLike` | A closed door has no FreePassage. Human-openable factionless doors are unsuitable. A real player-owned door rejects ordinary opening by a prisoner hosted by that faction. Do not repeatedly close doors to conceal a failure. |
| `Building_Bed.RoomCanBePrisonCell`; `Room.ProperRoom`, `IsHuge` | `ProperRoom && !IsHuge`; ProperRoom excludes edge-touching and needs a Normal district. Huge means more than 60 regions. No player-home/colonist/warden or roofing predicate. |
| `Building_Bed.ForPrisoners`; `Room.Notify_RoomShapeChanged` | Real designation plus a qualifying room containing a prisoner bed derives `IsPrisonCell`. Designation alone is insufficient. Use ordinary district/room notifications after actual region rebuild and require the resulting room predicate. |
| `RegionAndRoomUpdater.RebuildAllRegionsAndRooms` / `TryRebuildDirtyRegionsAndRooms` | Rebuild real map region/room state. Disabled/working/error cases can skip work, so final predicates must be checked after the call. |
| `RestUtility.CanUseBedNow` / `IsValidBedFor` / `FindBedFor`; `SocialProperness` | Same-map spawned prisoner bed in a recognized cell, ordinary reachability/reservation/forbidden checks and matching prisoner room. Move only the owned Pawn into the verified interior before normal bed selection. |
| `Pawn_Ownership.ClaimBedIfNonMedical` | Real public assignment. It can evict another owner, so choose an available bed or the same Pawn's bed first; preserve a previous 029 captive when 030B starts. |
| `Thing.Position`; `Pawn.Notify_Teleported`; vanilla `CompAbilityEffect_Teleport.Apply` | The audited vanilla positioning pattern updates thing grids and resets movement/jobs. QA may relocate its exact owned Pawn once before CapturedBy; no warden simulation, stun, repeated recapture or AI freeze is required. |
| `Map.IsPlayerHome`; `WorldObjectDef.canBePlayerHome`; plain `MapParent` | Home status depends on player-parent/home-def conditions (with separate gravship exceptions). Player-owned bed/door/wall Things do not make the factionless, non-home, no-comp/no-target TestSite into a colony. Plain parent/removal safety stays intact. |
| `PrisonBreakUtility.InitiatePrisonBreakMtbDays`; `GuestTrackerTickInterval` | Real prison cells still permit ordinary stochastic prison breaks; guest ticks check that path on 2500 hash ticks. A legitimate fixture prevents immediate open-route escape, not every future random break. Lost real custody must fail truthfully. |

## Non-home jobs and XML evidence

Humanlike.xml's non-player-home branch, when no undowned free colonist is spawned, invokes the **same** prisoner escape giver, then WanderColony. Decompiled `ThinkNode_ConditionalInNonPlayerHomeMap` and `ThinkNode_ConditionalAnyUndownedColonistSpawnedNearby` confirm these gates. They do not bypass the escape giver's closed-enclosure predicate, forcibly release custody or require a fake colonist to preserve status.

`JobGiver_WanderColony` uses radius 7 and ordinary `JobGiver_Wander`, whose `canBashDoors` defaults false. `WanderUtility.GetColonyWanderRoot` selects reachable eligible targets, else the current position. `RCellFinder.RandomWanderDestFor` / `CanWanderToCell` use ordinary ByPawn reachability. The closed player-owned prison boundary therefore also constrains normal wandering.

Audited `JobGiver_GetFood`, `FoodUtility`, `TraverseParms`, `JobDriver_Ingest`, `Toils_Goto`, `Toils_Ingest` and `RCellFinder.SpotToStandDuringJobInRegion` retain normal reachability/forbidden/social-properness checks without door bashing; hosted standing eating spots stay in the current room. `WardenFeedUtility.ShouldBeFed` requests assisted feeding for a prisoner in bed who is downed or needs medical rest, rather than every healthy captive. No fundamental home/colonist/warden or added food infrastructure requirement was found for the bounded custody interval.

## Dedicated-lab reset API audit

The same actual supplied assembly/version/SHA above was decompiled for `ThingOwnerUtility`, `MapPawns`, `GenDebug`, `Thing`, `Building`, `ThingWithComps`, `TerrainGrid`, `Building_Casket`, `CompSpawnPawnOnDestroyed`, `CompAssignableToPawn`, `CompExplosive`, `CompHasPawnSources`, `CompTreeConnection`, `CompObelisk_Abductor`, `PocketMapUtility`, `WorldPawns`, `Plant` and `GenLeaving`. Additional decompilations are retained in `/tmp/pr13-lab-audit`; `Thing.cs`/`Map.cs` remain in `/tmp/pr13-custody-audit`. These are actual assembly predicates, not XML-inferred implementations.

| Audited API | Reset consequence |
|---|---|
| `ThingOwnerUtility.GetAllThingsRecursively(map, ..., allowUnreal: true)`; `MapPawns.AllPawns` | Census includes recursively held/unspawned subjects; combine it with every cell grid. Any Pawn, including corpse-contained Pawns, blocks before writes. |
| `GenDebug.ClearArea` | Vanilla clears roofs first, then destroyable Things with default Vanish. The QA reset uses that ordering within stronger exact-site/ownership/no-Pawn preflight, rather than calling a generic arbitrary-map clear. |
| `Thing.Destroy(DestroyMode.Vanish)` | Checks destroyability and sends quest-destroyed signals; reject non-destroyable/quest-linked Things before mutation. The snapshotted destruction target is never a Pawn. |
| `Building.DeSpawn/Destroy`; `ThingWithComps.Destroy` | Attachments may be removed/minified and comp callbacks run even with Vanish. If callbacks leave/spawn Things or throw, STOP for inspection; do not silently erase them or claim atomic rollback. |
| `Building_Casket.Destroy`; `CompSpawnPawnOnDestroyed.PostDestroy` | Casket releases contents for Deconstruct/KillFinalize, and this comp spawns Pawns only for KillFinalize. Vanish avoids those paths, while the census still refuses ANY contained Pawn before any holder is destroyed. |
| `CompAssignableToPawn.PostDeSpawn`; real bed ownership | Removal can unassign off-map subjects. Any existing assignment/bed owner blocks reset. |
| `CompExplosive.PostDestroy` | `explodeOnDestroyed` can detonate regardless of mode; reject that property before writes. Vanish alone is insufficient. |
| `CompHasPawnSources.PostDestroy`; `WorldPawns.RemovePawnSources` | Removes WorldPawns retention-source records even during Vanish; refuse the comp before writes. |
| `CompTreeConnection.PostDestroy` | Without a mode guard, notifies connected subjects and kills dryads, including off-map references outside the holder census; refuse the comp. |
| `CompObelisk_Abductor.PostDestroy`; `PocketMapUtility.DestroyPocketMap` | Without a mode guard, can remove a linked labyrinth pocket-map parent and deinitialize another map; refuse the comp. |
| `TerrainGrid.SetTerrain`; `Plant.DeSpawn`; `GenLeaving` | Use real terrain/despawn/leaving semantics on the emptied exact map. Normalize every cell only after the zero-leftover check; no Pawn/world transfer or general debris whitelist. |

Separate XML evidence at pinned repository commit `9fcca42215c247135067694cd97c1ed7a4d86a7b` defines [Concrete](https://github.com/D3athAn63l/zRim_Source_XMLs/blob/9fcca42215c247135067694cd97c1ed7a4d86a7b/1.6/Data/Core/Defs/TerrainDefs/Terrain_Floors.xml#L57), [Stool/Table2x2c](https://github.com/D3athAn63l/zRim_Source_XMLs/blob/9fcca42215c247135067694cd97c1ed7a4d86a7b/1.6/Data/Core/Defs/ThingDefs_Buildings/Buildings_Furniture.xml#L597) and the ordinary factionless [AncientShipBeacon](https://github.com/D3athAn63l/zRim_Source_XMLs/blob/9fcca42215c247135067694cd97c1ed7a4d86a7b/1.6/Data/Core/Defs/ThingDefs_Buildings/Buildings_Ancient_Outdoors.xml#L67) / [AncientMechDropBeacon](https://github.com/D3athAn63l/zRim_Source_XMLs/blob/9fcca42215c247135067694cd97c1ed7a4d86a7b/1.6/Data/Core/Defs/ThingDefs_Buildings/Buildings_Ancient_Indoors.xml#L439) encountered by the owner. These vanilla beacon definitions are inert Building definitions; patched loaded Things still face actual callback/quest/assignment/explosion guards. XML defines content; the supplied assembly defines destruction behavior.

## Conclusion and proof limits

The audited paths did not establish a fundamental player-home/warden requirement. Provisioning now belongs to explicit armed Create and destructive whole-map Reset, not automatic TestCompound.Ensure. The non-home TestSite uses Concrete, a camp/table/stools, an ordinary room and the audited real two-bed prison; `QaLab.Validate` derives prepared status read-only from actual map/building/room/path state. Known hazardous callbacks fail preflight; unknown removal effects STOP without automatic cleanup. Production custody/promotion semantics and the one pre-capture relocation remain unchanged.

Headless reset policy, geometry, source/API wiring and fake lifecycle/real Scribe tests do not prove live Unity clearing, callback safety on the active mod list, recognized room/path state or real custody through two watches. Those remain owner **Create → Reset → 029**, then **030B SAVE/LOAD → 030V** evidence. One 026 sanity run is recommended before final merge because the physical lab changed. See the [current 37-item validation report](PHASE32B_VALIDATION.md#pr-13-dedicated-qa-lab-current-owner-review-record) and [owner workflow](RUNTIME_TESTING.md#190-explicit-lab-setup).

**Transient S1 custody before terminal batch is not currently latched as durable promotion evidence; 029 intentionally proves sustained custody through terminal batch. Capture→escape-before-batch requires separate design review, likely Phase 3.2C.** S11 remains FAIL / rescue STOPPED, R-50 OPEN, O-20 LOCKED, S21/S26/S27 PARTIAL; later phases remain unimplemented. No owner PASS for this new prison fixture is claimed.


## Disposable non-humanlike Pawn removal audit

This extends the earlier zero-Pawn reset audit above; that earlier table records the prior provisioning delivery. The same supplied RimWorld **1.6.9676.17735** assembly and SHA-256 in Evidence provenance were inspected for Pawn removal, corpse/holder traversal and linked-entity callbacks. The saved decompilation workspace is `/workspace/scratch/c53f8d9f2faf/qa-wildlife-audit`; proprietary decompiled source is not added to this repository.

| Actual supplied API | Consequence and selected use |
|---|---|
| `Pawn.Destroy(Vanish)` | Destroys the Pawn and its inventory/equipment; by itself it can call ordinary `PassToWorld` and retain the destroyed Pawn. It is therefore insufficient as the standalone reset operation. |
| `Pawn.Discard` | Marks a destroyed Pawn discarded, clears relations and paths, and notifies logs, tales, quests and memories. A direct call on a live Pawn omits the required ordinary destruction sequence. |
| `Pawn.DeSpawn(Vanish)` / `ThingOwner.Remove` | First detach only the preflight-approved exact-map subject from the map or recursive holder; corpse-contained Pawns use their real holder. No Kill, slaughter or generated corpse is requested. |
| `WorldPawns.PassToWorld(..., PawnDiscardDecideMode.Discard)` | Explicit Discard invokes `DiscardPawn` instead of `AddPawn`. Its `pawnsBeingDiscarded` scope guards `Pawn.Destroy` against ordinary world reinsertion, establishes Destroyed, then calls Discard. Reset asserts Discarded and absent from WorldPawns afterward. The API name does not imply world retention in this mode. |
| `Corpse.Destroy` | After its disposable InnerPawn is detached, the empty wrapper can be vanished in the existing non-Pawn snapshot. The null InnerPawn guard avoids another Pawn destruction operation. |
| `Pawn_RelationsTracker.ClearAllRelations` / `PawnRelationWorker_Overseer.OnRelationRemoved` | Removing an Overseer relation unassigns the mech from its mechanitor's control group, even on another map. Other ordinary destruction/discard callbacks can update linked quests, assignments, memories and logs. |

The off-map-mechanitor case initially triggered the brief's STOP requirement. The owner subsequently confirmed that animals and mechs are incidental, unnecessary for these scenarios, and should be deleted in the disposable QA save, authorizing ordinary vanilla linked-entity cleanup. This is an explicit exception for removal of approved incidental Pawns; no reset loop targets off-map Pawns. Existing guards for hazardous non-Pawn callbacks, quest-linked Things and occupied structures remain.

The complete read-only census still combines recursive holders with `allowUnreal: true`, the whole cell grid and MapPawns. Any Humanlike Pawn or positive Network physical owner/binding refuses before roofs or any other map write. Ownership is race independent and checked against retained registry indexes, durable KnownCharacter bindings and incomplete Episode bindings, including missing/mismatched cached pointers and ThingIDs. Scope/race/ownership are checked again immediately before detachment. Unknown ownership, a discarded/world-retained subject or an inconsistent holder fails closed.

Headless tests exercise the policy with actual vanilla RaceProperties, real retained-registry indexes, durable binding cases, recursive Corpse traversal/detachment and source boundaries. They do not run the full Unity map reset or prove every modded callback. Owner **Create → Reset → RT-PHYX-029**, followed by **030B SAVE/LOAD → 030V**, remains required. The 029/030B capture architecture, production lifecycle, save format 5 and no-production-Harmony policy are unchanged.
