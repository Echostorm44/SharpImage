using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class GameDevOpsTests
{
    private static ImageFrame CreateSolid(uint w, uint h, ushort r, ushort g, ushort b, bool alpha = true)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, alpha);
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                int off = x * ch;
                row[off] = r; row[off + 1] = g; row[off + 2] = b;
                if (alpha) row[off + 3] = Quantum.MaxValue;
            }
        }
        return frame;
    }

    private static ImageFrame CreateEquirect(uint w, uint h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                int off = x * 3;
                row[off] = (ushort)(x * Quantum.MaxValue / (int)w);
                row[off + 1] = (ushort)(y * Quantum.MaxValue / (int)h);
                row[off + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }
        return frame;
    }

    // ─── Sprite Sheet Tests ─────────────────────────────────────

    [Test]
    public async Task PackSpriteSheet_CreatesAtlas()
    {
        var red = CreateSolid(16, 16, Quantum.MaxValue, 0, 0);
        var green = CreateSolid(16, 16, 0, Quantum.MaxValue, 0);
        var blue = CreateSolid(16, 16, 0, 0, Quantum.MaxValue);

        var (atlas, json) = GameDevOps.PackSpriteSheet(
            [red, green, blue],
            ["red", "green", "blue"],
            padding: 1, columns: 2);

        await Assert.That(atlas.Columns).IsGreaterThan(0u);
        await Assert.That(atlas.Rows).IsGreaterThan(0u);
        await Assert.That(json).IsNotNull();
        await Assert.That(json.Contains("red")).IsTrue();
        await Assert.That(json.Contains("green")).IsTrue();
        await Assert.That(json.Contains("blue")).IsTrue();

        atlas.Dispose(); red.Dispose(); green.Dispose(); blue.Dispose();
    }

    [Test]
    public async Task PackSpriteSheet_MetadataHasCorrectCount()
    {
        var s1 = CreateSolid(8, 8, Quantum.MaxValue, 0, 0);
        var s2 = CreateSolid(8, 8, 0, Quantum.MaxValue, 0);

        var (atlas, json) = GameDevOps.PackSpriteSheet([s1, s2], padding: 0, columns: 2);

        await Assert.That(json.Contains("\"spriteCount\": 2")).IsTrue();

        atlas.Dispose(); s1.Dispose(); s2.Dispose();
    }

    [Test]
    public async Task PackSpriteSheet_AutoColumns()
    {
        var sprites = new ImageFrame[9];
        for (int i = 0; i < 9; i++)
            sprites[i] = CreateSolid(8, 8, (ushort)(i * 7000), 0, 0);

        var (atlas, json) = GameDevOps.PackSpriteSheet(sprites);

        // sqrt(9) = 3, so 3 columns × 3 rows
        await Assert.That(json.Contains("\"spriteCount\": 9")).IsTrue();

        atlas.Dispose();
        foreach (var s in sprites) s.Dispose();
    }

    // ─── Cubemap Tests ──────────────────────────────────────────

    [Test]
    public async Task EquirectToCubeFace_ProducesSquareFace()
    {
        using var equirect = CreateEquirect(64, 32);
        using var face = GameDevOps.EquirectToCubeFace(equirect, CubeFace.PositiveZ, faceSize: 32);

        await Assert.That(face.Columns).IsEqualTo(32u);
        await Assert.That(face.Rows).IsEqualTo(32u);
    }

    [Test]
    public async Task EquirectToCubeFace_AllFacesGenerate()
    {
        using var equirect = CreateEquirect(64, 32);
        foreach (CubeFace f in Enum.GetValues<CubeFace>())
        {
            using var face = GameDevOps.EquirectToCubeFace(equirect, f, faceSize: 16);
            await Assert.That(face.Columns).IsEqualTo(16u);
        }
    }

    [Test]
    public async Task CubemapToEquirect_ProducesCorrectSize()
    {
        using var equirect = CreateEquirect(64, 32);
        var faces = new Dictionary<CubeFace, ImageFrame>();
        foreach (CubeFace f in Enum.GetValues<CubeFace>())
            faces[f] = GameDevOps.EquirectToCubeFace(equirect, f, faceSize: 16);

        using var roundtrip = GameDevOps.CubemapToEquirect(faces, 64, 32);

        await Assert.That(roundtrip.Columns).IsEqualTo(64u);
        await Assert.That(roundtrip.Rows).IsEqualTo(32u);

        foreach (var f in faces.Values) f.Dispose();
    }

    [Test]
    public async Task CubemapRoundtrip_PreservesApproximateColors()
    {
        using var equirect = CreateEquirect(64, 32);
        var faces = new Dictionary<CubeFace, ImageFrame>();
        foreach (CubeFace f in Enum.GetValues<CubeFace>())
            faces[f] = GameDevOps.EquirectToCubeFace(equirect, f, faceSize: 32);

        using var roundtrip = GameDevOps.CubemapToEquirect(faces, 64, 32);

        // Center pixel should be approximately preserved
        var srcRow = equirect.GetPixelRow(16).ToArray();
        var dstRow = roundtrip.GetPixelRow(16).ToArray();
        // At least one pixel in center region should be close
        bool found = false;
        for (int x = 20; x < 44; x++)
        {
            int diff = Math.Abs(srcRow[x * 3] - dstRow[x * 3]);
            if (diff < Quantum.MaxValue / 3) { found = true; break; }
        }
        await Assert.That(found).IsTrue();

        foreach (var f in faces.Values) f.Dispose();
    }
}
