#!/bin/bash
# Weaves the Terraria MonoMod patches into the game under .NET (no Mono needed).
# Usage: weave-net.sh <work dir> <dotnet root>
#   <work dir> holds the game's Terraria.exe, ReLogic.dll and libraries, the *.mm.dll patches and monomod/ (MonoMod.exe, Cecil,
#   TileConverter.dll). The woven MONOMODDED_Terraria.exe / MONOMODDED_ReLogic.dll are renamed in place to Terraria.exe / ReLogic.dll
#   unless KEEP=1; the originals go to orig/.
set -euo pipefail
W=$(cd "${1:?work dir}" && pwd); NET=${2:?dotnet root}
FW=$(ls -d "$NET"/shared/Microsoft.NETCore.App/*/ | tail -1); FW=${FW%/}
cat > "$W/monomod/MonoMod.runtimeconfig.json" <<J
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"${FW##*/}"},"rollForward":"Major","configProperties":{"System.Globalization.Invariant":true}}}
J
cd "$W"; rm -f MONOMODDED_*
# a weave done before: start again from the game's own files
[ -d orig ] && cp -f orig/ReLogic.dll orig/Terraria.exe .
export DOTNET_ROOT="$NET" MONOMOD_DEPDIRS="$W:$W/monomod:$W/ref" DOTNET_gcServer=0
for a in ReLogic.dll Terraria.exe; do
  "$NET/dotnet" monomod/MonoMod.exe "$a" 2>&1 | grep -E "Done|Exception|\[tiles\]|\[net\]|rror" | cut -c1-300
  [ -f "MONOMODDED_$a" ] || { echo "weave of $a failed" >&2; exit 1; }
done
if [ "${KEEP:-0}" != 1 ]; then
  mkdir -p orig; for a in ReLogic.dll Terraria.exe; do mv -f "$a" "orig/$a"; mv -f "MONOMODDED_$a" "$a"; rm -f "MONOMODDED_$a.mdb" "MONOMODDED_$a.pdb"; done
fi
