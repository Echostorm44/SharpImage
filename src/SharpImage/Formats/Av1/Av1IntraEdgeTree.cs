// AV1 intra edge tree — tracks which edges are available for intra prediction
// Ported from dav1d: src/intra_edge.c / intra_edge.h (VideoLAN dav1d, BSD-2-Clause)
// Pre-built immutable tree mapping superblock partition positions to edge availability flags.

using System;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Edge availability flags for intra prediction. Indicates whether top-right
/// and bottom-left neighbors exist for each chroma subsampling format.
/// Maps to dav1d EdgeFlags (intra_edge.h).
/// </summary>
[Flags]
public enum Av1EdgeFlags : byte
{
    None = 0,
    I444TopHasRight = 1 << 0,
    I422TopHasRight = 1 << 1,
    I420TopHasRight = 1 << 2,
    I444LeftHasBottom = 1 << 3,
    I422LeftHasBottom = 1 << 4,
    I420LeftHasBottom = 1 << 5,
    AllTopHasRight = I444TopHasRight | I422TopHasRight | I420TopHasRight,
    AllLeftHasBottom = I444LeftHasBottom | I422LeftHasBottom | I420LeftHasBottom,
    AllTrAndBl = AllTopHasRight | AllLeftHasBottom,
}

/// <summary>
/// A node in the intra edge tree. Each node stores edge flags for various
/// partition outcomes at this position: original (o), horizontal splits (h[2]),
/// and vertical splits (v[2]).
/// </summary>
public struct Av1EdgeNode
{
    public Av1EdgeFlags O;
    public Av1EdgeFlags H0, H1;
    public Av1EdgeFlags V0, V1;

    // Extended fields for branch nodes (BL > 8x8)
    public Av1EdgeFlags H4, V4;

    // Extended fields for tip nodes (BL == 8x8, 4x4 split)
    public Av1EdgeFlags Split0, Split1, Split2;

    // Children indices into the tree array (-1 = no child)
    public int Child0, Child1, Child2, Child3;

    public readonly bool IsTip => Child0 < 0;
}

/// <summary>
/// Pre-built intra edge availability tree for AV1 superblocks.
/// Two trees: one for 128×128 SB (1+4+16+64 branches + 256 tips = 341 nodes)
/// and one for 64×64 SB (1+4+16 branches + 64 tips = 85 nodes).
/// </summary>
public static class Av1IntraEdgeTree
{
    /// <summary>Tree for 128×128 superblocks (index 0 = root).</summary>
    public static readonly Av1EdgeNode[] Tree128;

    /// <summary>Tree for 64×64 superblocks (index 0 = root).</summary>
    public static readonly Av1EdgeNode[] Tree64;

    static Av1IntraEdgeTree()
    {
        // 128×128: 1 root (bl128) + 4 (bl64) + 16 (bl32) + 64 (bl16) = 85 branches + 256 tips = 341
        Tree128 = new Av1EdgeNode[341];
        int nextIdx = 1;
        InitBranch(Tree128, 0, Av1BlockLevel.Bl128x128, ref nextIdx,
            topHasRight: true, leftHasBottom: false);

        // 64×64: 1 root (bl64) + 4 (bl32) + 16 (bl16) = 21 branches + 64 tips = 85
        Tree64 = new Av1EdgeNode[85];
        nextIdx = 1;
        InitBranch(Tree64, 0, Av1BlockLevel.Bl64x64, ref nextIdx,
            topHasRight: true, leftHasBottom: false);
    }

