// twin_kern.c: aomtwin_lf.dll leaf exports. Every kernel goes through libaom 3.14.1's run-time dispatch (the SIMD
// versions the encoder uses on an AVX2 machine), plus a synthetic-frame driver for av1_loop_filter_frame_mt.
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "config/aom_scale_rtcd.h"
#include "aom_dsp/psnr.h"
#include "aom_dsp/binary_codes_writer.h"
#include "aom_mem/aom_mem.h"
#include "av1/common/av1_common_int.h"
#include "av1/common/av1_loopfilter.h"
#include "av1/common/restoration.h"
#include "av1/common/thread_common.h"
#include "av1/encoder/encoder.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) {
  av1_rtcd();
  aom_dsp_rtcd();
  aom_scale_rtcd();
}

// ---- deblocking kernels -------------------------------------------------------------------------------------------
// kind: 4, 6, 8, 14; vert: 1 = vertical edge (aom_lpf_vertical_*), 0 = horizontal; variant 0 single, 1 dual, 2 quad
EXPORT int twin_lpf(int kind, int vert, int variant, uint8_t *s, int pitch, const uint8_t *b0, const uint8_t *l0,
                    const uint8_t *t0, const uint8_t *b1, const uint8_t *l1, const uint8_t *t1) {
#define ONE(K)                                                                  \
  if (vert) {                                                                   \
    if (variant == 0) aom_lpf_vertical_##K(s, pitch, b0, l0, t0);               \
    else if (variant == 1) aom_lpf_vertical_##K##_dual(s, pitch, b0, l0, t0, b1, l1, t1); \
    else aom_lpf_vertical_##K##_quad(s, pitch, b0, l0, t0);                     \
  } else {                                                                      \
    if (variant == 0) aom_lpf_horizontal_##K(s, pitch, b0, l0, t0);             \
    else if (variant == 1) aom_lpf_horizontal_##K##_dual(s, pitch, b0, l0, t0, b1, l1, t1); \
    else aom_lpf_horizontal_##K##_quad(s, pitch, b0, l0, t0);                   \
  }
  switch (kind) {
    case 4: ONE(4) break;
    case 6: ONE(6) break;
    case 8: ONE(8) break;
    case 14: ONE(14) break;
    default: return -1;
  }
#undef ONE
  return 0;
}

// ---- SSE / variance -------------------------------------------------------------------------------------------------
static void yv12_one(YV12_BUFFER_CONFIG *b, uint8_t *p, int stride, int w, int h) {
  memset(b, 0, sizeof(*b));
  b->y_buffer = p;
  b->y_stride = stride;
  b->y_crop_width = w;
  b->y_crop_height = h;
}

EXPORT int64_t twin_get_y_sse_part(uint8_t *a, int as, uint8_t *b, int bs, int hstart, int width, int vstart,
                                   int height) {
  YV12_BUFFER_CONFIG ya, yb;
  yv12_one(&ya, a, as, 0, 0);
  yv12_one(&yb, b, bs, 0, 0);
  return aom_get_y_sse_part(&ya, &yb, hstart, width, vstart, height);
}

EXPORT int64_t twin_get_y_sse(uint8_t *a, int as, uint8_t *b, int bs, int w, int h) {
  YV12_BUFFER_CONFIG ya, yb;
  yv12_one(&ya, a, as, w, h);
  yv12_one(&yb, b, bs, w, h);
  return aom_get_y_sse(&ya, &yb);
}

EXPORT uint64_t twin_get_y_var(uint8_t *a, int as, int hstart, int width, int vstart, int height) {
  YV12_BUFFER_CONFIG ya;
  yv12_one(&ya, a, as, 0, 0);
  return aom_get_y_var(&ya, hstart, width, vstart, height);
}

