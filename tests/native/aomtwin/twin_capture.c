// twin_capture.c: encodes one still through libaom's public API (AOM_USAGE_ALL_INTRA, tune=psnr, end usage AOM_Q, one
// thread) and, through linker --wrap hooks on the post-filter entry points, dumps libaom's own inputs and decisions:
//   'LPF0' before av1_pick_filter_level: frame/config header, source planes, pre-deblock recon (full aligned planes),
//          the mi grid;   'LPF1' after it: the chosen levels;
//   'DBK0' at av1_loop_restoration_save_boundary_lines(after_cdef = 0): the deblocked frame;
//   'RST0' before av1_pick_filter_restoration: rdmult, lr rate costs, tiles, then per (unit size, plane) the
//          instrumented per-unit search (twin_pickrst.c);   'RST1' after it: the chosen rst_info;
//   'FIN0' after av1_loop_restoration_filter_frame: the final frame (crop).
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "aom/aom_encoder.h"
#include "aom/aomcx.h"
#include "av1/encoder/encoder.h"
#include "av1/encoder/picklpf.h"
#include "av1/encoder/pickrst.h"
#include "av1/common/restoration.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

static FILE *g_f;
static int g_after_pick;   // set once av1_pick_filter_level returned: the next whole-frame deblock is the real one

static void tag(const char *t) { fwrite(t, 1, 4, g_f); }
static void i32(int v) { int32_t x = v; fwrite(&x, 4, 1, g_f); }
static void plane_dump(const uint8_t *p, int stride, int w, int h) {
  i32(w);
  i32(h);
  for (int r = 0; r < h; r++) fwrite(p + (size_t)r * stride, 1, w, g_f);
}

void twin_rst_dump_units(const YV12_BUFFER_CONFIG *src, AV1_COMP *cpi, FILE *f);

void __real_av1_pick_filter_level(const YV12_BUFFER_CONFIG *sd, AV1_COMP *cpi, LPF_PICK_METHOD method);
void __wrap_av1_pick_filter_level(const YV12_BUFFER_CONFIG *sd, AV1_COMP *cpi, LPF_PICK_METHOD method);
void __wrap_av1_pick_filter_level(const YV12_BUFFER_CONFIG *sd, AV1_COMP *cpi, LPF_PICK_METHOD method) {
  if (g_f) {
    AV1_COMMON *cm = &cpi->common;
    const SequenceHeader *seq = cm->seq_params;
    const int np = av1_num_planes(cm);
    tag("LPF0");
    int hdr[40] = { 0 };
    hdr[0] = cm->width;
    hdr[1] = cm->height;
    hdr[2] = seq->subsampling_x;
    hdr[3] = seq->subsampling_y;
    hdr[4] = np;
    hdr[5] = seq->bit_depth;
    hdr[6] = cm->quant_params.base_qindex;
    hdr[7] = cpi->oxcf.algo_cfg.sharpness;
    hdr[8] = method;
    hdr[9] = is_inter_tx_size_search_level_one(&cpi->sf.tx_sf);
    hdr[10] = cpi->sf.lpf_sf.use_coarse_filter_level_search;
    hdr[11] = cm->features.tx_mode;
    hdr[12] = cm->current_frame.frame_type;
    hdr[13] = cm->mi_params.mi_rows;
    hdr[14] = cm->mi_params.mi_cols;
    hdr[15] = seq->sb_size;
    hdr[16] = cm->lf.mode_ref_delta_enabled;
    hdr[17] = cm->delta_q_info.delta_lf_present_flag;
    hdr[18] = cm->seg.enabled;
    hdr[19] = cm->features.coded_lossless;
    hdr[20] = cpi->oxcf.algo_cfg.enable_adaptive_sharpness;
    hdr[21] = cpi->oxcf.algo_cfg.loopfilter_control;
    hdr[22] = cpi->sf.lpf_sf.skip_loop_filter_using_filt_error;
    hdr[23] = cpi->sf.lpf_sf.adaptive_luma_loop_filter_skip;
    hdr[24] = is_cdef_used(cm);
    hdr[25] = is_restoration_used(cm);
    hdr[26] = cpi->oxcf.mode;
    hdr[27] = cpi->mt_info.num_mod_workers[MOD_LPF];
    hdr[28] = get_lpf_opt_level(&cpi->sf);
    hdr[29] = cm->lf.filter_level[0];  // the level state try_filter_frame starts from
    hdr[30] = cm->lf.filter_level[1];
    hdr[31] = cm->tiles.large_scale;
    hdr[32] = cm->features.allow_intrabc;
    fwrite(hdr, 4, 40, g_f);
    fwrite(cm->lf.ref_deltas, 1, REF_FRAMES, g_f);
    fwrite(cm->lf.mode_deltas, 1, MAX_MODE_LF_DELTAS, g_f);
    for (int p = 0; p < np; p++) plane_dump(sd->buffers[p], sd->strides[p > 0], sd->crop_widths[p > 0], sd->crop_heights[p > 0]);
    const YV12_BUFFER_CONFIG *fb = &cm->cur_frame->buf;
    for (int p = 0; p < np; p++) plane_dump(fb->buffers[p], fb->strides[p > 0], fb->widths[p > 0], fb->heights[p > 0]);
    const CommonModeInfoParams *mp = &cm->mi_params;
    for (int r = 0; r < mp->mi_rows; r++)
      for (int c = 0; c < mp->mi_cols; c++) {
        const MB_MODE_INFO *m = mp->mi_grid_base[r * mp->mi_stride + c];
        uint8_t rec[8] = { 255, 255, 255, 255, 255, 255, 255, 255 };
        if (m) {
          rec[0] = m->bsize;
          rec[1] = m->tx_size;
          rec[2] = m->skip_txfm;
          rec[3] = (uint8_t)m->ref_frame[0];
          rec[4] = m->mode;
          rec[5] = m->segment_id;
          rec[6] = m->use_intrabc;
          // block origin: is this the top-left mi of its block (the grid shares one MB_MODE_INFO per block)
          const int top = r == 0 || mp->mi_grid_base[(r - 1) * mp->mi_stride + c] != m;
          const int left = c == 0 || mp->mi_grid_base[r * mp->mi_stride + c - 1] != m;
          rec[7] = (uint8_t)(top | (left << 1));
        }
        fwrite(rec, 1, 8, g_f);
      }
  }
  __real_av1_pick_filter_level(sd, cpi, method);
  if (g_f) {
    struct loopfilter *lf = &cpi->common.lf;
    tag("LPF1");
    i32(lf->filter_level[0]);
    i32(lf->filter_level[1]);
    i32(lf->filter_level_u);
    i32(lf->filter_level_v);
    i32(lf->sharpness_level);
    g_after_pick = 1;
  }
}

