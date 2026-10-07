// aomtwin_ml.dll: exports around libaom 3.14.1's neural-net inference, its ML prune models and their feature
// extraction (linked from the pristine SIMD build's libaom.a, through its run-time CPU dispatch), for the C# port's
// twin tests (tests/SharpImage.Tests/Formats/AomMlTwinTests.cs). Static functions come from twin_tx.c /
// twin_part.c (copies of tx_search.c / partition_strategy.c compiled with those files' own flags).
#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "av1/common/quant_common.h"
#include "av1/encoder/ml.h"
#include "twin_ml_configs.h"
#include "twin_fpcw.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

int twin_fpcw_target = 0x37F;

EXPORT void twin_init(void) { av1_rtcd(); aom_dsp_rtcd(); }
EXPORT void twin_set_fpcw_target(int cw) { twin_fpcw_target = cw; }

EXPORT int twin_num_cfgs(void) { return TWIN_NUM_CFGS; }

// info: num_inputs, num_outputs, num_hidden_layers, num_hidden_nodes[0..9]
EXPORT void twin_cfg_info(int id, int *info) {
  const NN_CONFIG *c = twin_cfgs[id];
  info[0] = c->num_inputs; info[1] = c->num_outputs; info[2] = c->num_hidden_layers;
  for (int l = 0; l < NN_MAX_HIDDEN_LAYERS; l++) info[3 + l] = c->num_hidden_nodes[l];
}

EXPORT void twin_cfg_layer(int id, int layer, float *w, float *b) {
  const NN_CONFIG *c = twin_cfgs[id];
  int nin = layer == 0 ? c->num_inputs : c->num_hidden_nodes[layer - 1];
  int nout = layer == c->num_hidden_layers ? c->num_outputs : c->num_hidden_nodes[layer];
  memcpy(w, c->weights[layer], sizeof(float) * nin * nout);
  memcpy(b, c->bias[layer], sizeof(float) * nout);
}

EXPORT void twin_nn_predict(int id, const float *in, int reduce_prec, float *out) {
  av1_nn_predict(in, twin_cfgs[id], reduce_prec, out);
}

// an arbitrary network (to reach every AVX2 / SSE3 layer shape): weights and biases concatenated layer by layer
EXPORT void twin_nn_predict_custom(int num_inputs, int num_outputs, int num_hidden, const int *nodes, const float *w,
                                   const float *b, const float *in, int reduce_prec, float *out) {
  NN_CONFIG c;
  memset(&c, 0, sizeof(c));
  c.num_inputs = num_inputs; c.num_outputs = num_outputs; c.num_hidden_layers = num_hidden;
  int nin = num_inputs;
  for (int l = 0; l <= num_hidden; l++) {
    int nout = l == num_hidden ? num_outputs : nodes[l];
    if (l < num_hidden) c.num_hidden_nodes[l] = nout;
    c.weights[l] = w; c.bias[l] = b;
    w += nin * nout; b += nout;
    nin = nout;
  }
  av1_nn_predict(in, &c, reduce_prec, out);
}

EXPORT void twin_nn_softmax(const float *in, float *out, int n) { WITH_FPCW(av1_nn_softmax(in, out, n)); }
EXPORT void twin_nn_fast_softmax_16(const float *in, float *out) { av1_nn_fast_softmax_16(in, out); }
EXPORT void twin_nn_output_prec_reduce(float *out, int n) { av1_nn_output_prec_reduce(out, n); }

// the libm functions the ML code calls, as this toolchain resolves them
EXPORT void twin_expf_bits(uint32_t start, uint32_t count, float *out) {
  unsigned short old = twin_fpcw_enter();
  for (uint32_t i = 0; i < count; i++) { float x; uint32_t u = start + i; memcpy(&x, &u, 4); out[i] = expf(x); }
  twin_fpcw_leave(old);
}
EXPORT void twin_log1pf_bits(uint32_t start, uint32_t step, uint32_t count, float *out) {
  unsigned short old = twin_fpcw_enter();
  for (uint32_t i = 0; i < count; i++) { float x; uint32_t u = start + i * step; memcpy(&x, &u, 4); out[i] = log1pf(x); }
  twin_fpcw_leave(old);
}
EXPORT void twin_log1pf_many(const float *in, float *out, int n) {
  WITH_FPCW(for (int i = 0; i < n; i++) out[i] = log1pf(in[i]));
}
EXPORT int twin_fpcw(void) { unsigned short cw; __asm__ volatile("fnstcw %0" : "=m"(cw)); return cw; }

EXPORT void twin_horver(const int16_t *diff, int stride, int w, int h, float *hv) {
  av1_get_horver_correlation_full(diff, stride, w, h, &hv[0], &hv[1]);
}

EXPORT void twin_blk_sse_sum(const int16_t *data, int stride, int bw, int bh, int *x_sum, int64_t *x2_sum) {
  aom_get_blk_sse_sum(data, stride, bw, bh, x_sum, x2_sum);
}

EXPORT int twin_dc_q(int qindex, int bd) { return av1_dc_quant_QTX(qindex, 0, (aom_bit_depth_t)bd); }
