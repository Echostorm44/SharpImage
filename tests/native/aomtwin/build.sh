#!/bin/sh
# build.sh <out-dir>: the libaom "twin" libraries the differential tests (tests/SharpImage.Tests/Formats/Aom*TwinTests)
# load. Fetches libaom at the pinned release, builds it as a static PIC library with libavif's local-libaom options
# (decoder + encoder, high bit depth, runtime CPU detection, no tests / tools / docs), then links each twin against it
# into <out-dir>/<name>.so (.dll under MSYS2 / mingw-w64). The variables to point the tests at them go to
# <out-dir>/twins.env (NAME=path lines).
#
# AOM_SRC / AOM_BUILD, when both set, use an existing libaom source tree and build (with libaom.a) instead of fetching.
# Needs git, cmake, a C compiler (gcc) and nasm for the fetch-and-build path. A finished <out-dir>/aom-build is reused,
# so CI caches <out-dir> keyed on this script.
set -eu

AOM_TAG=v3.14.1
AOM_COMMIT=03087864cf4bea6abb0d28f95cf7843511413d8f

HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${1:?usage: build.sh <out-dir>}
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) EXT=dll; OSFLAGS="-D_WIN32_WINNT=0x0601" ;;
  *) EXT=so; OSFLAGS="-fPIC" ;;
esac

if [ -n "${AOM_SRC:-}" ] && [ -n "${AOM_BUILD:-}" ]; then
  SRC=$AOM_SRC
  BLD=$AOM_BUILD
else
  SRC=$OUT/aom-src
  BLD=$OUT/aom-build
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
fi

# The twins include libaom's internal headers (and some compile a libaom .c for its statics), so they compile with
# libaom's own defines: its structs carry NDEBUG-only fields, and a mismatch shifts AV1_COMP / MACROBLOCK offsets.
DEFS="-DNDEBUG -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=0 -D_CRT_SECURE_NO_WARNINGS -D_LARGEFILE_SOURCE -D_FILE_OFFSET_BITS=64 $OSFLAGS"
INC="-I$SRC -I$BLD -I$HERE"
LIBS="$BLD/libaom.a -lpthread -lm"
OBJ=$OUT/obj
mkdir -p "$OBJ"
: > "$OUT/twins.env"

# cc <obj> <src> <flags...>
cc_obj() {
  o=$1; s=$2; shift 2
  # shellcheck disable=SC2086
  gcc -std=c11 $DEFS "$@" -c -o "$OBJ/$o" "$HERE/$s" $INC
}

# link <name> <env var> <objs...> [-- <extra link flags...>]
link() {
  name=$1; var=$2; shift 2
  objs=""
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do objs="$objs $OBJ/$1"; shift; done
  [ $# -gt 0 ] && shift
  # shellcheck disable=SC2086
  gcc -shared -o "$OUT/$name.$EXT" $objs $LIBS "$@"
  if [ -n "$var" ]; then
    lib=$OUT/$name.$EXT
    if [ "$EXT" = dll ] && command -v cygpath >/dev/null 2>&1; then
      lib=$(cygpath -m "$lib")   # C:/... for the .NET test host, not MSYS's /c/...
    fi
    echo "$var=$lib" >> "$OUT/twins.env"
  fi
  echo "$OUT/$name.$EXT"
}

simple() {   # simple <name> <env var> <src>: one -O2 source
  cc_obj "$1.o" "$3" -O2
  link "$1" "$2" "$1.o"
}

simple aomtwin_inv SHARPIMAGE_AOMTWIN_INV twin_inv.c
simple aomtwin_sp SHARPIMAGE_AOMTWIN_SP twin_sp.c
simple aomtwin SHARPIMAGE_AOMTWIN aomtwin.c
simple aomtwin_cdef SHARPIMAGE_AOMTWIN_CDEF twin_cdef.c
simple aomtwin_hbd SHARPIMAGE_AOMTWIN_HBD twin_hbd.c
simple aomtwin_ibc SHARPIMAGE_AOMTWIN_IBC aomtwin_ibc.c
simple aomtwin_sf SHARPIMAGE_AOMTWIN_SF aomtwin_sf.c

# Palette: libaom.a's own leaves, and (next to it, found by the test) a copy of palette.c / tokenize.c for their statics.
simple aomtwin_pal SHARPIMAGE_AOMTWIN_PAL twin_pal.c
cc_obj twin_palsearch.o twin_palsearch.c -O2
cc_obj twin_tok.o twin_tok.c -O2
link aomtwin_palsearch "" twin_palsearch.o twin_tok.o

# Intra prediction: twin_recon.c compiles reconintra.c (its statics) with reconintra.c's own SIMD flags.
cc_obj twin_recon.o twin_recon.c -O3 -msse2 -msse3 -mssse3 -msse4.1 -msse4.2 -mavx2
cc_obj twin_pred.o twin_pred.c -O2
link aomtwin_pred SHARPIMAGE_AOMTWIN_PRED twin_pred.o twin_recon.o

# Loop filter / restoration: twin_pickrst.c is pickrst.c (its statics) with the external names renamed, at pickrst.c's
# flags (-O3, no -m); twin_capture.c wraps the post-filter entry points to dump libaom's inputs and decisions.
R="-Dav1_lowbd_pixel_proj_error_c=twincopy_lowbd_pixel_proj_error_c -Dav1_highbd_pixel_proj_error_c=twincopy_highbd_pixel_proj_error_c"
R="$R -Dav1_calc_proj_params_c=twincopy_calc_proj_params_c -Dav1_calc_proj_params_high_bd_c=twincopy_calc_proj_params_high_bd_c"
R="$R -Dav1_compute_stats_c=twincopy_compute_stats_c -Dav1_compute_stats_highbd_c=twincopy_compute_stats_highbd_c"
R="$R -Dav1_pick_filter_restoration=twincopy_pick_filter_restoration"
cc_obj twin_kern.o twin_kern.c -O2
cc_obj twin_capture.o twin_capture.c -O2
# shellcheck disable=SC2086
cc_obj twin_pickrst.o twin_pickrst.c -O3 $R
link aomtwin_lf SHARPIMAGE_AOMTWIN_LF twin_kern.o twin_capture.o twin_pickrst.o -- \
  -Wl,--wrap=av1_pick_filter_level -Wl,--wrap=av1_pick_filter_restoration \
  -Wl,--wrap=av1_loop_restoration_save_boundary_lines -Wl,--wrap=av1_loop_restoration_filter_frame \
  -Wl,--wrap=av1_loop_filter_frame_mt

# ML: twin_tx.c / twin_part.c / twin_hog.c compile tx_search.c / partition_strategy.c / the HOG prune for their statics
# at those files' own flags (-O3, no -m).
cc_obj aomtwin_ml.o aomtwin_ml.c -O2
cc_obj twin_tx.o twin_tx.c -O3
cc_obj twin_part.o twin_part.c -O3
cc_obj twin_hog.o twin_hog.c -O3
link aomtwin_ml SHARPIMAGE_AOMTWIN_ML aomtwin_ml.o twin_tx.o twin_part.o twin_hog.o