#include "av1/common/thread_common.h"
void __real_av1_loop_filter_frame_mt(YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, MACROBLOCKD *xd, int plane_start,
                                     int plane_end, int partial_frame, AVxWorker *workers, int num_workers,
                                     AV1LfSync *lf_sync, int lpf_opt_level);
void __wrap_av1_loop_filter_frame_mt(YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, MACROBLOCKD *xd, int plane_start,
                                     int plane_end, int partial_frame, AVxWorker *workers, int num_workers,
                                     AV1LfSync *lf_sync, int lpf_opt_level);
void __wrap_av1_loop_filter_frame_mt(YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, MACROBLOCKD *xd, int plane_start,
                                     int plane_end, int partial_frame, AVxWorker *workers, int num_workers,
                                     AV1LfSync *lf_sync, int lpf_opt_level) {
  __real_av1_loop_filter_frame_mt(frame, cm, xd, plane_start, plane_end, partial_frame, workers, num_workers, lf_sync,
                                  lpf_opt_level);
  if (g_f && g_after_pick) {
    g_after_pick = 0;
    tag("DBK1");
    i32(lpf_opt_level);
    const int np = av1_num_planes(cm);
    for (int p = 0; p < np; p++)
      plane_dump(frame->buffers[p], frame->strides[p > 0], frame->widths[p > 0], frame->heights[p > 0]);
  }
}

void __real_av1_loop_restoration_save_boundary_lines(const YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, int after_cdef);
void __wrap_av1_loop_restoration_save_boundary_lines(const YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, int after_cdef);
void __wrap_av1_loop_restoration_save_boundary_lines(const YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, int after_cdef) {
  if (g_f && !after_cdef) {
    tag("DBK0");
    const int np = av1_num_planes(cm);
    for (int p = 0; p < np; p++)
      plane_dump(frame->buffers[p], frame->strides[p > 0], frame->widths[p > 0], frame->heights[p > 0]);
  }
  __real_av1_loop_restoration_save_boundary_lines(frame, cm, after_cdef);
}

