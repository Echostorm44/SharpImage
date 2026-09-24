using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpImage.Tests.Formats;

// libaom's official AV1 decoder test vectors (test/test_vectors.cc, storage.googleapis.com/aom-test-data): every
// output frame must match the published per-frame MD5 (hashed as test/md5_helper.h does). Committed here: all 100
// frame-size vectors (16..66 px, 8-bit), every feature vector (all-intra, size up / down in Matroska, CDF update,
// motion vectors, motion-field projection, intra-only + intrabc extreme DVs, SVC L1T2 / L2T1 / L2T2, film grain 8 /
// 10-bit, monochrome 8 / 10-bit) and quantizers 0 / 21 / 42 / 63 at 8 and 10 bits. The dev harness (JpegProbe
// av1vec) matched all 242 vectors, 611 frames, including the 128 quantizer vectors not committed.
public sealed class Av1TestVectorTests
{
    public static IEnumerable<string> Vectors() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestAssets", "av1_vectors"))
            .Where(f => f.EndsWith(".ivf") || f.EndsWith(".mkv"))
            .Select(Path.GetFileName)
            .Order()!;

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task Decode_MatchesLibaomMd5(string vector)
    {
        string result = Av1Conformance.CheckVector(Path.Combine(AppContext.BaseDirectory, "TestAssets", "av1_vectors", vector));
        await Assert.That(result).StartsWith("ok ");
    }
}
