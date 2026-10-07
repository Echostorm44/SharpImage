// twin_part.c: libaom 3.14.1's partition_strategy.c compiled into the twin (its own flags) so the static
// ml_prune_ab_partition can be called, and a minimal AV1_COMP / MACROBLOCK for the exported ML partition prunes.
#include "av1/encoder/partition_strategy.c"

#include "twin_fpcw.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

static AV1_COMP *cpi_;
static MACROBLOCK *x_;
static YV12_BUFFER_CONFIG src_;
static SequenceHeader seq_;

typedef unsigned int (*vf_t)(const uint8_t *, int, const uint8_t *, int, unsigned int *);
static vf_t vfs[BLOCK_SIZES_ALL][4];

static void set_vf(BLOCK_SIZE b, vf_t v8, vf_t h8, vf_t h10, vf_t h12) {
  vfs[b][0] = v8; vfs[b][1] = h8; vfs[b][2] = h10; vfs[b][3] = h12;
}

static void setup(int bd, int hbd) {
  if (!cpi_) {
    cpi_ = calloc(1, sizeof(*cpi_));
    cpi_->ppi = calloc(1, sizeof(*cpi_->ppi));
    x_ = calloc(1, sizeof(*x_));
    seq_.monochrome = 1;
    cpi_->common.seq_params = &seq_;
#define S(B, W, H) set_vf(B, aom_variance##W##x##H, aom_highbd_8_variance##W##x##H, aom_highbd_10_variance##W##x##H, \
                          aom_highbd_12_variance##W##x##H)
    S(BLOCK_4X4, 4, 4); S(BLOCK_4X8, 4, 8); S(BLOCK_8X4, 8, 4); S(BLOCK_8X8, 8, 8); S(BLOCK_8X16, 8, 16);
    S(BLOCK_16X8, 16, 8); S(BLOCK_16X16, 16, 16); S(BLOCK_16X32, 16, 32); S(BLOCK_32X16, 32, 16);
    S(BLOCK_32X32, 32, 32); S(BLOCK_32X64, 32, 64); S(BLOCK_64X32, 64, 32); S(BLOCK_64X64, 64, 64);
    S(BLOCK_64X128, 64, 128); S(BLOCK_128X64, 128, 64); S(BLOCK_128X128, 128, 128); S(BLOCK_4X16, 4, 16);
    S(BLOCK_16X4, 16, 4); S(BLOCK_8X32, 8, 32); S(BLOCK_32X8, 32, 8); S(BLOCK_16X64, 16, 64); S(BLOCK_64X16, 64, 16);
#undef S
  }
  int k = !hbd ? 0 : bd == 8 ? 1 : bd == 10 ? 2 : 3;
  for (int b = 0; b < BLOCK_SIZES_ALL; b++) cpi_->ppi->fn_ptr[b].vf = vfs[b][k];
  x_->e_mbd.bd = bd;
  src_.flags = hbd ? YV12_FLAG_HIGHBITDEPTH : 0;
  x_->e_mbd.cur_buf = &src_;
}

static void set_src(const void *pix, int stride, int w, int h, int hbd) {
  src_.y_buffer = hbd ? CONVERT_TO_BYTEPTR((uint16_t *)pix) : (uint8_t *)pix;
  src_.y_stride = stride;
  src_.y_crop_width = src_.y_width = w;
  src_.y_crop_height = src_.y_height = h;
  cpi_->source = &src_;
}

EXPORT unsigned int twin_perpixel_variance(const void *pix, int stride, int bsize, int bd, int hbd) {
  setup(bd, hbd);
  struct buf_2d b;
  b.buf = hbd ? CONVERT_TO_BYTEPTR((uint16_t *)pix) : (uint8_t *)pix;
  b.stride = stride;
  return av1_get_perpixel_variance_facade(cpi_, &x_->e_mbd, &b, (BLOCK_SIZE)bsize, AOM_PLANE_Y);
}

// rd: [horz0, horz1, vert0, vert1, split0..3]; part4_allowed: [HORZ4, VERT4] in/out
EXPORT void twin_prune_4_partition(const void *pix, int stride, int bsize, int bd, int hbd, int part_ctx, int64_t best_rd,
                                   const int64_t *rd, unsigned int pb_source_variance, int frame_w, int frame_h,
                                   int level_index, int *part4_allowed) {
  setup(bd, hbd);
  cpi_->common.width = frame_w; cpi_->common.height = frame_h;
  cpi_->sf.part_sf.ml_4_partition_search_level_index = level_index;
  set_src(pix, stride, block_size_wide[bsize], block_size_high[bsize], hbd);
  PartitionSearchState ps;
  memset(&ps, 0, sizeof(ps));
  ps.part_blk_params.bsize = (BLOCK_SIZE)bsize;
  ps.rect_part_rd[HORZ][0] = rd[0]; ps.rect_part_rd[HORZ][1] = rd[1];
  ps.rect_part_rd[VERT][0] = rd[2]; ps.rect_part_rd[VERT][1] = rd[3];
  for (int i = 0; i < 4; i++) ps.split_rd[i] = rd[4 + i];
  WITH_FPCW(av1_ml_prune_4_partition(cpi_, x_, part_ctx, best_rd, &ps, part4_allowed, pb_source_variance));
}

