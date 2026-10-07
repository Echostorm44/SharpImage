// aomtwin.dll: thin exports around libaom 3.14.1's own encoder functions (linked from the pristine SIMD build's
// libaom.a) so the C# port can be compared with them call for call on the same inputs.
#include <stdint.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "aom_dsp/prob.h"
#include "av1/common/scan.h"
#include "av1/encoder/cost.h"
#include "av1/encoder/rd.h"
#include "av1/encoder/av1_quantize.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) { av1_rtcd(); aom_dsp_rtcd(); }

EXPORT int twin_cost_symbol(int p15) { return av1_cost_symbol((aom_cdf_prob)p15); }

// cdf: libaom layout (AOM_ICDF values, the last 0, then the count slot)
EXPORT void twin_cost_tokens_from_cdf(int *costs, const uint16_t *cdf) { av1_cost_tokens_from_cdf(costs, cdf, NULL); }

EXPORT int twin_rdmult_kf(int qindex, int bd) {
  return av1_compute_rd_mult_based_on_qindex((aom_bit_depth_t)bd, KF_UPDATE, qindex, AOM_TUNE_PSNR, ALLINTRA);
}

// out: 7 tables x 3 planes x 256 q x 2 (quant, quant_shift, zbin, round, quant_fp, round_fp, dequant)
EXPORT void twin_build_quantizer(int bd, int ydc, int udc, int uac, int vdc, int vac, int sharpness, int16_t *out) {
  static QUANTS q; static Dequants d;
  av1_build_quantizer((aom_bit_depth_t)bd, ydc, udc, uac, vdc, vac, &q, &d, sharpness);
  int16_t (*t[7][3])[8] = {
    { q.y_quant, q.u_quant, q.v_quant }, { q.y_quant_shift, q.u_quant_shift, q.v_quant_shift },
    { q.y_zbin, q.u_zbin, q.v_zbin }, { q.y_round, q.u_round, q.v_round },
    { q.y_quant_fp, q.u_quant_fp, q.v_quant_fp }, { q.y_round_fp, q.u_round_fp, q.v_round_fp },
    { d.y_dequant_QTX, d.u_dequant_QTX, d.v_dequant_QTX } };
  for (int k = 0; k < 7; k++)
    for (int p = 0; p < 3; p++)
      for (int qi = 0; qi < 256; qi++)
        for (int i = 0; i < 2; i++) out[((k * 3 + p) * 256 + qi) * 2 + i] = t[k][p][qi][i];
}

// the encoder's quantizers through libaom's run-time dispatch (SIMD where it has it); params: [dc, ac] pairs (the
// SIMD kernels read 8 lanes, so they are widened as av1_build_quantizer does)
static void widen(const int16_t *p2, int16_t *p8) { p8[0] = p2[0]; for (int i = 1; i < 8; i++) p8[i] = p2[1]; }

// The SIMD kernels use aligned loads / stores: libaom's encoder keeps coefficient, residual and quantizer buffers
// 32-byte aligned (aom_memalign / DECLARE_ALIGNED) with block-width strides. The caller's arrays are managed (8-byte
// aligned, any stride), so the wrappers below go through aligned copies.
#define ALIGNED32 __attribute__((aligned(32)))
static int32_t g_coeff[64 * 64] ALIGNED32, g_qcoeff[64 * 64] ALIGNED32, g_dqcoeff[64 * 64] ALIGNED32;
static int16_t g_diff[64 * 64] ALIGNED32;

// coefficients a transform of tx_size keeps (64-point sides keep 32)
static int twin_ncoef(int tx_size) {
  int w = tx_size_wide[tx_size], h = tx_size_high[tx_size];
  return (w > 32 ? 32 : w) * (h > 32 ? 32 : h);
}

// h rows of w residuals from (src, stride) into g_diff at a 16-element (32-byte) stride; returns that stride
static int twin_pack_diff(const int16_t *src, int stride, int w, int h) {
  int astride = (w + 15) & ~15;
  for (int r = 0; r < h; r++) memcpy(g_diff + r * astride, src + r * stride, sizeof(int16_t) * w);
  return astride;
}

