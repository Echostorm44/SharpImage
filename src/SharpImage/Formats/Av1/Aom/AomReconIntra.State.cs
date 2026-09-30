namespace SharpImage.Formats.Av1;

// MACROBLOCKD fields read by the intra prediction port (av1/common/blockd.h) that the core AomMacroblock.cs does not
// declare. Note: xd->plane[p].width / height (AomMbdPlane.Width / Height) are in PIXELS as libaom's set_plane_n4 sets
// them ((bw * MI_SIZE) >> ss_x, at least 4); av1_predict_intra_block_facade passes them as wpx / hpx.
internal sealed partial class AomMacroblockD
{
    /// <summary>xd->color_index_map_offset[2]: offset into xd->plane[0 / 1].color_index_map of the current block's
    /// palette color index map (luma / chroma).</summary>
    public readonly int[] ColorIndexMapOffset = new int[2];
}
