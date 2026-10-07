// aomtwin_palsearch.dll: a copy of libaom 3.14.1's palette.c compiled into this TU (same flags as libaom) so its
// file-static functions can be exported, with the tx search it calls (av1_pick_uniform_tx_size_type_yrd /
// av1_txfm_uvrd) replaced by a deterministic MOCK that SharpImage's AomPaletteTwinTests reproduces in C#. Everything
// else the search reaches is libaom's: intra_mode_info_cost_y/uv (static inline, compiled here), av1_get_palette_cache,
// av1_cost_color_map, av1_count_colors, store_winner_mode_stats, and the AVX2 k-means kernels through RTCD.
#define av1_pick_uniform_tx_size_type_yrd mock_pick_uniform_tx_size_type_yrd
#define av1_txfm_uvrd mock_txfm_uvrd
#include "av1/encoder/palette.c"

#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) {
  av1_rtcd();
  aom_dsp_rtcd();
}

// ---- statics ------------------------------------------------------------------------------------------------------
EXPORT int twin_remove_duplicates(int16_t *c, int n) { return remove_duplicates(c, n); }
EXPORT int twin_delta_encode_cost(const int *colors, int num, int bd, int min_val) {
  return delta_encode_cost(colors, num, bd, min_val);
}
EXPORT void twin_extend_map(uint8_t *map, int ow, int oh, int nw, int nh) { extend_palette_color_map(map, ow, oh, nw, nh); }
EXPORT void twin_optimize_colors(uint16_t *cache, int n_cache, int n_colors, int stride, int16_t *centroids, int bd) {
  optimize_palette_colors(cache, n_cache, n_colors, stride, centroids, bd);
}
EXPORT void twin_set_stage2(int winner, int end_n, int *out3) { set_stage2_params(&out3[0], &out3[1], &out3[2], winner, end_n); }
EXPORT void twin_fill_data(const uint8_t *src, int stride, int rows, int cols, int16_t *data, int *lb, int *ub) {
  fill_data_and_get_bounds(src, stride, rows, cols, 0, data, lb, ub);
}
EXPORT void twin_find_top_colors(const int *count_buf, int bd, int n_colors, int16_t *top) {
  find_top_colors(count_buf, bd, n_colors, top);
}

// ---- the mock tx search -------------------------------------------------------------------------------------------
// FNV-1a over the block's (extended) color map and the palette colors; the rate / tx size / tx types / validity follow
// from the hash, the distortion is the true palette prediction SSE over the visible pixels. Every call is logged.
static int32_t *mlog;
static int mlogn, mlogcap;
static void mlog_rec(int kind, int n, uint32_t h, int64_t sse, int64_t ref_best_rd, int rate) {
  if (mlogn + 8 > mlogcap) return;
  int32_t *p = mlog + mlogn;
  p[0] = kind, p[1] = n, p[2] = (int32_t)h, p[3] = (int32_t)sse, p[4] = (int32_t)ref_best_rd,
  p[5] = (int32_t)(ref_best_rd >> 32), p[6] = rate, p[7] = 0x5eed;
  mlogn += 8;
}
static uint32_t fnv(uint32_t h, uint32_t v) { return (h ^ v) * 16777619u; }

