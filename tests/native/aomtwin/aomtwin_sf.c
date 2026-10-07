// aomtwin_sf.dll: runs libaom 3.14.1's own speed-feature setup (av1_set_speed_features_framesize_independent /
// _dependent / _qindex_dependent, linked from the SIMD build's libaom.a) on a calloc'd AV1_COMP carrying exactly the
// state those functions read, and dumps the whole SPEED_FEATURES struct plus WinnerModeParams as path=value lines.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "config/aom_config.h"
#include "av1/encoder/encoder.h"
#include "av1/encoder/speed_features.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

static char *g_out;
static int g_len, g_cap;

static void P(const char *key, const char *v) {
  int n = snprintf(g_out + g_len, g_cap - g_len, "%s=%s\n", key, v);
  if (n > 0 && g_len + n < g_cap) g_len += n; else g_len = g_cap;
}
static void PV(const char *key, long long v) { char b[32]; snprintf(b, sizeof(b), "%lld", v); P(key, b); }
static const char *fbits(float f) {
  static char b[16]; uint32_t u; memcpy(&u, &f, 4); snprintf(b, sizeof(b), "%08x", u); return b;
}

#include "sf_dump.inc"

static void wmp_dump(const WinnerModeParams *w) {
  char k[96];
  for (int i = 0; i < MODE_EVAL_TYPES; i++) {
    for (int j = 0; j < 2; j++) {
      snprintf(k, sizeof(k), "winner_mode_params.coeff_opt_thresholds[%d][%d]", i, j); PV(k, w->coeff_opt_thresholds[i][j]);
    }
  }
  for (int i = 0; i < MODE_EVAL_TYPES; i++) { snprintf(k, sizeof(k), "winner_mode_params.tx_size_search_methods[%d]", i); PV(k, w->tx_size_search_methods[i]); }
  for (int i = 0; i < MODE_EVAL_TYPES; i++) { snprintf(k, sizeof(k), "winner_mode_params.use_transform_domain_distortion[%d]", i); PV(k, w->use_transform_domain_distortion[i]); }
  for (int i = 0; i < MODE_EVAL_TYPES; i++) { snprintf(k, sizeof(k), "winner_mode_params.tx_domain_dist_threshold[%d]", i); PV(k, w->tx_domain_dist_threshold[i]); }
  for (int i = 0; i < MODE_EVAL_TYPES; i++) { snprintf(k, sizeof(k), "winner_mode_params.skip_txfm_level[%d]", i); PV(k, w->skip_txfm_level[i]); }
  for (int i = 0; i < MODE_EVAL_TYPES; i++) { snprintf(k, sizeof(k), "winner_mode_params.predict_dc_level[%d]", i); PV(k, w->predict_dc_level[i]); }
}

// p: the inputs, in the order of SfTwinInputs on the C# side.
// ops: a list of calls: 1 = framesize_independent, 2 = framesize_dependent, 3 = qindex_dependent,
//      4 = set base_qindex to the next ops entry (then skipped). Returns the dump length (or -1 if out is too small).
EXPORT int sf_twin_run(const int *p, const int *ops, int nops, char *out, int cap) {
  AV1_PRIMARY *ppi = (AV1_PRIMARY *)calloc(1, sizeof(AV1_PRIMARY));
  AV1_COMP *cpi = (AV1_COMP *)calloc(1, sizeof(AV1_COMP));
  if (!ppi || !cpi) { free(ppi); free(cpi); return -2; }
  AV1_COMMON *cm = &cpi->common;
  cpi->ppi = ppi;
  cm->seq_params = &ppi->seq_params;
  ppi->seq_params_locked = 0;

  int k = 0;
  const int speed = p[k++];
  cm->width = p[k++];
  cm->height = p[k++];
  cpi->oxcf.use_highbitdepth = p[k++];
  cm->features.allow_screen_content_tools = p[k++];
  cpi->use_screen_content_tools = p[k++];
  cpi->is_screen_content_type = p[k++];
  cpi->twopass_frame.fr_content_type = (FRAME_CONTENT_TYPE)p[k++];
  cm->quant_params.base_qindex = p[k++];
  cm->current_frame.frame_type = (FRAME_TYPE)p[k++];
  cpi->gf_frame_index = 0;
  ppi->gf_group.update_type[0] = (FRAME_UPDATE_TYPE)p[k++];
  cpi->oxcf.algo_cfg.disable_trellis_quant = p[k++];
  cpi->oxcf.rc_cfg.best_allowed_q = p[k++];
  cpi->oxcf.rc_cfg.worst_allowed_q = p[k++];
  cpi->oxcf.txfm_cfg.enable_tx_size_search = p[k++];
  cpi->mt_info.num_workers = p[k++];
  cpi->oxcf.row_mt = p[k++];
  cpi->oxcf.pass = (enum aom_enc_pass)p[k++];
  ppi->lap_enabled = p[k++];
  cpi->compressor_stage = (COMPRESSOR_STAGE)p[k++];
  cpi->oxcf.rc_cfg.gf_cbr_boost_pct = p[k++];
  cpi->oxcf.tune_cfg.tuning = (aom_tune_metric)p[k++];
  ppi->seq_params_locked = p[k++];
  SequenceHeader *seq = cm->seq_params;
  seq->order_hint_info.enable_dist_wtd_comp = p[k++];
  seq->enable_dual_filter = (uint8_t)p[k++];
  seq->enable_restoration = (uint8_t)p[k++];
  seq->enable_interintra_compound = (uint8_t)p[k++];
  seq->enable_masked_compound = (uint8_t)p[k++];

  cpi->oxcf.mode = ALLINTRA;
  cpi->oxcf.speed = speed;
  cpi->speed = speed;
  cpi->oxcf.unit_test_cfg.motion_vector_unit_test = 0;
  cpi->oxcf.enable_low_complexity_decode = 0;

  for (int i = 0; i < nops; i++) {
    switch (ops[i]) {
      case 1: av1_set_speed_features_framesize_independent(cpi, speed); break;
      case 2: av1_set_speed_features_framesize_dependent(cpi, speed); break;
      case 3: av1_set_speed_features_qindex_dependent(cpi, speed); break;
      case 4: cm->quant_params.base_qindex = ops[++i]; break;
    }
  }

  g_out = out; g_len = 0; g_cap = cap;
  sf_dump_fields(&cpi->sf);
  wmp_dump(&cpi->winner_mode_params);
  PV("seq_params.enable_dist_wtd_comp", seq->order_hint_info.enable_dist_wtd_comp);
  PV("seq_params.enable_dual_filter", seq->enable_dual_filter);
  PV("seq_params.enable_restoration", seq->enable_restoration);
  PV("seq_params.enable_interintra_compound", seq->enable_interintra_compound);
  PV("seq_params.enable_masked_compound", seq->enable_masked_compound);
  int len = g_len >= cap ? -1 : g_len;
  if (len >= 0) out[len] = 0;
  free(cpi);
  free(ppi);
  return len;
}

// sizeof checks for the layout sanity of the harness build
EXPORT int sf_twin_sizeof_sf(void) { return (int)sizeof(SPEED_FEATURES); }
