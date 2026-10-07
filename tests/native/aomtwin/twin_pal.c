// aomtwin_pal.dll: exports around libaom 3.14.1's palette search leaves AS COMPILED INTO libaom.a (the SIMD libavif
// build): palette.c's public functions, the RTCD-dispatched k-means kernels (AVX2 on this machine), pred_common.c's
// av1_get_palette_cache, entropymode.c's av1_get_palette_color_index_context, tokenize.c's av1_cost_color_map,
// intra_mode_search.c's colour counting, plus msvcrt's qsort (the one libaom links) with a logging comparator.
// Twinned by SharpImage tests/.../AomPaletteTwinTests.cs against Formats/Av1/Aom/AomPalette.cs.
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "av1/common/pred_common.h"
#include "av1/common/entropymode.h"
#include "av1/encoder/encoder.h"
#include "av1/encoder/block.h"
#include "av1/encoder/palette.h"
#include "av1/encoder/tokenize.h"
#include "av1/encoder/intra_mode_search.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) {
  av1_rtcd();
  aom_dsp_rtcd();
}

// which: 0 = the RTCD pointer (AVX2 here), 1 = the C kernel, 2 = the SSE2 kernel
EXPORT int64_t twin_calc_indices(int dim, int which, const int16_t *data, const int16_t *centroids, uint8_t *indices,
                                 int want_dist, int n, int k) {
  int64_t d = -12345;
  void (*f)(const int16_t *, const int16_t *, uint8_t *, int64_t *, int, int);
  if (dim == 1)
    f = which == 0 ? av1_calc_indices_dim1 : which == 1 ? av1_calc_indices_dim1_c : av1_calc_indices_dim1_sse2;
  else
    f = which == 0 ? av1_calc_indices_dim2 : which == 1 ? av1_calc_indices_dim2_c : av1_calc_indices_dim2_sse2;
  f(data, centroids, indices, want_dist ? &d : NULL, n, k);
  return d;
}
EXPORT int twin_calc_indices_is_avx2(void) {
  return av1_calc_indices_dim1 == av1_calc_indices_dim1_avx2 && av1_calc_indices_dim2 == av1_calc_indices_dim2_avx2;
}

EXPORT void twin_k_means(int dim, const int16_t *data, int16_t *centroids, uint8_t *indices, int n, int k, int max_itr) {
  if (dim == 1)
    av1_k_means_dim1(data, centroids, indices, n, k, max_itr);
  else
    av1_k_means_dim2(data, centroids, indices, n, k, max_itr);
}

EXPORT int twin_index_color_cache(const uint16_t *cache, int n_cache, const uint16_t *colors, int n_colors,
                                  uint8_t *found, int *out) {
  return av1_index_color_cache(cache, n_cache, colors, n_colors, found, out);
}

static void set_pmi(PALETTE_MODE_INFO *pmi, const uint16_t *colors24, int s0, int s1) {
  memset(pmi, 0, sizeof(*pmi));
  memcpy(pmi->palette_colors, colors24, 3 * PALETTE_MAX_SIZE * sizeof(uint16_t));
  pmi->palette_size[0] = (uint8_t)s0;
  pmi->palette_size[1] = (uint8_t)s1;
}
EXPORT int twin_delta_bits_v(const uint16_t *colors24, int s1, int bd, int *zero_count, int *min_bits) {
  PALETTE_MODE_INFO pmi;
  set_pmi(&pmi, colors24, 0, s1);
  return av1_get_palette_delta_bits_v(&pmi, bd, zero_count, min_bits);
}
EXPORT int twin_color_cost_y(const uint16_t *colors24, int s0, const uint16_t *cache, int n_cache, int bd) {
  PALETTE_MODE_INFO pmi;
  set_pmi(&pmi, colors24, s0, 0);
  return av1_palette_color_cost_y(&pmi, cache, n_cache, bd);
}
EXPORT int twin_color_cost_uv(const uint16_t *colors24, int s1, const uint16_t *cache, int n_cache, int bd) {
  PALETTE_MODE_INFO pmi;
  set_pmi(&pmi, colors24, 0, s1);
  return av1_palette_color_cost_uv(&pmi, cache, n_cache, bd);
}

// above / left: NULL-able, each 24 colors + sizes
EXPORT int twin_palette_cache(int mb_to_top_edge, const uint16_t *above24, int as0, int as1, const uint16_t *left24,
                              int ls0, int ls1, int plane, uint16_t *cache) {
  MACROBLOCKD xd;
  MB_MODE_INFO above, left;
  memset(&xd, 0, sizeof(xd));
  memset(&above, 0, sizeof(above));
  memset(&left, 0, sizeof(left));
  xd.mb_to_top_edge = mb_to_top_edge;
  if (above24) {
    set_pmi(&above.palette_mode_info, above24, as0, as1);
    xd.above_mbmi = &above;
  }
  if (left24) {
    set_pmi(&left.palette_mode_info, left24, ls0, ls1);
    xd.left_mbmi = &left;
  }
  return av1_get_palette_cache(&xd, plane, cache);
}

