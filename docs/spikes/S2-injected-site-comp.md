# S2 — Injected site comp

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

## Question

`WorldObjectComp_NetworkSite`, added to the vanilla `Site` WorldObjectDef by the single XML patch
(`1.6/Patches/TheNetwork_SiteComp.xml`): do all callbacks fire; does its data persist; is it inert on ordinary
sites; and does removing the mod leave the site working with **no errors from the comp**?

## Build

Source commit `293e363`. Code under test: `Integration/WorldObjectComp_NetworkSite.cs` (including the
pre-removal check in `CompTickInterval`), `Integration/SiteCallbacks.cs`, the patch.

## Environment of this pass

Not run ([README](README.md#environment-of-this-pass)). Non-runtime evidence (not a pass): the patch targets
`Defs/WorldObjectDef[defName="Site"]/comps`, which exists in Core `WorldObjectDefs/WorldObjects.xml`; defs with
`ParentName="Site"` inherit the comp (unbound, inert). The comp stores one value (`networkOpportunityId`) inline in
the site node; `WorldObject.ExposeData` builds comps from the def's current list, so after removal the node is an
unknown child that Scribe ignores. The comp does nothing when `networkOpportunityId` is 0.

## Owner steps

1. Detailed Network logging on. Create a guarded opportunity (S1 step 2). Also let a **vanilla** item-stash quest
   generate (or use a vanilla dev action that spawns a site).
2. Visit the Network site: log shows `Site <id> (O<n>): PostMapGenerate`. Form a caravan with part of the loot:
   `PostCaravanFormed`. Leave completely: `map about to be removed (final sample)` exactly once, then
   `PostMyMapRemoved`, then `PostDestroy`. The same order when the last pawns leave by transport pods.
3. Visit the vanilla site: **no** `[TheNetwork]` line for it (inert comp).
4. Create another Network site; save; reload. Inspect the site on the world map: the Network inspect line is
   still there; **Dump opportunity…** shows the same site id. Save file: the site node contains
   `<networkOpportunityId>`.
5. Removal (combine with [S6](S6-network-removal.md)): with an active Network site, save, disable The Network,
   restart, load. Count errors: none may mention `WorldObjectComp_NetworkSite` or `networkOpportunityId`.
   Visit the site: it behaves like a vanilla stash site; its timeout still runs.

## Result

Not run.

## Logs

—

## Consequence

A comp-originated error on removal breaks acceptance A4. The fallback is to keep the binding only in the Network's
own store (resolve by `WorldObjectRef` and quest tag) and drop the persisted comp field; the callbacks would then
come from quest-tag signals (`MapGenerated`, `MapRemoved`, `Destroyed`) through the SignalBridge.
