// aomtwin_ibc.dll: exports around libaom 3.14.1's intrabc encoder pieces (hash.c / hash_motion.c, the nmv cost table,
// mv bit costs, av1_is_dv_valid, the dispatched SAD / variance kernels, av1_full_pixel_search, the mode costs the
// intrabc path adds) for the C# twins in AomIntrabcTwinTests.
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "av1/encoder/encoder.h"
#include "av1/encoder/hash.h"
#include "av1/encoder/hash_motion.h"
#include "av1/encoder/encodemv.h"
#include "av1/encoder/mcomp.h"
#include "av1/encoder/rd.h"
#include "av1/common/mvref_common.h"
#include "av1/common/entropymode.h"
#include "av1/common/entropymv.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) { av1_rtcd(); aom_dsp_rtcd(); }

static CRC32C g_crc;
static int g_crc_init;
EXPORT uint32_t twin_crc32c(const uint8_t *buf, int len) {
  if (!g_crc_init) { av1_crc32c_calculator_init(&g_crc); g_crc_init = 1; }
  return av1_get_crc32c_value(&g_crc, buf, (size_t)len);
}

static IntraBCHashInfo g_hi;
static uint32_t g_hbuf[2][AOM_BUFFER_SIZE_FOR_BLOCK_HASH];
EXPORT void twin_block_hash(const uint8_t *src, int stride, int size, uint32_t *h1, uint32_t *h2) {
  av1_hash_table_init(&g_hi);
  g_hi.hash_value_buffer[0] = g_hbuf[0];
  g_hi.hash_value_buffer[1] = g_hbuf[1];
  av1_get_block_hash_value(&g_hi, src, stride, size, h1, h2, 0);
}

// the frame table as encode_frame_internal builds it from a w x h luma plane; out: per bucket (in index order) the
// FNV-1a of its entries (x, y, hash2), folded into one value; also the total entry count
EXPORT uint64_t twin_hash_table(const uint8_t *y, int stride, int w, int h, int mib_size_log2, int max8, int *total) {
  YV12_BUFFER_CONFIG pic; memset(&pic, 0, sizeof(pic));
  pic.y_buffer = (uint8_t *)y; pic.y_stride = stride; pic.y_crop_width = w; pic.y_crop_height = h;
  IntraBCHashInfo hi; memset(&hi, 0, sizeof(hi));
  av1_hash_table_init(&hi);
  av1_hash_table_create(&hi.intrabc_hash_table);
  uint32_t *bh[2] = { malloc(sizeof(uint32_t) * w * h), malloc(sizeof(uint32_t) * w * h) };
  av1_generate_block_2x2_hash_value(&pic, bh[0]);
  int max_sb_size = 1 << (mib_size_log2 + 2);
  if (max8) max_sb_size = AOMMIN(8, max_sb_size);
  int src_idx = 0;
  for (int size = 4; size <= max_sb_size; size *= 2, src_idx = !src_idx) {
    const int dst_idx = !src_idx;
    av1_generate_block_hash_value(&hi, &pic, size, bh[src_idx], bh[dst_idx]);
    if (size >= 4) av1_add_to_hash_map_by_row_with_precal_data(&hi.intrabc_hash_table, bh[dst_idx], w, h, size);
  }
  uint64_t acc = 1469598103934665603ULL;
  int tot = 0;
  for (uint32_t k = 0; k < (6u << 16); k++) {
    int n = av1_hash_table_count(&hi.intrabc_hash_table, k);
    if (!n) continue;
    Iterator it = av1_hash_get_first_iterator(&hi.intrabc_hash_table, k);
    acc = (acc ^ k) * 1099511628211ULL;
    for (int i = 0; i < n; i++, aom_iterator_increment(&it)) {
      block_hash b = *(block_hash *)aom_iterator_get(&it);
      acc = (acc ^ (uint32_t)b.x) * 1099511628211ULL;
      acc = (acc ^ (uint32_t)b.y) * 1099511628211ULL;
      acc = (acc ^ b.hash_value2) * 1099511628211ULL;
    }
    tot += n;
  }
  *total = tot;
  av1_hash_table_destroy(&hi.intrabc_hash_table);
  free(bh[0]); free(bh[1]);
  return acc;
}

// av1_fill_dv_costs on the default ndvc: joint[4], comp[2][MV_VALS]
static nmv_context g_ndvc;
EXPORT void twin_dv_costs(int *joint, int *comp0, int *comp1) {
  FRAME_CONTEXT *fc = calloc(1, sizeof(FRAME_CONTEXT));
  AV1_COMMON cm; memset(&cm, 0, sizeof(cm)); cm.fc = fc;
  av1_init_mv_probs(&cm);
  g_ndvc = fc->ndvc;
  IntraBCMVCosts *c = calloc(1, sizeof(IntraBCMVCosts));
  av1_fill_dv_costs(&fc->ndvc, c);
  memcpy(joint, c->joint_mv, sizeof(int) * MV_JOINTS);
  memcpy(comp0, c->dv_costs_alloc[0], sizeof(int) * MV_VALS);
  memcpy(comp1, c->dv_costs_alloc[1], sizeof(int) * MV_VALS);
  free(c); free(fc);
}

