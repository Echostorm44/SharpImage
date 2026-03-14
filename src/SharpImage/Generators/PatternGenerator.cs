// Built-in pattern tile generator.
// Renders named patterns by tiling small predefined bitmaps.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Generators;

/// <summary>
/// Names of available built-in patterns.
/// </summary>
public enum PatternName
{
    Checkerboard,
    Bricks,
    HorizontalLines,
    VerticalLines,
    CrossHatch,
    DiagonalLeft,
    DiagonalRight,
    DiagonalCrossHatch,
    Hexagons,
    FishScales,
    Gray5, Gray10, Gray15, Gray20, Gray25,
    Gray30, Gray35, Gray40, Gray45, Gray50,
    Gray55, Gray60, Gray65, Gray70, Gray75,
    Gray80, Gray85, Gray90, Gray95,
}

/// <summary>
/// Generates patterned images by tiling small predefined bitmaps.
/// Foreground defaults to black, background to white.
/// </summary>
public static class PatternGenerator
{
    /// <summary>
    /// Creates a patterned image by tiling the named pattern.
    /// </summary>
    public static ImageFrame Generate(int width, int height, PatternName pattern,
        byte fgR = 0, byte fgG = 0, byte fgB = 0,
        byte bgR = 255, byte bgG = 255, byte bgB = 255)
    {
        var (tile, tileWidth, tileHeight) = GetPatternTile(pattern);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        ushort qfR = Quantum.ScaleFromByte(fgR), qfG = Quantum.ScaleFromByte(fgG), qfB = Quantum.ScaleFromByte(fgB);
        ushort qbR = Quantum.ScaleFromByte(bgR), qbG = Quantum.ScaleFromByte(bgG), qbB = Quantum.ScaleFromByte(bgB);
        int ch = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ty = y % tileHeight;

            for (int x = 0; x < width; x++)
            {
                int tx = x % tileWidth;
                bool isForeground = tile[ty * tileWidth + tx] != 0;

                int offset = x * ch;
                row[offset] = isForeground ? qfR : qbR;
                row[offset + 1] = isForeground ? qfG : qbG;
                row[offset + 2] = isForeground ? qfB : qbB;
            }
        }

