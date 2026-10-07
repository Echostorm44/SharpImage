// twin_tx.c: libaom 3.14.1's tx_search.c compiled into the twin (its own flags: -O3, no -m SIMD flags) so its static
// ML helpers can be called.
#include "av1/encoder/tx_search.c"

#include "twin_fpcw.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

static MACROBLOCK *twin_x(void) {
  static MACROBLOCK *x;
  static MB_MODE_INFO mbmi, *pmbmi = &mbmi;
  if (!x) { x = calloc(1, sizeof(*x)); x->e_mbd.mi = &pmbmi; }
  return x;
}

EXPORT void twin_energy_finer(const int16_t *diff, int stride, int bw, int bh, float *hordist, float *verdist) {
  get_energy_distribution_finer(diff, stride, bw, bh, hordist, verdist);
}

EXPORT int twin_mean_dev(const int16_t *data, int stride, int bw, int bh, float *features) {
  return get_mean_dev_features(data, stride, bw, bh, features);
}

// src_diff: the whole (bsize) residual block, stride block_size_wide[bsize]
EXPORT void twin_prune_tx_2d(int16_t *src_diff, int bsize, int tx_size, int blk_row, int blk_col, int tx_set_type,
                             int prune_mode, int *txk_map, int *allowed_mask) {
  MACROBLOCK *x = twin_x();
  x->plane[0].src_diff = src_diff;
  uint16_t m = (uint16_t)*allowed_mask;
  prune_tx_2D(x, (BLOCK_SIZE)bsize, (TX_SIZE)tx_size, blk_row, blk_col, (TxSetType)tx_set_type,
              (TX_TYPE_PRUNE_MODE)prune_mode, txk_map, &m);
  *allowed_mask = m;
}

EXPORT int twin_tx_split(int16_t *src_diff, int bsize, int blk_row, int blk_col, int tx_size) {
  MACROBLOCK *x = twin_x();
  x->plane[0].src_diff = src_diff;
  return ml_predict_tx_split(x, (BLOCK_SIZE)bsize, blk_row, blk_col, (TX_SIZE)tx_size);
}

// returns nn_prune_depths_for_intra_tx after the call (99 if the function left it alone)
EXPORT int twin_intra_tx_depth(int16_t *src_diff, int blk_row, int blk_col, int bsize, int tx_size, int lossless, int bd,
                               unsigned int source_variance, int qindex) {
  MACROBLOCK *x = twin_x();
  x->plane[0].src_diff = src_diff;
  x->e_mbd.mi[0]->segment_id = 0;
  x->e_mbd.lossless[0] = lossless;
  x->e_mbd.bd = bd;
  x->source_variance = source_variance;
  x->qindex = qindex;
  x->txfm_search_params.nn_prune_depths_for_intra_tx = (TX_PRUNE_TYPE)99;
  WITH_FPCW(ml_predict_intra_tx_depth_prune(x, blk_row, blk_col, (BLOCK_SIZE)bsize, (TX_SIZE)tx_size));
  return (int)x->txfm_search_params.nn_prune_depths_for_intra_tx;
}
