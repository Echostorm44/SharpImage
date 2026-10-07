// A copy of libaom 3.14.1's reconintra.c compiled into aomtwin_pred.dll, so its file-static functions (has_top_right,
// has_bottom_left, intra_edge_filter_strength, ...) can be exported; plus an end-to-end MACROBLOCKD harness that drives
// av1_predict_intra_block_facade / cfl_store_* on a synthetic frame.
#include "av1/common/reconintra.c"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT int twin_has_top_right(int sb_size, int bsize, int mi_row, int mi_col, int top_available, int right_available,
                              int partition, int txsz, int row_off, int col_off, int ss_x, int ss_y) {
  return has_top_right((BLOCK_SIZE)sb_size, (BLOCK_SIZE)bsize, mi_row, mi_col, top_available, right_available,
                       (PARTITION_TYPE)partition, (TX_SIZE)txsz, row_off, col_off, ss_x, ss_y);
}
EXPORT int twin_has_bottom_left(int sb_size, int bsize, int mi_row, int mi_col, int bottom_available,
                                int left_available, int partition, int txsz, int row_off, int col_off, int ss_x,
                                int ss_y) {
  return has_bottom_left((BLOCK_SIZE)sb_size, (BLOCK_SIZE)bsize, mi_row, mi_col, bottom_available, left_available,
                         (PARTITION_TYPE)partition, (TX_SIZE)txsz, row_off, col_off, ss_x, ss_y);
}
EXPORT int twin_edge_strength(int bs0, int bs1, int delta, int type) {
  return intra_edge_filter_strength(bs0, bs1, delta, type);
}
EXPORT void twin_edge_corner(uint8_t *p_above, uint8_t *p_left) { filter_intra_edge_corner(p_above, p_left); }
EXPORT int twin_scale_chroma_bsize(int bsize, int ssx, int ssy) { return scale_chroma_bsize(bsize, ssx, ssy); }

// ---- end-to-end ---------------------------------------------------------------------------------------------------
#define MAXMI 64
#define FB_MAX (600 * 600)
DECLARE_ALIGNED(32, static uint8_t, frame[3][FB_MAX]);
static int fstride[3];
static MB_MODE_INFO cells[MAXMI * MAXMI];
static MB_MODE_INFO *grid[MAXMI * MAXMI];
static MB_MODE_INFO cur;
static MACROBLOCKD xd;
static AV1_COMMON cm;
static SequenceHeader seq;
static YV12_BUFFER_CONFIG curbuf;
static uint8_t cmap[2][MAX_SB_SQUARE];

