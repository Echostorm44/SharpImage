// Exposes internal HEVC encoder building blocks + the residual test hook to the test project so the
// CABAC, transform/quant, residual, and HEIC round-trips are verified in CI.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SharpImage.Tests")]
