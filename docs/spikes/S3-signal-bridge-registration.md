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

Source commit `4dca3d9`. Code under test: `Integration/SiteCallbacks.cs` (`SignalBridge`),
`NetworkWorldComponent.EnsureStarted`.

## Environment of this pass

Not run. Non-runtime evidence (not a pass): `Register()` checks `receivers.Contains(this)` before
`RegisterReceiver` (vanilla logs "Tried to register the same receiver twice" otherwise); each game gets a new
runtime and a new `SignalManager`.

## Owner steps

1. Detailed Network logging on. New game: after the first tick the log shows
   `Signal receiver registered (…)` once.
2. Save, load: exactly one `Signal receiver registered` line for the load. Load a different save in the same
   session, then return to the menu and load again: one line per load, and never the vanilla
   "Tried to register the same receiver twice".
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