    private static void InitEdges(ref Av1EdgeNode node, Av1BlockLevel bl, Av1EdgeFlags edgeFlags)
    {
        node.O = edgeFlags;
        node.H0 = edgeFlags | Av1EdgeFlags.AllLeftHasBottom;
        node.V0 = edgeFlags | Av1EdgeFlags.AllTopHasRight;

        if (bl == Av1BlockLevel.Bl8x8)
        {
            // Tip node
            node.H1 = edgeFlags & (Av1EdgeFlags.AllLeftHasBottom | Av1EdgeFlags.I420TopHasRight);
            node.V1 = edgeFlags & (Av1EdgeFlags.AllTopHasRight |
                                   Av1EdgeFlags.I420LeftHasBottom | Av1EdgeFlags.I422LeftHasBottom);

            node.Split0 = (edgeFlags & Av1EdgeFlags.AllTopHasRight) | Av1EdgeFlags.I422LeftHasBottom;
            node.Split1 = edgeFlags | Av1EdgeFlags.I444TopHasRight;
            node.Split2 = edgeFlags & (Av1EdgeFlags.I420TopHasRight |
                                       Av1EdgeFlags.I420LeftHasBottom | Av1EdgeFlags.I422LeftHasBottom);

            node.Child0 = node.Child1 = node.Child2 = node.Child3 = -1;
        }
        else
        {
            // Branch node
            node.H1 = edgeFlags & Av1EdgeFlags.AllLeftHasBottom;
            node.V1 = edgeFlags & Av1EdgeFlags.AllTopHasRight;
            node.H4 = Av1EdgeFlags.AllLeftHasBottom;
            node.V4 = Av1EdgeFlags.AllTopHasRight;

            if (bl == Av1BlockLevel.Bl16x16)
            {
                node.H4 |= edgeFlags & Av1EdgeFlags.I420TopHasRight;
                node.V4 |= edgeFlags & (Av1EdgeFlags.I420LeftHasBottom |
                                         Av1EdgeFlags.I422LeftHasBottom);
            }
        }
    }

    private static void InitBranch(Av1EdgeNode[] tree, int idx, Av1BlockLevel bl,
        ref int nextIdx, bool topHasRight, bool leftHasBottom)
    {
        var edgeFlags = (topHasRight ? Av1EdgeFlags.AllTopHasRight : Av1EdgeFlags.None) |
                        (leftHasBottom ? Av1EdgeFlags.AllLeftHasBottom : Av1EdgeFlags.None);

        InitEdges(ref tree[idx], bl, edgeFlags);

        if (bl == Av1BlockLevel.Bl16x16)
        {
            // Children are tips (8x8)
            for (int n = 0; n < 4; n++)
            {
                int childIdx = nextIdx++;
                SetChild(ref tree[idx], n, childIdx);

                bool childThr = !(n == 3 || (n == 1 && !topHasRight));
                bool childLhb = n == 0 || (n == 2 && leftHasBottom);

                var childFlags = (childThr ? Av1EdgeFlags.AllTopHasRight : Av1EdgeFlags.None) |
                                 (childLhb ? Av1EdgeFlags.AllLeftHasBottom : Av1EdgeFlags.None);

                InitEdges(ref tree[childIdx], bl + 1, childFlags);
            }
        }
        else
        {
            // Children are branches
            for (int n = 0; n < 4; n++)
            {
                int childIdx = nextIdx++;
                SetChild(ref tree[idx], n, childIdx);

                bool childThr = !(n == 3 || (n == 1 && !topHasRight));
                bool childLhb = n == 0 || (n == 2 && leftHasBottom);

                InitBranch(tree, childIdx, bl + 1, ref nextIdx, childThr, childLhb);
            }
        }
    }

    private static void SetChild(ref Av1EdgeNode node, int childIndex, int treeIdx)
    {
        switch (childIndex)
        {
            case 0: node.Child0 = treeIdx; break;
            case 1: node.Child1 = treeIdx; break;
            case 2: node.Child2 = treeIdx; break;
            case 3: node.Child3 = treeIdx; break;
        }
    }

    /// <summary>Gets the child node index for the given split quadrant.</summary>
    public static int GetSplitChild(in Av1EdgeNode node, int quadrant) => quadrant switch
    {
        0 => node.Child0,
        1 => node.Child1,
        2 => node.Child2,
        3 => node.Child3,
        _ => -1,
    };
}
