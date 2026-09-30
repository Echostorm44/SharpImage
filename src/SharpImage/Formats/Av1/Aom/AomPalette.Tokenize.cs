using System;
using System.Collections.Generic;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>TokenExtra: one palette color index token (color_ctx -1 for the first index of a map).</summary>
internal readonly record struct AomPaletteToken(byte Token, sbyte ColorCtx);

internal sealed partial class AomComp
{
    /// <summary>The palette color map tokens of the final encode, in coding order (the tile token list).</summary>
    public readonly List<AomPaletteToken> PaletteTokens = new();
}

internal static partial class AomPalette
{
    /// <summary>av1_tokenize_color_map (PALETTE_MAP): the first index then cost_and_tokenize_map in token mode, which
    /// records the tokens and adapts the color index CDFs.</summary>
    internal static void TokenizeColorMap(AomComp cpi, AomMacroblock x, int plane, int bsize, int txSize, bool allowUpdateCdf)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        byte[] colorMap = xd.Plane[plane].ColorIndexMap;
        int n = plane != 0 ? mbmi.Palette.PaletteSize1 : mbmi.Palette.PaletteSize0;
        AomRdoptUtils.GetBlockDimensions(bsize, plane, xd, out int planeBlockWidth, out _, out int rows, out int cols);
        var tokens = cpi.PaletteTokens;
        // The first color index.
        tokens.Add(new AomPaletteToken(colorMap[0], -1));
        int paletteSizeIdx = n - PALETTE_MIN_SIZE;
        var m = x.TileCtx.Mode;
        for (int k = 1; k < rows + cols - 1; ++k)
            for (int j = Math.Min(k, cols - 1); j >= Math.Max(0, k - rows + 1); --j)
            {
                int i = k - j;
                int colorCtx = FastPaletteColorIndexContext(colorMap, planeBlockWidth, i, j, out int colorNewIdx);
                tokens.Add(new AomPaletteToken((byte)colorNewIdx, (sbyte)colorCtx));
                if (allowUpdateCdf) AomCdf.Update(m.ColorMap[((plane != 0 ? 1 : 0) * 7 + paletteSizeIdx) * 5 + colorCtx], colorNewIdx, n);
            }
    }
}
