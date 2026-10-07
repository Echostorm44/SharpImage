// aomtwin_hbd.dll: exports around libaom 3.14.1's high bit depth kernels (RTCD-dispatched: the SIMD versions avifenc
// runs on this machine) so the C# port's high bit depth paths (SharpImage Formats/Av1/Aom/AomHbd*.cs) can be compared
// call for call. Buffers are uint16 sample arrays; the CONVERT_TO_BYTEPTR conversions happen here.
#include <stdint.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "aom_ports/mem.h"
#include "aom_dsp/aom_dsp_common.h"
#include "av1/common/common_data.h"
#include "av1/common/scan.h"
#include "av1/common/idct.h"
#include "av1/common/blockd.h"
#include "av1/encoder/hybrid_fwd_txfm.h"
#include "av1/encoder/av1_quantize.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

// libaom's kernels use aligned loads / stores (coefficient buffers, residuals, frame rows): run them on aligned copies
DECLARE_ALIGNED(32, static int16_t, a_diff[64 * 64]);
DECLARE_ALIGNED(32, static int32_t, a_c0[64 * 64]);
DECLARE_ALIGNED(32, static int32_t, a_c1[64 * 64]);
DECLARE_ALIGNED(32, static int32_t, a_c2[64 * 64]);
DECLARE_ALIGNED(32, static uint16_t, a_px0[160 * 160]);
DECLARE_ALIGNED(32, static uint16_t, a_px1[160 * 160]);

EXPORT void twin_init(void) {
  av1_rtcd();
  aom_dsp_rtcd();
}

// ---- transforms ---------------------------------------------------------------------------------------------------
EXPORT void twin_fwd_txfm(const int16_t *src_diff, int stride, int32_t *coeff, int tx_size, int tx_type, int bd,
                          int lossless) {
  TxfmParam p;
  memset(&p, 0, sizeof(p));
  p.tx_type = (TX_TYPE)tx_type;
  p.tx_size = (TX_SIZE)tx_size;
  p.lossless = lossless;
  p.bd = bd;
  p.is_hbd = 1;
  p.tx_set_type = av1_get_ext_tx_set_type(p.tx_size, 0, 0);
  const int w = tx_size_wide[tx_size], h = tx_size_high[tx_size];
  for (int r = 0; r < h; r++) memcpy(a_diff + r * w, src_diff + r * stride, w * 2);
  av1_fwd_txfm(a_diff, a_c0, w, &p);
  memcpy(coeff, a_c0, av1_get_max_eob(p.tx_size) * 4);
}

// dst: uint16 samples (stride in samples)
EXPORT void twin_inv_txfm_add(const int32_t *dqcoeff, uint16_t *dst, int stride, int tx_size, int tx_type, int eob,
                              int bd, int lossless, int is_inter) {
  TxfmParam p;
  memset(&p, 0, sizeof(p));
  p.tx_type = (TX_TYPE)tx_type;
  p.tx_size = (TX_SIZE)tx_size;
  p.eob = eob;
  p.lossless = lossless;
  p.bd = bd;
  p.is_hbd = 1;
  p.tx_set_type = av1_get_ext_tx_set_type(p.tx_size, is_inter, 0);
  const int w = tx_size_wide[tx_size], h = tx_size_high[tx_size];
  memcpy(a_c0, dqcoeff, av1_get_max_eob(p.tx_size) * 4);
  for (int r = 0; r < h; r++) memcpy(a_px0 + r * 64, dst + r * stride, w * 2);
  av1_highbd_inv_txfm_add(a_c0, CONVERT_TO_BYTEPTR(a_px0), 64, &p);
  for (int r = 0; r < h; r++) memcpy(dst + r * stride, a_px0 + r * 64, w * 2);
}

// ---- quantizers ---------------------------------------------------------------------------------------------------
static void widen(const int16_t *p2, int16_t *p8) { p8[0] = p2[0]; for (int i = 1; i < 8; i++) p8[i] = p2[1]; }

EXPORT int twin_quantize_fp(const int32_t *coeff, int n, int tx_size, int tx_type, const int16_t *quant2,
                            const int16_t *dequant2, const int16_t *round2, int log_scale, int32_t *qcoeff,
                            int32_t *dqcoeff) {
  int16_t quant[8], dequant[8], round[8], zbin[8] = { 0 }, shift[8] = { 0 };
  widen(quant2, quant); widen(dequant2, dequant); widen(round2, round);
  const SCAN_ORDER *so = get_scan((TX_SIZE)tx_size, (TX_TYPE)tx_type);
  uint16_t eob = 0;
  memcpy(a_c0, coeff, n * 4);
  av1_highbd_quantize_fp(a_c0, n, zbin, round, quant, shift, a_c1, a_c2, dequant, &eob, so->scan, so->iscan,
                         log_scale);
  memcpy(qcoeff, a_c1, n * 4); memcpy(dqcoeff, a_c2, n * 4);
  return eob;
}