void __real_av1_pick_filter_restoration(const YV12_BUFFER_CONFIG *src, AV1_COMP *cpi);
void __wrap_av1_pick_filter_restoration(const YV12_BUFFER_CONFIG *src, AV1_COMP *cpi);
void __wrap_av1_pick_filter_restoration(const YV12_BUFFER_CONFIG *src, AV1_COMP *cpi) {
  if (g_f) {
    AV1_COMMON *cm = &cpi->common;
    MACROBLOCK *x = &cpi->td.mb;
    av1_fill_lr_rates(&x->mode_costs, x->e_mbd.tile_ctx);
    tag("RST0");
    const LOOP_FILTER_SPEED_FEATURES *s = &cpi->sf.lpf_sf;
    int hdr[32] = { 0 };
    hdr[0] = cpi->rd.RDMULT;
    hdr[1] = s->min_lr_unit_size;
    hdr[2] = s->max_lr_unit_size;
    hdr[3] = s->disable_loop_restoration_luma;
    hdr[4] = s->disable_loop_restoration_chroma;
    hdr[5] = s->disable_wiener_filter;
    hdr[6] = s->disable_sgr_filter;
    hdr[7] = s->prune_wiener_based_on_src_var;
    hdr[8] = s->prune_sgr_based_on_wiener;
    hdr[9] = s->reduce_wiener_window_size;
    hdr[10] = s->enable_sgr_ep_pruning;
    hdr[11] = s->dual_sgr_penalty_level;
    hdr[12] = s->switchable_lr_with_bias_level;
    hdr[13] = s->disable_wiener_coeff_refine_search;
    hdr[14] = s->use_downsampled_wiener_stats;
    hdr[15] = cm->tiles.rows;
    hdr[16] = cm->tiles.cols;
    hdr[17] = cm->seq_params->sb_size;
    hdr[18] = cm->quant_params.base_qindex;
    hdr[19] = cm->superres_upscaled_width;
    hdr[20] = cm->height;
    fwrite(hdr, 4, 32, g_f);
    fwrite(x->mode_costs.switchable_restore_cost, 4, RESTORE_SWITCHABLE_TYPES, g_f);
    fwrite(x->mode_costs.wiener_restore_cost, 4, 2, g_f);
    fwrite(x->mode_costs.sgrproj_restore_cost, 4, 2, g_f);
    for (int i = 0; i <= cm->tiles.rows; i++) i32(cm->tiles.row_start_sb[i]);
    for (int i = 0; i <= cm->tiles.cols; i++) i32(cm->tiles.col_start_sb[i]);
    twin_rst_dump_units(src, cpi, g_f);
    tag("RSTE");
  }
  __real_av1_pick_filter_restoration(src, cpi);
  if (g_f) {
    AV1_COMMON *cm = &cpi->common;
    tag("RST1");
    const int np = av1_num_planes(cm);
    for (int p = 0; p < np; p++) {
      const RestorationInfo *r = &cm->rst_info[p];
      i32(r->frame_restoration_type);
      i32(r->restoration_unit_size);
      i32(r->horz_units);
      i32(r->vert_units);
      const int n = r->frame_restoration_type == RESTORE_NONE ? 0 : r->num_rest_units;
      i32(n);
      for (int u = 0; u < n; u++) {
        const RestorationUnitInfo *ui = &r->unit_info[u];
        int32_t rec[1 + 16 + 3];
        rec[0] = ui->restoration_type;
        for (int i = 0; i < 8; i++) {
          rec[1 + i] = ui->restoration_type == RESTORE_WIENER ? ui->wiener_info.vfilter[i] : 0;
          rec[9 + i] = ui->restoration_type == RESTORE_WIENER ? ui->wiener_info.hfilter[i] : 0;
        }
        rec[17] = ui->restoration_type == RESTORE_SGRPROJ ? ui->sgrproj_info.ep : 0;
        rec[18] = ui->restoration_type == RESTORE_SGRPROJ ? ui->sgrproj_info.xqd[0] : 0;
        rec[19] = ui->restoration_type == RESTORE_SGRPROJ ? ui->sgrproj_info.xqd[1] : 0;
        fwrite(rec, 4, 20, g_f);
      }
    }
  }
}

void __real_av1_loop_restoration_filter_frame(YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, int optimized_lr,
                                              void *lr_ctxt);
void __wrap_av1_loop_restoration_filter_frame(YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, int optimized_lr,
                                              void *lr_ctxt);