// cells: 4 ints per mi (mode, uv_mode, ref_frame0, use_intrabc), mi_rows x mi_cols (stride mi_cols)
// curinfo: bsize, partition, mode, uv_mode, angle_delta0, angle_delta1, use_filter_intra, filter_intra_mode,
//          palette_size0, palette_size1, cfl_alpha_idx, cfl_alpha_signs
// planes: 3 buffers (stride[p] x rows[p]); dst_off[p]: offset of the block's first sample in plane p
// derived (out): up, left, chroma_up, chroma_left, is_chroma_ref, mb_to_top, bottom, left, right edges,
//                above idx, left idx, chroma above idx, chroma left idx (cells index r * mi_cols + c, -1 NULL,
//                -2 the current block)
EXPORT void twin_e2e_setup(int mi_rows, int mi_cols, const int *cellinfo, int tile_r0, int tile_r1, int tile_c0,
                           int tile_c1, int ssx, int ssy, int sb_size, int enable_edge_filter, int mi_row, int mi_col,
                           const int *curinfo, const uint16_t *palette, const uint8_t *const *planes,
                           const int *strides, const int *rows, const int *dst_off, const uint8_t *cmap0,
                           const uint8_t *cmap1, int *derived) {
  memset(&xd, 0, sizeof(xd));
  memset(&cur, 0, sizeof(cur));
  memset(&cm, 0, sizeof(cm));
  memset(&seq, 0, sizeof(seq));
  memset(&curbuf, 0, sizeof(curbuf));
  seq.sb_size = (BLOCK_SIZE)sb_size;
  seq.enable_intra_edge_filter = (uint8_t)enable_edge_filter;
  seq.subsampling_x = ssx;
  seq.subsampling_y = ssy;
  cm.seq_params = &seq;
  xd.cur_buf = &curbuf;
  xd.bd = 8;

  cur.bsize = (BLOCK_SIZE)curinfo[0];
  cur.partition = (PARTITION_TYPE)curinfo[1];
  cur.mode = (PREDICTION_MODE)curinfo[2];
  cur.uv_mode = (UV_PREDICTION_MODE)curinfo[3];
  cur.angle_delta[0] = (int8_t)curinfo[4];
  cur.angle_delta[1] = (int8_t)curinfo[5];
  cur.filter_intra_mode_info.use_filter_intra = (uint8_t)curinfo[6];
  cur.filter_intra_mode_info.filter_intra_mode = (FILTER_INTRA_MODE)curinfo[7];
  cur.palette_mode_info.palette_size[0] = (uint8_t)curinfo[8];
  cur.palette_mode_info.palette_size[1] = (uint8_t)curinfo[9];
  cur.cfl_alpha_idx = (uint8_t)curinfo[10];
  cur.cfl_alpha_signs = (int8_t)curinfo[11];
  cur.ref_frame[0] = INTRA_FRAME;
  cur.ref_frame[1] = NONE_FRAME;
  memcpy(cur.palette_mode_info.palette_colors, palette, 3 * PALETTE_MAX_SIZE * sizeof(uint16_t));

  const int bw = mi_size_wide[cur.bsize], bh = mi_size_high[cur.bsize];
  // libaom's mi grid: stride and rows aligned to 128x128 (calc_mi_size), entries outside the frame NULL
  const int mi_stride = (mi_cols + 31) & ~31, grid_rows = (mi_rows + 31) & ~31;
  memset(grid, 0, sizeof(grid[0]) * mi_stride * grid_rows);
  for (int r = 0; r < mi_rows; r++)
    for (int c = 0; c < mi_cols; c++) {
      const int i = r * mi_cols + c;
      MB_MODE_INFO *m = &cells[i];
      memset(m, 0, sizeof(*m));
      m->mode = (PREDICTION_MODE)cellinfo[i * 4 + 0];
      m->uv_mode = (UV_PREDICTION_MODE)cellinfo[i * 4 + 1];
      m->ref_frame[0] = (MV_REFERENCE_FRAME)cellinfo[i * 4 + 2];
      m->use_intrabc = (uint8_t)cellinfo[i * 4 + 3];
      grid[r * mi_stride + c] = m;
      if (r >= mi_row && r < mi_row + bh && c >= mi_col && c < mi_col + bw) grid[r * mi_stride + c] = &cur;
    }
  xd.mi_stride = mi_stride;
  xd.mi = &grid[mi_row * mi_stride + mi_col];
  xd.tile.mi_row_start = tile_r0;
  xd.tile.mi_row_end = tile_r1;
  xd.tile.mi_col_start = tile_c0;
  xd.tile.mi_col_end = tile_c1;
  for (int p = 0; p < 3; p++) {
    xd.plane[p].subsampling_x = p ? ssx : 0;
    xd.plane[p].subsampling_y = p ? ssy : 0;
    memcpy(frame[p], planes[p], (size_t)strides[p] * rows[p]);
    fstride[p] = strides[p];
    xd.plane[p].dst.buf = frame[p] + dst_off[p];
    xd.plane[p].dst.stride = strides[p];
  }
  set_mi_row_col(&xd, &xd.tile, mi_row, bh, mi_col, bw, mi_rows, mi_cols);
  set_plane_n4(&xd, bw, bh, 3);
  memcpy(cmap[0], cmap0, MAX_SB_SQUARE);
  memcpy(cmap[1], cmap1, MAX_SB_SQUARE);
  xd.plane[0].color_index_map = cmap[0];
  xd.plane[1].color_index_map = cmap[1];
  xd.color_index_map_offset[0] = xd.color_index_map_offset[1] = 0;
  cfl_init(&xd.cfl, &seq);

#define IDX(p) ((p) == NULL ? -1 : (p) == &cur ? -2 : (int)((p) - cells))
  derived[0] = xd.up_available;
  derived[1] = xd.left_available;
  derived[2] = xd.chroma_up_available;
  derived[3] = xd.chroma_left_available;
  derived[4] = xd.is_chroma_ref;
  derived[5] = xd.mb_to_top_edge;
  derived[6] = xd.mb_to_bottom_edge;
  derived[7] = xd.mb_to_left_edge;
  derived[8] = xd.mb_to_right_edge;
  derived[9] = IDX(xd.above_mbmi);
  derived[10] = IDX(xd.left_mbmi);
  derived[11] = xd.is_chroma_ref ? IDX(xd.chroma_above_mbmi) : -1;
  derived[12] = xd.is_chroma_ref ? IDX(xd.chroma_left_mbmi) : -1;
  derived[13] = xd.plane[0].width;
  derived[14] = xd.plane[0].height;
  derived[15] = xd.plane[1].width;
  derived[16] = xd.plane[1].height;
}

