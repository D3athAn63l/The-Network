# S7 — Removing an item mod mid-search and mid-site

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

## Question

An item mod (for example Beyond Our Reach) is removed while a search for its item is `Searching` and a Network
stash holding its items exists. Expected: the search is `Invalidated` with a refund and a letter; the opportunity
is `Invalidated` with a letter; vanilla errors are limited to the missing-def Things inside the stash; The Network
adds **no** errors.

## Build

Source commit `4dca3d9`. Code under test: `Core/NetValidator.cs` (external references on the first tick
after load), `IntelService.Invalidate`, `OpportunityService.Invalidate`, `Kernel/Refs.cs` (`DefRef`).

## Environment of this pass

Not run. Headless evidence of the Network side (not a pass): `Intel.InvalidateTopicMissing` (a topic that no
longer resolves → `Invalidated`, the running round's fee refunded, a letter event, a history record);
`Persist.ReferencesAreStrings` (a `DefRef` to a def that does not exist loads with no vanilla error and keeps its
label snapshot); `Intel.ThingCreationFailureFullRefund` (a def whose Things fail at runtime → full refund, def
unusable for the session).

## Owner steps

1. Install any item mod (Beyond Our Reach is the reference case; any mod with a stackable item works). In the
   Network catalog find its item; set it **Allowed** if it is marked unusual.
2. Ask a Fixer about it (search `Searching`). With **Force lead on next round** and **Run the next search round
   now…** on a second search, also create a site that holds its items. Save.
3. Quit, disable the item mod, restart, load.
4. Expected on the first tick after load:
   - letter "Inquiry called off: <item>" with the refund; a drop pod with the silver;
   - letter "Lead called off: <item>" for the opportunity; the vanilla site stays and times out normally;
   - one aggregated info line `[TheNetwork][Compat] N references to missing ThingDef '<def>' were invalidated.`;
   - **no** `[TheNetwork]` error; any red errors come from vanilla loading the missing-def Things inside the
     stash's `ThingOwner` (expected; count and attach them);
   - History still reads "… <item> …" from its snapshot label.
5. Re-enable the item mod and load the post-removal save: invalidated entities stay invalidated (terminal);
   the catalog offers the item again.

## Result

Not run.

## Logs

—

## Consequence

Any Network-originated error here means a missing silent-resolution path; fix it in the validator or the owning
service. Vanilla errors for the stash contents are outside The Network's control (documented in
COMPATIBILITY § 3).
