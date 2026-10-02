using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/common/warped_motion.c (av1_warp_plane) -- pending.
internal static partial class AomWarp
{
    public static void WarpPlane(AomInterPredParams p, byte[]? dst8, ushort[]? dst16, int dstOff, int dstStride)
        => throw new NotImplementedException("av1_warp_plane");

    /// <summary>av1_findSamples.</summary>
    public static int FindSamples(AomCommon cm, AomMacroblockD xd, int[] pts, int[] ptsInref)
        => throw new NotImplementedException("av1_findSamples");
}