EXPORT int twin_mv_bit_cost(int mr, int mc, int rr, int rc, const int *joint, int *comp0c, int *comp1c, int weight) {
  MV mv = { (int16_t)mr, (int16_t)mc }, ref = { (int16_t)rr, (int16_t)rc };
  int *mvcost[2] = { comp0c, comp1c };
  return av1_mv_bit_cost(&mv, &ref, joint, mvcost, weight);
}

// av1_is_dv_valid on a single-tile frame of mi_rows x mi_cols
EXPORT int twin_is_dv_valid(int dvr, int dvc, int mi_rows, int mi_cols, int mi_row, int mi_col, int bsize, int mib_size_log2,
                            int is_chroma_ref, int ssx, int ssy, int num_planes) {
  static AV1_COMMON cm; static SequenceHeader seq;
  memset(&cm, 0, sizeof(cm)); memset(&seq, 0, sizeof(seq));
  seq.monochrome = num_planes == 1;
  cm.seq_params = &seq;
  MACROBLOCKD xd; memset(&xd, 0, sizeof(xd));
  xd.tile.mi_row_start = 0; xd.tile.mi_row_end = mi_rows; xd.tile.mi_col_start = 0; xd.tile.mi_col_end = mi_cols;
  xd.is_chroma_ref = is_chroma_ref;
  xd.plane[1].subsampling_x = ssx; xd.plane[1].subsampling_y = ssy;
  MV dv = { (int16_t)dvr, (int16_t)dvc };
  return av1_is_dv_valid(dv, &cm, &xd, mi_row, mi_col, (BLOCK_SIZE)bsize, mib_size_log2);
}

// the dispatched distortion kernels per block size
static aom_variance_fn_ptr_t g_fn[BLOCK_SIZES_ALL];
static int g_fn_init;
#define FN(BT, W, H)                                                                        \
  g_fn[BT].sdf = aom_sad##W##x##H; g_fn[BT].sdx4df = aom_sad##W##x##H##x4d;                 \
  g_fn[BT].sdx3df = aom_sad##W##x##H##x3d; g_fn[BT].vf = aom_variance##W##x##H;
#define FNS(BT, W, H) g_fn[BT].sdsf = aom_sad_skip_##W##x##H; g_fn[BT].sdsx4df = aom_sad_skip_##W##x##H##x4d;
static void fn_init(void) {
  if (g_fn_init) return;
  g_fn_init = 1;
  FN(BLOCK_4X4, 4, 4) FN(BLOCK_4X8, 4, 8) FN(BLOCK_8X4, 8, 4) FN(BLOCK_8X8, 8, 8) FN(BLOCK_8X16, 8, 16)
  FN(BLOCK_16X8, 16, 8) FN(BLOCK_16X16, 16, 16) FN(BLOCK_16X32, 16, 32) FN(BLOCK_32X16, 32, 16) FN(BLOCK_32X32, 32, 32)
  FN(BLOCK_32X64, 32, 64) FN(BLOCK_64X32, 64, 32) FN(BLOCK_64X64, 64, 64) FN(BLOCK_64X128, 64, 128)
  FN(BLOCK_128X64, 128, 64) FN(BLOCK_128X128, 128, 128) FN(BLOCK_4X16, 4, 16) FN(BLOCK_16X4, 16, 4)
  FN(BLOCK_8X32, 8, 32) FN(BLOCK_32X8, 32, 8) FN(BLOCK_16X64, 16, 64) FN(BLOCK_64X16, 64, 16)
  FNS(BLOCK_16X16, 16, 16) FNS(BLOCK_16X32, 16, 32) FNS(BLOCK_32X16, 32, 16) FNS(BLOCK_32X32, 32, 32)
  FNS(BLOCK_32X64, 32, 64) FNS(BLOCK_64X32, 64, 32) FNS(BLOCK_64X64, 64, 64) FNS(BLOCK_64X128, 64, 128)
  FNS(BLOCK_128X64, 128, 64) FNS(BLOCK_128X128, 128, 128) FNS(BLOCK_8X16, 8, 16) FNS(BLOCK_4X16, 4, 16)
  FNS(BLOCK_8X32, 8, 32) FNS(BLOCK_16X64, 16, 64) FNS(BLOCK_64X16, 64, 16)

}

EXPORT unsigned twin_sad(int bsize, const uint8_t *a, int as, const uint8_t *b, int bs) { fn_init(); return g_fn[bsize].sdf(a, as, b, bs); }
EXPORT unsigned twin_sad_skip(int bsize, const uint8_t *a, int as, const uint8_t *b, int bs) { fn_init(); return g_fn[bsize].sdsf(a, as, b, bs); }
EXPORT unsigned twin_variance(int bsize, const uint8_t *a, int as, const uint8_t *b, int bs, unsigned *sse) {
  fn_init(); return g_fn[bsize].vf(a, as, b, bs, sse);
}

