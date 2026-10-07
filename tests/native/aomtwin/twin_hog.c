// twin_hog.c: libaom 3.14.1's HOG intra-mode prune (static inline in av1/encoder/intra_mode_search_utils.h, inlined
// into intra_mode_search.c: compiled with that file's flags, -O3 with no -m SIMD flags).
#include "av1/encoder/intra_mode_search_utils.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

static MACROBLOCK *hx_;
static YV12_BUFFER_CONFIG hbuf_;

static MACROBLOCK *hog_x(int plane, int ss_x, int ss_y, int hbd, int mb_to_right_edge, int mb_to_bottom_edge) {
  if (!hx_) {
    hx_ = calloc(1, sizeof(*hx_));
    hx_->pixel_gradient_info = calloc(PLANE_TYPES * MAX_SB_SQUARE, sizeof(*hx_->pixel_gradient_info));
  }
  MACROBLOCKD *xd = &hx_->e_mbd;
  xd->plane[plane].subsampling_x = ss_x;
  xd->plane[plane].subsampling_y = ss_y;
  xd->mb_to_right_edge = mb_to_right_edge;
  xd->mb_to_bottom_edge = mb_to_bottom_edge;
  hbuf_.flags = hbd ? YV12_FLAG_HIGHBITDEPTH : 0;
  xd->cur_buf = &hbuf_;
  hx_->is_sb_gradient_cached[0] = hx_->is_sb_gradient_cached[1] = false;
  return hx_;
}

static uint8_t *pix(const void *p, int hbd) { return hbd ? CONVERT_TO_BYTEPTR((uint16_t *)p) : (uint8_t *)p; }

// collect_hog_data through the direct (uncached) path; src at the block
EXPORT void twin_collect_hog(const void *src, int stride, int bsize, int mb_to_right_edge, int mb_to_bottom_edge,
                             int ss_x, int ss_y, int plane, int hbd, float *hog) {
  MACROBLOCK *x = hog_x(plane, ss_x, ss_y, hbd, mb_to_right_edge, mb_to_bottom_edge);
  x->plane[plane].src.buf = pix(src, hbd);
  x->plane[plane].src.stride = stride;
  memset(hog, 0, 32 * sizeof(float));
  collect_hog_data(x, (BLOCK_SIZE)bsize, BLOCK_128X128, plane, hog);
}

// collect_hog_data through the superblock gradient cache: sb_src at the superblock's (plane) origin, the block at
// (mi_row, mi_col) luma mi units inside it
EXPORT void twin_collect_hog_cached(const void *sb_src, int stride, int sb_size, int mi_row, int mi_col, int bsize,
                                    int mb_to_right_edge, int mb_to_bottom_edge, int ss_x, int ss_y, int plane, int hbd,
                                    float *hog) {
  MACROBLOCK *x = hog_x(plane, ss_x, ss_y, hbd, mb_to_right_edge, mb_to_bottom_edge);
  x->plane[plane].src.buf = pix(sb_src, hbd);
  x->plane[plane].src.stride = stride;
  compute_gradient_info_sb(x, (BLOCK_SIZE)sb_size, (PLANE_TYPE)plane);
  x->is_sb_gradient_cached[plane] = true;
  x->e_mbd.mi_row = mi_row;
  x->e_mbd.mi_col = mi_col;
  memset(hog, 0, 32 * sizeof(float));
  collect_hog_data(x, (BLOCK_SIZE)bsize, (BLOCK_SIZE)sb_size, plane, hog);
}

// prune_intra_mode_with_hog (uncached); mask[UV_INTRA_MODES] in/out
EXPORT void twin_prune_hog(const void *src, int stride, int bsize, int mb_to_right_edge, int mb_to_bottom_edge,
                           int ss_x, int ss_y, int is_chroma, int hbd, float th, uint8_t *mask) {
  int plane = is_chroma ? AOM_PLANE_U : AOM_PLANE_Y;
  MACROBLOCK *x = hog_x(plane, ss_x, ss_y, hbd, mb_to_right_edge, mb_to_bottom_edge);
  x->plane[plane].src.buf = pix(src, hbd);
  x->plane[plane].src.stride = stride;
  prune_intra_mode_with_hog(x, (BLOCK_SIZE)bsize, BLOCK_128X128, th, mask, is_chroma);
}
