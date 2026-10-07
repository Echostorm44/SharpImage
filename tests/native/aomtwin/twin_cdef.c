// twin_cdef.c: aomtwin_cdef.dll -- libaom 3.14.1's CDEF kernels through its run-time dispatch (the AVX2 versions the
// encoder runs on an AVX2 machine): cdef_find_dir, cdef_find_dir_dual, cdef_filter_8_{0..3}, cdef_copy_rect8_8bit_to_16bit,
// aom_sse and av1_cdef_filter_fb (8-bit output).
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "config/aom_scale_rtcd.h"
#include "av1/common/cdef_block.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) {
  av1_rtcd();
  aom_dsp_rtcd();
  aom_scale_rtcd();
}

EXPORT int twin_cdef_find_dir(const uint16_t *img, int stride, int32_t *var, int coeff_shift) {
  return cdef_find_dir(img, stride, var, coeff_shift);
}

EXPORT void twin_cdef_find_dir_dual(const uint16_t *img1, const uint16_t *img2, int stride, int32_t *var1, int32_t *var2,
                                    int coeff_shift, int *out1, int *out2) {
  cdef_find_dir_dual(img1, img2, stride, var1, var2, coeff_shift, out1, out2);
}

EXPORT void twin_cdef_filter_8(int idx, uint8_t *dst, int dstride, const uint16_t *in, int pri, int sec, int dir,
                               int pri_damping, int sec_damping, int coeff_shift, int bw, int bh) {
  const cdef_filter_block_func f[4] = { cdef_filter_8_0, cdef_filter_8_1, cdef_filter_8_2, cdef_filter_8_3 };
  f[idx](dst, dstride, in, pri, sec, dir, pri_damping, sec_damping, coeff_shift, bw, bh);
}

EXPORT void twin_cdef_copy_rect8(uint16_t *dst, int dstride, const uint8_t *src, int sstride, int width, int height) {
  cdef_copy_rect8_8bit_to_16bit(dst, dstride, src, sstride, width, height);
}

EXPORT int64_t twin_aom_sse(const uint8_t *a, int as, const uint8_t *b, int bs, int w, int h) {
  return aom_sse(a, as, b, bs, w, h);
}

// av1_cdef_filter_fb with 8-bit output. dlist: count (by, bx) byte pairs; dir / var: 16x16 int arrays (in/out);
// use_dirinit: pass &dirinit (the search's caching) else NULL; *dirinit_io in/out.
EXPORT void twin_cdef_filter_fb(uint8_t *dst8, int dstride, const uint16_t *in, int xdec, int ydec, int *dir, int use_dirinit,
                                int *dirinit_io, int *var, int pli, const uint8_t *dlist, int count, int level, int sec,
                                int damping, int coeff_shift) {
  cdef_list l[MI_SIZE_128X128 * MI_SIZE_128X128];
  for (int i = 0; i < count; i++) {
    l[i].by = dlist[2 * i];
    l[i].bx = dlist[2 * i + 1];
  }
  av1_cdef_filter_fb(dst8, NULL, dstride, in, xdec, ydec, (int(*)[CDEF_NBLOCKS])dir, use_dirinit ? dirinit_io : NULL,
                     (int(*)[CDEF_NBLOCKS])var, pli, l, count, level, sec, damping, coeff_shift);
}