EXPORT int twin_quantize_b(const int32_t *coeff, int n, int tx_size, int tx_type, const int16_t *zbin2,
                           const int16_t *round2, const int16_t *quant2, const int16_t *shift2, const int16_t *dequant2,
                           int log_scale, int32_t *qcoeff, int32_t *dqcoeff) {
  int16_t quant[8], dequant[8], round[8], zbin[8], shift[8];
  widen(quant2, quant); widen(dequant2, dequant); widen(round2, round); widen(zbin2, zbin); widen(shift2, shift);
  const SCAN_ORDER *so = get_scan((TX_SIZE)tx_size, (TX_TYPE)tx_type);
  uint16_t eob = 0;
  memcpy(a_c0, coeff, n * 4);
  if (log_scale == 0)
    aom_highbd_quantize_b(a_c0, n, zbin, round, quant, shift, a_c1, a_c2, dequant, &eob, so->scan, so->iscan);
  else if (log_scale == 1)
    aom_highbd_quantize_b_32x32(a_c0, n, zbin, round, quant, shift, a_c1, a_c2, dequant, &eob, so->scan, so->iscan);
  else
    aom_highbd_quantize_b_64x64(a_c0, n, zbin, round, quant, shift, a_c1, a_c2, dequant, &eob, so->scan, so->iscan);
  memcpy(qcoeff, a_c1, n * 4); memcpy(dqcoeff, a_c2, n * 4);
  return eob;
}

