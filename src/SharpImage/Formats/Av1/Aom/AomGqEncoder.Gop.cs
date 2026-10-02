using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/gop_structure.c (no frame-parallel encoding: num_fp_contexts 1).
internal sealed partial class AomGqEncoder
{
    private void SetMultiLayerParams(int start, int end, ref int curFrameIdx, ref int frameInd, ref int curDispIdx, int layerDepth,
        bool scaleMaxBoost)
    {
        var gf = _gfGroup;
        int numFramesToProcess = end - start;
        if (layerDepth > gf.MaxLayerDepthAllowed || numFramesToProcess < 3)
        {
            while (start < end)
            {
                gf.UpdateType[frameInd] = LF_UPDATE;
                gf.ArfSrcOffset[frameInd] = 0;
                gf.CurFrameIdx[frameInd] = curFrameIdx;
                gf.DisplayIdx[frameInd] = curDispIdx;
                gf.LayerDepth[frameInd] = MAX_ARF_LAYERS;
                gf.ArfBoost[frameInd] = CalcArfBoost(start, end - start, 0, scaleMaxBoost);
                gf.FrameType[frameInd] = INTER_FRAME;
                gf.RefbufState[frameInd] = REFBUF_UPDATE;
                gf.MaxLayerDepth = Math.Max(gf.MaxLayerDepth, layerDepth);
                ++frameInd;
                ++curFrameIdx;
                ++curDispIdx;
                ++start;
            }
        }
        else
        {
            int m = (start + end - 1) / 2;
            gf.UpdateType[frameInd] = INTNL_ARF_UPDATE;
            gf.ArfSrcOffset[frameInd] = m - start;
            gf.CurFrameIdx[frameInd] = curFrameIdx;
            gf.DisplayIdx[frameInd] = curDispIdx + gf.ArfSrcOffset[frameInd];
            gf.LayerDepth[frameInd] = layerDepth;
            gf.FrameType[frameInd] = INTER_FRAME;
            gf.RefbufState[frameInd] = REFBUF_UPDATE;
            gf.ArfBoost[frameInd] = CalcArfBoost(m, end - m, m - start, scaleMaxBoost);
            ++frameInd;
            SetMultiLayerParams(start, m, ref curFrameIdx, ref frameInd, ref curDispIdx, layerDepth + 1, scaleMaxBoost);
            gf.UpdateType[frameInd] = INTNL_OVERLAY_UPDATE;
            gf.ArfSrcOffset[frameInd] = 0;
            gf.CurFrameIdx[frameInd] = curFrameIdx;
            gf.DisplayIdx[frameInd] = curDispIdx;
            gf.ArfBoost[frameInd] = 0;
            gf.LayerDepth[frameInd] = layerDepth;
            gf.FrameType[frameInd] = INTER_FRAME;
            gf.RefbufState[frameInd] = REFBUF_UPDATE;
            ++frameInd;
            ++curFrameIdx;
            ++curDispIdx;
            SetMultiLayerParams(m + 1, end, ref curFrameIdx, ref frameInd, ref curDispIdx, layerDepth + 1, scaleMaxBoost);
        }
    }

