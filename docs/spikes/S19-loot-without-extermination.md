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

Source commit `SOURCE_COMMIT`. Code under test: `OpportunityService` (engagement, samples, `ResolveEngagement`),
`SiteAdapter` (`TrySampleRemaining`, `CountUncountedTransporterCargo`, `CountInCaravan`), `SiteCallbacks`.

## How claim accounting works (what to watch)

- At `PostMapGenerate` the target def on the map is counted (`initial`); a sample job repeats the count every 2,500
  ticks while the map exists.
- `PostCaravanFormed` tallies the target def in the departing caravan once and immediately re-samples the map
  (the caravan's pawns have already left it).
- At `PostMyMapRemoved`, transporters launched from the site tile and not already reflected in a sample add their
  cargo; then `recovered ≈ initial − last sample + that cargo`, capped at the committed target count; the band
  (Little / Some / Most / All) is what history reports. Nothing waits for defenders to die.
- Known approximation: items the player *brought* of the same def and sent home by pods from the site count as
  recovered; items destroyed on the map (fire) count as taken. The band is coarse by design.

## Environment of this pass

Not run. Headless evidence of the accounting logic only (fake site adapter, not vanilla maps): tests
`Claim.PartialRecoveryByCaravan`, `Claim.NoDoubleCountOnReentry`, `Claim.TransportPodsCountedOnce`,
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

**C. Re-entry and second departure**

1. New opportunity. Leave with part of the loot using **some** colonists (others stay, so the map stays), then send
   the caravan back in with the loot and leave again with the same loot and everyone.
2. Expected: recovered counts that loot once.

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
