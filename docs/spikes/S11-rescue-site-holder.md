# S11 — Site-part pawn holder for a rescue site

**Verdict: FAIL — SOURCE AUDIT** *(decided by the 1.6 source; no runtime run was needed or made)*

**Consequence: the rescue-site implementation is STOPPED** (Phase 3.2A prompt § G: *"If S11 FAILS: STOP the rescue-site implementation.
Document the failure and the narrowest viable alternative. Do not fake a PASS. Do not silently adopt Harmony."*). No rescue site, no
`SitePart.things` holder, no new world object and no Harmony were built. The narrowest viable alternative is in § 6. It is **not
implemented** and needs an owner decision and its own runtime proof.

## 1. The question

[PHYSICAL_LIFECYCLE § 25](../PHYSICAL_LIFECYCLE.md#25-open-questions-and-spikes), S11: can a Network-retained named pawn ride in
`SitePart.things` through a vanilla GenStep **without** `DownedRefugee`'s forced downing, and does a site destroyed first send the
pawns to the world? The Phase 3.2A prompt makes the criteria explicit. A safe mechanism must:

1. store the **same** bound contractor `Pawn` in `SitePart.things`;
2. survive a save/load before the site map is generated;
3. place the pawn onto the generated site **without inappropriate vanilla forced state mutation**;
4. handle a site destroyed or expired before generation (observe what vanilla does);
5. pass or retain the pawn **exactly once**;
6. produce no duplicate pawn and lose no binding;
7. use vanilla composition (a vanilla `Site` and vanilla parts), with no custom world-object lifecycle invented to get round S11.

## 2. Build and environment

| Item | Value |
|---|---|
| Base | `main` at `24a4881` (Phase 3.1 merged, owner runtime validated) |
| Game assemblies | `Assembly-CSharp 1.6.9676.17735` (the owner-provided reference set), decompiled with `ilspycmd 8.2.0.7535` for reading only; nothing proprietary is committed |
| Network code read | `Integration/SiteAdapter.cs`, `Integration/WorldObjectComp_NetworkSite.cs`, `Integration/SiteCallbacks.cs`, `Integration/Physical/RetainedPawnRegistry.cs` |
| Runtime | **not run.** The source settles the question (§ 4), so a runtime experiment could only confirm what the code already does |

## 3. The candidate

Give the person's retained `Pawn` to a vanilla site part's `things` (a `ThingOwner`, saved `Deep`) and let a vanilla GenStep place it
when the player enters the site. The only vanilla steps that consume a **pawn** from `parms.sitePart.things` are
`GenStep_DownedRefugee` and `GenStep_PrisonerWillingToJoin`. The only other consumers of `sitePart.things` are the item steps
`GenStep_ItemStash` and `GenStep_WorkSiteStash`.

## 4. What the 1.6 source says

| # | Fact (1.6.9676, decompiled) | Where | Which criterion it breaks |
|---|---|---|---|
| 1 | `ScatterAt` takes `things[0]`, then calls **`HealthUtility.DamageUntilDowned(pawn)`** and **`HealthUtility.DamageLegsUntilIncapableOfMoving(pawn)`**, spawns it, and sets **`mindState.WillJoinColonyIfRescued = true`** | `GenStep_DownedRefugee.cs:22–34` | 3: real, permanent-risk injuries are inflicted on a Network person because vanilla assumes a refugee |
| 2 | `ScatterAt` takes `things[0]`, builds a BaseGen **`prisonCell`** around it with the map's or a random **enemy** faction, and sets **`WillJoinColonyIfRescued = true`** on spawn | `GenStep_PrisonerWillingToJoin.cs:44–74` | 3: the pawn is staged as a captive willing to join the player |
| 3 | `WillJoinColonyIfRescued` turns the player's "Offer help" into **`JoinColonyBecauseRescuedBy` → `InteractionWorker_RecruitAttempt.DoRecruit`**: the pawn **joins the player's colony** | `JobDriver_OfferHelp.cs:21–25`; `Pawn_MindState.cs:590–595` | 3 and the purpose itself: a "rescue" through either step is a **recruitment** of the contractor by the player, the opposite of returning them to the Network |
| 4 | When no spawn cell is found, the BaseGen pawn symbol calls **`Find.WorldPawns.PassToWorld(singlePawnToSpawn)`** | `SymbolResolver_SinglePawn.cs:32–36` | 5: a vanilla pass the Network does not control, on a pawn the registry already reserves as a world pawn |
| 5 | The item steps hand `sitePart.things` to BaseGen as `stockpileConcreteContents`; `SymbolResolver_Stockpile` spawns what fits and **`Destroy()`s every content that did not spawn** | `GenStep_ItemStash.cs:50–53`; `GenStep_WorkSiteStash.cs:54–57`; `SymbolResolver_Stockpile.cs:16–38` (the `Destroy` at 35) | 5 and P3-INV-011: a retained pawn would be destroyed, never passed |
| 6 | The Network's own Last Known Location site already uses the vanilla `ItemStash` part for the contract goods (`stashPart.things = new ThingOwner<Thing>(stashPart, false)`, `dontTickContents = true`) | `Integration/SiteAdapter.cs:118–160` | 7: the one vanilla composition the Network uses cannot also carry a pawn (fact 5) |
| 7 | `ThingOwner.TryAdd` checks only `holdingOwner`; it never removes the thing from `WorldPawns` | `ThingOwner.cs:154–194` | 6: a retained pawn is a world pawn (M1, ADR-053), so adding it would make it **owned twice** (by `WorldPawns` and by the site part). Removing it from `WorldPawns` first would end the M1 reservation's coverage (`GetSituation` reads `ReservedByQuest` only for a world pawn) |
| 8 | `Site` calls `SitePartTickInterval` on every part, which sets every contained pawn's **food to 80%** whatever `dontTickContents` says | `Site.cs:334`; `SitePart.cs:60–73` | 3: a person who is not on any map is fed by the site |
| 9 | `SitePart.PostDestroy` → `things.ClearAndDestroyContentsOrPassToWorld()` → `DestroyOrPassToWorld`, which passes a pawn **only if `WorldPawns` does not already contain it** | `SitePart.cs:75–86`; `ThingOwner.cs:547–558`; `ThingUtility.cs:18–31` | 4: destruction before generation is benign only for a pawn that is not a world pawn, which a retained pawn always is (fact 7) |
| 10 | `SitePartWorker_DownedRefugee.PostDestroy` calls `relations.Notify_FailedRescueQuest()` and **`HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(pawn)`** on the pawn in `things[0]` | `SitePartWorker_DownedRefugee.cs:55–70` | 3: an expired site rewrites the person's health and relations |

## 5. Verdict

**FAIL.** Criteria 3, 5, 6 and 7 cannot be met by any vanilla pawn-holding site part and step:

* both vanilla steps that place a pawn from `things` force a state (downed with damaged legs, or a captive in a prison cell) **and** make the
  pawn join the player when "rescued" (facts 1–3);
* the item steps destroy unplaced contents, and the Network's own site already uses that part for goods (facts 5–6);
* a retained world pawn cannot be in `things` without being owned twice, and taking it out of `WorldPawns` drops the accepted M1 coverage
  (fact 7, ADR-053);
* the site feeds and, on expiry, heals and notifies the contained pawn (facts 8 and 10).

Getting round any of this would need a Network GenStep or SitePart worker that re-implements placement, or a Harmony patch on the two
steps. The prompt forbids the second; the first is a custom site lifecycle invented to get round S11. Neither was built.

**What 3.2A still proves of the rescue.** The domain half of a rescue already exists from Phase 3.0 (`OpStatus.Physical`,
`physicalEpisode`, `physicalResolution`, `OperationService.OnPhysicalResolved` through FOLLOW-UP). 3.2A proves it over a **real** Troubled
contract, headlessly: the handoff cancels the Troubled deadline, a stale deadline or a second path cannot resolve the operation again, and
the `OnPhysicalResolved` failure/retry matrix holds for the Found, WrittenOff and Solo-held branches (`Rescue.*` in
`Phase32aCustodyTests`). It also adds held outcomes to a rescue episode (a rescued person who ends up held stays held, watched). What is
**not** built is the player-facing rescue: the site, the trigger and the letter.

## 6. The narrowest viable alternative (documented, NOT implemented)

**No pre-generation holder at all.** The person stays exactly where 3.1 left them: a `Stored` world pawn covered by the M1 registry.
Nothing is put in `things`.

1. The rescue opportunity is an ordinary Network site (the existing `SiteAdapter` composition, `ItemStash` plus a vanilla threat part, with
   the goods or with no goods).
2. At the existing map-generated hook (`WorldObjectComp_NetworkSite.PostMapGenerate` → `SiteCallbacks.OnMapGenerated`), the Network
   **plans and materializes a Rescue episode** for the operation's people through the owner-validated 3.1 path: `Plan` (cause = the
   operation, `OpStatus.Physical`), then `Materialize`/`Place` of the **same** retained pawn onto the now-existing map. It is placed exactly
   like a 3.1 visitor: no vanilla refugee step, no forced downing, no `WillJoinColonyIfRescued`.
3. A site destroyed or expired before generation touches **no pawn** (the person never left `WorldPawns`), and the operation stays on its
   abstract Troubled path because no handoff happened.
4. Save/load before generation is the ordinary save of a `Stored` person (validated in 3.1, RT-PHYX-010).

**What it needs before it can be built:** an owner decision (this is the first player-visible content); the rescue opportunity's trigger
(the existing Last Known Location rule fires from a resolved outcome for the goods, not from a Troubled operation); the captor-side staging
on the site (how the person appears as a captive of the site's faction without a vanilla refugee step); and its own physical-tier runtime
proof. It needs **no Harmony, no new world-object type and no save-format change** (the episode's `EpisodeCause.operation` and the
operation's `physicalEpisode` already carry the links).

## 7. What was not changed

No production rescue-site code, no `SitePart.things` use for pawns, no GenStep, no Harmony, no new persisted field (save format **5**). The
3.1 `SiteAdapter` is untouched.

## 8. Owner-selected rescue semantics (documentation only)

**S11 remains FAIL. The rescue site, captive staging, trigger and letter remain STOPPED / unimplemented.** This locks the desired story outcome for a future separately proven alternative; it is not a runtime result or authorization to build that alternative in PR #11.

| Outcome | Same person's future |
|---|---|
| **Rescue** | The player physically frees the same captive/stranded Pawn. Once positive evidence proves the person is genuinely free and has left the rescue situation, they return to Network custody with `heldBy = None`, `heldSinceTick = -1`, the same KnownCharacter and original contractor organization. They may return Wounded; after recovery they can resume that career. Being freed does not rewrite permanent organization/history or make them join the colony. |
| **Recruitment** | Vanilla makes the same Pawn `Faction.OfPlayer`. Identity, relationships, provenance and history remain, but old NPC availability permanently ends. Phase 3 keeps `Defected` + `OutOfCustody(PlayerColonist)`; future Phase 4 participation comes from the real colony / PlayerProxy, never a former-contractor simulator. |

Do not use vanilla `WillJoinColonyIfRescued` for a contractor rescue. Rescue and recruitment must be visibly separate outcomes.

**Preferred future physical question:** captor state → liberation → free contractor temporarily behaves on their own contractor side → leaves the map → Network stores the same pawn again. Reuse of the existing temporary encounter-faction architecture is the preferred direction: it provides only a physical vanilla shell, while Network Actor / KnownCharacter remain identity. Organizations must not become permanent RimWorld factions. The future alternative spike must first prove the exact faction, guest and Lord transitions safely; none is implemented here.
