# S19 — Loot without extermination

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

This is the main Phase 1 gameplay proof and it has **not** been run.

## Question

Generate a guarded Network opportunity; enter; recover **only part** of the target payload; leave by caravan (and
separately by transport pods) while some defenders are alive. Does the vanilla `ItemStash` + threat-part
composition (with the `Site` def's comps: `TimedDetectionRaids`, `EnterCooldown`, `DefeatAllEnemiesQuest`,
`TimedMakeFactionHostile`, timeout, and `Site.ShouldRemoveMapNow`) let the player leave, and does anything require
all enemies dead?

Pass criteria: the caravan or pods can leave; recovered items stay recovered; the opportunity resolves `Claimed`
with a partial `recoveredBand`; history says "partial recovery"; no duplicated loot on re-entry or a second
departure; the site cleans up as vanilla does. Leaving empty-handed resolves as `Abandoned`.

## Build

Source commit `SOURCE_COMMIT`. Code under test: `OpportunityService` (engagement, samples, `ResolveEngagement`,
`RecoveredEstimate`), `SiteAdapter.TrySampleRemaining`, `SiteCallbacks`, and the comp's pre-removal check
(`WorldObjectComp_NetworkSite.CompTickInterval`).

## How claim accounting works (what to watch)

**Rule: items the player brought into the site map never count as recovered Network payload.** Recovery is measured
only as the depletion of the site's own stock, never by adding up what the player carried out:

- **Site stock (`initial`).** Counted once, at `PostMapGenerate`. In 1.6 the map is generated and `PostMapGenerate`
  runs *before* the arriving caravan or pods put anything on it (`CaravanArrivalAction_VisitSite.DoEnter` and
  `TransportersArrivalAction_VisitSite.Arrived` call `GetOrGenerateMap` first). No later sample may set it, because
  a later sample can include the player's cargo.
- **Samples.** A sample counts every item of the target def on the map: ground, containers, every pawn's inventory,
  corpses, minified things. Samples are taken every 2,500 ticks while the map exists, after each caravan departure
  (`PostCaravanFormed` runs after the caravan's pawns have exited the map), and **once more right before vanilla
  removes the map**: `MapParent.TickInterval` ticks the comps and then calls `CheckRemoveMapNow` in the same call,
  so the comp takes the final sample when `ShouldRemoveMapNow` is true. Anything that left by pods or shuttle is
  simply no longer on the map at that moment.
- **Recovered** = `initial − last sample`, capped at the committed target count. The band (Little / Some / Most /
  All) is what history reports. Nothing waits for defenders to die.
- **Why the player's own copies cannot inflate it.** They are not in `initial`. While on the map they only *raise* a
  sample (lowering the estimate); when they leave, the sample drops back to the site's remaining stock. Carried-out
  totals (the caravan's count) are kept as a diagnostic only and never used. Pod cargo is not counted separately at
  all, so it cannot be counted twice.
- Worked examples (headless tests): cache 100, player brings 100 and takes none → 0, `Abandoned`
  (`LeftEmptyHanded`); cache 100, player brings 50, takes 30, leaves with 80 → 30; the same by pods → 30.

Known limitations (under-count, never over-count):

- Own copies the player **leaves behind** on the site map raise the final sample, so the estimate is lower by that
  amount (it may even read as `Abandoned`).
- If the map is removed by a path that skips the comp tick (dev tools, moving the colony, a gravship leaving the map
  in a way that abandons it), the estimate uses the last periodic or caravan sample, which may still include cargo the
  player was carrying: again only an under-count.
- Unchanged site-side approximations: cache items destroyed on the map (fire) count as taken; same-def stock the
  holder already had on the map counts as the site's stock (capped at the target).
- To check in game: a gravship landing on a Network site. If its cargo were on the map before `PostMapGenerate`,
  `initial` would include it; the decompiled code suggests the ship is placed after generation (the gravship gen
  steps only reserve the area), but this is unverified.

## Environment of this pass

Not run. Headless evidence of the accounting logic only (a fake site adapter driven in vanilla's order, not vanilla
maps): tests `Claim.PartialRecoveryByCaravan`, `Claim.BroughtOnlyCargoRecoversNothing`,
`Claim.MixedProvenanceCountsOnlyCache`, `Claim.NoDoubleCountOnReentry`, `Claim.TransportPodsCountedOnce`,
`Claim.NeverInflatedByOwnCargo` (400 randomized sequences: the estimate never exceeds the cache that really left),
`Claim.EmptyHandedAndBadIntel`, `Claim.SettledIsClaimedAll`, `Claim.RecoveryBasisIgnoresHolderStock` pass.

## Owner steps

Setup: detailed logging on; a caravan of 3–4 armed colonists and a pack animal.

**A. Partial by caravan, defenders alive**

1. **Force source kind for next round…** → `HostileFaction` (or `Pirates`), then **Create representative
   opportunity…** → `Steel`. **Dump opportunity…**: note the target count (say 300) and `threat Outpost` or
   `BanditCamp`.
2. Travel there. On arrival, Dump: `Engaged`, `initial ≈ 300`.
3. Without fighting the whole outpost, pick up part of the steel (carry or load into the caravan while forming it)
   and leave from the map edge with everyone. Note how much steel left with the caravan.
4. Expected: the map closes; Dump shows `Claimed`, `recovered ≈ what you left with`, band `Some`/`Most`; a letter
   "Recovered: steel … with some of it"; History: "A partial recovery: your people took some of the steel …".
   Steel at home equals what you carried (no duplication).
5. Record whether leaving was possible while enemies were alive and whether any comp (raids, cooldown, faction
   hostility) interfered.

**B. Partial by transport pods**

1. New guarded opportunity. At the site, place a pod launcher and transport pod with chemfuel (dev "Spawn thing"),
   load part of the target and a colonist, launch to home. Leave with the others by caravan.
2. Expected: `Claimed`; recovered ≈ pods + caravan; no double count (pod cargo counted once even if a sample ran
   while it flew).
3. Last pawns leave by pods: with verbose logging, the log shows "map about to be removed (final sample)" before the
   map is removed, and `last remaining` in Dump equals what was left on the map.

**C. Re-entry and second departure**

1. New opportunity. Leave with part of the loot using **some** colonists (others stay, so the map stays), then send
   the caravan back in with the loot and leave again with the same loot and everyone.
2. Expected: recovered counts that loot once.

**F. Own copies of the target def (provenance)**

1. New opportunity for Steel. Enter with a caravan already carrying about 100 steel of your own.
2. Take nothing from the cache and leave with your own steel. Expected: `Abandoned` ("Left empty-handed"),
   recovered 0; Dump shows the caravan carried ≈ 100 (diagnostic) but recovered 0.
3. New opportunity. Enter carrying 50 steel, take about 30 from the cache, leave with all 80. Expected: `Claimed`,
   recovered ≈ 30 (not 80).
4. Repeat 3, leaving by transport pods instead. Expected: recovered ≈ 30.

**D. Empty-handed**

1. New opportunity; enter; leave without taking anything. Expected: `Abandoned`, letter "Left empty-handed",
   history line; the site cleans up.

**E. Settle** — see [S3](S3-signal-bridge-registration.md) step 3.

## Result

Not run.

## Logs

—

## Consequence

If the composition forces extermination or breaks partial recovery, record exactly what blocked leaving or what
duplicated, and switch Phase 1 to another vanilla composition or to the minimal Network `SitePartDef` fallback named
by ADR-015 ([IMPLEMENTATION_PHASES § 4.4](../IMPLEMENTATION_PHASES.md#44-phase-1-spikes)). The Opportunity state
machine does not change.