        return frame;
    }

    /// <summary>
    /// Returns (tile data, width, height) for the named pattern.
    /// Tile data: 0 = background, 1 = foreground.
    /// </summary>
    private static (byte[] Tile, int Width, int Height) GetPatternTile(PatternName pattern)
    {
        return pattern switch
        {
            PatternName.Checkerboard => (Checkerboard8x8, 8, 8),
            PatternName.Bricks => (Bricks16x8, 16, 8),
            PatternName.HorizontalLines => (HorizontalLines4x4, 4, 4),
            PatternName.VerticalLines => (VerticalLines4x4, 4, 4),
            PatternName.CrossHatch => (CrossHatch8x8, 8, 8),
            PatternName.DiagonalLeft => (DiagonalLeft8x8, 8, 8),
            PatternName.DiagonalRight => (DiagonalRight8x8, 8, 8),
            PatternName.DiagonalCrossHatch => (DiagonalCrossHatch8x8, 8, 8),
            PatternName.Hexagons => (Hexagons12x10, 12, 10),
            PatternName.FishScales => (FishScales8x8, 8, 8),
            >= PatternName.Gray5 and <= PatternName.Gray95 => GetGrayTile(pattern),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern))
        };
    }

    private static (byte[] Tile, int Width, int Height) GetGrayTile(PatternName pattern)
    {
        // Gray patterns: fill percentage of 4x4 tile (16 pixels)
        int level = ((int)pattern - (int)PatternName.Gray5 + 1) * 5;
        int filledPixels = (int)Math.Round(16 * level / 100.0);

        // Bayer-ordered dither positions for smooth distribution
        int[] bayerOrder = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

        byte[] tile = new byte[16];
        for (int i = 0; i < filledPixels && i < 16; i++)
            tile[bayerOrder[i]] = 1;

        return (tile, 4, 4);
    }

    // 8x8 checkerboard: alternating 4x4 blocks
    private static readonly byte[] Checkerboard8x8 =
    [
        1,1,1,1, 0,0,0,0,
        1,1,1,1, 0,0,0,0,
        1,1,1,1, 0,0,0,0,
        1,1,1,1, 0,0,0,0,
        0,0,0,0, 1,1,1,1,
        0,0,0,0, 1,1,1,1,
        0,0,0,0, 1,1,1,1,
        0,0,0,0, 1,1,1,1,
    ];

    // 16x8 brick pattern (offset by half each row)
    private static readonly byte[] Bricks16x8 =
    [
        1,1,1,1,1,1,1,0, 1,1,1,1,1,1,1,0,
        0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
        1,1,1,0, 1,1,1,1,1,1,1,0, 1,1,1,1,
        0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,
    ];

    // 4x4 horizontal lines
    private static readonly byte[] HorizontalLines4x4 =
    [
        1,1,1,1,
        0,0,0,0,
        0,0,0,0,
        0,0,0,0,
    ];

    // 4x4 vertical lines
    private static readonly byte[] VerticalLines4x4 =
    [
        1,0,0,0,
        1,0,0,0,
        1,0,0,0,
        1,0,0,0,
    ];

    // 8x8 crosshatch (horizontal + vertical)
    private static readonly byte[] CrossHatch8x8 =
    [
        1,1,1,1,1,1,1,1,
        1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
    ];

    // 8x8 diagonal left (top-left to bottom-right)
    private static readonly byte[] DiagonalLeft8x8 =
    [
        1,0,0,0,0,0,0,0,
        0,1,0,0,0,0,0,0,
        0,0,1,0,0,0,0,0,
        0,0,0,1,0,0,0,0,
        0,0,0,0,1,0,0,0,
        0,0,0,0,0,1,0,0,
        0,0,0,0,0,0,1,0,
        0,0,0,0,0,0,0,1,
    ];

    // 8x8 diagonal right (top-right to bottom-left)
    private static readonly byte[] DiagonalRight8x8 =
    [
        0,0,0,0,0,0,0,1,
        0,0,0,0,0,0,1,0,
        0,0,0,0,0,1,0,0,
        0,0,0,0,1,0,0,0,
        0,0,0,1,0,0,0,0,
        0,0,1,0,0,0,0,0,
        0,1,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,
    ];

    // 8x8 diagonal crosshatch (both diagonals)
    private static readonly byte[] DiagonalCrossHatch8x8 =
    [
        1,0,0,0,0,0,0,1,
        0,1,0,0,0,0,1,0,
        0,0,1,0,0,1,0,0,
        0,0,0,1,1,0,0,0,
        0,0,0,1,1,0,0,0,
        0,0,1,0,0,1,0,0,
        0,1,0,0,0,0,1,0,
        1,0,0,0,0,0,0,1,
    ];

    // 12x10 hexagon-like pattern
    private static readonly byte[] Hexagons12x10 =
    [
        0,0,1,1,1,1, 0,0,0,0,0,0,
        0,1,0,0,0,0, 1,0,0,0,0,0,
        1,0,0,0,0,0, 0,1,0,0,0,0,
        1,0,0,0,0,0, 0,1,0,0,0,0,
        0,1,0,0,0,0, 1,0,0,0,0,0,
        0,0,1,1,1,1, 0,0,1,1,1,1,
        0,0,0,0,0,1, 0,0,0,0,0,0,
        0,0,0,0,0,0, 1,0,0,0,0,1,
        0,0,0,0,0,0, 1,0,0,0,0,1,
        0,0,0,0,0,1, 0,0,0,0,0,0,
    ];

    // 8x8 fish scale pattern
    private static readonly byte[] FishScales8x8 =
    [
        0,0,0,1,1,0,0,0,
        0,0,1,0,0,1,0,0,
        0,1,0,0,0,0,1,0,
        1,0,0,0,0,0,0,1,
        0,0,0,0,1,1,0,0,
        0,0,0,1,0,0,1,0,
        0,0,1,0,0,0,0,1,
        0,1,0,0,0,0,0,0,
    ];

    /// <summary>
    /// Returns all available pattern names.
    /// </summary>
    public static PatternName[] GetAllPatternNames() =>
        Enum.GetValues<PatternName>();
}
