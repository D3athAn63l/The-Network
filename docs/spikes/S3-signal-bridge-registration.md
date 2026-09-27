# S3 — SignalBridge registration

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

## Question

The `SignalBridge` (an `ISignalReceiver`) is registered with `Find.SignalManager` from the WorldComponent's first
tick. `SignalManager` is not persisted, so registration must happen on every new game and every load, never twice.
Do signals from Network-tagged world objects arrive after each path?

Phase 1 uses one signal: `TheNetwork.Opp.<id>.MapSettled`, because `WorldObjectComp.PostMyMapSettled` is
**not virtual** in 1.6 and cannot be overridden (found while implementing; see the PR's deviations list).
`MapParent.Notify_MyMapSettled` sends the quest-target signal `MapSettled` for the site's quest tags before the
site is destroyed.

## Build

Source commit `293e363`. Code under test: `Integration/SiteCallbacks.cs` (`SignalBridge`),
`NetworkWorldComponent.RunStartup` (through `NetworkRuntime.EnsureStarted`).

## Environment of this pass

Not run. Non-runtime evidence (not a pass): `Register()` checks `receivers.Contains(this)` before
`RegisterReceiver` (vanilla logs "Tried to register the same receiver twice" otherwise); each game gets a new
runtime and a new `SignalManager`.

Whether a bridge from an earlier runtime can stay registered in the same `SignalManager` (a reload within one game
session) is exactly what this spike answers; the code does not assume either way. What exists so a duplicate is
easy to spot and harmless (static guards, not a redesign):

- Each bridge instance has a number. Registration logs `Signal bridge #N registered (R receivers, K Network
  bridge(s))`; if K > 1 a one-time warning says so and asks for it to be reported here.
- Every handled signal logs `Signal TheNetwork.Opp.<id>.MapSettled handled by bridge #N.` A bridge that is not
  the current runtime's logs `… at stale bridge #N: ignored.` and does nothing, so an old bridge could never act on
  old state or send a second letter.
- `OnMapSettled` is idempotent: a settled opportunity is terminal, so a second `MapSettled` changes nothing.
- A failed start-up ignores signals entirely (the session is inactive).

## Owner steps

1. Detailed Network logging on. New game: after the first tick the log shows
   `Signal bridge #N registered (…, 1 Network bridge(s))` once.
2. Save, load: exactly one `Signal bridge #N registered` line for the load, with `1 Network bridge(s)`. Load a
   different save in the same session, then return to the menu and load again: one line per load, never
   `2 Network bridge(s)` or the one-time warning, and never the vanilla "Tried to register the same receiver twice".
   **New game → save → load → reload in the same game session**, then settle a Network site (step 3): the log must
   show exactly one `handled by bridge #N` line for the `MapSettled` signal and no `stale bridge` line; History must
   show one full recovery and one letter.
3. Settle test: create a Network opportunity (S1), visit it, clear the immediate threats, use the vanilla
   **Settle** command on that map. Log shows `Signal TheNetwork.Opp.<n>.MapSettled`; **Dump opportunity…** shows
   `Claimed` with `settled` and band `All`; History shows a full recovery.
4. Repeat step 3 after a save/load between visiting and settling.

## Result

Not run.

## Logs

—

## Consequence

If signals do not arrive after load, settling would fall to reconciliation (the post-window `opp.sample` job
would see the site gone and mark it `Vanished`, which is wrong for a settle). The fix would be re-registering in
`FinalizeInit` as well, or detecting a player settlement on the site tile during reconciliation.
