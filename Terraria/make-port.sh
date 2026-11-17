#!/bin/bash
# Fills the Terraria port repo (Terraria-PortMaster: Terraria.sh + terraria/, laid out as PortMaster ships it) with what is built
# here and elsewhere: our patches and the rules' helper, MonoMod, the reference assemblies the weave resolves against, our FNA /
# FNA3D / FAudio, gpuastc, trpatch. The scripts, settings and texts in the repo are edited there; this only refreshes binaries.
#   PORT         the port repo  (default ../Terraria-PortMaster next to FNAPatches)
#   UPSTREAM_ZIP upstream terraria.zip, only for trpatch, its Cecil and licenses (wild1walker, MIT)  (default ~/Downloads/terraria.zip)
#   REFDIR       Mono 4.5 reference assemblies  (default /usr/lib/mono/4.5)
#   GPUASTC      GPU-ASTC checkout with the aarch64 gpuastc in build-aarch64/  (default ../GPU-ASTC)
#   PACK=1       also writes terraria.zip next to the repo
# Run `make Terraria` first. The .NET runtime goes to PortMaster/libs separately: ../PortMaster-dotnet (build.sh) builds it.
set -euo pipefail
FP=$(cd "$(dirname "$0")/.." && pwd); OURS=$FP/deps/Terraria
PORT=$(cd "${PORT:-$FP/../Terraria-PortMaster}" && pwd); T=$PORT/terraria
UP=${UPSTREAM_ZIP:-$HOME/Downloads/terraria.zip}; REFDIR=${REFDIR:-/usr/lib/mono/4.5}; GA=${GPUASTC:-$FP/../GPU-ASTC}
for f in "$OURS/FNA.dll" "$OURS/FNA.dll.config" "$OURS/arm64-sdl2-libs/libFNA3D.so.0.26.11" "$OURS/arm64-sdl2-libs/libFAudio.so.0.26.11" \
         "$FP/out/Terraria/Terraria.TerrariaPatches.mm.dll" "$FP/out/Terraria/TileConverter.dll" "$GA/build-aarch64/gpuastc" "$UP" "$PORT/Terraria.sh"; do
  [ -f "$f" ] || { echo "missing $f"; exit 1; }
done
rm -rf "$T/monomod" "$T/patches" "$T/libs" "$T/dlls"
mkdir -p "$T/monomod/ref" "$T/patches" "$T/libs" "$T/dlls" "$T/tools" "$T/licenses"
# our patches and the helper their rules run
cp "$FP"/out/Terraria/{Terraria,ReLogic}.{TerrariaPatches,TileRecords,NetAccess,Lazy,Fonts,Pack,Speed}.mm.dll "$T/patches/" 2>/dev/null || true
rm -f "$T"/patches/ReLogic.TileRecords.mm.dll
cp "$FP"/deps/MonoMod/{MonoMod.Common,MonoMod.Utils}.dll "$FP/deps/MonoMod/MonoMod.exe" "$FP/out/Terraria/TileConverter.dll" "$T/monomod/"
for a in mscorlib System System.Core System.Xml System.Drawing System.Windows.Forms System.Runtime.Serialization WindowsBase \
         System.Configuration System.Data System.Numerics System.Security System.Transactions System.EnterpriseServices \
         System.Xml.Linq System.Xaml System.ServiceModel.Internals System.Runtime.Serialization.Formatters.Soap \
         Accessibility Microsoft.CSharp Mono.Posix Mono.Security Mono.WebBrowser; do
  cp "$REFDIR/$a.dll" "$T/monomod/ref/"
done
# trpatch (stubs, extract, content) and its Cecil, which MonoMod 22.05 runs on too; licenses (wild1walker, MIT)
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
unzip -q -o "$UP" 'terraria/bin/trpatch/*' 'terraria/licenses/*' -d "$W"
cp "$W/terraria/bin/trpatch/trpatch.exe" "$T/tools/"
cp "$W/terraria/bin/trpatch/"{Mono.Cecil.dll,Mono.Cecil.Mdb.dll,Mono.Cecil.Pdb.dll,Mono.Cecil.Rocks.dll} "$T/monomod/"
cp "$W"/terraria/licenses/* "$T/licenses/"
# our native libraries and FNA, gpuastc
# plain files (no symlinks: exFAT cards), named as .NET looks for them: FNA's DllImport "FNA3D" -> libFNA3D.so
for l in FNA3D FAudio; do cp "$OURS/arm64-sdl2-libs/lib$l.so.0.26.11" "$T/libs/lib$l.so"; done
cp "$OURS/FNA.dll" "$OURS/FNA.dll.config" "$T/dlls/"
cp "$GA/build-aarch64/gpuastc" "$T/tools/"
cp "$GA/THIRD_PARTY_NOTICES.md" "$T/licenses/gpuastc.THIRD_PARTY_NOTICES.md"
cp "$FP/LICENSE" "$T/licenses/fnapatches.LICENSE"
echo "port repo refreshed: $PORT"
if [ "${PACK:-0}" = 1 ]; then
  Z=$(cd "$PORT/.." && pwd)/terraria.zip; rm -f "$Z"
  (cd "$PORT" && zip -q -r -y "$Z" Terraria.sh terraria/gamedata/gitkeep.txt \
     terraria/{dlls,libs,licenses,monomod,patches,tools,terraria.ini})
  echo "built $Z ($(du -h "$Z" | cut -f1))"
fi
