// twin_sp.c: libaom 3.14.1 kernels (RTCD-dispatched, as avifenc runs them on an AVX2 machine) for the search-speed
// work: each call either runs the kernel once (twin check) or iters times in a loop and returns the elapsed ns
// (QueryPerformanceCounter / CLOCK_MONOTONIC), so the C# port can be timed against the same inputs.
#ifndef _WIN32
#define _POSIX_C_SOURCE 200112L   // clock_gettime under -std=c11
#endif
#include <stdint.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "av1/common/av1_txfm.h"
#include "av1/common/blockd.h"
#include "av1/encoder/hybrid_fwd_txfm.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) { av1_rtcd(); aom_dsp_rtcd(); }

static double now_ns(void) {
#ifdef _WIN32
  LARGE_INTEGER f, c;
  QueryPerformanceFrequency(&f);
  QueryPerformanceCounter(&c);
  return (double)c.QuadPart * 1e9 / (double)f.QuadPart;
#else
  struct timespec ts;
  clock_gettime(CLOCK_MONOTONIC, &ts);
  return (double)ts.tv_sec * 1e9 + (double)ts.tv_nsec;
#endif
}

// av1_lowbd_fwd_txfm (8-bit, lossy) on src_diff (stride) -> coeff (64 * 64 ints); iters > 0: the loop's ns. The SIMD
// kernels use aligned loads / stores: in the encoder the residual and coefficient buffers are 32-byte aligned
// (aom_memalign) and the residual stride is the block width, so every row of a tx block 16+ wide is 32-byte aligned.
// The caller's arrays (managed, 8-byte aligned, any stride) are therefore repacked into such a buffer.
EXPORT double twin_fwd(const int16_t *src_diff, int stride, int32_t *coeff, int tx_size, int tx_type, int iters) {
  static int16_t diff_buf[64 * 64] __attribute__((aligned(32)));
  static int32_t coeff_buf[64 * 64] __attribute__((aligned(32)));
  const int w = tx_size_wide[tx_size], h = tx_size_high[tx_size];
  const int astride = (w + 15) & ~15;
  for (int r = 0; r < h; r++) memcpy(diff_buf + r * astride, src_diff + r * stride, sizeof(int16_t) * w);
  TxfmParam p;
  memset(&p, 0, sizeof(p));
  p.tx_type = (TX_TYPE)tx_type;
  p.tx_size = (TX_SIZE)tx_size;
  p.bd = 8;
  p.tx_set_type = av1_get_ext_tx_set_type((TX_SIZE)tx_size, 0, 0);
  double t = 0;
  if (iters <= 0) {
    av1_lowbd_fwd_txfm(diff_buf, coeff_buf, astride, &p);
  } else {
    t = now_ns();
    for (int i = 0; i < iters; i++) av1_lowbd_fwd_txfm(diff_buf, coeff_buf, astride, &p);
    t = now_ns() - t;
  }
  memcpy(coeff, coeff_buf, sizeof(coeff_buf));
  return t;
}

// av1_quantize_fp (n_coeffs <= 256 path: the plain fp quantizer; log_scale 0)
EXPORT double twin_quant_fp(const int32_t *coeff, int n, const int16_t *zbin, const int16_t *round, const int16_t *quant,
                            const int16_t *qshift, int32_t *qcoeff, int32_t *dqcoeff, const int16_t *dequant,
                            uint16_t *eob, const int16_t *scan, const int16_t *iscan, int log_scale, int iters) {
  double t = now_ns();
  int k = iters <= 0 ? 1 : iters;
  for (int i = 0; i < k; i++) {
    if (log_scale == 0)
      av1_quantize_fp(coeff, n, zbin, round, quant, qshift, qcoeff, dqcoeff, dequant, eob, scan, iscan);
    else if (log_scale == 1)
      av1_quantize_fp_32x32(coeff, n, zbin, round, quant, qshift, qcoeff, dqcoeff, dequant, eob, scan, iscan);
    else
      av1_quantize_fp_64x64(coeff, n, zbin, round, quant, qshift, qcoeff, dqcoeff, dequant, eob, scan, iscan);
  }
  return now_ns() - t;
}

EXPORT double twin_sum_squares_2d_i16(const int16_t *src, int stride, int w, int h, uint64_t *out, int iters) {
  double t = now_ns();
  int k = iters <= 0 ? 1 : iters;
  uint64_t s = 0;
  for (int i = 0; i < k; i++) s += aom_sum_squares_2d_i16(src, stride, w, h);
  *out = s / k;
  return now_ns() - t;
}

EXPORT double twin_txb_init_levels(const int32_t *coeff, int w, int h, uint8_t *levels, int iters) {
  double t = now_ns();
  int k = iters <= 0 ? 1 : iters;
  for (int i = 0; i < k; i++) av1_txb_init_levels(coeff, w, h, levels);
  return now_ns() - t;
}

EXPORT double twin_satd(const int32_t *coeff, int n, int *out, int iters) {
  double t = now_ns();
  int k = iters <= 0 ? 1 : iters;
  int s = 0;
  for (int i = 0; i < k; i++) s += aom_satd(coeff, n);
  *out = s / k;
  return now_ns() - t;
}