void mock_pick_uniform_tx_size_type_yrd(const AV1_COMP *const cpi, MACROBLOCK *x, RD_STATS *rd_stats, BLOCK_SIZE bs,
                                        int64_t ref_best_rd) {
  (void)cpi;
  MACROBLOCKD *const xd = &x->e_mbd;
  MB_MODE_INFO *const mbmi = xd->mi[0];
  av1_init_rd_stats(rd_stats);
  int bw, bh, rows, cols;
  av1_get_block_dimensions(bs, 0, xd, &bw, &bh, &rows, &cols);
  const PALETTE_MODE_INFO *pmi = &mbmi->palette_mode_info;
  const int n = pmi->palette_size[0];
  const uint8_t *map = xd->plane[0].color_index_map;
  const uint8_t *src = x->plane[0].src.buf;
  const int ss = x->plane[0].src.stride;
  int64_t sse = 0;
  uint32_t h = 2166136261u;
  for (int r = 0; r < rows; r++)
    for (int c = 0; c < cols; c++) {
      const int d = src[r * ss + c] - pmi->palette_colors[map[r * bw + c]];
      sse += d * d;
    }
  for (int i = 0; i < bw * bh; i++) h = fnv(h, map[i]);
  for (int i = 0; i < n; i++) h = fnv(h, pmi->palette_colors[i]);
  h = fnv(h, (uint32_t)sse);
  mbmi->tx_size = (TX_SIZE)(h % 5);
  const int nb = mi_size_wide[bs] * mi_size_high[bs];
  for (int i = 0; i < nb; i++) xd->tx_type_map[i] = (uint8_t)((h >> (i & 15)) & 15);
  rd_stats->rate = (int)(h % 3000) + 40 * n;
  rd_stats->dist = sse * 16;
  rd_stats->sse = sse * 16 + 7;
  rd_stats->skip_txfm = (h % 5) == 0;
  if (h % 13 == 4 || (RDCOST(x->rdmult, rd_stats->rate, rd_stats->dist) > ref_best_rd && (h & 2)))
    av1_invalid_rd_stats(rd_stats);
  mlog_rec(0, n, h, sse, ref_best_rd, rd_stats->rate);
}

int mock_txfm_uvrd(const AV1_COMP *const cpi, MACROBLOCK *x, RD_STATS *rd_stats, BLOCK_SIZE bsize,
                   int64_t ref_best_rd) {
  (void)cpi;
  MACROBLOCKD *const xd = &x->e_mbd;
  MB_MODE_INFO *const mbmi = xd->mi[0];
  av1_init_rd_stats(rd_stats);
  int bw, bh, rows, cols;
  av1_get_block_dimensions(bsize, 1, xd, &bw, &bh, &rows, &cols);
  const PALETTE_MODE_INFO *pmi = &mbmi->palette_mode_info;
  const int n = pmi->palette_size[1];
  const uint8_t *map = xd->plane[1].color_index_map;
  int64_t sse = 0;
  uint32_t h = 2166136261u;
  for (int p = 1; p < 3; p++) {
    const uint8_t *src = x->plane[p].src.buf;
    const int ss = x->plane[p].src.stride;
    for (int r = 0; r < rows; r++)
      for (int c = 0; c < cols; c++) {
        const int d = src[r * ss + c] - pmi->palette_colors[p * PALETTE_MAX_SIZE + map[r * bw + c]];
        sse += d * d;
      }
  }
  for (int i = 0; i < bw * bh; i++) h = fnv(h, map[i]);
  for (int i = 0; i < n; i++) h = fnv(fnv(h, pmi->palette_colors[8 + i]), pmi->palette_colors[16 + i]);
  h = fnv(h, (uint32_t)sse);
  rd_stats->rate = (int)(h % 2000) + 30 * n;
  rd_stats->dist = sse * 16;
  rd_stats->sse = sse * 16 + 3;
  rd_stats->skip_txfm = (h % 7) == 0;
  if (h % 11 == 4 || (RDCOST(x->rdmult, rd_stats->rate, rd_stats->dist) > ref_best_rd && (h & 4)))
    av1_invalid_rd_stats(rd_stats);
  mlog_rec(1, n, h, sse, ref_best_rd, rd_stats->rate);
  return rd_stats->rate != INT_MAX;
}

// ---- the search, on a synthetic AV1_COMP / MACROBLOCK -------------------------------------------------------------
static AV1_COMP cpi;
static SequenceHeader seq;
static MACROBLOCK x;
static MB_MODE_INFO cur, above, left, best;
static MB_MODE_INFO *mi_ptr;
static PALETTE_BUFFER pbuf;
static WinnerModeStats wstats[3];
static PICK_MODE_CONTEXT ctx;
static uint8_t txmap_xd[1024];

