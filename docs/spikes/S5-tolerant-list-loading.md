# S5 — Tolerant per-element list loading

**Verdict: PARTIAL — the real Verse `Scribe` loader was exercised headlessly; an in-game load of a corrupted save
is still required.**

## Question

Can Network lists load element by element under Scribe (`NetScribe.LookListTolerant`), so that a corrupted
element is quarantined with its raw XML, its siblings load, and Scribe's state is not corrupted?

## Build

Source commit `293e363`. Code under test: `Kernel/NetScribe.cs` (`LookListTolerant`, `TryLoadElement`).

## Environment of this pass

Mono 6.8, the real `Assembly-CSharp 1.6.9676.17735` `ScribeLoader`/`ScribeSaver`, outside the game
([README](README.md#environment-of-this-pass)). Test-only adaptations: `Log.*` and `DeepProfiler` are stubbed
(they need the Unity player) and `GenTypes` is shown TheNetwork.dll as if it were a loaded mod.

## Steps run

`Tests/run-tests.sh` → `Persist.TolerantLoaderQuarantinesAndContinues` loads
`Tests/Fixtures/save/v1_intel_store_corrupted.xml`: an `<intel>` store with a good request, an element of an
unknown persisted class, an element with an unreadable `state`, and an element whose nested `topic` has an
unknown class, followed by a sibling node that must still be read.

Related tests: `Settings.MigrationFromV0AndQuarantine` (templates: malformed enum, missing id, duplicate id, an
unconstructible entry kept verbatim), `Persist.StateRoundTrip` (every store through save and load),
`Kernel.JournalBounds` (unknown event classes dropped quietly).

## Result (headless)

| Check | Result |
|---|---|
| Element of an unknown class → not loaded, recorded as quarantine with reason `UnknownClass:…` and its raw XML | yes |
| Siblings load completely (state, round, seed, leads, topic) | yes |
| Unreadable enum → entity kept, `quarantinedReason = MalformedEnum:state` (never guessed) | yes |
| Scribe state intact: the node after the store reads correctly | yes |
| Red errors from the list loader | none |
| Vanilla lines | exactly one, from vanilla's own `Scribe_Deep` for the deliberately corrupted **nested** class ("Could not find class TheNetwork.Kernel.NoSuchTopic …"); the element still loaded with an empty topic, which the validator invalidates |

## Owner steps (in game)

1. Make a save with a Fixer and one search. Close the game.
2. In the save XML, inside `<li Class="TheNetwork.NetworkWorldComponent">` → `<actors>` → a Fixer's
   `<components>`, change one `Class="TheNetwork.Persist.FixerProfile"` to `Class="TheNetwork.Persist.Missing"`.
   In `<intel><requests>`, change a `<state>` value to `Nonsense`.
3. Load. Expected: no red error from The Network; the Fixer loads without that component; the request is kept but
   skipped (Dev: **Quarantine and failed-consumer report** lists both, the component with its raw XML); everything
   else works.

## Consequence

If in-game loading differs (for example the post-load pass double-registers elements), the documented fallback is
plain `Scribe_Collections` plus post-load validation (DATA_MODEL § 17.2).