EXPORT double twin_block_error(const int32_t *coeff, const int32_t *dqcoeff, int n, int64_t *sse, int64_t *out, int iters) {
  double t = now_ns();
  int k = iters <= 0 ? 1 : iters;
  int64_t s = 0;
  for (int i = 0; i < k; i++) s += av1_block_error(coeff, dqcoeff, n, sse);
  *out = s / k;
  return now_ns() - t;
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

// av1_optimize_txb iters times on the same block (qcoeff / dqcoeff restored from q0 / dq0 each time); returns ns
EXPORT double twin_optimize_txb_bench(const int32_t *costs, int plane, int tx_size, int tx_type, int txb_skip_ctx, int dc_sign_ctx,
                             int32_t *coeff, int32_t *qcoeff, int32_t *dqcoeff, const int32_t *q0, const int32_t *dq0, int n, int eob,
                             int16_t *dequant2, int rdmult, int sharpness, int *rate, int iters) {
  twin_ctx_init();
  memcpy(&g_cpi->td.mb.coeff_costs, costs, sizeof(CoeffCosts));
  g_cpi->sf.tx_sf.use_chroma_trellis_rd_mult = 0;
  g_cpi->oxcf.tune_cfg.tuning = AOM_TUNE_PSNR;
  g_cpi->oxcf.tune_cfg.dist_metric = AOM_DIST_METRIC_PSNR;
  TXB_CTX ctx = { txb_skip_ctx, dc_sign_ctx };
  setup_block(plane, rdmult, dequant2, coeff, qcoeff, dqcoeff, eob);
  int r = 0;
  double t = now_ns();
  for (int i = 0; i < iters; i++) {
    memcpy(qcoeff, q0, n * 4); memcpy(dqcoeff, dq0, n * 4);
    g_cpi->td.mb.plane[plane].eobs[0] = (uint16_t)eob;
    r += av1_optimize_txb(g_cpi, &g_cpi->td.mb, plane, 0, (TX_SIZE)tx_size, (TX_TYPE)tx_type, &ctx, rate, sharpness);
  }
  return now_ns() - t;
}

// av1_optimize_txb over a capture (records: tx, type, plane, skip ctx, dc ctx, eob, dq0, dq1, rdmult, sharpness, n,
// coeff[n], qcoeff[n], dqcoeff[n]); qcoeff / dqcoeff are rewritten in place by the trellis (copies kept by the caller
// in work, of the same layout). Returns ns; rate_sum / eob_sum: sums for the twin check.
EXPORT double twin_optimize_txb_file(const int32_t *costs, const int32_t *rec, int nrec, int32_t *work, long long *rate_sum,
                                     long long *eob_sum) {
  twin_ctx_init();
  memcpy(&g_cpi->td.mb.coeff_costs, costs, sizeof(CoeffCosts));
  g_cpi->sf.tx_sf.use_chroma_trellis_rd_mult = 0;
  g_cpi->oxcf.tune_cfg.tuning = AOM_TUNE_PSNR;
  g_cpi->oxcf.tune_cfg.dist_metric = AOM_DIST_METRIC_PSNR;
  long long rs = 0, es = 0;
  double t = now_ns();
  const int32_t *r = rec; int32_t *w = work;
  for (int k = 0; k < nrec; k++) {
    int n = r[10];
    if (k == 0) { int16_t d0[2] = { 1, 1 }; setup_block(0, 1, d0, w, w, w, 0); }
    static uint16_t eobs[4]; static uint8_t ectx[4]; static int16_t deq[8];
    deq[0] = (int16_t)r[6]; for (int i = 1; i < 8; i++) deq[i] = (int16_t)r[7];
    struct macroblock_plane *p = &g_cpi->td.mb.plane[r[2]];
    p->coeff = (int32_t *)(r + 11); p->qcoeff = w + 11 + n; p->dqcoeff = w + 11 + 2 * n; p->eobs = eobs; p->txb_entropy_ctx = ectx;
    p->dequant_QTX = deq; eobs[0] = (uint16_t)r[5]; g_cpi->td.mb.rdmult = r[8];
    TXB_CTX ctx = { r[3], r[4] };
    int rate;
    es += av1_optimize_txb(g_cpi, &g_cpi->td.mb, r[2], 0, (TX_SIZE)r[0], (TX_TYPE)r[1], &ctx, &rate, r[9]);
    rs += rate;
    r += 11 + 3 * n; w += 11 + 3 * n;
  }
  double el = now_ns() - t;
  *rate_sum = rs; *eob_sum = es;
  return el;
}

// ---- the partition CNN (av1_cnn_predict_img_multi_out on av1_intra_mode_cnn_partition_cnn_config), iters runs ----
#include "av1/encoder/cnn.h"
#include "av1/encoder/partition_cnn_weights.h"
EXPORT double twin_cnn_partition_bench(const uint8_t *src, int stride, float *cnn_buffer, int iters) {
  const CNN_CONFIG *cnn_config = &av1_intra_mode_cnn_partition_cnn_config;
  const CNN_THREAD_DATA thread_data = { .num_workers = 1, .workers = NULL };
  const int output_dims[4] = { 1, 2, 4, 8 };
  const int out_chs[4] = { CNN_BRANCH_0_OUT_CH, CNN_BRANCH_1_OUT_CH, CNN_BRANCH_2_OUT_CH, CNN_BRANCH_3_OUT_CH };
  float *output_buffer[CNN_TOT_OUT_CH];
  float **cur = output_buffer; float *p = cnn_buffer;
  for (int o = 0; o < 4; o++) { for (int ch = 0; ch < out_chs[o]; ch++) { cur[ch] = p; p += output_dims[o] * output_dims[o]; } cur += out_chs[o]; }
  CNN_MULTI_OUT output = { .num_outputs = 4, .output_channels = out_chs, .output_strides = output_dims, .output_buffer = output_buffer };
  uint8_t *image[1] = { (uint8_t *)src };
  double t = now_ns();
  for (int i = 0; i < iters; i++) av1_cnn_predict_img_multi_out(image, 65, 65, stride, cnn_config, &thread_data, &output);
  return now_ns() - t;
}
