// twin_pickrst.c: a copy of libaom 3.14.1's pickrst.c (its own flags: -O3, no -m SIMD) compiled into aomtwin_lf.dll
// for its static functions. Its external symbols are renamed on the command line (build.sh) so the library's own
// pickrst.o still provides the real ones.
#include "av1/encoder/pickrst.c"

#include <stdio.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_wiener_decompose(int win, int64_t *M, int64_t *H, int32_t *a, int32_t *b) {
  wiener_decompose_sep_sym(win, M, H, a, b);
}

EXPORT void twin_finalize_sym_filter(int win, int32_t *f, int16_t *out8) {
  InterpKernel k;
  memset(k, 0, sizeof(k));
  finalize_sym_filter(win, f, k);
  memcpy(out8, k, 8 * sizeof(int16_t));
}

EXPORT int64_t twin_compute_score(int win, int64_t *M, int64_t *H, const int16_t *vf, const int16_t *hf) {
  InterpKernel v, h;
  memcpy(v, vf, sizeof(v));
  memcpy(h, hf, sizeof(h));
  return compute_score(win, M, H, v, h);
}

EXPORT int twin_linsolve(int n, int64_t *A, int stride, int64_t *b, int64_t *x) {
  return linsolve_wiener(n, A, stride, b, x);
}

static int32_t *g_rstbuf;
EXPORT void twin_search_sgr(const uint8_t *dat, int w, int h, int dstride, const uint8_t *src, int sstride, int pu_w,
                            int pu_h, int pruning, int *out3) {
  if (!g_rstbuf) g_rstbuf = (int32_t *)aom_memalign(32, RESTORATION_TMPBUF_SIZE);
  struct aom_internal_error_info err;
  memset(&err, 0, sizeof(err));
  SgrprojInfo s = search_selfguided_restoration(dat, w, h, dstride, src, sstride, 0, 8, pu_w, pu_h, g_rstbuf, pruning,
                                                &err);
  out3[0] = s.ep;
  out3[1] = s.xqd[0];
  out3[2] = s.xqd[1];
}

EXPORT int twin_count_wiener_bits(int win, const int16_t *vf, const int16_t *hf, const int16_t *rvf,
                                  const int16_t *rhf) {
  WienerInfo a, r;
  memcpy(a.vfilter, vf, 16); memcpy(a.hfilter, hf, 16);
  memcpy(r.vfilter, rvf, 16); memcpy(r.hfilter, rhf, 16);
  return count_wiener_bits(win, &a, &r);
}

EXPORT int twin_count_sgrproj_bits(int ep, int x0, int x1, int rx0, int rx1) {
  SgrprojInfo a = { ep, { x0, x1 } }, r = { 0, { rx0, rx1 } };
  return count_sgrproj_bits(&a, &r);
}

