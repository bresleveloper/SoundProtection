// SpDspSelect.h - picks the DSP library the plug-in is built against.
//
//   default       native\dsp\SpDsp.h   the real DSP (Task 1), found through /I native\dsp
//   /DSP_DSP_STUB SpDspStub.h          pass-through bodies of the same public API, for plumbing tests
#pragma once

#ifdef SP_DSP_STUB
#include "SpDspStub.h"
#else
#include "SpDsp.h"
#endif
