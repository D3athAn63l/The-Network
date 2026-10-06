# Phase 3.2B custody QA: actual RimWorld 1.6 audit

This read-only audit preceded the TestCompound implementation. It supports a narrow developer-QA fixture on the existing non-home TestSite. It does not claim live prison/escape-AI success or change production promotion policy. The owner accepted 026/027/028 on the prior corrected DLL; previous 029 failed after its real captive escaped. Corrected 029 remains an owner rerun.

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

## Conclusion and proof limits

The home-map/warden STOP condition was **not established** by the audited paths. The approved narrow QA implementation uses real walls, a closed player-owned door, designated real prisoner beds, actual recognized room/bed state and ordinary assignment on the existing isolated TestSite. The helper derives structures from deterministic cells, preserves valid/occupied assets and refuses other maps. It adds no fake held state, escaped-Pawn repair, timer suppression, Harmony, colony mutation or production custody change.

Headless geometry/policy/API wiring and fake pending-state tests cannot prove actual Unity generation, region/path behavior, prison escape AI or survival through two 250-tick watches. Those remain corrected owner 029 and 030B→SAVE/LOAD→030V evidence. See the [current validation report](PHASE32B_VALIDATION.md) and [owner workflow](RUNTIME_TESTING.md#19-phase-32b-groups-and-progressive-concretization).

**Transient S1 custody before terminal batch is not currently latched as durable promotion evidence; 029 intentionally proves sustained custody through terminal batch. Capture→escape-before-batch requires separate design review, likely Phase 3.2C.** S11 remains FAIL / rescue STOPPED, R-50 OPEN, O-20 LOCKED, S21/S26/S27 PARTIAL; later phases remain unimplemented. No owner PASS for this new prison fixture is claimed.