// MB_MODE_INFO <-> int[32]: bsize, mode, uv_mode, tx_size, use_filter_intra, palette_size0, palette_size1, colors[24],
// segment_id
static void mi_in(MB_MODE_INFO *m, const int *s) {
  memset(m, 0, sizeof(*m));
  m->bsize = (BLOCK_SIZE)s[0];
  m->mode = (PREDICTION_MODE)s[1];
  m->uv_mode = (UV_PREDICTION_MODE)s[2];
  m->tx_size = (TX_SIZE)s[3];
  m->filter_intra_mode_info.use_filter_intra = (uint8_t)s[4];
  m->palette_mode_info.palette_size[0] = (uint8_t)s[5];
  m->palette_mode_info.palette_size[1] = (uint8_t)s[6];
  for (int i = 0; i < 24; i++) m->palette_mode_info.palette_colors[i] = (uint16_t)s[7 + i];
  m->segment_id = (uint8_t)s[31];
}
static void mi_out(const MB_MODE_INFO *m, int *s) {
  s[0] = m->bsize, s[1] = m->mode, s[2] = m->uv_mode, s[3] = m->tx_size, s[4] = m->filter_intra_mode_info.use_filter_intra;
  s[5] = m->palette_mode_info.palette_size[0], s[6] = m->palette_mode_info.palette_size[1];
  for (int i = 0; i < 24; i++) s[7 + i] = m->palette_mode_info.palette_colors[i];
  s[31] = m->segment_id;
}

