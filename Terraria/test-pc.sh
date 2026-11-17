#!/bin/bash
# Weaves the Terraria MonoMod patches into the game and runs it on this PC (system Mono, x86_64 SDL2 FNA3D/FAudio).
# Usage: Terraria/test-pc.sh <work dir> [x86_64 libs dir with libFNA3D.so.0 and libFAudio.so.0]
#   needs: `make Terraria MMLoader` done, deps/Terraria filled, the GOG game's Content folder (GAME=...)
set -euo pipefail
R=$(cd "$(dirname "$0")/.." && pwd)
W=${1:?work dir}; LIBS=${2:-$W/lib}; GAME=${GAME:-$HOME/GOG\ Games/Terraria/game}
D=$R/deps/Terraria
mkdir -p "$W/monomod" "$W/lib"
for f in Terraria.exe ReLogic.dll Ionic.Zip.CF.dll Newtonsoft.Json.dll CsvHelper.dll NVorbis.dll MP3Sharp.dll Steamworks.NET.dll RailSDK.Net.dll SteelSeriesEngineWrapper.dll System.ValueTuple.dll FNA.dll FNA.dll.config; do cp "$D/$f" "$W/"; done
[ "$LIBS" = "$W/lib" ] || cp -a "$LIBS"/libFNA3D.so* "$LIBS"/libFAudio.so* "$W/lib/"
cp "$R"/out/Terraria/*.mm.dll "$R/out/MMLoader/MMLoader.exe" "$W/"
cp "$R"/deps/MonoMod/*.dll "$R"/deps/MonoMod/MonoMod.exe "$R"/deps/MonoMod/MonoMod.exe.config "$R/out/Terraria/TileConverter.dll" "$W/monomod/"
[ -e "$W/Content" ] || ln -s "$GAME/Content" "$W/Content"
cd "$W"; rm -f MONOMODDED_*
export MONOMOD_DEPDIRS="$W:$W/monomod:/usr/lib/mono/4.5" MONO_PATH="$W:$W/monomod"
for a in ReLogic.dll Terraria.exe; do mono monomod/MonoMod.exe "$a" 2>&1 | grep -E "Done|Exception|\[tiles\]|rror" | cut -c1-300; done
[ "${WEAVE_ONLY:-0}" = 1 ] && exit 0
FNA_PLATFORM_BACKEND=SDL2 FNA3D_FORCE_DRIVER=OpenGL MONO_IOMAP=all LD_LIBRARY_PATH="$W/lib" exec mono MMLoader.exe MONOMODDED_Terraria.exe ${TERRARIA_ARGS:+-- $TERRARIA_ARGS}