// ---- restoration encoder kernels ----------------------------------------------------------------------------------
static int16_t *g_avg;
EXPORT void twin_compute_stats(int win, const uint8_t *dgd, const uint8_t *src, int h_start, int h_end, int v_start,
                               int v_end, int dgd_stride, int src_stride, int64_t *M, int64_t *H, int ds) {
  const int buf_size = sizeof(int16_t) * 6 * RESTORATION_UNITSIZE_MAX * RESTORATION_UNITSIZE_MAX;
  if (!g_avg) {
    g_avg = (int16_t *)aom_memalign(32, buf_size);
    memset(g_avg, 0, buf_size);
  }
  av1_compute_stats(win, dgd, src, g_avg, g_avg + 3 * RESTORATION_UNITSIZE_MAX * RESTORATION_UNITSIZE_MAX, h_start,
                    h_end, v_start, v_end, dgd_stride, src_stride, M, H, ds);
}

EXPORT void twin_calc_proj_params(const uint8_t *src, int w, int h, int ss, const uint8_t *dat, int ds, int32_t *flt0,
                                  int f0s, int32_t *flt1, int f1s, int64_t *H4, int64_t *C2, int ep) {
  int64_t H[2][2] = { { 0, 0 }, { 0, 0 } };
  int64_t C[2] = { 0, 0 };
  av1_calc_proj_params(src, w, h, ss, dat, ds, flt0, f0s, flt1, f1s, H, C, &av1_sgr_params[ep]);
  H4[0] = H[0][0]; H4[1] = H[0][1]; H4[2] = H[1][0]; H4[3] = H[1][1];
  C2[0] = C[0]; C2[1] = C[1];
}

EXPORT int64_t twin_pixel_proj_error(const uint8_t *src, int w, int h, int ss, const uint8_t *dat, int ds,
                                     int32_t *flt0, int f0s, int32_t *flt1, int f1s, int xq0, int xq1, int ep) {
  int xq[2] = { xq0, xq1 };
  return av1_lowbd_pixel_proj_error(src, w, h, ss, dat, ds, flt0, f0s, flt1, f1s, xq, &av1_sgr_params[ep]);
}

EXPORT int twin_selfguided(const uint8_t *dgd, int w, int h, int stride, int32_t *flt0, int32_t *flt1, int flt_stride,
                           int ep) {
  return av1_selfguided_restoration(dgd, w, h, stride, flt0, flt1, flt_stride, ep, 8, 0);
}

static int32_t *g_tmp;
EXPORT int twin_apply_sgr(const uint8_t *dat, int w, int h, int stride, int ep, int xqd0, int xqd1, uint8_t *dst,
                          int dst_stride) {
  if (!g_tmp) g_tmp = (int32_t *)aom_memalign(32, RESTORATION_TMPBUF_SIZE);
  int xqd[2] = { xqd0, xqd1 };
  return av1_apply_selfguided_restoration(dat, w, h, stride, ep, xqd, dst, dst_stride, g_tmp, 8, 0);
}

EXPORT void twin_wiener_convolve(const uint8_t *src, int ss, uint8_t *dst, int ds, const int16_t *hf, const int16_t *vf,
                                 int w, int h) {
  DECLARE_ALIGNED(16, int16_t, fx[8]);
  DECLARE_ALIGNED(16, int16_t, fy[8]);
  memcpy(fx, hf, sizeof(fx));
  memcpy(fy, vf, sizeof(fy));
  const WienerConvolveParams cp = get_conv_params_wiener(8);
  av1_wiener_convolve_add_src(src, ss, dst, ds, fx, 16, fy, 16, w, h, &cp);
}

EXPORT int twin_count_refsubexpfin(int n, int k, int ref, int v) {
  return aom_count_primitive_refsubexpfin((uint16_t)n, (uint16_t)k, (uint16_t)ref, (uint16_t)v);
}