EXPORT int twin_color_index_context(const uint8_t *map, int stride, int r, int c, int palette_size, uint8_t *order,
                                    int *idx) {
  return av1_get_palette_color_index_context(map, stride, r, c, palette_size, order, idx);
}

EXPORT int twin_count_colors(const uint8_t *src, int stride, int rows, int cols, int *val_count) {
  int n = -1;
  av1_count_colors(src, stride, rows, cols, val_count, &n);
  return n;
}
EXPORT int twin_count_colors_thr(const uint8_t *src, int stride, int rows, int cols, int thr, int *num) {
  return av1_count_colors_with_threshold(src, stride, rows, cols, thr, num);
}

// ---- MACROBLOCK-level: av1_cost_color_map, av1_restore_uv_color_map ------------------------------------------------
static AV1_COMP cpi;
static SequenceHeader seq;
static MACROBLOCK x;
static MB_MODE_INFO mbmi;
static MB_MODE_INFO *mi_ptr;
static PALETTE_BUFFER pbuf;

static void setup_block(int bsize, int ssx, int ssy, int mb_to_right, int mb_to_bottom) {
  memset(&x, 0, sizeof(x));
  memset(&mbmi, 0, sizeof(mbmi));
  memset(&seq, 0, sizeof(seq));
  seq.bit_depth = AOM_BITS_8;
  cpi.common.seq_params = &seq;
  mbmi.bsize = (BLOCK_SIZE)bsize;
  mi_ptr = &mbmi;
  x.e_mbd.mi = &mi_ptr;
  x.e_mbd.mb_to_right_edge = mb_to_right;
  x.e_mbd.mb_to_bottom_edge = mb_to_bottom;
  for (int p = 1; p < 3; p++) {
    x.e_mbd.plane[p].subsampling_x = ssx;
    x.e_mbd.plane[p].subsampling_y = ssy;
  }
  x.palette_buffer = &pbuf;
}

// color_cost: [7][5][8] of the plane
EXPORT int twin_cost_color_map(int plane, int bsize, int ssx, int ssy, int mb_to_right, int mb_to_bottom, int n_colors,
                               uint8_t *map, const int *color_cost) {
  setup_block(bsize, ssx, ssy, mb_to_right, mb_to_bottom);
  mbmi.palette_mode_info.palette_size[plane] = (uint8_t)n_colors;
  x.e_mbd.plane[plane].color_index_map = map;
  memcpy(plane ? (void *)x.mode_costs.palette_uv_color_cost : (void *)x.mode_costs.palette_y_color_cost, color_cost,
         sizeof(x.mode_costs.palette_y_color_cost));
  return av1_cost_color_map(&x, plane, (BLOCK_SIZE)bsize, TX_4X4, PALETTE_MAP);
}

// kbuf: kmeans_data_buf in / out (2 * 4096); map: plane 1 color map in / out
EXPORT void twin_restore_uv(int bsize, int ssx, int ssy, int mb_to_right, int mb_to_bottom, uint8_t *u, uint8_t *v,
                            int stride, const uint16_t *colors24, int s1, int16_t *kbuf, uint8_t *map) {
  setup_block(bsize, ssx, ssy, mb_to_right, mb_to_bottom);
  set_pmi(&mbmi.palette_mode_info, colors24, 0, s1);
  x.plane[1].src.buf = u;
  x.plane[2].src.buf = v;
  x.plane[1].src.stride = x.plane[2].src.stride = stride;
  x.e_mbd.plane[1].color_index_map = map;
  memcpy(pbuf.kmeans_data_buf, kbuf, sizeof(pbuf.kmeans_data_buf));
  av1_restore_uv_color_map(&cpi, &x);
  memcpy(kbuf, pbuf.kmeans_data_buf, sizeof(pbuf.kmeans_data_buf));
}

// ---- msvcrt qsort: log every comparator call (the slots compared) -------------------------------------------------
typedef struct {
  int key, id;
} QE;
static QE *qbase;
static int *qlog, qlogn;
static int qcmp(const void *a, const void *b) {
  const QE *p = (const QE *)a, *q = (const QE *)b;
  qlog[qlogn++] = (int)(p - qbase);
  qlog[qlogn++] = (int)(q - qbase);
  return (p->key > q->key) - (p->key < q->key);
}
// keys in, ids out (the element order after the sort); returns the number of comparator calls
EXPORT int twin_qsort(const int *keys, int *ids, int n, int *log) {
  QE a[4096];
  for (int i = 0; i < n; i++) a[i].key = keys[i], a[i].id = i;
  qbase = a;
  qlog = log;
  qlogn = 0;
  qsort(a, n, sizeof(QE), qcmp);
  for (int i = 0; i < n; i++) ids[i] = a[i].id;
  return qlogn / 2;
}
