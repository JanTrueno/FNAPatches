#!/bin/sh
# Usage: ./run.sh /path/to/libfmod.so
# Symlinks the given file as libfmod.so in a scratch dir and points LD_LIBRARY_PATH at it,
# since the target filename varies (libfmod.so.13, libfmod.so.13.18, ...) but the DllImport
# in fmod_diag.cs resolves the bare name "fmod" -> "libfmod.so" via Mono's Unix fallback.
set -eu
if [ $# -lt 1 ]; then
    echo "Usage: $0 /path/to/libfmod.so" >&2
    exit 2
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT
ln -sf "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")" "$SCRATCH/libfmod.so"

LD_LIBRARY_PATH="$SCRATCH${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" mono "$DIR/fmod_diag.exe"
