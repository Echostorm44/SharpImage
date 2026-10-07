// twin_inv.c: libaom 3.14.1's 8-bit inverse transform + add as the encoder calls it (av1_inverse_transform_block ->
// av1_inv_txfm_add, RTCD-dispatched: av1_inv_txfm_add_avx2 -> av1_lowbd_inv_txfm2d_add_avx2 on an AVX2 machine), for
// AomInvTxfmTwinTests.
#include <stdint.h>
#include <string.h>
#include "config/aom_config.h"
#include "config/aom_dsp_rtcd.h"
#include "config/av1_rtcd.h"
#include "av1/common/av1_txfm.h"
#include "av1/common/blockd.h"
#include "av1/common/idct.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT void twin_init(void) { av1_rtcd(); aom_dsp_rtcd(); }

// 1 when av1_inv_txfm_add dispatches to the AVX2 kernel set (the path being twinned)
EXPORT int twin_inv_is_avx2(void) { return av1_inv_txfm_add == av1_inv_txfm_add_avx2; }

// av1_inv_txfm_add on (a 32-byte aligned copy of) dqcoeff[0 .. ncoef): lossless = 0, bd = 8, tx_set_type the
// intra / non-reduced set (unused by the lowbd SIMD path).
EXPORT void twin_inv_txfm_add(const int32_t *dqcoeff, int ncoef, uint8_t *dst, int stride, int tx_type, int tx_size,
                              int eob) {
  static int32_t buf[64 * 64] __attribute__((aligned(32)));
  memset(buf, 0, sizeof(buf));
  memcpy(buf, dqcoeff, sizeof(int32_t) * ncoef);
  TxfmParam p;
  memset(&p, 0, sizeof(p));
  p.tx_type = (TX_TYPE)tx_type;
  p.tx_size = (TX_SIZE)tx_size;
  p.lossless = 0;
  p.bd = 8;
  p.is_hbd = 0;
  p.tx_set_type = av1_get_ext_tx_set_type((TX_SIZE)tx_size, 0, 0);
  p.eob = eob;
  av1_inv_txfm_add(buf, dst, stride, &p);
}
