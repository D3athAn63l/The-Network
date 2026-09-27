# S8 — Reload determinism

**Verdict: PARTIAL — the Network's own determinism is shown headlessly across a real Scribe save/load; the vanilla
tile finder and the Odyssey case need an in-game run.**

## Question

Resolve Intel, reload a save made before the round's due tick, let it resolve again: identical tile, amount,
archetype and threat both times. Repeat with Odyssey active.

## Build

Source commit `4dca3d9`. Code under test: `IntelService.ResolveRound`, `OpportunityGenerator`,
`SourceResolver`, `LeadReporter`, `SiteAdapter.TryFindTile`, `WorldFactsAdapter.PickStuff`.

## How determinism is built

- Every request stores `seed` (from the network seed and its id) and `rerollNonce`; each round draws from
  `NetRng(seed, "intel.round.<nonce>", round)`; the opportunity uses named streams of its own seed
  (`opp.source`, `opp.quantity`, `opp.threat`, `opp.quality`, `opp.extra`, `opp.window`, `lead.report`).
- The world threat basis is frozen when the round **starts** (`roundThreatBasis`), so colony wealth changes during
  the round do not change its outcome.
- The round resolves at its committed due tick. The vanilla tile finder runs under `Rand.PushState(seed)` with
  `selectLandmarkChance = 0` (Odyssey's landmark roll mixes in `TicksGame`), surface only; stuff and quality are
  picked under pushed seeds; results are committed at once.
- Nothing reads ambient `Verse.Rand` for a Network decision.

## Environment and steps run (headless)

Mono 6.8 with the real Scribe ([README](README.md#environment-of-this-pass)). `Intel.DeterministicAcrossSaveLoad`:
two identical Network states; in the second, the request is saved and reloaded through Scribe before round 1 and
again between rounds; six rounds resolve by the seeded draws. Compared per lead: hidden divergence, reported
amount range, reported threat, true amount, source context, threat points, tile, cargo lines. `Persist.StateRoundTrip`
checks that every persisted field survives a save/load.

## Result (headless)

All compared values identical. This covers the Network's logic only; the fake world adapter returns fixed facts.

## Owner steps (in game)

1. Development mode, detailed logging. Ask a Fixer about an item. **Dump intel request…**: note `seed` and `due`.
2. Save (`S8-before`). Let the round resolve at its due tick by playing (not "Run the next search round now…",
   which resolves at the current tick). **Dump opportunity…**: note tile, target count, stuff/quality, threat
   profile and points, source kind and holder.
3. Load `S8-before`. Play to the same due tick without changing factions or the world. Dump again: all values
   identical.
4. Repeat with Odyssey active and a request whose opportunity may land near landmarks.
5. Optional: repeat with "Run the next search round now…" at the same tick in both runs.

## Consequence

A difference in tile only (other values equal) points at vanilla `TileFinder` consuming extra `Rand` state; the fix
is to commit the tile before the round resolves (at round start) instead of at resolution.