// rd: [horz0, horz1, vert0, vert1, split0..3]; ab: [HORZ_A, HORZ_B, VERT_A, VERT_B] in/out
EXPORT void twin_prune_ab_partition(int bsize, int part_ctx, int var_ctx, int64_t best_rd, const int64_t *rd, int *ab) {
  setup(8, 0);
  PartitionSearchState ps;
  memset(&ps, 0, sizeof(ps));
  ps.part_blk_params.bsize = (BLOCK_SIZE)bsize;
  ps.rect_part_rd[HORZ][0] = rd[0]; ps.rect_part_rd[HORZ][1] = rd[1];
  ps.rect_part_rd[VERT][0] = rd[2]; ps.rect_part_rd[VERT][1] = rd[3];
  for (int i = 0; i < 4; i++) ps.split_rd[i] = rd[4 + i];
  ml_prune_ab_partition(cpi_, part_ctx, var_ctx, best_rd, &ps, ab);
}

// split_rd[4]; prune: [HORZ, VERT] in/out
EXPORT void twin_prune_rect_partition(const void *pix, int stride, int bsize, int bd, int hbd, int64_t best_rd,
                                      int64_t none_rd, const int64_t *split_rd, int *prune) {
  setup(bd, hbd);
  x_->plane[0].src.buf = hbd ? CONVERT_TO_BYTEPTR((uint16_t *)pix) : (uint8_t *)pix;
  x_->plane[0].src.stride = stride;
  PartitionSearchState ps;
  memset(&ps, 0, sizeof(ps));
  ps.part_blk_params.bsize = (BLOCK_SIZE)bsize;
  ps.prune_rect_part[HORZ] = prune[0]; ps.prune_rect_part[VERT] = prune[1];
  WITH_FPCW(av1_ml_prune_rect_partition(cpi_, x_, best_rd, none_rd, split_rd, &ps));
  prune[0] = ps.prune_rect_part[HORZ]; prune[1] = ps.prune_rect_part[VERT];
}

// children: 4 x [block_size, partitioning]; sms: [none_feat1, split0..3 none_feat1, rect_feat 0..7]
// returns terminate_partition_search
EXPORT int twin_early_term_after_split(int bsize, int frame_w, int frame_h, int level, int qindex, int bd,
                                       int64_t best_rd, int64_t part_none_rd, int64_t part_split_rd,
                                       const int64_t *split_block_rd, const int *children, const unsigned int *sms) {
  setup(bd, bd > 8);
  cpi_->common.width = frame_w; cpi_->common.height = frame_h;
  cpi_->sf.part_sf.ml_early_term_after_part_split_level = level;
  x_->qindex = qindex;
  SIMPLE_MOTION_DATA_TREE root, kids[4];
  memset(&root, 0, sizeof(root)); memset(kids, 0, sizeof(kids));
  root.block_size = (BLOCK_SIZE)bsize;
  root.partitioning = PARTITION_SPLIT;
  root.sms_none_valid = root.sms_rect_valid = 1;
  root.sms_none_feat[1] = sms[0];
  for (int i = 0; i < 4; i++) {
    root.split[i] = &kids[i];
    kids[i].block_size = (BLOCK_SIZE)children[2 * i];
    kids[i].partitioning = (PARTITION_TYPE)children[2 * i + 1];
    kids[i].sms_none_valid = 1;
    kids[i].sms_none_feat[1] = sms[1 + i];
  }
  for (int i = 0; i < 8; i++) root.sms_rect_feat[i] = sms[5 + i];
  PartitionSearchState ps;
  memset(&ps, 0, sizeof(ps));
  ps.part_blk_params.bsize = (BLOCK_SIZE)bsize;
  int64_t sbr[4];
  memcpy(sbr, split_block_rd, sizeof(sbr));
  WITH_FPCW(av1_ml_early_term_after_split(cpi_, x_, &root, best_rd, part_none_rd, part_split_rd, sbr, &ps));
  return ps.terminate_partition_search;
}

#include "twin_part_cnn.inc"
