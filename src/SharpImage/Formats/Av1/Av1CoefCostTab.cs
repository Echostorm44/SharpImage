using System;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Coefficient symbol costs of a coefficient-CDF context, built in one pass as libaom's av1_fill_coeff_costs does at
/// every superblock (coeff_cost_upd_level SB): the rate estimates then read one table entry per symbol instead of the
/// CDFs. Values are exactly Av1CoeffEncode.SymBits / BoolBits / HiTokBits of the CDFs at build time.
/// </summary>
internal sealed class Av1CoefCostTab
{
    public readonly double[] Base = new double[5 * 2 * 41 * 4];      // [ctx idx][level 0..3]
    public readonly double[] Br = new double[4 * 2 * 21 * 16];       // [br ctx idx][level 0..15]: br tokens' total (level >= 3)
    public readonly double[] EobBase = new double[5 * 2 * 4 * 3];    // [ctx idx][level - 1]
    public readonly double[] Skip = new double[5 * 13 * 2];
    public readonly double[] DcSign = new double[2 * 3 * 2];
    public readonly double[] EobHi = new double[5 * 2 * 9 * 2];
    public readonly double[] EobPt = new double[7 * 4 * 16];         // [size ctx][slot][eob_pt]

    public void Build(Av1CdfCoefContext c)
    {
        for (int i = 0; i < 5 * 2 * 41; i++) { var cdf = c.BaseTok[i]; for (int k = 0; k < 4; k++) Base[i * 4 + k] = Av1CoeffEncode.SymBits(cdf, k); }
        for (int i = 0; i < 4 * 2 * 21; i++) { var cdf = c.BrTok[i]; for (int m = 3; m < 16; m++) Br[i * 16 + m] = Av1CoeffEncode.HiTokBitsOf(cdf, m); }
        for (int i = 0; i < 5 * 2 * 4; i++) { var cdf = c.EobBaseTok[i]; for (int k = 0; k < 3; k++) EobBase[i * 3 + k] = Av1CoeffEncode.SymBits(cdf, k); }
        for (int i = 0; i < 5 * 13; i++) { ushort f = c.CoefSkip[i][0]; Skip[i * 2] = Av1CoeffEncode.BoolBits(f, 0); Skip[i * 2 + 1] = Av1CoeffEncode.BoolBits(f, 1); }
        for (int i = 0; i < 2 * 3; i++) { ushort f = c.DcSign[i][0]; DcSign[i * 2] = Av1CoeffEncode.BoolBits(f, 0); DcSign[i * 2 + 1] = Av1CoeffEncode.BoolBits(f, 1); }
        for (int i = 0; i < 5 * 2 * 9; i++) { ushort f = c.EobHiBit[i][0]; EobHi[i * 2] = Av1CoeffEncode.BoolBits(f, 0); EobHi[i * 2 + 1] = Av1CoeffEncode.BoolBits(f, 1); }
        for (int sz = 0; sz < 7; sz++)
            for (int slot = 0; slot < (sz <= 4 ? 4 : 2); slot++)
            {
                var cdf = Av1CoeffEncode.EobCdf(c, sz, slot);
                for (int k = 0; k < cdf.Length && k < 16; k++) EobPt[(sz * 4 + slot) * 16 + k] = Av1CoeffEncode.SymBits(cdf, k);
            }
    }
}
