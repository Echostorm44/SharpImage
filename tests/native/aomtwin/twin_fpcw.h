// twin_fpcw.h: run libm-calling code under the x87 control word a mingw-w64 executable (avifenc.exe) starts with
// (0x37F: 64-bit extended precision), not the host process's (.NET: 0x27F). log1pf is x87 code (fyl2x / fyl2xp1)
// whose result precision follows this control word. twin_fpcw_target: 0 = leave the host's.
#ifndef TWIN_FPCW_H
#define TWIN_FPCW_H
extern int twin_fpcw_target;
static inline unsigned short twin_fpcw_enter(void) {
  unsigned short old;
  __asm__ volatile("fnstcw %0" : "=m"(old));
  if (twin_fpcw_target) { unsigned short cw = (unsigned short)twin_fpcw_target; __asm__ volatile("fldcw %0" : : "m"(cw)); }
  return old;
}
static inline void twin_fpcw_leave(unsigned short old) { __asm__ volatile("fldcw %0" : : "m"(old)); }
#define WITH_FPCW(stmt) do { unsigned short fpcw_old_ = twin_fpcw_enter(); stmt; twin_fpcw_leave(fpcw_old_); } while (0)
#endif