// ---- distortion ---------------------------------------------------------------------------------------------------
typedef unsigned int (*vfn)(const uint8_t *a, int as, const uint8_t *b, int bs, unsigned int *sse);
#define VARS(bd)                                                                                                     \
  { aom_highbd_##bd##_variance4x4,   aom_highbd_##bd##_variance4x8,   aom_highbd_##bd##_variance8x4,              \
    aom_highbd_##bd##_variance8x8,   aom_highbd_##bd##_variance8x16,  aom_highbd_##bd##_variance16x8,             \
    aom_highbd_##bd##_variance16x16, aom_highbd_##bd##_variance16x32, aom_highbd_##bd##_variance32x16,            \
    aom_highbd_##bd##_variance32x32, aom_highbd_##bd##_variance32x64, aom_highbd_##bd##_variance64x32,            \
    aom_highbd_##bd##_variance64x64, aom_highbd_##bd##_variance64x128, aom_highbd_##bd##_variance128x64,          \
    aom_highbd_##bd##_variance128x128, aom_highbd_##bd##_variance4x16, aom_highbd_##bd##_variance16x4,            \
    aom_highbd_##bd##_variance8x32,  aom_highbd_##bd##_variance32x8,  aom_highbd_##bd##_variance16x64,            \
    aom_highbd_##bd##_variance64x16 }

EXPORT unsigned int twin_variance(int bd, int bsize, const uint16_t *a, int as, const uint16_t *b, int bs,
                                  unsigned int *sse) {
  vfn t[3][BLOCK_SIZES_ALL] = { VARS(8), VARS(10), VARS(12) };
  const int w = block_size_wide[bsize], h = block_size_high[bsize];
  for (int r = 0; r < h; r++) { memcpy(a_px0 + r * 160, a + r * as, w * 2); memcpy(a_px1 + r * 160, b + r * bs, w * 2); }
  return t[(bd - 8) >> 1][bsize](CONVERT_TO_BYTEPTR(a_px0), 160, CONVERT_TO_BYTEPTR(a_px1), 160, sse);
}

EXPORT int64_t twin_sse(const uint16_t *a, int as, const uint16_t *b, int bs, int w, int h) {
  for (int r = 0; r < h; r++) { memcpy(a_px0 + r * 160, a + r * as, w * 2); memcpy(a_px1 + r * 160, b + r * bs, w * 2); }
  return aom_highbd_sse(CONVERT_TO_BYTEPTR(a_px0), 160, CONVERT_TO_BYTEPTR(a_px1), 160, w, h);
}

EXPORT int64_t twin_block_error(const int32_t *coeff, const int32_t *dq, int n, int64_t *ssz, int bd) {
  memcpy(a_c0, coeff, n * 4); memcpy(a_c1, dq, n * 4);
  return av1_highbd_block_error(a_c0, a_c1, n, ssz, bd);
}

EXPORT void twin_subtract(int rows, int cols, int16_t *diff, int ds, const uint16_t *src, int ss, const uint16_t *pred,
                          int ps) {
  for (int r = 0; r < rows; r++) { memcpy(a_px0 + r * 160, src + r * ss, cols * 2); memcpy(a_px1 + r * 160, pred + r * ps, cols * 2); }
  aom_highbd_subtract_block(rows, cols, a_diff, 64, CONVERT_TO_BYTEPTR(a_px0), 160, CONVERT_TO_BYTEPTR(a_px1), 160);
  for (int r = 0; r < rows; r++) memcpy(diff + r * ds, a_diff + r * 64, cols * 2);
}

// ---- intra prediction ---------------------------------------------------------------------------------------------
typedef void (*hpfn)(uint16_t *dst, ptrdiff_t stride, const uint16_t *above, const uint16_t *left, int bd);
#define HALL_SIZES(t)                                                                                                \
  { aom_highbd_##t##_predictor_4x4,   aom_highbd_##t##_predictor_8x8,   aom_highbd_##t##_predictor_16x16,         \
    aom_highbd_##t##_predictor_32x32, aom_highbd_##t##_predictor_64x64, aom_highbd_##t##_predictor_4x8,           \
    aom_highbd_##t##_predictor_8x4,   aom_highbd_##t##_predictor_8x16,  aom_highbd_##t##_predictor_16x8,          \
    aom_highbd_##t##_predictor_16x32, aom_highbd_##t##_predictor_32x16, aom_highbd_##t##_predictor_32x64,         \
    aom_highbd_##t##_predictor_64x32, aom_highbd_##t##_predictor_4x16,  aom_highbd_##t##_predictor_16x4,          \
    aom_highbd_##t##_predictor_8x32,  aom_highbd_##t##_predictor_32x8,  aom_highbd_##t##_predictor_16x64,         \
    aom_highbd_##t##_predictor_64x16 }
DECLARE_ALIGNED(32, static uint16_t, e_above[512]);
DECLARE_ALIGNED(32, static uint16_t, e_left[512]);
DECLARE_ALIGNED(32, static uint16_t, e_dst[64 * 128]);
// above / left: caller buffers with 64 samples before and 448 after the pointer; dst stride 128 here
static void ein(const uint16_t *above, const uint16_t *left) { memcpy(e_above, above - 64, 1024); memcpy(e_left, left - 64, 1024); }
static void eout(uint16_t *dst, int stride, int bw, int bh) { for (int r = 0; r < bh; r++) memcpy(dst + r * stride, e_dst + r * 128, bw * 2); }

// kind: 0 dc_128, 1 dc_top, 2 dc_left, 3 dc, 4 v, 5 h, 6 smooth, 7 smooth_v, 8 smooth_h, 9 paeth
EXPORT void twin_pred(int kind, int tx_size, uint16_t *dst, int stride, const uint16_t *above, const uint16_t *left, int bd) {
  hpfn t[10][TX_SIZES_ALL] = { HALL_SIZES(dc_128), HALL_SIZES(dc_top), HALL_SIZES(dc_left), HALL_SIZES(dc),
                               HALL_SIZES(v), HALL_SIZES(h), HALL_SIZES(smooth), HALL_SIZES(smooth_v),
                               HALL_SIZES(smooth_h), HALL_SIZES(paeth) };
  ein(above, left);
  t[kind][tx_size](e_dst, 128, e_above + 64, e_left + 64, bd);
  eout(dst, stride, tx_size_wide[tx_size], tx_size_high[tx_size]);
}
EXPORT void twin_dr(int zone, uint16_t *dst, int stride, int bw, int bh, const uint16_t *above, const uint16_t *left,
                    int up_above, int up_left, int dx, int dy, int bd) {
  ein(above, left);
  if (zone == 1) av1_highbd_dr_prediction_z1(e_dst, 128, bw, bh, e_above + 64, e_left + 64, up_above, dx, dy, bd);
  else if (zone == 2) av1_highbd_dr_prediction_z2(e_dst, 128, bw, bh, e_above + 64, e_left + 64, up_above, up_left, dx, dy, bd);
  else av1_highbd_dr_prediction_z3(e_dst, 128, bw, bh, e_above + 64, e_left + 64, up_left, dx, dy, bd);
  eout(dst, stride, bw, bh);
}
// p: buffer with 64 samples before and 448 after the pointer; the whole window is copied back (side effects)
EXPORT void twin_filter_edge(uint16_t *p, int sz, int strength) {
  memcpy(e_above, p - 64, 1024); av1_highbd_filter_intra_edge(e_above + 64, sz, strength); memcpy(p - 64, e_above, 1024);
}
EXPORT void twin_upsample_edge(uint16_t *p, int sz, int bd) {
  memcpy(e_above, p - 64, 1024); av1_highbd_upsample_intra_edge(e_above + 64, sz, bd); memcpy(p - 64, e_above, 1024);
}

// ---- the lowbd kernels nonrd block_yrd runs on high bit depth residuals -------------------------------------------
// kind: 0 aom_fdct4x4, 1 aom_hadamard_8x8, 2 aom_hadamard_16x16; diff stride 16 (64 for 16x16 not needed)
EXPORT void twin_lbd_txfm(int kind, const int16_t *diff, int stride, int32_t *coeff) {
  int n = kind == 0 ? 4 : kind == 1 ? 8 : 16;
  for (int r = 0; r < n; r++) memcpy(a_diff + r * 64, diff + r * stride, n * 2);
  if (kind == 0) aom_fdct4x4(a_diff, a_c0, 64);
  else if (kind == 1) aom_hadamard_8x8(a_diff, 64, a_c0);
  else aom_hadamard_16x16(a_diff, 64, a_c0);
  memcpy(coeff, a_c0, n * n * 4);
}