void __wrap_av1_loop_restoration_filter_frame(YV12_BUFFER_CONFIG *frame, AV1_COMMON *cm, int optimized_lr,
                                              void *lr_ctxt) {
  __real_av1_loop_restoration_filter_frame(frame, cm, optimized_lr, lr_ctxt);
  if (g_f) {
    tag("FIN0");
    const int np = av1_num_planes(cm);
    for (int p = 0; p < np; p++)
      plane_dump(frame->buffers[p], frame->strides[p > 0], frame->crop_widths[p > 0], frame->crop_heights[p > 0]);
  }
}

// planes: 8-bit y, u, v (u/v at the subsampled size), ss444 = 1 for 4:4:4 else 4:2:0. quantizer: libaom's 0..63
// (rc_min = rc_max = cq_level). skip_postproc: AV1E_SET_SKIP_POSTPROC_FILTERING (avifenc sets 1; 0 so the final
// restoration is applied and dumped). Returns the compressed size, or < 0 on error.
EXPORT int twin_capture2(const uint8_t *y, const uint8_t *u, const uint8_t *v, int w, int h, int ss444, int speed,
                         int quantizer, int skip_postproc, int tile_cols_log2, int tile_rows_log2, int sharpness,
                         const char *out_path) {
  aom_codec_iface_t *iface = aom_codec_av1_cx();
  aom_codec_enc_cfg_t cfg;
  if (aom_codec_enc_config_default(iface, &cfg, AOM_USAGE_ALL_INTRA)) return -1;
  cfg.g_w = w;
  cfg.g_h = h;
  cfg.g_profile = ss444 ? 1 : 0;
  cfg.g_bit_depth = AOM_BITS_8;
  cfg.g_input_bit_depth = 8;
  cfg.g_limit = 1;
  cfg.g_threads = 1;
  cfg.rc_end_usage = AOM_Q;
  cfg.rc_min_quantizer = quantizer;
  cfg.rc_max_quantizer = quantizer;
  aom_codec_ctx_t ctx;
  if (aom_codec_enc_init(&ctx, iface, &cfg, 0)) return -2;
  aom_codec_control(&ctx, AOME_SET_CPUUSED, speed);
  aom_codec_control(&ctx, AOME_SET_CQ_LEVEL, quantizer);
  aom_codec_control(&ctx, AOME_SET_TUNING, AOM_TUNE_PSNR);
  aom_codec_control(&ctx, AV1E_SET_SKIP_POSTPROC_FILTERING, skip_postproc);
  if (tile_cols_log2) aom_codec_control(&ctx, AV1E_SET_TILE_COLUMNS, tile_cols_log2);
  if (tile_rows_log2) aom_codec_control(&ctx, AV1E_SET_TILE_ROWS, tile_rows_log2);
  if (sharpness) aom_codec_control(&ctx, AOME_SET_SHARPNESS, sharpness);
  aom_image_t img;
  const aom_img_fmt_t fmt = ss444 ? AOM_IMG_FMT_I444 : AOM_IMG_FMT_I420;
  if (!aom_img_alloc(&img, fmt, w, h, 16)) return -3;
  const int cw = ss444 ? w : (w + 1) >> 1, ch = ss444 ? h : (h + 1) >> 1;
  for (int r = 0; r < h; r++) memcpy(img.planes[0] + r * img.stride[0], y + r * w, w);
  for (int r = 0; r < ch; r++) {
    memcpy(img.planes[1] + r * img.stride[1], u + r * cw, cw);
    memcpy(img.planes[2] + r * img.stride[2], v + r * cw, cw);
  }
  g_f = fopen(out_path, "wb");
  g_after_pick = 0;
  if (!g_f) return -4;
  int size = 0;
  if (aom_codec_encode(&ctx, &img, 0, 1, 0)) size = -5;
  if (size == 0 && aom_codec_encode(&ctx, NULL, 0, 1, 0)) size = -6;
  const aom_codec_cx_pkt_t *pkt;
  aom_codec_iter_t it = NULL;
  while (size >= 0 && (pkt = aom_codec_get_cx_data(&ctx, &it)) != NULL)
    if (pkt->kind == AOM_CODEC_CX_FRAME_PKT) size += (int)pkt->data.frame.sz;
  tag("END0");
  fclose(g_f);
  g_f = NULL;
  aom_img_free(&img);
  aom_codec_destroy(&ctx);
  return size;
}

EXPORT int twin_capture(const uint8_t *y, const uint8_t *u, const uint8_t *v, int w, int h, int ss444, int speed,
                        int quantizer, int skip_postproc, const char *out_path) {
  return twin_capture2(y, u, v, w, h, ss444, speed, quantizer, skip_postproc, 0, 0, 0, out_path);
}