EXPORT int twin_quantize_fp(const int32_t *coeff, int n, int tx_size, int tx_type, const int16_t *quant2,
                            const int16_t *dequant2, const int16_t *round2, int log_scale, int32_t *qcoeff, int32_t *dqcoeff) {
  int16_t quant[8] ALIGNED32, dequant[8] ALIGNED32, round[8] ALIGNED32, zbin[8] ALIGNED32 = { 0 }, shift[8] ALIGNED32 = { 0 };
  widen(quant2, quant); widen(dequant2, dequant); widen(round2, round);
  const SCAN_ORDER *so = get_scan((TX_SIZE)tx_size, (TX_TYPE)tx_type);
  uint16_t eob = 0;
  memcpy(g_coeff, coeff, sizeof(int32_t) * n);
  if (log_scale == 0)
    av1_quantize_fp(g_coeff, n, zbin, round, quant, shift, g_qcoeff, g_dqcoeff, dequant, &eob, so->scan, so->iscan);
  else if (log_scale == 1)
    av1_quantize_fp_32x32(g_coeff, n, zbin, round, quant, shift, g_qcoeff, g_dqcoeff, dequant, &eob, so->scan, so->iscan);
  else
    av1_quantize_fp_64x64(g_coeff, n, zbin, round, quant, shift, g_qcoeff, g_dqcoeff, dequant, &eob, so->scan, so->iscan);
  memcpy(qcoeff, g_qcoeff, sizeof(int32_t) * n);
  memcpy(dqcoeff, g_dqcoeff, sizeof(int32_t) * n);
  return eob;
}

EXPORT int twin_quantize_b(const int32_t *coeff, int n, int tx_size, int tx_type, const int16_t *zbin2, const int16_t *round2,
                           const int16_t *quant2, const int16_t *shift2, const int16_t *dequant2, int log_scale,
                           int32_t *qcoeff, int32_t *dqcoeff) {
  int16_t quant[8] ALIGNED32, dequant[8] ALIGNED32, round[8] ALIGNED32, zbin[8] ALIGNED32, shift[8] ALIGNED32;
  widen(quant2, quant); widen(dequant2, dequant); widen(round2, round); widen(zbin2, zbin); widen(shift2, shift);
  const SCAN_ORDER *so = get_scan((TX_SIZE)tx_size, (TX_TYPE)tx_type);
  uint16_t eob = 0;
  memcpy(g_coeff, coeff, sizeof(int32_t) * n);
  if (log_scale == 0)
    aom_quantize_b(g_coeff, n, zbin, round, quant, shift, g_qcoeff, g_dqcoeff, dequant, &eob, so->scan, so->iscan);
  else if (log_scale == 1)
    aom_quantize_b_32x32(g_coeff, n, zbin, round, quant, shift, g_qcoeff, g_dqcoeff, dequant, &eob, so->scan, so->iscan);
  else
    aom_quantize_b_64x64(g_coeff, n, zbin, round, quant, shift, g_qcoeff, g_dqcoeff, dequant, &eob, so->scan, so->iscan);
  memcpy(qcoeff, g_qcoeff, sizeof(int32_t) * n);
  memcpy(dqcoeff, g_dqcoeff, sizeof(int32_t) * n);
  return eob;
}

// the scan libaom uses for (tx_size, tx_type), for the C# side to compare with its tables
EXPORT void twin_scan(int tx_size, int tx_type, int16_t *out, int n) {
  const SCAN_ORDER *so = get_scan((TX_SIZE)tx_size, (TX_TYPE)tx_type);
  memcpy(out, so->scan, n * sizeof(int16_t));
}

// ---- coefficient costs / cost_coeffs_txb / optimize_txb ----
#include <stdlib.h>
#include "av1/common/entropy.h"
#include "av1/common/quant_common.h"
#include "av1/encoder/encoder.h"
#include "av1/encoder/encodetxb.h"
#include "av1/encoder/txb_rdopt.h"

static AV1_COMP *g_cpi;
static FRAME_CONTEXT g_fc;
static MB_MODE_INFO g_mbmi, *g_mbmi_ptr = &g_mbmi;

static void twin_ctx_init(void) {
  if (g_cpi) return;
  g_cpi = (AV1_COMP *)calloc(1, sizeof(AV1_COMP));
  AV1_COMMON *cm = &g_cpi->common;
  static SequenceHeader seq; seq.bit_depth = AOM_BITS_8; cm->seq_params = &seq;
  cm->fc = &g_fc;
  av1_qm_init(&cm->quant_params, 3);
  MACROBLOCK *x = &g_cpi->td.mb;
  x->e_mbd.mi = &g_mbmi_ptr;
  x->e_mbd.bd = 8;
}

// the coefficient costs from libaom's default CDFs for base_qindex: out = CoeffCosts as ints
EXPORT int twin_default_coeff_costs(int base_qindex, int32_t *out, int out_len) {
  twin_ctx_init();
  AV1_COMMON *cm = &g_cpi->common;
  cm->quant_params.base_qindex = base_qindex;
  av1_default_coef_probs(cm);
  CoeffCosts *cc = &g_cpi->td.mb.coeff_costs;
  av1_fill_coeff_costs(cc, cm->fc, 3);
  int n = (int)(sizeof(CoeffCosts) / sizeof(int));
  if (n <= out_len) memcpy(out, cc, sizeof(CoeffCosts));
  return n;
}