// ---- synthetic-frame deblocking through av1_loop_filter_frame_mt ----------------------------------------------------
// planes: pointers at each plane's (0,0), strides; widths/heights: crop sizes (luma, chroma). The mi grid: per-mi
// block index into the per-block arrays (bsize, tx_size, skip, ref0, mode, segment).
EXPORT void twin_lf_frame(uint8_t *y, uint8_t *u, uint8_t *v, int ystride, int uvstride, int cw, int ch, int ss_x,
                          int ss_y, int mono, int mi_rows, int mi_cols, const int32_t *blk_of_mi, int nblk,
                          const uint8_t *bsize, const uint8_t *tx_size, const uint8_t *skip, const int8_t *ref0,
                          const uint8_t *mode, const uint8_t *seg, int lvl0, int lvl1, int lvlu, int lvlv, int sharp,
                          int plane_start, int plane_end, int partial, int lpf_opt_level, int sb_size) {
  static AV1_COMMON cm;
  static SequenceHeader seq;
  static MACROBLOCKD xd;
  memset(&cm, 0, sizeof(cm));
  memset(&seq, 0, sizeof(seq));
  memset(&xd, 0, sizeof(xd));
  cm.seq_params = &seq;
  seq.sb_size = (BLOCK_SIZE)sb_size;
  seq.mib_size = mi_size_wide[sb_size];
  seq.mib_size_log2 = mi_size_wide_log2[sb_size];
  seq.subsampling_x = ss_x;
  seq.subsampling_y = ss_y;
  seq.monochrome = mono;
  seq.bit_depth = AOM_BITS_8;
  seq.use_highbitdepth = 0;
  MB_MODE_INFO *blocks = (MB_MODE_INFO *)calloc(nblk, sizeof(MB_MODE_INFO));
  for (int b = 0; b < nblk; b++) {
    blocks[b].bsize = (BLOCK_SIZE)bsize[b];
    blocks[b].tx_size = (TX_SIZE)tx_size[b];
    blocks[b].skip_txfm = skip[b];
    blocks[b].ref_frame[0] = ref0[b];
    blocks[b].ref_frame[1] = NONE_FRAME;
    blocks[b].mode = mode[b];
    blocks[b].segment_id = seg[b];
  }
  MB_MODE_INFO **grid = (MB_MODE_INFO **)calloc((size_t)mi_rows * mi_cols, sizeof(*grid));
  for (int i = 0; i < mi_rows * mi_cols; i++) grid[i] = blk_of_mi[i] >= 0 ? &blocks[blk_of_mi[i]] : NULL;
  cm.mi_params.mi_rows = mi_rows;
  cm.mi_params.mi_cols = mi_cols;
  cm.mi_params.mi_stride = mi_cols;
  cm.mi_params.mi_grid_base = grid;
  struct loopfilter *lf = &cm.lf;
  lf->filter_level[0] = lvl0;
  lf->filter_level[1] = lvl1;
  lf->filter_level_u = lvlu;
  lf->filter_level_v = lvlv;
  lf->sharpness_level = sharp;
  lf->mode_ref_delta_enabled = 1;
  av1_set_default_ref_deltas(lf->ref_deltas);
  av1_set_default_mode_deltas(lf->mode_deltas);
  av1_loop_filter_init(&cm);
  const int np = mono ? 1 : 3;
  for (int p = 0; p < MAX_MB_PLANE; p++) {
    xd.plane[p].subsampling_x = p ? ss_x : 0;
    xd.plane[p].subsampling_y = p ? ss_y : 0;
  }
  YV12_BUFFER_CONFIG fb;
  memset(&fb, 0, sizeof(fb));
  fb.y_buffer = y;
  fb.u_buffer = u;
  fb.v_buffer = v;
  fb.y_stride = ystride;
  fb.uv_stride = uvstride;
  fb.y_crop_width = cw;
  fb.y_crop_height = ch;
  fb.uv_crop_width = (cw + ss_x) >> ss_x;
  fb.uv_crop_height = (ch + ss_y) >> ss_y;
  if (plane_end > np) plane_end = np;
  av1_loop_filter_frame_mt(&fb, &cm, &xd, plane_start, plane_end, partial, NULL, 1, NULL, lpf_opt_level);
  free(grid);
  free(blocks);
}
