// SharpImage — Game development utilities: sprite sheet packing and cubemap conversion.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Effects;

/// <summary>
/// Cubemap face identifiers for skybox format conversion.
/// </summary>
public enum CubeFace
{
    PositiveX, // Right
    NegativeX, // Left
    PositiveY, // Top
    NegativeY, // Bottom
    PositiveZ, // Front
    NegativeZ  // Back
}

/// <summary>
/// Game development utilities: sprite sheet/atlas packing with JSON metadata
/// and cubemap ↔ equirectangular projection conversion.
/// </summary>
public static class GameDevOps
{
    // ─── Sprite Sheet / Atlas ───────────────────────────────────

    /// <summary>
    /// Packs multiple images into a grid-based sprite sheet atlas.
    /// Returns the packed atlas image and JSON metadata describing frame locations.
    /// </summary>
    /// <param name="sprites">Array of sprite images to pack.</param>
    /// <param name="names">Optional array of sprite names for metadata.</param>
    /// <param name="padding">Padding between sprites in pixels.</param>
    /// <param name="columns">Number of columns in the grid (0 = auto-calculate).</param>
    /// <returns>Tuple of (atlas image, JSON metadata string).</returns>
    public static (ImageFrame atlas, string metadata) PackSpriteSheet(
        ImageFrame[] sprites, string[]? names = null, int padding = 1, int columns = 0)
    {
        if (sprites.Length == 0)
            throw new ArgumentException("At least one sprite is required.", nameof(sprites));

        int count = sprites.Length;
        if (columns <= 0) columns = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / columns);

        // Find max sprite dimensions
        int maxW = 0, maxH = 0;
        for (int i = 0; i < count; i++)
        {
            maxW = Math.Max(maxW, (int)sprites[i].Columns);
            maxH = Math.Max(maxH, (int)sprites[i].Rows);
        }

        int cellW = maxW + padding * 2;
        int cellH = maxH + padding * 2;
        uint atlasW = (uint)(columns * cellW);
        uint atlasH = (uint)(rows * cellH);

        var atlas = new ImageFrame();
        atlas.Initialize(atlasW, atlasH, ColorspaceType.RGB, true);

