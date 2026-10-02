using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>TplDepStats (libaom 3.14.1 av1/encoder/tpl_model.h).</summary>
internal sealed class AomTplDepStats
{
    public long SrcrfSse, SrcrfDist, RecrfSse, RecrfDist, IntraSse, IntraDist;
    public readonly long[] CmpRecrfDist = new long[2];
    public long McDepRate, McDepDist;
    public readonly long[] PredError = new long[INTER_REFS_PER_FRAME];
    public int IntraCost, InterCost, SrcrfRate, RecrfRate, IntraRate;
    public readonly int[] CmpRecrfRate = new int[2];
    public readonly AomMv[] Mv = new AomMv[INTER_REFS_PER_FRAME];
    public readonly sbyte[] RefFrameIndex = new sbyte[2];

    public void Clear()
    {
        SrcrfSse = SrcrfDist = RecrfSse = RecrfDist = IntraSse = IntraDist = McDepRate = McDepDist = 0;
        CmpRecrfDist[0] = CmpRecrfDist[1] = 0;
        Array.Clear(PredError);
        IntraCost = InterCost = SrcrfRate = RecrfRate = IntraRate = 0;
        CmpRecrfRate[0] = CmpRecrfRate[1] = 0;
        Array.Clear(Mv);
        RefFrameIndex[0] = RefFrameIndex[1] = 0;
    }

    public void CopyFrom(AomTplDepStats s)
    {
        SrcrfSse = s.SrcrfSse; SrcrfDist = s.SrcrfDist; RecrfSse = s.RecrfSse; RecrfDist = s.RecrfDist; IntraSse = s.IntraSse; IntraDist = s.IntraDist;
        CmpRecrfDist[0] = s.CmpRecrfDist[0]; CmpRecrfDist[1] = s.CmpRecrfDist[1];
        McDepRate = s.McDepRate; McDepDist = s.McDepDist;
        Array.Copy(s.PredError, PredError, INTER_REFS_PER_FRAME);
        IntraCost = s.IntraCost; InterCost = s.InterCost; SrcrfRate = s.SrcrfRate; RecrfRate = s.RecrfRate; IntraRate = s.IntraRate;
        CmpRecrfRate[0] = s.CmpRecrfRate[0]; CmpRecrfRate[1] = s.CmpRecrfRate[1];
        Array.Copy(s.Mv, Mv, INTER_REFS_PER_FRAME);
        RefFrameIndex[0] = s.RefFrameIndex[0]; RefFrameIndex[1] = s.RefFrameIndex[1];
    }
}

/// <summary>TplDepFrame.</summary>
internal sealed class AomTplDepFrame
{
    public bool IsValid;
    public AomTplDepStats[]? Stats;
    public AomFrameBuffer? GfPicture, RecPicture;
    public readonly int[] RefMapIndex = new int[REF_FRAMES];
    public int Stride, Width, Height, MiRows, MiCols, BaseRdmult;
    public uint FrameDisplayIndex;
    public bool UsePredSad;
}

/// <summary>TplParams (ppi->tpl_data): tpl_frame[i] is Buffer[i + REF_FRAMES + 1] (the 8 entries below it are the
/// GOP's previous references).</summary>
internal sealed class AomTplData
{
    public const int MAX_LAG_BUFFERS = 48, MAX_TPL_FRAME_IDX = 2 * MAX_LAG_BUFFERS, MAX_LENGTH_TPL_FRAME_STATS = MAX_TPL_FRAME_IDX + REF_FRAMES + 1,
        TPL_DEP_COST_SCALE_LOG2 = 4, BlockMisLog2 = 2, TplBsize1d = 16, BorderInPixels = 32;
    public readonly AomTplDepFrame[] Buffer = NewFrames();
    public AomTplDepStats[]?[] StatsPool = Array.Empty<AomTplDepStats[]?>();
    public AomFrameBuffer?[] RecPool = Array.Empty<AomFrameBuffer?>();
    public bool Ready;
    public int FrameIdx;
    public double R0AdjustFactor = 1.0;
    public readonly AomFrameBuffer?[] RefFrame = new AomFrameBuffer?[INTER_REFS_PER_FRAME], SrcRefFrame = new AomFrameBuffer?[INTER_REFS_PER_FRAME];

    public AomTplDepFrame Frame(int idx) => Buffer[idx + REF_FRAMES + 1];

    private static AomTplDepFrame[] NewFrames()
    {
        var a = new AomTplDepFrame[MAX_LENGTH_TPL_FRAME_STATS];
        for (int i = 0; i < a.Length; i++) a[i] = new AomTplDepFrame();
        return a;
    }

    /// <summary>av1_tpl_ptr_pos.</summary>
    public static int PtrPos(int miRow, int miCol, int stride) => (miRow >> BlockMisLog2) * stride + (miCol >> BlockMisLog2);

    /// <summary>av1_tpl_stats_ready.</summary>
    public bool StatsReady(int gfFrameIndex) => Ready && gfFrameIndex < MAX_TPL_FRAME_IDX && Frame(gfFrameIndex).IsValid;
}