// cfg: see AomPaletteTwinTests.SearchCase (the order there); costs: y_size[49] uv_size[49] y_color[280] uv_color[280]
// y_mode[42] uv_mode[4] intrabc[2] filter_intra[44] filter_intra_mode[5]
// state (in / out): cur[32] above[32] left[32] best[32] (above / left bsize < 0: NULL)
// maps (in / out): cmap0[16384] cmap1[16384] best_map[4096] kbuf[8192] (int16), wmaps[3 * 4096]
// wstat (in / out): per slot rd lo, rd hi, mbmi[32]; winner count
// io64 (in / out): best_rd, distortion; io32 (in / out): rate, rate_tokenonly, skippable, beat_best_rd
// txmap_out: ctx tx type map [1024]
EXPORT int twin_search(int luma, const int *cfg, const int *costs, int *state, uint8_t *cmap0, uint8_t *cmap1,
                       uint8_t *best_map, int16_t *kbuf, uint8_t *wmaps, int *wstat, int *wcount, int64_t *io64,
                       int *io32, uint8_t *txmap_out, const uint8_t *const *planes, const int *strides,
                       const int *offsets, int32_t *log, int logcap) {
  memset(&cpi, 0, sizeof(cpi));
  memset(&seq, 0, sizeof(seq));
  memset(&x, 0, sizeof(x));
  memset(&ctx, 0, sizeof(ctx));
  seq.bit_depth = AOM_BITS_8;
  seq.use_highbitdepth = 0;
  seq.enable_filter_intra = (uint8_t)cfg[20];
  cpi.common.seq_params = &seq;
  cpi.common.features.allow_screen_content_tools = 1;
  cpi.common.features.allow_intrabc = cfg[16];
  cpi.sf.intra_sf.prune_palette_search_level = cfg[6];
  cpi.sf.intra_sf.prune_luma_palette_size_search_level = cfg[7];
  cpi.sf.intra_sf.early_term_chroma_palette_size_search = cfg[8];
  cpi.sf.rt_sf.discount_color_cost = cfg[9];
  cpi.sf.rt_sf.use_nonrd_pick_mode = cfg[10];
  cpi.sf.rt_sf.increase_color_thresh_palette = cfg[11];
  cpi.rc.high_source_sad = cfg[12];
  cpi.sf.winner_mode_sf.multi_winner_mode_type = cfg[15];

  const int bsize = cfg[0];
  mi_in(&cur, state);
  mi_in(&best, state + 96);
  mi_ptr = &cur;
  MACROBLOCKD *xd = &x.e_mbd;
  xd->mi = &mi_ptr;
  if (state[32] >= 0) mi_in(&above, state + 32), xd->above_mbmi = &above, xd->up_available = 1;
  if (state[64] >= 0) mi_in(&left, state + 64), xd->left_mbmi = &left, xd->left_available = 1;
  xd->mb_to_right_edge = cfg[1];
  xd->mb_to_bottom_edge = cfg[2];
  xd->mb_to_top_edge = cfg[3];
  xd->mb_to_left_edge = 0;
  for (int p = 1; p < 3; p++) xd->plane[p].subsampling_x = cfg[4], xd->plane[p].subsampling_y = cfg[5];
  xd->plane[0].color_index_map = cmap0;
  xd->plane[1].color_index_map = cmap1;
  xd->is_chroma_ref = 1;
  xd->lossless[cur.segment_id] = cfg[19];
  xd->tx_type_map = txmap_xd;
  xd->tx_type_map_stride = mi_size_wide[bsize];
  memset(txmap_xd, 0, sizeof(txmap_xd));
  for (int p = 0; p < 3; p++) x.plane[p].src.buf = (uint8_t *)planes[p] + offsets[p], x.plane[p].src.stride = strides[p];
  x.palette_buffer = &pbuf;
  memcpy(pbuf.kmeans_data_buf, kbuf, sizeof(pbuf.kmeans_data_buf));
  x.rdmult = cfg[17];
  x.color_palette_thresh = cfg[18];
  x.source_variance = (unsigned)cfg[13];
  x.color_sensitivity[0] = (uint8_t)(cfg[14] & 1);
  x.color_sensitivity[1] = (uint8_t)((cfg[14] >> 1) & 1);
  x.min_dist_inter_uv = (int64_t)cfg[21] * 1000;
  x.txfm_search_params.tx_mode_search_type = TX_MODE_LARGEST;
  x.winner_mode_stats = wstats;
  x.winner_mode_count = *wcount;
  for (int i = 0; i < 3; i++) {
    memset(&wstats[i], 0, sizeof(wstats[i]));
    wstats[i].rd = (int64_t)(((uint64_t)(uint32_t)wstat[i * 34 + 1] << 32) | (uint32_t)wstat[i * 34]);
    mi_in(&wstats[i].mbmi, wstat + i * 34 + 2);
    memcpy(wstats[i].color_index_map, wmaps + i * 4096, 4096);
  }
  const int *c = costs;
  memcpy(x.mode_costs.palette_y_size_cost, c, 49 * 4), c += 49;
  memcpy(x.mode_costs.palette_uv_size_cost, c, 49 * 4), c += 49;
  memcpy(x.mode_costs.palette_y_color_cost, c, 280 * 4), c += 280;
  memcpy(x.mode_costs.palette_uv_color_cost, c, 280 * 4), c += 280;
  memcpy(x.mode_costs.palette_y_mode_cost, c, 42 * 4), c += 42;
  memcpy(x.mode_costs.palette_uv_mode_cost, c, 4 * 4), c += 4;
  memcpy(x.mode_costs.intrabc_cost, c, 2 * 4), c += 2;
  memcpy(x.mode_costs.filter_intra_cost, c, 44 * 4), c += 44;
  memcpy(x.mode_costs.filter_intra_mode_cost, c, 5 * 4), c += 5;
  ctx.num_4x4_blk = mi_size_wide[bsize] * mi_size_high[bsize];

  mlog = log;
  mlogn = 0;
  mlogcap = logcap;
  int rate = io32[0], rate_tokenonly = io32[1], beat = io32[3];
  uint8_t skippable = (uint8_t)io32[2];
  int64_t best_rd = io64[0], distortion = io64[1];
  if (luma)
    av1_rd_pick_palette_intra_sby(&cpi, &x, (BLOCK_SIZE)bsize, cfg[22], &best, best_map, &best_rd, &rate,
                                  &rate_tokenonly, &distortion, &skippable, &beat, &ctx, txmap_out);
  else
    av1_rd_pick_palette_intra_sbuv(&cpi, &x, cfg[22], best_map, &best, &best_rd, &rate, &rate_tokenonly, &distortion,
                                   &skippable);
  io32[0] = rate, io32[1] = rate_tokenonly, io32[2] = skippable, io32[3] = beat;
  io64[0] = best_rd, io64[1] = distortion;
  mi_out(&cur, state);
  mi_out(&best, state + 96);
  memcpy(kbuf, pbuf.kmeans_data_buf, sizeof(pbuf.kmeans_data_buf));
  *wcount = x.winner_mode_count;
  for (int i = 0; i < 3; i++) {
    wstat[i * 34] = (int)(uint32_t)wstats[i].rd;
    wstat[i * 34 + 1] = (int)(uint32_t)((uint64_t)wstats[i].rd >> 32);
    mi_out(&wstats[i].mbmi, wstat + i * 34 + 2);
    memcpy(wmaps + i * 4096, wstats[i].color_index_map, 4096);
  }
  return mlogn / 8;
}