static void setup_block(int plane, int rdmult, int16_t *dequant2, int32_t *coeff, int32_t *qcoeff, int32_t *dqcoeff, int eob) {
  MACROBLOCK *x = &g_cpi->td.mb;
  static uint16_t eobs[4]; static uint8_t ectx[4]; static int16_t deq[8];
  deq[0] = dequant2[0]; for (int i = 1; i < 8; i++) deq[i] = dequant2[1];
  struct macroblock_plane *p = &x->plane[plane];
  p->coeff = coeff; p->qcoeff = qcoeff; p->dqcoeff = dqcoeff; p->eobs = eobs; p->txb_entropy_ctx = ectx; p->dequant_QTX = deq;
  eobs[0] = (uint16_t)eob;
  x->rdmult = rdmult;
  memset(&g_mbmi, 0, sizeof(g_mbmi));   // intra, DC_PRED, segment 0: tx_type cost from zeroed mode costs = 0
  for (int pl = 0; pl < 3; pl++)
    for (int t = 0; t < TX_SIZES_ALL; t++)
      x->e_mbd.plane[pl].seg_iqmatrix[0][t] = g_cpi->common.quant_params.giqmatrix[NUM_QM_LEVELS - 1][pl][t];
}

// costs: CoeffCosts as ints (the C# side's tables, so both run on the same numbers)
EXPORT int twin_cost_coeffs_txb(const int32_t *costs, int plane, int tx_size, int tx_type, int txb_skip_ctx, int dc_sign_ctx,
                                int32_t *qcoeff, int eob) {
  twin_ctx_init();
  memcpy(&g_cpi->td.mb.coeff_costs, costs, sizeof(CoeffCosts));
  static int16_t d2[2] = { 1, 1 };
  setup_block(plane, 1, d2, qcoeff, qcoeff, qcoeff, eob);
  TXB_CTX ctx = { txb_skip_ctx, dc_sign_ctx };
  return av1_cost_coeffs_txb(&g_cpi->td.mb, plane, 0, (TX_SIZE)tx_size, (TX_TYPE)tx_type, &ctx, 0);
}

EXPORT int twin_optimize_txb(const int32_t *costs, int plane, int tx_size, int tx_type, int txb_skip_ctx, int dc_sign_ctx,
                             int32_t *coeff, int32_t *qcoeff, int32_t *dqcoeff, int eob, int16_t *dequant2, int rdmult,
                             int sharpness, int chroma_trellis_mult, int *rate) {
  twin_ctx_init();
  memcpy(&g_cpi->td.mb.coeff_costs, costs, sizeof(CoeffCosts));
  setup_block(plane, rdmult, dequant2, coeff, qcoeff, dqcoeff, eob);
  g_cpi->sf.tx_sf.use_chroma_trellis_rd_mult = chroma_trellis_mult;
  g_cpi->oxcf.tune_cfg.tuning = AOM_TUNE_PSNR;
  g_cpi->oxcf.tune_cfg.dist_metric = AOM_DIST_METRIC_PSNR;
  TXB_CTX ctx = { txb_skip_ctx, dc_sign_ctx };
  return av1_optimize_txb(g_cpi, &g_cpi->td.mb, plane, 0, (TX_SIZE)tx_size, (TX_TYPE)tx_type, &ctx, rate, sharpness);
}


// ---- mode costs ----
#include <stddef.h>
#include "av1/common/entropymode.h"
// fills ModeCosts from libaom's default mode CDFs; out = the struct as ints; offs = field offsets (ints) in this order:
// partition, skip_txfm, y_mode, intra_uv_mode, filter_intra_mode, filter_intra, palette_y_size, palette_uv_size,
// palette_y_mode, palette_uv_mode, palette_y_color, palette_uv_color, cfl, tx_size, intra_tx_type, angle_delta, intrabc
EXPORT int twin_default_mode_costs(int enable_filter_intra, int32_t *out, int out_len, int32_t *offs) {
  twin_ctx_init();
  AV1_COMMON *cm = &g_cpi->common;
  static SequenceHeader seq2;
  seq2 = *cm->seq_params; seq2.enable_filter_intra = (uint8_t)enable_filter_intra; cm->seq_params = &seq2;
  cm->current_frame.frame_type = KEY_FRAME;
  av1_init_mode_probs(cm->fc);
  static ModeCosts mc;
  memset(&mc, 0, sizeof(mc));
  av1_fill_mode_rates(cm, &mc, cm->fc);
  int n = (int)(sizeof(ModeCosts) / sizeof(int));
  if (n <= out_len) memcpy(out, &mc, sizeof(mc));
  int k = 0;
#define OFF(f) offs[k++] = (int32_t)(offsetof(ModeCosts, f) / sizeof(int))
  OFF(partition_cost); OFF(skip_txfm_cost); OFF(y_mode_costs); OFF(intra_uv_mode_cost); OFF(filter_intra_mode_cost);
  OFF(filter_intra_cost); OFF(palette_y_size_cost); OFF(palette_uv_size_cost); OFF(palette_y_mode_cost);
  OFF(palette_uv_mode_cost); OFF(palette_y_color_cost); OFF(palette_uv_color_cost); OFF(cfl_cost); OFF(tx_size_cost);
  OFF(intra_tx_type_costs); OFF(angle_delta_cost); OFF(intrabc_cost);
#undef OFF
  return n;
}

