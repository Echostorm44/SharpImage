// tokenize.c compiled into aomtwin_palsearch.dll for its static av1_fast_palette_color_index_context (the encoder's
// colour context used by av1_cost_color_map).
#include "av1/encoder/tokenize.c"
#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif
EXPORT int twin_fast_ctx(const uint8_t *map, int stride, int r, int c, int *idx) {
  return av1_fast_palette_color_index_context(map, stride, r, c, idx);
}
