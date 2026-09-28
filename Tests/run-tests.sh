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
