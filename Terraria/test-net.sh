#!/bin/bash
# Builds the game the way the handheld's setup does, but on this PC under .NET, and runs it: the patches woven with MonoMod,
# the Windows stand-ins, the textures' sizes and uncompressed fonts, and the textures as one GPU-ASTC archive.
# Usage: Terraria/test-net.sh <work dir>        (then the game starts; TERRARIA_ARGS passes through)
#   needs: `make Terraria`, deps/Terraria filled (x86_64-sdl2-libs too), the GOG game (GAME=...), ~/.dotnet (8 or newer),
#   gpuastc (GPUASTC=path to the x86 build).
#   REBUILD=1 weaves again; ASTC=0 leaves the archive out.
set -euo pipefail
R=$(cd "$(dirname "$0")/.." && pwd); D=$R/deps/Terraria
W=$(mkdir -p "${1:?work dir}" && cd "$1" && pwd)
GAME=${GAME:-$HOME/GOG Games/Terraria/game}
NET=${NET:-$HOME/.dotnet}; FW=$(ls -d "$NET"/shared/Microsoft.NETCore.App/*/ | tail -1); FW=${FW%/}
export DOTNET_ROOT=$NET DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_gcServer=0

if [ ! -f "$W/game/Terraria.exe" ] || [ "${REBUILD:-0}" = 1 ]; then
  rm -rf "$W/weave" "$W/game"; mkdir -p "$W/weave/monomod" "$W/weave/ref" "$W/game/lib"
  # MonoMod and its Cecil, trsetup, the rules' helper
  cp "$R"/deps/Cecil/*.dll "$R/out/Terraria/trsetup.exe" "$W/weave/monomod/"
  cp "$R"/deps/MonoMod/{MonoMod.Common,MonoMod.Utils}.dll "$R/deps/MonoMod/MonoMod.exe" "$R/out/Terraria/TileConverter.dll" "$W/weave/monomod/"
  for a in mscorlib System System.Core System.Xml System.Drawing System.Windows.Forms System.Runtime.Serialization WindowsBase \
           System.Configuration System.Data System.Numerics System.Security System.Transactions System.EnterpriseServices \
           System.Xml.Linq System.Xaml System.ServiceModel.Internals System.Runtime.Serialization.Formatters.Soap \
           Accessibility Microsoft.CSharp Mono.Posix Mono.Security Mono.WebBrowser; do
    cp "/usr/lib/mono/4.5/$a.dll" "$W/weave/ref/"
  done
  cat > "$W/weave/monomod/MonoMod.runtimeconfig.json" <<J
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"${FW##*/}"},"rollForward":"Major","configProperties":{"System.Globalization.Invariant":true}}}
J
  cd "$W/weave"
  cp "$D/Terraria.exe" .
  "$NET/dotnet" monomod/trsetup.exe extract Terraria.exe . > /dev/null
  cp "$D/FNA.dll" "$D/FNA.dll.config" .
  cp "$R"/out/Terraria/*.mm.dll .
  export MONOMOD_DEPDIRS="$W/weave:$W/weave/monomod:$W/weave/ref"
  for a in ReLogic.dll Terraria.exe; do
    "$NET/dotnet" monomod/MonoMod.exe "$a" 2>&1 | grep -E "Done|Exception|\[tiles\]|\[net\]|\[lazy\]|\[fonts\]|\[slim\]|rror" | cut -c1-200
    mv -f "MONOMODDED_$a" "$a"; rm -f "MONOMODDED_$a".*
  done
  rm -f *.mm.dll
  cp *.dll Terraria.exe FNA.dll.config "$W/game/"
  cd "$W/game"
  "$NET/dotnet" "$W/weave/monomod/trsetup.exe" stubs . "$FW" Terraria.exe ReLogic.dll FNA.dll CsvHelper.dll Ionic.Zip.CF.dll MP3Sharp.dll NVorbis.dll \
    Newtonsoft.Json.dll RailSDK.Net.dll Steamworks.NET.dll SteelSeriesEngineWrapper.dll 2>&1 | { grep -v "members$" || true; }
  cp "$D"/x86_64-sdl2-libs/lib* lib/
  (cd lib && ln -sf libFNA3D.so.0 libFNA3D.so && ln -sf libFAudio.so.0 libFAudio.so)
  cat > Terraria.runtimeconfig.json <<J
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"},"rollForward":"Major","configProperties":{"System.GC.Concurrent":false,"System.Globalization.Invariant":true,"System.Globalization.PredefinedCulturesOnly":false,"System.Runtime.TieredPGO":false}}}
J
  # Content: the game's own folders, linked; the sizes table, the uncompressed fonts and the archive made here
  mkdir -p Content; for d in "$GAME"/Content/*; do [ "$(basename "$d")" = Fonts ] || ln -sfn "$d" "Content/$(basename "$d")"; done
  FNA_DLL="$W/game/FNA.dll" "$NET/dotnet" "$W/weave/monomod/trsetup.exe" content "$GAME" "$W/content" > /dev/null
  cp -r "$W/content/Content/." Content/
  if [ "${ASTC:-1}" = 1 ]; then
    "${GPUASTC:?GPUASTC=<gpuastc binary>}" -pack "$GAME/Content/Images" Content/Images.astcpak | tail -1
  fi
fi
cd "$W/game"
FNA_PLATFORM_BACKEND=SDL2 FNA3D_FORCE_DRIVER=OpenGL LD_LIBRARY_PATH="$W/game/lib" \
  exec "$NET/dotnet" exec --runtimeconfig Terraria.runtimeconfig.json Terraria.exe ${TERRARIA_ARGS:-}
