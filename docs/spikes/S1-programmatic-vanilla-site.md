# S1 — Programmatic vanilla Site

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

## Question

Does a vanilla `Site` built outside QuestGen — the vanilla `ItemStash` part carrying Things the Network
created, plus one vanilla threat part — behave like a vanilla item-stash quest site? Map generation places the
stash; the timeout removes the site when it is never visited; leaving removes the map.

Pass criteria ([RIMWORLD_INTEGRATION § 5](../RIMWORLD_INTEGRATION.md#5-runtime-spikes)): the stash items are
present on the map; no errors; timeout and removal behave like vanilla item-stash quests.

## Build

Branch `claude/phase-1-foundation-intel`, source commit `293e363` (see the PR for the head SHA).
Code under test: `Integration/SiteAdapter.cs` (`Materialize`, `ChooseThreatPart`, `MakeThings`).

## Environment of this pass

Linux container without the RimWorld player ([README](README.md#environment-of-this-pass)). **Not run.**

What exists as non-runtime evidence (not a pass):

- The code compiles against `Assembly-CSharp 1.6.9676.17735`.
- It uses only APIs read in the decompiled 1.6 source: `SiteMaker.MakeSite(IEnumerable<SitePartDef>, PlanetTile,
  Faction, bool, float?, WorldObjectDef)`; `SitePart.things` set exactly like
  `SitePartWorker_ItemStash.Notify_GeneratedByQuestGen` does (`new ThingOwner<Thing>(part, false)`,
  `dontTickContents = true`, `TryAddRangeOrTransfer(list, false)`); `GenStep_ItemStash` spawns
  `parms.sitePart.things` through BaseGen's `stockpileConcreteContents`; `TimeoutComp.StartTimeout`;
  `MapGenerator.GenerateMap` runs the GenSteps before `MapParent.PostMapGenerate`.
- Every vanilla generator call (tile finder, `ThingMaker` with quality, site part parameters, manhunter kind)
  runs inside `Rand.PushState(seed)` and its result is committed immediately.

## Owner steps

1. New colony (Crashlanded is fine), development mode on. Mod Settings → The Network → tick
   **Detailed Network logging** (verbose `[TheNetwork][Sites]` lines).
2. Debug menu → The Network → **Create representative opportunity…** → `Steel`. Then again with
   `Gun_AssaultRifle` (quality + single stacks) and `MealSurvivalPack`. For a guarded one first use
   **Force source kind for next round…** → `HostileFaction` (Outpost) or `Mechanoids` (SleepingMechanoids);
   for an unguarded one → `AbandonedCache`.
3. For each: **Dump opportunity…**. Note the payload lines (def, count, stuff, quality), the threat profile
   and points, the site id. On the world map the site is labelled `cache: <item>`, shows the vanilla timeout and
   the inspect line "Network lead from …" (for Intel-made sites).
4. Visit the guarded Steel site with a caravan. On arrival check:
   - the stash exists and holds exactly the dumped payload (count per def, quality, stuff);
   - the threat part generated (outpost buildings and defenders / sleeping mechs / manhunters);
   - `Player.log` has no red errors. With verbose logging: `…PostMapGenerate` and the opportunity is `Engaged`
     (Dump opportunity: `initial` equals the target count).
5. Leave with every colonist (form a caravan at the edge). The map is removed and the site disappears, as with a
   vanilla stash quest. Dump opportunity: `Claimed` or `Abandoned` depending on what you took.
6. Create another opportunity and never visit it. Let the window pass (dev time controls, or play on). The site
   is removed by its vanilla timeout; Dump opportunity shows `Expired`, then `Closed` a day later; History shows
   "The lead on … went cold before anyone got there."
7. Repeat step 2 for a minifiable building set to **Allowed** in the Network catalog (for example `Armchair`):
   the stash holds a minified armchair.

## Result

Not run.

## Logs

—

## Consequence

If the stash is empty, the Things differ from the payload, the threat part fails to generate, or any error
appears: record it here. Per [IMPLEMENTATION_PHASES § 4.4](../IMPLEMENTATION_PHASES.md#44-phase-1-spikes) the
fallback is a different vanilla composition or a minimal Network `SitePartDef` (ADR-015's upgrade path); Phase 0
is not redesigned.