// av1_full_pixel_search with the intrabc setup (dv costs as the mv costs, no cost list / second best)
static search_site_config g_ss[NUM_DISTINCT_SEARCH_METHODS];
EXPORT int twin_full_pixel_search(const uint8_t *src, int src_stride, const uint8_t *ref, int ref_stride, int bsize, int method,
                                  int step_param, int start_row, int start_col, int refmv_row, int refmv_col, const int *limits,
                                  int force_mesh_thresh, const int *mesh, int prune_mesh, int is_intra, int errorperbit,
                                  int sadperbit, const int *joint, const int *comp0c, const int *comp1c, int skip_sad,
                                  int *best_row, int *best_col) {
  fn_init();
  for (SEARCH_METHODS i = DIAMOND; i < NUM_DISTINCT_SEARCH_METHODS; i++) {
    const int level = ((i == NSTEP_8PT) || (i == CLAMPED_DIAMOND)) ? 1 : 0;
    av1_init_motion_compensation[i](&g_ss[i], ref_stride, level);
  }
  static struct MESH_PATTERN mp[2][MAX_MESH_STEP];
  for (int k = 0; k < 2; k++)
    for (int i = 0; i < MAX_MESH_STEP; i++) { mp[k][i].range = mesh[(k * 4 + i) * 2]; mp[k][i].interval = mesh[(k * 4 + i) * 2 + 1]; }
  static struct buf_2d sb, rb;
  sb.buf = (uint8_t *)src; sb.stride = src_stride;
  rb.buf = (uint8_t *)ref; rb.stride = ref_stride;
  FULLPEL_MOTION_SEARCH_PARAMS p; memset(&p, 0, sizeof(p));
  p.bsize = (BLOCK_SIZE)bsize;
  p.vfp = &g_fn[bsize];
  p.ms_buffers.src = &sb; p.ms_buffers.ref = &rb;
  av1_set_mv_search_method(&p, g_ss, (SEARCH_METHODS)method);
  p.mesh_patterns[0] = mp[0]; p.mesh_patterns[1] = mp[1];
  p.force_mesh_thresh = force_mesh_thresh;
  p.prune_mesh_search = prune_mesh;
  p.mesh_search_mv_diff_threshold = 4;
  p.is_intra_mode = is_intra;
  p.mv_limits.col_min = limits[0]; p.mv_limits.col_max = limits[1]; p.mv_limits.row_min = limits[2]; p.mv_limits.row_max = limits[3];
  static MV refmv; refmv.row = (int16_t)refmv_row; refmv.col = (int16_t)refmv_col;
  p.mv_cost_params.ref_mv = &refmv;
  p.mv_cost_params.full_ref_mv = get_fullmv_from_mv(&refmv);
  p.mv_cost_params.mv_cost_type = MV_COST_ENTROPY;
  p.mv_cost_params.error_per_bit = errorperbit;
  p.mv_cost_params.sad_per_bit = sadperbit;
  p.mv_cost_params.mvjcost = joint;
  p.mv_cost_params.mvcost[0] = comp0c;
  p.mv_cost_params.mvcost[1] = comp1c;
  p.sdf = skip_sad ? p.vfp->sdsf : p.vfp->sdf;
  p.sdx4df = skip_sad ? p.vfp->sdsx4df : p.vfp->sdx4df;
  p.sdx3df = skip_sad ? p.vfp->sdsx4df : p.vfp->sdx3df;
  FULLPEL_MV start = { (int16_t)start_row, (int16_t)start_col }, best;
  FULLPEL_MV_STATS st;
  int var = av1_full_pixel_search(start, &p, step_param, NULL, &best, &st, NULL);
  *best_row = best.row; *best_col = best.col;
  return var;
}

// the mode costs the intrabc path adds: txfm_partition_cost[21][2] then inter_tx_type_costs[4][4][16]
EXPORT void twin_inter_mode_costs(int *out) {
  FRAME_CONTEXT *fc = calloc(1, sizeof(FRAME_CONTEXT));
  AV1_COMMON *cm = calloc(1, sizeof(AV1_COMMON));
  SequenceHeader *seq = calloc(1, sizeof(SequenceHeader));
  cm->seq_params = seq; cm->fc = fc;
  cm->current_frame.frame_type = KEY_FRAME;
  av1_init_mode_probs(fc);
  ModeCosts *mc = calloc(1, sizeof(ModeCosts));
  av1_fill_mode_rates(cm, mc, fc);
  memcpy(out, mc->txfm_partition_cost, sizeof(mc->txfm_partition_cost));
  memcpy(out + TXFM_PARTITION_CONTEXTS * 2, mc->inter_tx_type_costs, sizeof(mc->inter_tx_type_costs));
  free(mc); free(seq); free(cm); free(fc);
}
