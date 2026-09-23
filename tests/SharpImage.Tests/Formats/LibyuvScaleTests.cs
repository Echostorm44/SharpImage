using System;
using System.Security.Cryptography;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// LibyuvScale vs libyuv's ScalePlane / ScalePlane_12 (kFilterBox, the call libavif makes), as run by libavif's x86
// build (SSSE3/AVX2 rows). Inputs are regenerated exactly as the reference dumper made them (xorshift32 seeded 12345,
// one stream across all cases, 8-bit cases first); expected values are the first 16 hex digits of SHA-256 over
// libyuv's output as little-endian u16. Covers Down2/Down4/Down34/Down38, the box filter, bilinear down and up,
// Up2 and vertical-only paths.
public sealed class LibyuvScaleTests
{
    private static readonly int[][] Sizes =
    [
        [800, 600, 400, 300], [801, 601, 400, 300], [400, 300, 100, 75], [400, 300, 300, 225], [400, 320, 150, 120],
        [640, 480, 240, 180], [400, 300, 133, 100], [400, 300, 399, 299], [400, 300, 250, 200], [400, 300, 200, 100],
        [400, 300, 380, 120], [128, 160, 512, 600], [128, 160, 64, 600], [200, 150, 400, 300], [201, 151, 400, 300],
        [64, 80, 512, 600], [400, 300, 400, 150], [400, 300, 400, 60], [400, 300, 100, 300], [37, 29, 11, 7],
        [1000, 700, 123, 45], [96, 64, 32, 16], [96, 64, 64, 48], [100, 100, 30, 30], [50, 50, 150, 20],
    ];

    private static readonly string[] Expected =
    [
        "b93130c013371e53", "d2a23c02835c3581", "a6a4389c8efb749a", "c2507f8c0d97ecdb", "ca6e0c1c109b9575",
        "c54f5a077ea2e1c8", "fca6d1b78dace37a", "84fe9be8e6dfc831", "93dd6510e4295fa8", "bafb2d64d434ed8d",
        "0d59a87279993f28", "1e77d3d03aa2f5f0", "e3e1bfdf6b15deed", "da43b3ba1e403ef0", "0e011b15ac8cd0d2",
        "0f1c5493b98c3c39", "4875816f2b8d4423", "4f6d9026bf4b1fd8", "6caa481d80c56172", "9d7f78dedb47e63a",
        "b96b725f4e7806b5", "fb074394963561ae", "7380ab3e7fd4dc65", "58ca645c5ec2fb03", "168fb6d5ca1569cf",
        "188cbff289d13a15", "f3e7d219316d0225", "9024a825cbaa08cf", "eed03bcb8bac39c2", "aa4c6b4a49aedbd1",
        "6c50bf8c94be1db1", "a7a78707c85432e9", "8d9d1ea82903409a", "04703d983acb9db3", "1a277e3c3a95b342",
        "1960071f792293d4", "96e05b09ebc80d3c", "cba8d95a3f9229e6", "4c1161304e06d282", "233a20de6b3905bd",
        "3e41b978852bc5ad", "63940ed951ecb950", "1a006cbc89d45f33", "58b3c43b2bce6f78", "9a9d5b795d16d87a",
        "238d38292af07b7b", "7457b197067e46b8", "5dd79479febacdc4", "bc301db6e49bd1b6", "1844abbfee76c968",
    ];

    [Test]
    public async Task ScalePlane_MatchesLibyuvOnEveryPath()
    {
        uint rng = 12345;
        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }
        var mismatches = new System.Collections.Generic.List<string>();
        int n = 0;
        for (int hbd = 0; hbd < 2; hbd++)
            foreach (var s in Sizes)
            {
                int sw = s[0], sh = s[1], dw = s[2], dh = s[3];
                var src = new ushort[sw * sh];
                for (int k = 0; k < sw * sh; k++)
                {
                    int x = k % sw, y = k / sw;
                    uint v = (uint)((x * 7 + y * 3) % 256) ^ (Next() & 31);
                    src[k] = hbd == 1 ? (ushort)(((v << 4) | (Next() & 15)) & 4095) : (ushort)(v & 255);
                }
                var dst = LibyuvScale.ScalePlane(src, sw, sw, sh, dw, dh, hbd == 1);
                var bytes = new byte[dst.Length * 2];
                Buffer.BlockCopy(dst, 0, bytes, 0, bytes.Length);
                string h = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
                if (h != Expected[n]) mismatches.Add($"{sw}x{sh}->{dw}x{dh} hbd={hbd}");
                n++;
            }
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }
}
