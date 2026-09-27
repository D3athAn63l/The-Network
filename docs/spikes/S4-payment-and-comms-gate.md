# S4 — Payment, refunds and the Comms Console gate

**Verdict: NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

## Question

1. Fees through vanilla beacon trade silver (`TradeUtility.ColonyHasEnoughSilver` / `LaunchSilver` on the home map
   with the most launchable silver): taken correctly; a clear refusal without beacons or silver.
2. Refunds as silver by drop pod at the home map's trade drop spot.
3. The Comms Console gate: every outgoing command needs a spawned `Building_CommsConsole` (any subclass) on a
   player home map with `CanUseCommsNow`. Refused with a reason when there is none, it is unpowered, or an
   electricity-disabling condition (solar flare) is active. Reading still works; nothing in progress is lost;
   commands work again when a console is usable.

## Build

Source commit `4dca3d9`. Code under test: `Integration/CommsAndPayment.cs`, `Domain/Intel/IntelService.cs`
(gates, charges, refunds), `UI/MainTabWindow_Network.cs` (disabled buttons with tooltips).

## Environment of this pass

Not run. Headless evidence of the **Network side only** (fake ports, not vanilla behaviour): tests
`Intel.SubmitGateAndCharge`, `Intel.SubmitRefusals`, `Intel.CommsLossDoesNotStopSearch`,
`Intel.CancelRefundPolicy`, `Intel.InvalidateTopicMissing` pass: refused commands charge nothing and create
nothing; a search keeps resolving rounds with comms down; continue/end are refused then work again; the ledger
records every charge and refund in the same step.

## Owner steps

Do **not** use the dev fee waiver or comms override for this spike.

1. Colony with an orbital trade beacon, ~1,000 silver inside its range, a powered comms console.
2. Network tab → pick a Fixer and `Steel` → the dialog shows the fee → confirm. Beacon silver drops by exactly
   that fee. **Dump intel request…**: `money PlayerPaid <fee>`.
3. Move the silver out of beacon range: the request button is disabled with "not enough silver within range of an
   orbital trade beacon"; nothing is charged.
4. Cancel a search early in its first round (**Dump intel request…** shows the due tick; cancel in the first
   half): a drop pod with half the fee lands at the trade drop spot. Cancel another late: nothing.
5. Remove the item's mod or use a request whose round fails (see S7) to see a full refund by pod.
6. Deconstruct the console: the banner says no console; every button (request, continue, end, cancel) is
   disabled with its reason; opening the tab and reading still works. With a search running, advance time
   ("Run the next search round now…" is fine): the lead letter still arrives.
7. Rebuild the console and cut its power: reason "no power". Restore power, then add a Solar flare
   (vanilla debug: add game condition): reason "electricity is down". End the flare: commands work.
8. No home map with silver at all (for example a caravan-only situation): refusal "you have no home to pay from";
   a refund due at that moment is recorded as pending and retried daily until a home exists.

## Result

Not run.

## Logs

—

## Consequence

The final payment UX is decided here (for example whether to also accept silver in a stockpile without a beacon).
Any change is confined to `PaymentAdapter`; the Intel machine only sees `CanCharge / TryCharge / TryRefund`.