EXPORT void twin_e2e_facade(int plane, int blk_col, int blk_row, int tx_size) {
  av1_predict_intra_block_facade(&cm, &xd, plane, blk_col, blk_row, (TX_SIZE)tx_size);
}
EXPORT void twin_e2e_set_cfl(int use_dc_pred_cache, int alpha_idx, int alpha_signs) {
  xd.cfl.use_dc_pred_cache = use_dc_pred_cache;
  cur.cfl_alpha_idx = (uint8_t)alpha_idx;
  cur.cfl_alpha_signs = (int8_t)alpha_signs;
}
EXPORT void twin_e2e_clear_cfl_cache(void) { clear_cfl_dc_pred_cache_flags(&xd.cfl); }
EXPORT void twin_e2e_cfl_store_block(int bsize, int tx_size) { cfl_store_block(&xd, (BLOCK_SIZE)bsize, (TX_SIZE)tx_size); }
EXPORT void twin_e2e_cfl_store_tx(int row, int col, int tx_size, int bsize) {
  cfl_store_tx(&xd, row, col, (TX_SIZE)tx_size, (BLOCK_SIZE)bsize);
}
// direct av1_predict_intra_block (the facade's arguments made explicit)
EXPORT void twin_e2e_predict(int plane, int blk_col, int blk_row, int tx_size, int mode, int angle_delta,
                             int use_palette, int filter_intra_mode) {
  struct macroblockd_plane *pd = &xd.plane[plane];
  uint8_t *dst = &pd->dst.buf[(blk_row * pd->dst.stride + blk_col) << MI_SIZE_LOG2];
  av1_predict_intra_block(&xd, seq.sb_size, seq.enable_intra_edge_filter, pd->width, pd->height, (TX_SIZE)tx_size,
                          (PREDICTION_MODE)mode, angle_delta, use_palette, (FILTER_INTRA_MODE)filter_intra_mode, dst,
                          pd->dst.stride, dst, pd->dst.stride, blk_col, blk_row, plane);
}
EXPORT void twin_e2e_get_plane(int p, uint8_t *out, int n) { memcpy(out, frame[p], n); }
// cfl state: recon_buf_q3, ac_buf_q3 (CFL_BUF_SQUARE each), ints: buf_width, buf_height, are_parameters_computed,
// dc_pred_is_cached[0], [1]
EXPORT void twin_e2e_get_cfl(uint16_t *recon, int16_t *ac, int *ints) {
  memcpy(recon, xd.cfl.recon_buf_q3, sizeof(xd.cfl.recon_buf_q3));
  memcpy(ac, xd.cfl.ac_buf_q3, sizeof(xd.cfl.ac_buf_q3));
  ints[0] = xd.cfl.buf_width;
  ints[1] = xd.cfl.buf_height;
  ints[2] = xd.cfl.are_parameters_computed;
  ints[3] = xd.cfl.dc_pred_is_cached[0];
  ints[4] = xd.cfl.dc_pred_is_cached[1];
}
