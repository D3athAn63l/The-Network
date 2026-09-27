# S6 — Removing and re-adding The Network

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

## Question

With an active Network site, a running search and some history: does removing The Network give **exactly one**
error (the missing WorldComponent class; nothing from the site comp), prepared or not, with the game staying
playable? Does re-adding the mod bootstrap cleanly?

## Build

Source commit `SOURCE_COMMIT`. Code under test: `NetworkWorldComponent`, `Integration/RemovalPreparer.cs`,
`WorldObjectComp_NetworkSite`, the vanilla-only letters.

## Environment of this pass

Not run. Non-runtime evidence (not a pass): nothing Network-owned is stored in vanilla containers except the
WorldComponent node and one value inside each bound site node (ignored when the comp is gone). Letters use vanilla
classes (`LetterDefOf`, `LetterStack.ReceiveLetter`); site labels and quest tags are plain strings; no
`Scribe_Defs`/`Scribe_References` to Network defs anywhere. The MainButtonDef and the patch disappear with the mod.

## Owner steps

Setup: a colony with a Network site not yet visited, a search in `Searching`, a few history lines.

**A. Unprepared removal**

1. Save as `S6-unprepared`. Quit. Disable The Network. Restart, load.
2. Count red errors. Expected: exactly one, about the missing class `TheNetwork.NetworkWorldComponent`. None about
   `WorldObjectComp_NetworkSite`, `networkOpportunityId`, letters or defs.
3. The site is still on the world map with its vanilla label and timeout; visiting it generates a normal stash
   map. Letters in the archive open.

**B. Prepared removal**

1. Load the setup save with the mod. Mod Settings → The Network → **Prepare this save for removal** → confirm.
   The summary lists sites unbound, searches called off and silver refunded (drop pod).
2. Save as `S6-prepared`, quit, disable the mod, restart, load. Expected: exactly one error (the component class).

**C. Re-adding**

1. Enable The Network again and load `S6-prepared` saved **without** the mod (after B, save once more while the
   mod is disabled, then re-enable). Expected: a fresh bootstrap (log: "The Network bootstrapped"), a new cast
   snapshot, no errors. The old unbound site stays a vanilla site.
2. Load the original setup save (never saved without the mod) with the mod enabled: everything continues.

## Result

Not run.

## Logs

—

## Consequence

More than one error without preparation breaks A4. Candidate causes and fixes: the comp field (drop the persisted
field; see S2's fallback) or a Network type inside a vanilla container (none are known).
