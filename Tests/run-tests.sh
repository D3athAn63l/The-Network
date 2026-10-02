#!/usr/bin/env bash
# Builds The Network, then builds and runs the headless tests under Mono.
#
#   Tests/run-tests.sh <RimWorld Managed folder> <path to 0Harmony.dll>
#   RIMWORLD_MANAGED=... HARMONY_DLL=... Tests/run-tests.sh
#
# 0Harmony.dll is needed by the TEST RUNNER only (it stubs Unity-only logging so Verse's Scribe can run
# outside the game). The mod never references Harmony; a test checks that.
set -euo pipefail
cd "$(dirname "$0")/.."
MANAGED="${1:-${RIMWORLD_MANAGED:-}}"
HARMONY="${2:-${HARMONY_DLL:-}}"
[ -n "$MANAGED" ] && [ -f "$MANAGED/Assembly-CSharp.dll" ] || { echo "usage: Tests/run-tests.sh <RimWorld Managed folder> <0Harmony.dll>" >&2; exit 1; }
[ -n "$HARMONY" ] && [ -f "$HARMONY" ] || { echo "error: 0Harmony.dll not found (needed by the test runner only)" >&2; exit 1; }

./build.sh "$MANAGED"

echo "### Source scan: no code path names a specific item, faction or mod (A11)"
if grep -rniE "tenebri|beyond ?our ?reach|\bBOR_|grandmaster|regennanite" Source/ ; then
  echo "FAIL: Source names a specific mod or item" >&2; exit 1
fi
if grep -rn "HarmonyLib\|0Harmony" Source/ ; then
  echo "FAIL: Source references Harmony" >&2; exit 1
fi
echo "ok"

echo "### Source scan: spatial continuity creates no pawn, caravan or world object (SPATIAL § 10)"
if grep -rnE "PawnGenerator|CaravanMaker|WorldObjectMaker|MakeWorldObject|GeneratePawn|WorldObjects\.Add|SpawnSetup" Source/TheNetwork/Domain/Spatial Source/TheNetwork/Integration/SpatialWorldAdapter.cs ; then
  echo "FAIL: spatial code creates world things" >&2; exit 1
fi
echo "ok"

echo "### Source scan: abstract charter spawns no craft and moves no money (ADR-045)"
if grep -rnE "ThingMaker|GenSpawn|SkyfallerMaker|TransportShuttle|CompShuttle|DropPodUtility|ctx\.payment|MoneyRecord|ledger" Source/TheNetwork/Domain/Spatial Source/TheNetwork/Integration/SpatialWorldAdapter.cs ; then
  echo "FAIL: spatial code spawns things or touches money" >&2; exit 1
fi
echo "ok"

echo "### Source scan: derived Tags are read models, never a second stat system (ADR-046)"
if grep -rnE "CareerTags|\.Tags\(|HasTag\(" Source/TheNetwork/Domain/Operations Source/TheNetwork/Domain/Contracts Source/TheNetwork/Domain/Opportunities Source/TheNetwork/Domain/Consequences Source/TheNetwork/Domain/Contractors/ContractorService.cs Source/TheNetwork/Domain/Contractors/UpkeepService.cs Source/TheNetwork/Domain/Contractors/MoraleModel.cs ; then
  echo "FAIL: a Tag is read where state decides outcomes (resolver, pricing, willingness, upkeep)" >&2; exit 1
fi
echo "ok"

echo "### Source scan: careers add no pawn, inventory, vehicle, augmentation or Harmony (ADR-046)"
if grep -rnE "PawnGenerator|Hediff|Bionic|Implant|ThingMaker|Vehicle|Inventory|HarmonyLib" Source/TheNetwork/Domain/CareerPolicy.cs Source/TheNetwork/Domain/Contractors/CareerService.cs Source/TheNetwork/Domain/Contractors/CareerDistribution.cs ; then
  echo "FAIL: career code touches physical things" >&2; exit 1
fi
echo "ok"

echo "### Source scan: runtime tests never spawn, spend, launch or touch the live scheduler (ADR-047)"
# The scratch worlds reach the colony through nothing: no Thing creation, no spawn, no trade/launch, no drop pod, no silver movement,
# no live scheduler. Only the read-only Live suite may name RimWorld world APIs (and only to READ them).
if grep -rnE "ThingMaker|GenSpawn|GenPlace|SkyfallerMaker|DropPodUtility|TradeUtility\.LaunchSilver|\.TryCharge|\.TryRefund|NetworkRuntime\.Current\.Scheduler|rt\.Scheduler\.(Schedule|Cancel|RegisterKind|Clear)|ctx\.scheduler\.Schedule\(\"devtest|\"devtest\." Source/TheNetwork/Diagnostics/RuntimeTests ; then
  echo "FAIL: runtime-test code reaches a gameplay effect or the live scheduler" >&2; exit 1
fi
if grep -rnE "HarmonyLib|0Harmony|Scribe_|IExposable|ExposeData" Source/TheNetwork/Diagnostics/RuntimeTests Source/TheNetwork/Diagnostics/NetworkDevActions.RuntimeTests.cs ; then
  echo "FAIL: runtime-test code uses Harmony or is Scribed (it must be runtime only)" >&2; exit 1
fi
if grep -n "TheNetwork.Tests" Source/TheNetwork/TheNetwork.csproj ; then
  echo "FAIL: the production project references the test project" >&2; exit 1
fi
if grep -rnE "LetterStack|Find\.LetterStack|Letter\b|ContractLetterConsumer|LetterConsumer" Source/TheNetwork/Diagnostics/RuntimeTests ; then
  echo "FAIL: runtime-test code sends a gameplay letter" >&2; exit 1
fi
echo "ok"

OUT="${TEST_OUT:-$(mktemp -d)}"
EXTRA=()
if [ ! -f "$MANAGED/netstandard.dll" ]; then
  for f in /usr/lib/mono/4.8-api/Facades/netstandard.dll /usr/lib/mono/4.7.2-api/Facades/netstandard.dll; do
    if [ -f "$f" ]; then EXTRA+=("-p:NetStandardDll=$f"); break; fi
  done
fi
dotnet build Tests/TheNetwork.Tests/TheNetwork.Tests.csproj -c Release -nologo -v:quiet \
  -p:RimWorldManaged="$MANAGED" -p:HarmonyDll="$HARMONY" -p:TestOut="$OUT/" "${EXTRA[@]}" | grep -vE "MSB3277|^\s*$" || true
[ -f "$OUT/TheNetwork.Tests.exe" ] || { echo "test build failed" >&2; exit 1; }
cp "$MANAGED"/*.dll "$OUT/" 2>/dev/null || true
cp -r Tests/Fixtures "$OUT/"
echo "### Running tests"
(cd "$OUT" && mono TheNetwork.Tests.exe "${TEST_FILTER:-}")
