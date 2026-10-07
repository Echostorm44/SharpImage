// aomtwin_pred.dll: exports around libaom 3.14.1's intra prediction (reconintra.c, aom_dsp intrapred, cfl.c) so the
// C# port (SharpImage Formats/Av1/Aom/AomReconIntra*.cs, AomIntraPred.cs, AomCfl.cs) can be compared call for call.
// The dsp kernels are called through libaom's run-time dispatch (the SIMD versions avifenc runs).
#include <stdint.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "av1/common/reconintra.h"
#include "av1/common/cfl.h"
#include "aom_ports/mem.h"
#include "av1/common/common_data.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) {
  av1_rtcd();
  aom_dsp_rtcd();
  av1_init_intra_predictors();
}

typedef void (*pfn)(uint8_t *dst, ptrdiff_t stride, const uint8_t *above, const uint8_t *left);

#define ALL_SIZES(t)                                                                                     \
  { aom_##t##_predictor_4x4,   aom_##t##_predictor_8x8,   aom_##t##_predictor_16x16,                   \
    aom_##t##_predictor_32x32, aom_##t##_predictor_64x64, aom_##t##_predictor_4x8,                     \
    aom_##t##_predictor_8x4,   aom_##t##_predictor_8x16,  aom_##t##_predictor_16x8,                    \
    aom_##t##_predictor_16x32, aom_##t##_predictor_32x16, aom_##t##_predictor_32x64,                   \
    aom_##t##_predictor_64x32, aom_##t##_predictor_4x16,  aom_##t##_predictor_16x4,                    \
    aom_##t##_predictor_8x32,  aom_##t##_predictor_32x8,  aom_##t##_predictor_16x64,                   \
    aom_##t##_predictor_64x16 }

// libaom's kernels use aligned SIMD loads / stores on the edge buffers (DECLARE_ALIGNED(16) above_data + 16) and on
// dst (frame rows are 32-aligned): run them on aligned copies of the caller's buffers.
typedef struct {
  DECLARE_ALIGNED(32, uint8_t, above[256]);
  DECLARE_ALIGNED(32, uint8_t, left[256]);
  DECLARE_ALIGNED(32, uint8_t, dst[64 * 128]);
} Scratch;
static Scratch sc;
#define DS 128
static void in(const uint8_t *above, const uint8_t *left, const uint8_t *dst, int stride, int bh) {
  memcpy(sc.above, above - 32, 256);
  memcpy(sc.left, left - 32, 256);
  for (int r = 0; r < bh; r++) memcpy(sc.dst + r * DS, dst + r * stride, 64);
}
static void out(uint8_t *dst, int stride, int bw, int bh) {
  for (int r = 0; r < bh; r++) memcpy(dst + r * stride, sc.dst + r * DS, bw);
}

// kind: 0 dc_128, 1 dc_top, 2 dc_left, 3 dc, 4 v, 5 h, 6 smooth, 7 smooth_v, 8 smooth_h, 9 paeth
// above / left: caller buffers with at least 32 bytes before and 224 after the pointer
EXPORT void twin_pred(int kind, int tx_size, uint8_t *dst, int stride, const uint8_t *above, const uint8_t *left) {
  // the function pointers are RTCD variables: build the table at call time
  pfn t[10][TX_SIZES_ALL] = { ALL_SIZES(dc_128), ALL_SIZES(dc_top), ALL_SIZES(dc_left), ALL_SIZES(dc),
                              ALL_SIZES(v),      ALL_SIZES(h),      ALL_SIZES(smooth), ALL_SIZES(smooth_v),
                              ALL_SIZES(smooth_h), ALL_SIZES(paeth) };
  const int bw = tx_size_wide[tx_size], bh = tx_size_high[tx_size];
  in(above, left, dst, stride, bh);
  t[kind][tx_size](sc.dst, DS, sc.above + 32, sc.left + 32);
  out(dst, stride, bw, bh);
}

EXPORT void twin_dr_z1(uint8_t *dst, int stride, int bw, int bh, const uint8_t *above, const uint8_t *left,
                       int upsample_above, int dx, int dy) {
  in(above, left, dst, stride, bh);
  av1_dr_prediction_z1(sc.dst, DS, bw, bh, sc.above + 32, sc.left + 32, upsample_above, dx, dy);
  out(dst, stride, bw, bh);
}
EXPORT void twin_dr_z2(uint8_t *dst, int stride, int bw, int bh, const uint8_t *above, const uint8_t *left,
                       int upsample_above, int upsample_left, int dx, int dy) {
  in(above, left, dst, stride, bh);
  av1_dr_prediction_z2(sc.dst, DS, bw, bh, sc.above + 32, sc.left + 32, upsample_above, upsample_left, dx, dy);
  out(dst, stride, bw, bh);
}
EXPORT void twin_dr_z3(uint8_t *dst, int stride, int bw, int bh, const uint8_t *above, const uint8_t *left,
                       int upsample_left, int dx, int dy) {
  in(above, left, dst, stride, bh);
  av1_dr_prediction_z3(sc.dst, DS, bw, bh, sc.above + 32, sc.left + 32, upsample_left, dx, dy);
  out(dst, stride, bw, bh);
}
EXPORT void twin_filter_intra(uint8_t *dst, int stride, int tx_size, const uint8_t *above, const uint8_t *left,
                              int mode) {
  const int bw = tx_size_wide[tx_size], bh = tx_size_high[tx_size];
  in(above, left, dst, stride, bh);
  av1_filter_intra_predictor(sc.dst, DS, (TX_SIZE)tx_size, sc.above + 32, sc.left + 32, mode);
  out(dst, stride, bw, bh);
}
EXPORT void twin_filter_edge(uint8_t *p, int sz, int strength) { av1_filter_intra_edge(p, sz, strength); }
EXPORT void twin_upsample_edge(uint8_t *p, int sz) { av1_upsample_intra_edge(p, sz); }
EXPORT int twin_get_dx(int angle) { return av1_get_dx(angle); }
EXPORT int twin_get_dy(int angle) { return av1_get_dy(angle); }
EXPORT int twin_use_upsample(int bs0, int bs1, int delta, int type) {
  return av1_use_intra_edge_upsample(bs0, bs1, delta, type);
}

// CfL kernels via dispatch. sub: 0 = 420, 1 = 422, 2 = 444
EXPORT void twin_cfl_subsample(int sub, int tx_size, const uint8_t *input, int stride, uint16_t *out_q3) {
  cfl_subsample_lbd_fn f = sub == 0   ? cfl_get_luma_subsampling_420_lbd((TX_SIZE)tx_size)
                           : sub == 1 ? cfl_get_luma_subsampling_422_lbd((TX_SIZE)tx_size)
                                      : cfl_get_luma_subsampling_444_lbd((TX_SIZE)tx_size);
  f(input, stride, out_q3);
}
EXPORT void twin_cfl_subtract_average(int tx_size, const uint16_t *src, int16_t *dst) {
  cfl_get_subtract_average_fn((TX_SIZE)tx_size)(src, dst);
}
EXPORT void twin_cfl_predict(int tx_size, const int16_t *ac, uint8_t *dst, int stride, int alpha_q3) {
  cfl_get_predict_lbd_fn((TX_SIZE)tx_size)(ac, dst, stride, alpha_q3);
}