// av1_fwd_txfm through the run-time dispatch (8-bit: av1_lowbd_fwd_txfm, the SIMD kernels libaom's encoder runs)
#include "av1/common/av1_txfm.h"
#include "av1/encoder/hybrid_fwd_txfm.h"
EXPORT void twin_fwd_txfm(const int16_t *src_diff, int stride, int tx_size, int tx_type, int lossless, int32_t *coeff) {
  TxfmParam p; memset(&p, 0, sizeof(p));
  p.tx_type = (TX_TYPE)tx_type; p.tx_size = (TX_SIZE)tx_size; p.lossless = lossless; p.bd = 8; p.is_hbd = 0;
  p.tx_set_type = EXT_TX_SET_ALL16;
  int astride = twin_pack_diff(src_diff, stride, tx_size_wide[tx_size], tx_size_high[tx_size]);
  av1_fwd_txfm(g_diff, g_coeff, astride, &p);
  memcpy(coeff, g_coeff, sizeof(int32_t) * twin_ncoef(tx_size));
}

EXPORT int64_t twin_block_error(const int32_t *coeff, const int32_t *dqcoeff, intptr_t n, int64_t *ssz) {
  memcpy(g_coeff, coeff, sizeof(int32_t) * n);
  memcpy(g_dqcoeff, dqcoeff, sizeof(int32_t) * n);
  return av1_block_error(g_coeff, g_dqcoeff, n, ssz);
}
EXPORT uint64_t twin_sum_squares_2d_i16(const int16_t *src, int stride, int w, int h) {
  int astride = twin_pack_diff(src, stride, w, h);
  return aom_sum_squares_2d_i16(g_diff, astride, w, h);
}
EXPORT uint64_t twin_sum_sse_2d_i16(const int16_t *src, int stride, int w, int h, int *sum) {
  int astride = twin_pack_diff(src, stride, w, h);
  return aom_sum_sse_2d_i16(g_diff, astride, w, h, sum);
}
EXPORT int64_t twin_sse(const uint8_t *a, int as, const uint8_t *b, int bs, int w, int h) { return aom_sse(a, as, b, bs, w, h); }
EXPORT int twin_satd(const int32_t *coeff, int n) {
  memcpy(g_coeff, coeff, sizeof(int32_t) * n);
  return aom_satd(g_coeff, n);
}

// av1_inv_txfm_add through the dispatch (the 8-bit reconstruction the encoder's search and final encode use)
EXPORT void twin_inv_txfm_add(const int32_t *dqcoeff, uint8_t *dst, int stride, int tx_size, int tx_type, int eob) {
  TxfmParam p; memset(&p, 0, sizeof(p));
  p.tx_type = (TX_TYPE)tx_type; p.tx_size = (TX_SIZE)tx_size; p.lossless = 0; p.bd = 8; p.is_hbd = 0; p.eob = eob;
  p.tx_set_type = av1_get_ext_tx_set_type((TX_SIZE)tx_size, 0, 0);
  memset(g_dqcoeff, 0, sizeof(g_dqcoeff));
  memcpy(g_dqcoeff, dqcoeff, sizeof(int32_t) * twin_ncoef(tx_size));
  av1_inv_txfm_add(g_dqcoeff, dst, stride, &p);
}

// the Hadamard transforms through the dispatch (av1_quick_txfm's wht_fwd_txfm)
EXPORT void twin_hadamard(int tx_size, const int16_t *src, int stride, int32_t *coeff) {
  int w = tx_size_wide[tx_size];
  int astride = twin_pack_diff(src, stride, w, w);
  switch (tx_size) {
    case TX_4X4: aom_hadamard_4x4(g_diff, astride, g_coeff); break;
    case TX_8X8: aom_hadamard_8x8(g_diff, astride, g_coeff); break;
    case TX_16X16: aom_hadamard_16x16(g_diff, astride, g_coeff); break;
    default: aom_hadamard_32x32(g_diff, astride, g_coeff); break;
  }
  memcpy(coeff, g_coeff, sizeof(int32_t) * w * w);
}