// Capture-time instrumentation: the per-unit search results (rusi) and per-type totals of every (unit size, plane)
// restoration_search libaom's av1_pick_filter_restoration would run, written to f before the real pick runs (the real
// one redoes everything from scratch). Mirrors av1_pick_filter_restoration's set-up exactly.
void twin_rst_dump_units(const YV12_BUFFER_CONFIG *src, AV1_COMP *cpi, FILE *f);
void twin_rst_dump_units(const YV12_BUFFER_CONFIG *src, AV1_COMP *cpi, FILE *f) {
  AV1_COMMON *const cm = &cpi->common;
  MACROBLOCK *const x = &cpi->td.mb;
  const SequenceHeader *const seq_params = cm->seq_params;
  const LOOP_FILTER_SPEED_FEATURES *lpf_sf = &cpi->sf.lpf_sf;
  const int num_planes = av1_num_planes(cm);
  const int highbd = cm->seq_params->use_highbitdepth;
  av1_fill_lr_rates(&x->mode_costs, x->e_mbd.tile_ctx);
  int min_lr_unit_size = AOMMAX(cpi->sf.lpf_sf.min_lr_unit_size, block_size_wide[cm->seq_params->sb_size]);
  int max_lr_unit_size = AOMMAX(min_lr_unit_size, cpi->sf.lpf_sf.max_lr_unit_size);
  RestUnitSearchInfo *rusi[MAX_MB_PLANE];
  for (int plane = 0; plane < num_planes; ++plane)
    rusi[plane] = allocate_search_structs(cm, &cm->rst_info[plane], plane > 0, min_lr_unit_size);
  x->rdmult = cpi->rd.RDMULT;
  if (aom_realloc_frame_buffer(&cpi->trial_frame_rst, cm->superres_upscaled_width, cm->superres_upscaled_height,
                               seq_params->subsampling_x, seq_params->subsampling_y, highbd,
                               AOM_RESTORATION_FRAME_BORDER, cm->features.byte_alignment, NULL, NULL, NULL, false, 0))
    return;
  RestSearchCtxt rsc;
  const int buf_size = sizeof(int16_t) * 6 * RESTORATION_UNITSIZE_MAX * RESTORATION_UNITSIZE_MAX;
  int16_t *avg = (int16_t *)aom_memalign(32, buf_size);
  memset(avg, 0, buf_size);
  rsc.dgd_avg = avg;
  rsc.src_avg = avg + 3 * RESTORATION_UNITSIZE_MAX * RESTORATION_UNITSIZE_MAX;
  int plane_start = lpf_sf->disable_loop_restoration_luma ? AOM_PLANE_U : AOM_PLANE_Y;
  int plane_end = (num_planes == 1 || lpf_sf->disable_loop_restoration_chroma) ? AOM_PLANE_Y : AOM_PLANE_V;
  bool disable_lr_filter[RESTORE_TYPES] = { false };
  av1_derive_flags_for_lr_processing(lpf_sf, disable_lr_filter);
  for (int plane = plane_start; plane <= plane_end; plane++) {
    const YV12_BUFFER_CONFIG *dgd = &cm->cur_frame->buf;
    const int is_uv = plane != AOM_PLANE_Y;
    int plane_w, plane_h;
    av1_get_upsampled_plane_size(cm, is_uv, &plane_w, &plane_h);
    av1_extend_frame(dgd->buffers[plane], plane_w, plane_h, dgd->strides[is_uv], RESTORATION_BORDER,
                     RESTORATION_BORDER, highbd);
  }
  // every size, not only the ones the real search reaches (it may stop early)
  for (int luma_unit_size = max_lr_unit_size; luma_unit_size >= min_lr_unit_size; luma_unit_size >>= 1) {
    for (int plane = plane_start; plane <= plane_end; ++plane) {
      set_restoration_unit_size(cm, &cm->rst_info[plane], plane > 0, luma_unit_size);
      init_rsc(src, &cpi->common, x, lpf_sf, plane, rusi[plane], &cpi->trial_frame_rst, &rsc);
      restoration_search(cm, plane, &rsc, disable_lr_filter);
      const int n = cm->rst_info[plane].num_rest_units;
      int32_t hdr[4] = { 0x55525354, luma_unit_size, plane, n };
      fwrite(hdr, 4, 4, f);
      fwrite(rsc.total_sse, 8, RESTORE_TYPES, f);
      fwrite(rsc.total_bits, 8, RESTORE_TYPES, f);
      for (int u = 0; u < n; u++) {
        int32_t rec[3 + 16 + 3];
        const RestUnitSearchInfo *r = &rusi[plane][u];
        rec[0] = r->best_rtype[0];
        rec[1] = r->best_rtype[1];
        rec[2] = r->best_rtype[2];
        for (int i = 0; i < 8; i++) {
          rec[3 + i] = r->wiener.vfilter[i];
          rec[11 + i] = r->wiener.hfilter[i];
        }
        rec[19] = r->sgrproj.ep;
        rec[20] = r->sgrproj.xqd[0];
        rec[21] = r->sgrproj.xqd[1];
        fwrite(rec, 4, 22, f);
      }
    }
  }
  aom_free(avg);
  for (int plane = 0; plane < num_planes; plane++) aom_free(rusi[plane]);
}