    /// <summary>construct_multi_layer_gf_structure.</summary>
    private int ConstructMultiLayerGfStructure(int baselineGfInterval, int firstFrameUpdateType)
    {
        var gf = _gfGroup;
        int gfInterval = baselineGfInterval - 1;
        int frameIndex = 0, curFrameIndex = 0;
        int curDispIndex = firstFrameUpdateType == KF_UPDATE ? 0 : _frameNumber;
        Array.Clear(gf.FrameParallelLevel);
        Array.Clear(gf.IsFrameNonRef);
        Array.Clear(gf.SrcOffset);
        Array.Clear(gf.IsFrameDropped);
        for (int i = 0; i < AomGfGroup.MaxLen; i++)
        {
            for (int j = 0; j < REF_FRAMES; j++) gf.SkipFrameRefresh[i, j] = -1;
            gf.SkipFrameAsRef[i] = -1;
        }
        bool kfDecomp = KfFilteringEnabled > 1;
        if (baselineGfInterval == MAX_STATIC_GF_GROUP_LENGTH) kfDecomp = false;
        if (firstFrameUpdateType == KF_UPDATE)
        {
            gf.UpdateType[frameIndex] = kfDecomp ? ARF_UPDATE : KF_UPDATE;
            gf.ArfSrcOffset[frameIndex] = 0;
            gf.CurFrameIdx[frameIndex] = curFrameIndex;
            gf.LayerDepth[frameIndex] = 0;
            gf.FrameType[frameIndex] = KEY_FRAME;
            gf.RefbufState[frameIndex] = REFBUF_RESET;
            gf.MaxLayerDepth = 0;
            gf.DisplayIdx[frameIndex] = curDispIndex;
            if (!kfDecomp) curDispIndex++;
            ++frameIndex;
            if (kfDecomp)
            {
                gf.UpdateType[frameIndex] = OVERLAY_UPDATE;
                gf.ArfSrcOffset[frameIndex] = 0;
                gf.CurFrameIdx[frameIndex] = curFrameIndex;
                gf.LayerDepth[frameIndex] = 0;
                gf.FrameType[frameIndex] = INTER_FRAME;
                gf.RefbufState[frameIndex] = REFBUF_UPDATE;
                gf.MaxLayerDepth = 0;
                gf.DisplayIdx[frameIndex] = curDispIndex;
                curDispIndex++;
                ++frameIndex;
            }
            curFrameIndex++;
        }
        if (firstFrameUpdateType == GF_UPDATE)
        {
            gf.UpdateType[frameIndex] = GF_UPDATE;
            gf.ArfSrcOffset[frameIndex] = 0;
            gf.CurFrameIdx[frameIndex] = curFrameIndex;
            gf.LayerDepth[frameIndex] = 0;
            gf.FrameType[frameIndex] = INTER_FRAME;
            gf.RefbufState[frameIndex] = REFBUF_UPDATE;
            gf.MaxLayerDepth = 0;
            gf.DisplayIdx[frameIndex] = curDispIndex;
            curDispIndex++;
            ++frameIndex;
            ++curFrameIndex;
        }
        bool useAltref = gf.MaxLayerDepthAllowed > 0;
        bool isFwdKf = _rc.FramesToFwdKf == gfInterval;
        if (useAltref)
        {
            gf.UpdateType[frameIndex] = ARF_UPDATE;
            gf.ArfSrcOffset[frameIndex] = gfInterval - curFrameIndex;
            gf.CurFrameIdx[frameIndex] = curFrameIndex;
            gf.LayerDepth[frameIndex] = 1;
            gf.ArfBoost[frameIndex] = _pRc.GfuBoost;
            gf.FrameType[frameIndex] = isFwdKf ? KEY_FRAME : INTER_FRAME;
            gf.IsSframeDue = false;
            gf.RefbufState[frameIndex] = REFBUF_UPDATE;
            gf.MaxLayerDepth = 1;
            gf.ArfIndex = frameIndex;
            gf.DisplayIdx[frameIndex] = curDispIndex + gf.ArfSrcOffset[frameIndex];
            ++frameIndex;
        }
        else gf.ArfIndex = -1;
        bool scaleMaxBoost = _cfg.Usage != REALTIME;
        SetMultiLayerParams(curFrameIndex, gfInterval, ref curFrameIndex, ref frameIndex, ref curDispIndex, (useAltref ? 1 : 0) + 1, scaleMaxBoost);
        if (useAltref)
        {
            gf.UpdateType[frameIndex] = OVERLAY_UPDATE;
            gf.ArfSrcOffset[frameIndex] = 0;
            gf.CurFrameIdx[frameIndex] = curFrameIndex;
            gf.LayerDepth[frameIndex] = MAX_ARF_LAYERS;
            gf.ArfBoost[frameIndex] = NORMAL_BOOST;
            gf.FrameType[frameIndex] = INTER_FRAME;
            gf.RefbufState[frameIndex] = isFwdKf ? REFBUF_RESET : REFBUF_UPDATE;
            gf.DisplayIdx[frameIndex] = curDispIndex;
            ++frameIndex;
        }
        else
        {
            for (; curFrameIndex <= gfInterval; ++curFrameIndex)
            {
                gf.UpdateType[frameIndex] = LF_UPDATE;
                gf.ArfSrcOffset[frameIndex] = 0;
                gf.CurFrameIdx[frameIndex] = curFrameIndex;
                gf.LayerDepth[frameIndex] = MAX_ARF_LAYERS;
                gf.ArfBoost[frameIndex] = NORMAL_BOOST;
                gf.FrameType[frameIndex] = INTER_FRAME;
                gf.RefbufState[frameIndex] = REFBUF_UPDATE;
                gf.MaxLayerDepth = Math.Max(gf.MaxLayerDepth, 2);
                gf.DisplayIdx[frameIndex] = curDispIndex;
                curDispIndex++;
                ++frameIndex;
            }
        }
        for (int gfIdx = frameIndex; gfIdx < MAX_STATIC_GF_GROUP_LENGTH; ++gfIdx)
        {
            gf.UpdateType[gfIdx] = LF_UPDATE;
            gf.ArfSrcOffset[gfIdx] = 0;
            gf.CurFrameIdx[gfIdx] = gfIdx;
            gf.LayerDepth[gfIdx] = MAX_ARF_LAYERS;
            gf.ArfBoost[gfIdx] = NORMAL_BOOST;
            gf.FrameType[gfIdx] = INTER_FRAME;
            gf.RefbufState[gfIdx] = REFBUF_UPDATE;
            gf.MaxLayerDepth = Math.Max(gf.MaxLayerDepth, 2);
        }
        return frameIndex;
    }

    /// <summary>set_ld_layer_depth.</summary>
    private void SetLdLayerDepth(int gopLength)
    {
        var gf = _gfGroup;
        int logGopLength = 0;
        while ((1 << logGopLength) < gopLength) ++logGopLength;
        for (int gfIndex = 0; gfIndex < gf.Size; ++gfIndex)
        {
            int count = 0;
            for (; count < MAX_ARF_LAYERS; ++count) if (((gfIndex >> count) & 1) != 0) break;
            gf.LayerDepth[gfIndex] = Math.Max(logGopLength - count, 0);
        }
        gf.MaxLayerDepth = Math.Min(logGopLength, MAX_ARF_LAYERS);
    }

    /// <summary>av1_gop_setup_structure.</summary>
    private void GopSetupStructure(bool isFinalPass)
    {
        var gf = _gfGroup;
        bool keyFrame = _rc.FramesSinceKey == 0;
        int firstFrameUpdateType = ARF_UPDATE;
        if (keyFrame)
        {
            firstFrameUpdateType = KF_UPDATE;
            // kf_max_pyr_height -1
        }
        else if (_arfGfBoostLst == 0) firstFrameUpdateType = GF_UPDATE;
        if (_cfg.Sharpness == 3) gf.MaxLayerDepthAllowed = Math.Min(gf.MaxLayerDepthAllowed, 2);
        gf.Size = ConstructMultiLayerGfStructure(_pRc.BaselineGfInterval, firstFrameUpdateType);
        if (gf.MaxLayerDepthAllowed == 0) SetLdLayerDepth(_pRc.BaselineGfInterval);
    }
}
