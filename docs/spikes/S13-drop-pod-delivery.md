# S13 — Drop-pod delivery

**Verdict: PARTIAL — the normal home-map delivery PASSED in the owner's runtime test; the edge cases
(steps 3–9 below) are NOT RUN — OWNER RUNTIME VALIDATION REQUIRED**

The environment that built Phase 2 cannot launch RimWorld. Compiling, the decompiled-source reading and the
headless tests are **not** a runtime pass. The one runtime result below comes from the owner's own test.

## Question

Phase 2 delivers procurement goods by **vanilla drop pods** (STATE_MACHINES § 4.4). Does delivery land or reroute
cleanly in every situation, so that **no contract becomes permanently stuck**?

1. Normal home map: the committed goods (exact def, stuff, quality and count) land near a trade beacon or comms
   console.
2. The preferred map is gone (abandoned or destroyed): the goods go to another player home map.
3. A roofed or crowded destination: pods never punch roofs; a clear spot is chosen near a beacon, console or
   colony building.
4. No suitable drop spot anywhere: nothing is created and no silver is taken. Retried daily; after three misses
   the contract goes to **Hold** with a letter.
5. Incoming transporters when a map is removed: pods already launched are ordinary vanilla objects, and the
   Network holds no reference to them.
6. Fallback to another home map.
7. Retry and Hold: a Hold that outlasts 15 days fails the contract (`UndeliverableNoHome`), refunds any balance
   already paid, and leaves nothing stuck.

**PASS**: every case lands or reroutes cleanly, no contract stays non-terminal forever, no red errors come from
`TheNetwork`, and the balance is charged only when the goods can actually land.

## Build

Code under test:
- `Integration/DeliveryAdapter.cs` (`DropPodDelivery`: `Plan`, `Deliver`, `DefaultHomeMapId`).
- `Domain/Contracts/ProcurementOutcomes.cs` (`BeginDelivery`, `TryDeliverNow`, `DeliveryFailed`,
  `PaymentDefault`, `PartialHandover`).
- `Integration/SiteAdapter.MakeThings` (shared with sites: committed stuff and quality, minified buildings).

How it works:
- `Plan` is side-effect free. It lists player home maps (preferred first, then by `uniqueID`) and searches each
  with `DropCellFinder.TryFindDropSpotNear(center, map, out cell, allowFogged: false, canRoofPunch: false,
  allowIndoors: false)`. Centers are tried in order: orbital trade beacons, comms consoles, colony buildings,
  then the map centre. The search runs under `Rand.PushState(seed)`. It never uses `TradeDropSpot` (which
  shuffles and can log errors) and never picks a random cell.
- Only when a plan succeeds is the balance charged. `Deliver` then creates the Things from the committed payload
  and calls `DropPodUtility.DropThingsNear(cell, map, things, 110, false, false, canRoofPunch: false, forbid:
  false, allowFogged: false)`. If Thing creation fails, everything is discarded, the def is marked unusable,
  and the contract is **Voided** with a full refund.

## Environment of this pass

Not run in RimWorld. Evidence of the **Network side only**, with fake ports (not vanilla behaviour):
- `Procurement.FullSuccessDeliversCommittedPayload`: the delivered payload is exactly the committed def, count
  and quality; the balance is charged once.
- `Delivery.HoldRerouteAndLimit`: with no home map the contract goes to Hold and nothing is charged. When a home
  exists again, delivery is rerouted there. With no drop spot there are three retries, then Hold, and after 15
  days `Failed(UndeliverableNoHome)`.
- `Payment.DefaultHoldsOrHandsOver`: an unpaid balance leads to AwaitingPayment (pay later) or a partial handover.
- `Persist.EveryContractStateRoundTrips`: a contract in Hold survives save/load with its delivery state.
- The soak harness makes one delivery attempt in twenty fail: no stuck contract in 1,080 simulated days.

## Owner steps

Setup: Development mode on. The debug menu category is **"The Network (Phase 2)"**. Use "Toggle comms-gate
override" only if you have no console. Leave the fee waiver off, so the balance charge is real.

1. **Normal home.** Colony with an orbital trade beacon, silver in range and a powered comms console. Procurement
   tab: pick `Steel`, exactly 150, a Fixer, Open, "Ask for quotes". Use "Close bidding window now…" and then
   accept a quote. Use "Force outcome band on next resolution… → Triumph", then "Run next operation checkpoint
   now…" four times.
   Expect: pods land near the beacon (not on a roof), 150 steel arrive, the balance leaves beacon silver, and a
   "Delivered" letter appears. **Dump contract…** shows `delivered` with the map id.
2. **Quality and stuff.** Repeat with a quality, stuff-made weapon (for example a longsword). Expect the delivered
   item's quality and stuff to match the `outcome` line and the `payload` in **Dump contract…**.
3. **Roofed or crowded.** Roof over the beacon area and fill the nearby cells. Expect pods to land at the next
   clear, unroofed spot near a console or colony building. No roof punching.
4. **No drop spot.** Use "Force the next 3 delivery attempts to fail (→ Hold)" before the last checkpoint.
   Expect three daily retries ("Deliver now…" skips the wait), then a "Delivery on hold" letter. No silver is
   charged while nothing can land. Clear the force and "Deliver now…": delivered.
5. **Preferred map missing.** Two home maps: the contract is addressed to map A (the current map when posted).
   Abandon map A before delivery. Expect delivery to map B and **Dump contract…** `delivered` on B.
6. **No home at all.** A caravan-only situation (abandon every home map) before delivery. Expect Hold with a
   letter and daily retries. Settle a new home within 15 days: delivered there. Otherwise, after 15 days:
   `Failed(UndeliverableNoHome)`, and any paid balance is refunded when a home exists.
7. **Pods in flight and map removal.** Trigger a delivery, then abandon the destination map while the pods are
   falling. Expect no `TheNetwork` errors. The contract is already closed (goods launched), and vanilla handles
   the pods.
8. **Payment default.** Move the silver out of beacon range before delivery. Expect a "Payment due" letter and the
   contract held (AwaitingPayment), or a partial handover, depending on the contractor. "Pay the … you owe" in
   the Procurement tab releases the goods. With no answer for seven days, a partial handover happens.
9. Save and reload in the middle of steps 4, 6 and 8. Expect the same state after loading, the retry job
   recreated if missing ("Validate all" reports it), and the same outcome.

## Result

**Owner runtime test (Phase 2 vertical slice, before the merge of PR #3): the normal path PASSED.**
Three contractors bid on an exact request for 500 of a modded crystal item. The contract was paid from
beacon-accessible silver, insured, ran late (a contractor delay), and came back with a partial result of 499 of
500 (the contractor was a Solo). The player accepted the partial fulfilment, the money was adjusted and refunded
correctly, and 499 items arrived by vanilla transport pod on the home map.

Covered by that run: step 1 (normal home, a real balance charge from beacon silver) and the partial-result
delivery path. **Not yet verified in game:** step 2 (quality and stuff on a crafted item), step 3 (roofed or
crowded drop area), step 4 (no drop spot → retries → Hold), step 5 (preferred map missing), step 6 (no home at
all), step 7 (map removed while pods are in flight), step 8 (payment default and handover) and step 9 (reload in
those states).

## Logs

—

## Consequence

The drop-spot search order and the retry count (3), the Hold limit (15 days) and the payment grace period
(7 days) are `NetworkContractKindDef` tuning (`1.6/Defs/ContractKindDefs/TheNetwork_ContractKinds.xml`). If
vanilla drop behaviour surprises us, the fix is confined to `DropPodDelivery`. The Domain only sees
`IDelivery.Plan / Deliver`.
