#!/bin/sh
# build.sh <out-dir>: the libaom "twin" libraries the differential tests load (AomInvTxfmTwinTests through
# SHARPIMAGE_AOMTWIN_INV, AomSearchPerfTwinTests through SHARPIMAGE_AOMTWIN_SP). Fetches libaom at the pinned
# release, builds it as a static PIC library with libavif's local-libaom options (decoder + encoder, high bit depth,
# runtime CPU detection, no tests / tools / docs), then links each twin_*.c against it:
#   <out-dir>/aomtwin_inv.so|.dll, <out-dir>/aomtwin_sp.so|.dll
# Needs git, cmake, a C compiler (gcc; MSYS2 mingw64 on Windows) and nasm. A finished <out-dir>/aom-build is reused,
# so CI caches <out-dir> keyed on this directory's contents.
set -eu

AOM_TAG=v3.14.1
AOM_COMMIT=03087864cf4bea6abb0d28f95cf7843511413d8f

HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${1:?usage: build.sh <out-dir>}
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)
SRC=$OUT/aom-src
BLD=$OUT/aom-build

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) EXT=dll; OSFLAGS="-D_WIN32_WINNT=0x0601" ;;
  *) EXT=so; OSFLAGS="-fPIC" ;;
esac

if [ ! -f "$BLD/libaom.a" ]; then
  rm -rf "$SRC" "$BLD"
  git clone --quiet --depth 1 --branch "$AOM_TAG" https://aomedia.googlesource.com/aom "$SRC"
  got=$(git -C "$SRC" rev-parse HEAD)
  if [ "$got" != "$AOM_COMMIT" ]; then
    echo "libaom $AOM_TAG resolved to $got, expected $AOM_COMMIT" >&2
    exit 1
  fi
  cmake -S "$SRC" -B "$BLD" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DCONFIG_PIC=1 \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DENABLE_DOCS=0 -DENABLE_EXAMPLES=0 -DENABLE_TESTDATA=0 -DENABLE_TESTS=0 \
    -DENABLE_TOOLS=0 >/dev/null
  cmake --build "$BLD" --target aom --parallel "$(nproc 2>/dev/null || echo 4)" >/dev/null
fi

# The twins include libaom's internal headers, so they compile with libaom's own defines (NDEBUG etc.).
FLAGS="-O2 -DNDEBUG -std=c11 -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=0 -D_LARGEFILE_SOURCE -D_FILE_OFFSET_BITS=64 $OSFLAGS"
for twin in inv sp; do
  # shellcheck disable=SC2086
  gcc $FLAGS -shared -o "$OUT/aomtwin_$twin.$EXT" "$HERE/twin_$twin.c" -I"$SRC" -I"$BLD" "$BLD/libaom.a" -lpthread -lm
  echo "$OUT/aomtwin_$twin.$EXT"
done