        // Clear to transparent
        for (int y = 0; y < (int)atlasH; y++)
        {
            var row = atlas.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)atlasW; x++)
                row[x * 4 + 3] = 0;
        }

        var frames = new List<(string name, int x, int y, int width, int height)>();

        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            int destX = col * cellW + padding;
            int destY = row * cellH + padding;

            var sprite = sprites[i];
            int sw = (int)sprite.Columns;
            int sh = (int)sprite.Rows;
            int sCh = sprite.NumberOfChannels;
            bool sAlpha = sprite.HasAlpha;

            // Copy sprite into atlas
            for (int sy = 0; sy < sh; sy++)
            {
                int dy = destY + sy;
                if (dy < 0 || dy >= (int)atlasH) continue;
                var srcRow = sprite.GetPixelRow(sy);
                var dstRow = atlas.GetPixelRowForWrite(dy);

                for (int sx = 0; sx < sw; sx++)
                {
                    int dx = destX + sx;
                    if (dx < 0 || dx >= (int)atlasW) continue;

                    int sOff = sx * sCh;
                    int dOff = dx * 4;
                    dstRow[dOff] = srcRow[sOff];
                    dstRow[dOff + 1] = sCh > 1 ? srcRow[sOff + 1] : srcRow[sOff];
                    dstRow[dOff + 2] = sCh > 2 ? srcRow[sOff + 2] : srcRow[sOff];
                    dstRow[dOff + 3] = sAlpha ? srcRow[sOff + sCh - 1] : Quantum.MaxValue;
                }
            }

            string name = (names != null && i < names.Length) ? names[i] : $"sprite_{i}";
            frames.Add((name, destX, destY, sw, sh));
        }

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"atlasWidth\": {atlasW},");
        sb.AppendLine($"  \"atlasHeight\": {atlasH},");
        sb.AppendLine($"  \"spriteCount\": {count},");
        sb.AppendLine("  \"frames\": [");
        for (int f = 0; f < frames.Count; f++)
        {
            var (name, fx, fy, fw, fh) = frames[f];
            sb.Append($"    {{ \"name\": \"{name}\", \"x\": {fx}, \"y\": {fy}, \"width\": {fw}, \"height\": {fh} }}");
            sb.AppendLine(f < frames.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        string json = sb.ToString();
        return (atlas, json);
    }

    // ─── Equirectangular → Cubemap ──────────────────────────────

    /// <summary>
    /// Converts an equirectangular panorama to a single cubemap face.
    /// </summary>
    /// <param name="equirect">Equirectangular source (2:1 aspect ratio).</param>
    /// <param name="face">Which cube face to extract.</param>
    /// <param name="faceSize">Output face resolution (square).</param>
    public static ImageFrame EquirectToCubeFace(ImageFrame equirect, CubeFace face, uint faceSize = 256)
    {
        int ew = (int)equirect.Columns;
        int eh = (int)equirect.Rows;
        int eCh = equirect.NumberOfChannels;
        int fs = (int)faceSize;
        bool hasAlpha = equirect.HasAlpha;

        var result = new ImageFrame();
        result.Initialize(faceSize, faceSize, equirect.Colorspace, hasAlpha);

        Parallel.For(0, fs, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < fs; x++)
            {
                // Map face pixel to 3D direction
                double u = (2.0 * x / fs) - 1.0;
                double v = (2.0 * y / fs) - 1.0;

                double dx, dy, dz;
                (dx, dy, dz) = face switch
                {
                    CubeFace.PositiveX => (1.0, -v, -u),
                    CubeFace.NegativeX => (-1.0, -v, u),
                    CubeFace.PositiveY => (u, 1.0, v),
                    CubeFace.NegativeY => (u, -1.0, -v),
                    CubeFace.PositiveZ => (u, -v, 1.0),
                    CubeFace.NegativeZ => (-u, -v, -1.0),
                    _ => (0, 0, 1)
                };

                // 3D direction → spherical → equirectangular UV
                double theta = Math.Atan2(dz, dx); // longitude
                double phi = Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)); // latitude

                double eu = (theta / Math.PI + 1.0) * 0.5; // 0..1
                double ev = (phi / (Math.PI * 0.5) + 1.0) * 0.5; // 0..1

                int sx = Math.Clamp((int)(eu * ew), 0, ew - 1);
                int sy = Math.Clamp((int)(ev * eh), 0, eh - 1);

                var srcRow = equirect.GetPixelRow(sy);
                int sOff = sx * eCh;
                int dOff = x * (hasAlpha ? 4 : 3);

                dstRow[dOff] = srcRow[sOff];
                dstRow[dOff + 1] = eCh > 1 ? srcRow[sOff + 1] : srcRow[sOff];
                dstRow[dOff + 2] = eCh > 2 ? srcRow[sOff + 2] : srcRow[sOff];
                if (hasAlpha) dstRow[dOff + 3] = eCh > 3 ? srcRow[sOff + 3] : Quantum.MaxValue;
            }
        });

        return result;
    }

    /// <summary>
    /// Converts six cubemap faces back to an equirectangular panorama.
    /// </summary>
    /// <param name="faces">Dictionary of face images keyed by CubeFace.</param>
    /// <param name="outputWidth">Width of the equirectangular output.</param>
    /// <param name="outputHeight">Height of the equirectangular output (typically width/2).</param>
    public static ImageFrame CubemapToEquirect(Dictionary<CubeFace, ImageFrame> faces,
        uint outputWidth = 512, uint outputHeight = 256)
    {
        int ow = (int)outputWidth;
        int oh = (int)outputHeight;
        bool hasAlpha = faces.Values.First().HasAlpha;
        int outCh = hasAlpha ? 4 : 3;

        var result = new ImageFrame();
        result.Initialize(outputWidth, outputHeight, faces.Values.First().Colorspace, hasAlpha);

        Parallel.For(0, oh, y =>
        {
            var dstRow = result.GetPixelRowForWrite(y);
            double phi = (y / (double)oh - 0.5) * Math.PI; // -π/2 to π/2

            for (int x = 0; x < ow; x++)
            {
                double theta = (x / (double)ow - 0.5) * 2.0 * Math.PI; // -π to π

                // Spherical to 3D direction
                double dx = Math.Cos(phi) * Math.Cos(theta);
                double dy = Math.Sin(phi);
                double dz = Math.Cos(phi) * Math.Sin(theta);

                // Determine dominant face
                double ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
                CubeFace face;
                double u, v;

                if (ax >= ay && ax >= az)
                {
                    face = dx > 0 ? CubeFace.PositiveX : CubeFace.NegativeX;
                    double invAbs = 1.0 / ax;
                    u = dx > 0 ? -dz * invAbs : dz * invAbs;
                    v = -dy * invAbs;
                }
                else if (ay >= ax && ay >= az)
                {
                    face = dy > 0 ? CubeFace.PositiveY : CubeFace.NegativeY;
                    double invAbs = 1.0 / ay;
                    u = dx * invAbs;
                    v = dy > 0 ? dz * invAbs : -dz * invAbs;
                }
                else
                {
                    face = dz > 0 ? CubeFace.PositiveZ : CubeFace.NegativeZ;
                    double invAbs = 1.0 / az;
                    u = dz > 0 ? dx * invAbs : -dx * invAbs;
                    v = -dy * invAbs;
                }

                if (!faces.TryGetValue(face, out var faceImg)) continue;

                int fs = (int)faceImg.Columns;
                int fCh = faceImg.NumberOfChannels;
                int fx = Math.Clamp((int)((u + 1.0) * 0.5 * fs), 0, fs - 1);
                int fy = Math.Clamp((int)((v + 1.0) * 0.5 * (int)faceImg.Rows), 0, (int)faceImg.Rows - 1);

                var fRow = faceImg.GetPixelRow(fy);
                int fOff = fx * fCh;
                int dOff = x * outCh;

                dstRow[dOff] = fRow[fOff];
                dstRow[dOff + 1] = fCh > 1 ? fRow[fOff + 1] : fRow[fOff];
                dstRow[dOff + 2] = fCh > 2 ? fRow[fOff + 2] : fRow[fOff];
                if (hasAlpha) dstRow[dOff + 3] = fCh > 3 ? fRow[fOff + 3] : Quantum.MaxValue;
            }
        });

        return result;
    }
}
