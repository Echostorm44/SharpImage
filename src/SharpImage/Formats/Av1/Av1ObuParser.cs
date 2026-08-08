// AV1 OBU (Open Bitstream Unit) parsing for the decoder
// Ported from dav1d: src/obu.c (VideoLAN dav1d, BSD-2-Clause)
// Reference: AV1 Bitstream & Decoding Process Specification v1.0.0

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Parses AV1 OBU headers, sequence headers, frame headers, and tile groups
/// for the decoder pipeline. This is the entry point for all bitstream parsing.
/// </summary>
public static class Av1ObuParser
{
    // Default loop filter mode/reference deltas (from dav1d obu.c)
    private static readonly Av1LoopfilterModeRefDeltas DefaultModeRefDeltas = new()
    {
        ModeDelta0 = 0, ModeDelta1 = 0,
        RefDelta0 = 1, RefDelta1 = 0, RefDelta2 = 0, RefDelta3 = 0,
        RefDelta4 = -1, RefDelta5 = 0, RefDelta6 = -1, RefDelta7 = -1,
    };

    // Default identity warp params: type=Identity, matrix={0,0,1<<16, 0,0,1<<16}
    private static readonly Av1WarpedMotionParams DefaultWmParams = new()
    {
        Type = Av1WarpedMotionType.Identity,
        Matrix0 = 0, Matrix1 = 0, Matrix2 = 1 << 16,
        Matrix3 = 0, Matrix4 = 0, Matrix5 = 1 << 16,
    };

    /// <summary>
    /// Result codes from OBU parsing operations.
    /// </summary>
    public enum ParseResult
    {
        /// <summary>Parsing succeeded.</summary>
        Ok = 0,
        /// <summary>Invalid bitstream data.</summary>
        InvalidData = -1,
        /// <summary>OBU skipped (not relevant to current operating point).</summary>
        Skipped = -2,
        /// <summary>Need more data.</summary>
        NeedMoreData = -3,
    }

    // ========================================================================
    // Sequence header parsing
    // ========================================================================

    /// <summary>
    /// Parse an AV1 sequence header OBU.
    /// Ported from dav1d parse_seq_hdr() in obu.c.
    /// </summary>
    public static ParseResult ParseSequenceHeader(Av1DecoderSequenceHeader hdr, ReadOnlySpan<byte> data)
    {
        var gb = new Av1GetBits(data);
        hdr.Clear();

        hdr.Profile = (Av1Profile)gb.GetBits(3);
        if ((int)hdr.Profile > 2) return ParseResult.InvalidData;

        hdr.StillPicture = gb.GetBool();
        hdr.ReducedStillPictureHeader = gb.GetBool();
        if (hdr.ReducedStillPictureHeader && !hdr.StillPicture)
            return ParseResult.InvalidData;

        if (hdr.ReducedStillPictureHeader)
        {
            hdr.NumOperatingPoints = 1;
            hdr.OperatingPoints[0].MajorLevel = (byte)gb.GetBits(3);
            hdr.OperatingPoints[0].MinorLevel = (byte)gb.GetBits(2);
            hdr.OperatingPoints[0].InitialDisplayDelay = 10;
        }
        else
        {
            hdr.TimingInfoPresent = gb.GetBool();
            if (hdr.TimingInfoPresent)
            {
                hdr.NumUnitsInTick = gb.GetBits(32);
                hdr.TimeScale = gb.GetBits(32);
                hdr.EqualPictureInterval = gb.GetBool();
                if (hdr.EqualPictureInterval)
                {
                    uint numTicksPerPicture = gb.GetVlc();
                    if (numTicksPerPicture == uint.MaxValue)
                        return ParseResult.InvalidData;
                    hdr.NumTicksPerPicture = numTicksPerPicture + 1;
                }

                hdr.DecoderModelInfoPresent = gb.GetBool();
                if (hdr.DecoderModelInfoPresent)
                {
                    hdr.EncoderDecoderBufferDelayLength = (byte)(gb.GetBits(5) + 1);
                    hdr.NumUnitsInDecodingTick = gb.GetBits(32);
                    hdr.BufferRemovalDelayLength = (byte)(gb.GetBits(5) + 1);
                    hdr.FramePresentationDelayLength = (byte)(gb.GetBits(5) + 1);
                }
            }

            hdr.DisplayModelInfoPresent = gb.GetBool();
            hdr.NumOperatingPoints = (byte)(gb.GetBits(5) + 1);
            for (int i = 0; i < hdr.NumOperatingPoints; i++)
            {
                ref var op = ref hdr.OperatingPoints[i];
                op.Idc = (ushort)gb.GetBits(12);
                if (op.Idc != 0 && ((op.Idc & 0xFF) == 0 || (op.Idc & 0xF00) == 0))
                    return ParseResult.InvalidData;
                op.MajorLevel = (byte)(2 + gb.GetBits(3));
                op.MinorLevel = (byte)gb.GetBits(2);
                if (op.MajorLevel > 3)
                    op.Tier = (byte)gb.GetBit();
                if (hdr.DecoderModelInfoPresent)
                {
                    op.DecoderModelParamPresent = gb.GetBool();
                    if (op.DecoderModelParamPresent)
                    {
                        ref var opi = ref hdr.OperatingParameterInfo[i];
                        opi.DecoderBufferDelay = gb.GetBits(hdr.EncoderDecoderBufferDelayLength);
                        opi.EncoderBufferDelay = gb.GetBits(hdr.EncoderDecoderBufferDelayLength);
                        opi.LowDelayMode = gb.GetBool();
                    }
                }
                if (hdr.DisplayModelInfoPresent)
                    op.DisplayModelParamPresent = gb.GetBool();
                op.InitialDisplayDelay = op.DisplayModelParamPresent
                    ? (byte)(gb.GetBits(4) + 1) : (byte)10;
            }
        }

        hdr.WidthNBits = (byte)(gb.GetBits(4) + 1);
        hdr.HeightNBits = (byte)(gb.GetBits(4) + 1);
        hdr.MaxWidth = (int)(gb.GetBits(hdr.WidthNBits) + 1);
        hdr.MaxHeight = (int)(gb.GetBits(hdr.HeightNBits) + 1);

        if (!hdr.ReducedStillPictureHeader)
        {
            hdr.FrameIdNumbersPresent = gb.GetBool();
            if (hdr.FrameIdNumbersPresent)
            {
                hdr.DeltaFrameIdNBits = (byte)(gb.GetBits(4) + 2);
                hdr.FrameIdNBits = (byte)(gb.GetBits(3) + hdr.DeltaFrameIdNBits + 1);
            }
        }

        hdr.Sb128 = gb.GetBool();
        hdr.FilterIntra = gb.GetBool();
        hdr.IntraEdgeFilter = gb.GetBool();

        if (hdr.ReducedStillPictureHeader)
        {
            hdr.ScreenContentTools = Av1AdaptiveBoolean.Adaptive;
            hdr.ForceIntegerMv = Av1AdaptiveBoolean.Adaptive;
        }
        else
        {
            hdr.InterIntra = gb.GetBool();
            hdr.MaskedCompound = gb.GetBool();
            hdr.WarpedMotion = gb.GetBool();
            hdr.DualFilter = gb.GetBool();
            hdr.OrderHint = gb.GetBool();
            if (hdr.OrderHint)
            {
                hdr.JntComp = gb.GetBool();
                hdr.RefFrameMvs = gb.GetBool();
            }
            hdr.ScreenContentTools = gb.GetBool()
                ? Av1AdaptiveBoolean.Adaptive
                : (Av1AdaptiveBoolean)gb.GetBit();
            hdr.ForceIntegerMv = hdr.ScreenContentTools != Av1AdaptiveBoolean.Off
                ? (gb.GetBool() ? Av1AdaptiveBoolean.Adaptive : (Av1AdaptiveBoolean)gb.GetBit())
                : Av1AdaptiveBoolean.On;
            if (hdr.OrderHint)
                hdr.OrderHintNBits = (byte)(gb.GetBits(3) + 1);
        }

        hdr.SuperRes = gb.GetBool();
        hdr.Cdef = gb.GetBool();
        hdr.Restoration = gb.GetBool();

        // Color config
        hdr.Hbd = (byte)gb.GetBit();
        if ((int)hdr.Profile == 2 && hdr.Hbd != 0)
            hdr.Hbd += (byte)gb.GetBit();
        if ((int)hdr.Profile != 1)
            hdr.Monochrome = gb.GetBool();
        hdr.ColorDescriptionPresent = gb.GetBool();
        if (hdr.ColorDescriptionPresent)
        {
            hdr.ColorPrimaries = (Av1ColorPrimaries)gb.GetBits(8);
            hdr.TransferCharacteristics = (Av1TransferCharacteristics)gb.GetBits(8);
            hdr.MatrixCoefficients = (Av1MatrixCoefficients)gb.GetBits(8);
        }
        else
        {
            hdr.ColorPrimaries = Av1ColorPrimaries.Unspecified;
            hdr.TransferCharacteristics = Av1TransferCharacteristics.Unspecified;
            hdr.MatrixCoefficients = Av1MatrixCoefficients.Unspecified;
        }

        if (hdr.Monochrome)
        {
            hdr.ColorRange = (byte)gb.GetBit();
            hdr.Layout = Av1PixelLayout.I400;
            hdr.SubsamplingX = hdr.SubsamplingY = 1;
            hdr.ChromaSamplePosition = Av1ChromaSamplePosition.Unknown;
        }
        else if (hdr.ColorPrimaries == Av1ColorPrimaries.Bt709 &&
                 hdr.TransferCharacteristics == Av1TransferCharacteristics.Srgb &&
                 hdr.MatrixCoefficients == Av1MatrixCoefficients.Identity)
        {
            hdr.Layout = Av1PixelLayout.I444;
            hdr.ColorRange = 1;
            if ((int)hdr.Profile != 1 && !((int)hdr.Profile == 2 && hdr.Hbd == 2))
                return ParseResult.InvalidData;
        }
        else
        {
            hdr.ColorRange = (byte)gb.GetBit();
            switch ((int)hdr.Profile)
            {
                case 0:
                    hdr.Layout = Av1PixelLayout.I420;
                    hdr.SubsamplingX = hdr.SubsamplingY = 1;
                    break;
                case 1:
                    hdr.Layout = Av1PixelLayout.I444;
                    break;
                case 2:
                    if (hdr.Hbd == 2)
                    {
                        hdr.SubsamplingX = (byte)gb.GetBit();
                        if (hdr.SubsamplingX != 0)
                            hdr.SubsamplingY = (byte)gb.GetBit();
                    }
                    else
                    {
                        hdr.SubsamplingX = 1;
                    }
                    hdr.Layout = hdr.SubsamplingX != 0
                        ? (hdr.SubsamplingY != 0 ? Av1PixelLayout.I420 : Av1PixelLayout.I422)
                        : Av1PixelLayout.I444;
                    break;
            }
            hdr.ChromaSamplePosition = (hdr.SubsamplingX & hdr.SubsamplingY) != 0
                ? (Av1ChromaSamplePosition)gb.GetBits(2)
                : Av1ChromaSamplePosition.Unknown;
        }

        if (!hdr.Monochrome)
            hdr.SeparateUvDeltaQ = gb.GetBool();

        hdr.FilmGrainPresent = gb.GetBool();

        if (gb.Error) return ParseResult.InvalidData;

        // Note: trailing_bits() and byte_alignment() are at the END of
        // frame_header_obu(), processed after all fields below.

        return ParseResult.Ok;
    }

    // ========================================================================
    // Frame header parsing
    // ========================================================================

    /// <summary>
    /// Parse an AV1 frame header.
    /// Ported from dav1d parse_frame_hdr() in obu.c.
    /// </summary>
    /// <param name="hdr">Frame header to populate.</param>
    /// <param name="seqHdr">Current sequence header.</param>
    /// <param name="refFrames">Reference frame slots (for inter-frame parsing).</param>
    /// <param name="data">Bitstream data starting at the frame header payload.</param>
    /// <param name="bytesConsumed">Number of bytes consumed from data.</param>
    /// <returns>Parse result code.</returns>
    public static ParseResult ParseFrameHeader(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames,
        ReadOnlySpan<byte> data,
        out int bytesConsumed,
        bool isObuFrame = false)
    {
        bytesConsumed = 0;
        var gb = new Av1GetBits(data);

        if (!seqHdr.ReducedStillPictureHeader)
            hdr.ShowExistingFrame = gb.GetBool();


        if (hdr.ShowExistingFrame)
        {
            hdr.ExistingFrameIdx = (byte)gb.GetBits(3);
            if (seqHdr.DecoderModelInfoPresent && !seqHdr.EqualPictureInterval)
                hdr.FramePresentationDelay = gb.GetBits(seqHdr.FramePresentationDelayLength);
            if (seqHdr.FrameIdNumbersPresent)
                hdr.FrameId = gb.GetBits(seqHdr.FrameIdNBits);
            bytesConsumed = gb.BytePosition;
            return gb.Error ? ParseResult.InvalidData : ParseResult.Ok;
        }

        if (seqHdr.ReducedStillPictureHeader)
        {
            hdr.FrameType = Av1FrameType.Key;
            hdr.ShowFrame = true;
        }
        else
        {
            hdr.FrameType = (Av1FrameType)gb.GetBits(2);
            hdr.ShowFrame = gb.GetBool();
        }

        if (hdr.ShowFrame)
        {
            if (seqHdr.DecoderModelInfoPresent && !seqHdr.EqualPictureInterval)
                hdr.FramePresentationDelay = gb.GetBits(seqHdr.FramePresentationDelayLength);
            hdr.ShowableFrame = hdr.FrameType != Av1FrameType.Key;
        }
        else
        {
            hdr.ShowableFrame = gb.GetBool();
        }

        hdr.ErrorResilientMode =
            (hdr.FrameType == Av1FrameType.Key && hdr.ShowFrame) ||
            hdr.FrameType == Av1FrameType.Switch ||
            seqHdr.ReducedStillPictureHeader || gb.GetBool();

        hdr.DisableCdfUpdate = gb.GetBool();
        hdr.AllowScreenContentTools = seqHdr.ScreenContentTools == Av1AdaptiveBoolean.Adaptive
            ? gb.GetBool()
            : seqHdr.ScreenContentTools == Av1AdaptiveBoolean.On;
        if (hdr.AllowScreenContentTools)
            hdr.ForceIntegerMv = seqHdr.ForceIntegerMv == Av1AdaptiveBoolean.Adaptive
                ? gb.GetBool()
                : seqHdr.ForceIntegerMv == Av1AdaptiveBoolean.On;

        if (hdr.IsIntra)
            hdr.ForceIntegerMv = true;

        if (seqHdr.FrameIdNumbersPresent)
            hdr.FrameId = gb.GetBits(seqHdr.FrameIdNBits);

        if (!seqHdr.ReducedStillPictureHeader)
            hdr.FrameSizeOverride = hdr.FrameType == Av1FrameType.Switch || gb.GetBool();

        if (seqHdr.OrderHint)
            hdr.FrameOffset = (byte)gb.GetBits(seqHdr.OrderHintNBits);

        hdr.PrimaryRefFrame = !hdr.ErrorResilientMode && hdr.IsInterOrSwitch
            ? (byte)gb.GetBits(3) : (byte)Av1Constants.PrimaryRefNone;

        if (seqHdr.DecoderModelInfoPresent)
        {
            hdr.BufferRemovalTimePresent = gb.GetBool();
            if (hdr.BufferRemovalTimePresent)
            {
                for (int i = 0; i < seqHdr.NumOperatingPoints; i++)
                {
                    ref var seqOp = ref seqHdr.OperatingPoints[i];
                    if (seqOp.DecoderModelParamPresent)
                    {
                        int inTemporal = (seqOp.Idc >> hdr.TemporalId) & 1;
                        int inSpatial = (seqOp.Idc >> (hdr.SpatialId + 8)) & 1;
                        if (seqOp.Idc == 0 || (inTemporal != 0 && inSpatial != 0))
                            hdr.OperatingPointBufferRemovalTime[i] = gb.GetBits(seqHdr.BufferRemovalDelayLength);
                    }
                }
            }
        }

        if (hdr.IsIntra)
        {
            hdr.RefreshFrameFlags = (hdr.FrameType == Av1FrameType.Key && hdr.ShowFrame)
                ? (byte)0xFF : (byte)gb.GetBits(8);
            if (hdr.RefreshFrameFlags != 0xFF && hdr.ErrorResilientMode && seqHdr.OrderHint)
                for (int i = 0; i < 8; i++)
                    gb.GetBits(seqHdr.OrderHintNBits);

            if (ReadFrameSize(hdr, seqHdr, refFrames, ref gb, useRef: false) < 0)
                return ParseResult.InvalidData;
            if (hdr.AllowScreenContentTools && !hdr.SuperResEnabled)
                hdr.AllowIntraBc = gb.GetBool();
        }
        else
        {
            // Inter/Switch frame
            hdr.RefreshFrameFlags = hdr.FrameType == Av1FrameType.Switch
                ? (byte)0xFF : (byte)gb.GetBits(8);
            if (hdr.ErrorResilientMode && seqHdr.OrderHint)
                for (int i = 0; i < 8; i++)
                    gb.GetBits(seqHdr.OrderHintNBits);

            if (seqHdr.OrderHint)
            {
                hdr.FrameRefShortSignaling = gb.GetBool();
                if (hdr.FrameRefShortSignaling)
                {
                    hdr.SetRefIdx(0, (sbyte)gb.GetBits(3));
                    hdr.SetRefIdx(1, -1);
                    hdr.SetRefIdx(2, -1);
                    hdr.SetRefIdx(3, (sbyte)gb.GetBits(3));

                    // Derive remaining reference indices from order hints
                    DeriveShortRefIdxs(hdr, seqHdr, refFrames);
                }
            }

            for (int i = 0; i < 7; i++)
            {
                if (!hdr.FrameRefShortSignaling)
                    hdr.SetRefIdx(i, (sbyte)gb.GetBits(3));
                if (seqHdr.FrameIdNumbersPresent)
                    gb.GetBits(seqHdr.DeltaFrameIdNBits); // delta_ref_frame_id, not stored
            }

            bool useRef = !hdr.ErrorResilientMode && hdr.FrameSizeOverride;
            if (ReadFrameSize(hdr, seqHdr, refFrames, ref gb, useRef) < 0)
                return ParseResult.InvalidData;

            if (!hdr.ForceIntegerMv)
                hdr.Hp = gb.GetBool();
            hdr.SubpelFilterMode = gb.GetBool()
                ? Av1FilterMode.Switchable
                : (Av1FilterMode)gb.GetBits(2);
            hdr.SwitchableMotionMode = gb.GetBool();

            if (!hdr.ErrorResilientMode && seqHdr.RefFrameMvs &&
                seqHdr.OrderHint && hdr.IsInterOrSwitch)
            {
                hdr.UseRefFrameMvs = gb.GetBool();
            }
        }

        if (!seqHdr.ReducedStillPictureHeader && !hdr.DisableCdfUpdate)
            hdr.RefreshContext = !gb.GetBool();

        // Tile info
        ParseTileInfo(hdr, seqHdr, ref gb);

        // Quantization
        hdr.QuantBaseQIdx = (byte)gb.GetBits(8);
        if (gb.GetBool())
            hdr.QuantYDcDelta = (sbyte)gb.GetSignedBits(7);
        if (!seqHdr.Monochrome)
        {
            bool diffUvDelta = seqHdr.SeparateUvDeltaQ && gb.GetBool();
            if (gb.GetBool()) hdr.QuantUDcDelta = (sbyte)gb.GetSignedBits(7);
            if (gb.GetBool()) hdr.QuantUAcDelta = (sbyte)gb.GetSignedBits(7);
            if (diffUvDelta)
            {
                if (gb.GetBool()) hdr.QuantVDcDelta = (sbyte)gb.GetSignedBits(7);
                if (gb.GetBool()) hdr.QuantVAcDelta = (sbyte)gb.GetSignedBits(7);
            }
            else
            {
                hdr.QuantVDcDelta = hdr.QuantUDcDelta;
                hdr.QuantVAcDelta = hdr.QuantUAcDelta;
            }
        }
        hdr.QuantUseQMatrix = gb.GetBool();
        if (hdr.QuantUseQMatrix)
        {
            hdr.QmY = (byte)gb.GetBits(4);
            hdr.QmU = (byte)gb.GetBits(4);
            hdr.QmV = seqHdr.SeparateUvDeltaQ ? (byte)gb.GetBits(4) : hdr.QmU;
        }

        // Segmentation
        ParseSegmentation(hdr, seqHdr, refFrames, ref gb);

        // Delta Q
        if (hdr.QuantBaseQIdx != 0)
        {
            hdr.DeltaQPresent = gb.GetBool();
            if (hdr.DeltaQPresent)
            {
                hdr.DeltaQResLog2 = (byte)gb.GetBits(2);
                if (!hdr.AllowIntraBc)
                {
                    hdr.DeltaLfPresent = gb.GetBool();
                    if (hdr.DeltaLfPresent)
                    {
                        hdr.DeltaLfResLog2 = (byte)gb.GetBits(2);
                        hdr.DeltaLfMulti = gb.GetBool();
                    }
                }
            }
        }

        // Derive lossless flags
        bool deltaLossless = hdr.QuantYDcDelta == 0 && hdr.QuantUDcDelta == 0 &&
            hdr.QuantUAcDelta == 0 && hdr.QuantVDcDelta == 0 && hdr.QuantVAcDelta == 0;
        hdr.AllLossless = true;
        for (int i = 0; i < Av1Constants.MaxSegments; i++)
        {
            int segDeltaQ = hdr.SegmentationEnabled
                ? hdr.SegmentationData.Segments[i].DeltaQ : 0;
            hdr.SegmentationQIdx[i] = (byte)Math.Clamp(hdr.QuantBaseQIdx + segDeltaQ, 0, 255);
            hdr.SegmentationLossless[i] = hdr.SegmentationQIdx[i] == 0 && deltaLossless;
            hdr.AllLossless &= hdr.SegmentationLossless[i];
        }

        // Loop filter
        ParseLoopFilter(hdr, seqHdr, refFrames, ref gb);

        // CDEF
        if (!hdr.AllLossless && seqHdr.Cdef && !hdr.AllowIntraBc)
        {
            hdr.CdefDamping = (byte)(gb.GetBits(2) + 3);
            hdr.CdefNBits = (byte)gb.GetBits(2);
            for (int i = 0; i < (1 << hdr.CdefNBits); i++)
            {
                hdr.SetCdefYStrength(i, (byte)gb.GetBits(6));
                if (!seqHdr.Monochrome)
                    hdr.SetCdefUvStrength(i, (byte)gb.GetBits(6));
            }
        }

        // Restoration
        if ((!hdr.AllLossless || hdr.SuperResEnabled) && seqHdr.Restoration && !hdr.AllowIntraBc)
        {
            hdr.SetLrType(0, (Av1RestorationType)gb.GetBits(2));
            if (!seqHdr.Monochrome)
            {
                hdr.SetLrType(1, (Av1RestorationType)gb.GetBits(2));
                hdr.SetLrType(2, (Av1RestorationType)gb.GetBits(2));
            }

            if (hdr.LrType0 != Av1RestorationType.None ||
                hdr.LrType1 != Av1RestorationType.None ||
                hdr.LrType2 != Av1RestorationType.None)
            {
                hdr.LrUnitSizeY = (byte)(6 + (seqHdr.Sb128 ? 1 : 0));
                if (gb.GetBool())
                {
                    hdr.LrUnitSizeY++;
                    if (!seqHdr.Sb128)
                        hdr.LrUnitSizeY += (byte)gb.GetBit();
                }
                hdr.LrUnitSizeUv = hdr.LrUnitSizeY;
                if ((hdr.LrType1 != Av1RestorationType.None || hdr.LrType2 != Av1RestorationType.None) &&
                    seqHdr.SubsamplingX == 1 && seqHdr.SubsamplingY == 1)
                {
                    hdr.LrUnitSizeUv -= (byte)gb.GetBit();
                }
            }
            else
            {
                hdr.LrUnitSizeY = 8;
            }
        }

        // Transform mode
        if (!hdr.AllLossless)
            hdr.TxfmMode = gb.GetBool() ? Av1TxfmMode.Switchable : Av1TxfmMode.Largest;

        // Reference mode
        if (hdr.IsInterOrSwitch)
            hdr.SwitchableCompRefs = gb.GetBool();

        // Skip mode
        if (hdr.SwitchableCompRefs && hdr.IsInterOrSwitch && seqHdr.OrderHint)
            DeriveSkipMode(hdr, seqHdr, refFrames);
        if (hdr.SkipModeAllowed)
            hdr.SkipModeEnabled = gb.GetBool();

        // Warp motion
        if (!hdr.ErrorResilientMode && hdr.IsInterOrSwitch && seqHdr.WarpedMotion)
            hdr.WarpMotion = gb.GetBool();

        hdr.ReducedTxSet = gb.GetBool();

        // Global motion
        for (int i = 0; i < 7; i++)
            hdr.Gmv[i] = DefaultWmParams;

        if (hdr.IsInterOrSwitch)
            ParseGlobalMotion(hdr, seqHdr, refFrames, ref gb);

        // Film grain
        if (seqHdr.FilmGrainPresent && (hdr.ShowFrame || hdr.ShowableFrame))
            ParseFilmGrain(hdr, seqHdr, refFrames, ref gb);

        if (gb.Error) return ParseResult.InvalidData;

        // trailing_bits() + byte_alignment() at end of frame_header_obu()
        // NOTE: OBU_FRAME (type 6) does NOT have a trailing bit — only byte alignment.
        // OBU_FRAME_HEADER (type 3) has trailing_one_bit + byte_alignment.
        if (!isObuFrame)
        {
            gb.GetBit(); // trailing_one_bit (must be 1, enforced by spec)
        }
        gb.ByteAlign();
        bytesConsumed = gb.BytePosition;
        return ParseResult.Ok;
    }

    // ========================================================================
    // Helper: read_frame_size
    // ========================================================================

    private static int ReadFrameSize(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames,
        ref Av1GetBits gb,
        bool useRef)
    {
        if (useRef)
        {
            for (int i = 0; i < 7; i++)
            {
                if (gb.GetBool())
                {
                    var refIdx = hdr.GetRefIdx(i);
                    if (refIdx < 0 || refIdx >= 8) return -1;
                    var refFrame = refFrames[refIdx];
                    if (!refFrame.Valid) return -1;
                    hdr.SuperResUpscaledWidth = refFrame.Width;
                    hdr.Height = refFrame.Height;
                    hdr.RenderWidth = refFrame.RenderWidth;
                    hdr.RenderHeight = refFrame.RenderHeight;
                    hdr.SuperResEnabled = seqHdr.SuperRes && gb.GetBool();
                    if (hdr.SuperResEnabled)
                    {
                        hdr.SuperResScaleDenominator = (byte)(9 + gb.GetBits(3));
                        int d = hdr.SuperResScaleDenominator;
                        hdr.CodedWidth = Math.Max((hdr.SuperResUpscaledWidth * 8 + (d >> 1)) / d,
                            Math.Min(16, hdr.SuperResUpscaledWidth));
                    }
                    else
                    {
                        hdr.SuperResScaleDenominator = 8;
                        hdr.CodedWidth = hdr.SuperResUpscaledWidth;
                    }
                    return 0;
                }
            }
        }

        if (hdr.FrameSizeOverride)
        {
            hdr.SuperResUpscaledWidth = (int)(gb.GetBits(seqHdr.WidthNBits) + 1);
            hdr.Height = (int)(gb.GetBits(seqHdr.HeightNBits) + 1);
        }
        else
        {
            hdr.SuperResUpscaledWidth = seqHdr.MaxWidth;
            hdr.Height = seqHdr.MaxHeight;
        }

        hdr.SuperResEnabled = seqHdr.SuperRes && gb.GetBool();
        if (hdr.SuperResEnabled)
        {
            hdr.SuperResScaleDenominator = (byte)(9 + gb.GetBits(3));
            int d = hdr.SuperResScaleDenominator;
            hdr.CodedWidth = Math.Max((hdr.SuperResUpscaledWidth * 8 + (d >> 1)) / d,
                Math.Min(16, hdr.SuperResUpscaledWidth));
        }
        else
        {
            hdr.SuperResScaleDenominator = 8;
            hdr.CodedWidth = hdr.SuperResUpscaledWidth;
        }

        hdr.HaveRenderSize = gb.GetBool();
        if (hdr.HaveRenderSize)
        {
            hdr.RenderWidth = (int)(gb.GetBits(16) + 1);
            hdr.RenderHeight = (int)(gb.GetBits(16) + 1);
        }
        else
        {
            hdr.RenderWidth = hdr.SuperResUpscaledWidth;
            hdr.RenderHeight = hdr.Height;
        }
        return 0;
    }

    // ========================================================================
    // Helper: tile_log2
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TileLog2(int sz, int tgt)
    {
        int k = 0;
        while ((sz << k) < tgt) k++;
        return k;
    }

    // ========================================================================
    // Tile info parsing
    // ========================================================================

    private static void ParseTileInfo(Av1DecoderFrameHeader hdr, Av1DecoderSequenceHeader seqHdr, ref Av1GetBits gb)
    {
        hdr.TileUniform = (byte)(gb.GetBool() ? 1 : 0);
        int sbSzMin1 = (64 << (seqHdr.Sb128 ? 1 : 0)) - 1;
        int sbSzLog2 = 6 + (seqHdr.Sb128 ? 1 : 0);
        int sbw = (hdr.CodedWidth + sbSzMin1) >> sbSzLog2;
        int sbh = (hdr.Height + sbSzMin1) >> sbSzLog2;
        int maxTileWidthSb = 4096 >> sbSzLog2;
        int maxTileAreaSb = 4096 * 2304 >> (2 * sbSzLog2);

        hdr.TileMinLog2Cols = (byte)TileLog2(maxTileWidthSb, sbw);
        hdr.TileMaxLog2Cols = (byte)TileLog2(1, Math.Min(sbw, Av1Constants.MaxTileCols));
        hdr.TileMaxLog2Rows = (byte)TileLog2(1, Math.Min(sbh, Av1Constants.MaxTileRows));

        int minLog2Tiles = Math.Max(TileLog2(maxTileAreaSb, sbw * sbh), hdr.TileMinLog2Cols);

        if (hdr.TileUniform != 0)
        {
            hdr.TileLog2Cols = hdr.TileMinLog2Cols;
            while (hdr.TileLog2Cols < hdr.TileMaxLog2Cols && gb.GetBool())
                hdr.TileLog2Cols++;
            int tileW = 1 + ((sbw - 1) >> hdr.TileLog2Cols);
            hdr.TileCols = 0;
            for (int sbx = 0; sbx < sbw; sbx += tileW)
            {
                hdr.TileColStartSb[hdr.TileCols] = (ushort)sbx;
                hdr.TileCols++;
            }

            hdr.TileMinLog2Rows = (byte)Math.Max(minLog2Tiles - hdr.TileLog2Cols, 0);
            hdr.TileLog2Rows = hdr.TileMinLog2Rows;
            while (hdr.TileLog2Rows < hdr.TileMaxLog2Rows && gb.GetBool())
                hdr.TileLog2Rows++;
            int tileH = 1 + ((sbh - 1) >> hdr.TileLog2Rows);
            hdr.TileRows = 0;
            for (int sby = 0; sby < sbh; sby += tileH)
            {
                hdr.TileRowStartSb[hdr.TileRows] = (ushort)sby;
                hdr.TileRows++;
            }
        }
        else
        {
            hdr.TileCols = 0;
            int widestTile = 0;
            int localMaxTileAreaSb = sbw * sbh;
            for (int sbx = 0; sbx < sbw && hdr.TileCols < Av1Constants.MaxTileCols;)
            {
                int tileWidthSb = Math.Min(sbw - sbx, maxTileWidthSb);
                int tileW = tileWidthSb > 1 ? (int)(1 + gb.GetUniform((uint)tileWidthSb)) : 1;
                hdr.TileColStartSb[hdr.TileCols] = (ushort)sbx;
                sbx += tileW;
                widestTile = Math.Max(widestTile, tileW);
                hdr.TileCols++;
            }
            hdr.TileLog2Cols = (byte)TileLog2(1, hdr.TileCols);
            if (minLog2Tiles != 0)
                localMaxTileAreaSb >>= minLog2Tiles + 1;
            int maxTileHeightSb = Math.Max(localMaxTileAreaSb / Math.Max(widestTile, 1), 1);

            hdr.TileRows = 0;
            for (int sby = 0; sby < sbh && hdr.TileRows < Av1Constants.MaxTileRows;)
            {
                int tileHeightSb = Math.Min(sbh - sby, maxTileHeightSb);
                int tileH = tileHeightSb > 1 ? (int)(1 + gb.GetUniform((uint)tileHeightSb)) : 1;
                hdr.TileRowStartSb[hdr.TileRows] = (ushort)sby;
                sby += tileH;
                hdr.TileRows++;
            }
            hdr.TileLog2Rows = (byte)TileLog2(1, hdr.TileRows);
        }

        hdr.TileColStartSb[hdr.TileCols] = (ushort)sbw;
        hdr.TileRowStartSb[hdr.TileRows] = (ushort)sbh;

        if (hdr.TileLog2Cols != 0 || hdr.TileLog2Rows != 0)
        {
            hdr.TileUpdate = (ushort)gb.GetBits(hdr.TileLog2Cols + hdr.TileLog2Rows);
            hdr.TileNBytes = (byte)(gb.GetBits(2) + 1);
        }
    }

    // ========================================================================
    // Segmentation parsing
    // ========================================================================

    private static void ParseSegmentation(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames,
        ref Av1GetBits gb)
    {
        hdr.SegmentationEnabled = gb.GetBool();
        if (hdr.SegmentationEnabled)
        {
            if (hdr.PrimaryRefFrame == Av1Constants.PrimaryRefNone)
            {
                hdr.SegmentationUpdateMap = true;
                hdr.SegmentationUpdateData = true;
            }
            else
            {
                hdr.SegmentationUpdateMap = gb.GetBool();
                if (hdr.SegmentationUpdateMap)
                    hdr.SegmentationTemporal = gb.GetBool();
                hdr.SegmentationUpdateData = gb.GetBool();
            }

            if (hdr.SegmentationUpdateData)
            {
                hdr.SegmentationData.LastActiveSegId = -1;
                var segments = hdr.SegmentationData.Segments;
                for (int i = 0; i < Av1Constants.MaxSegments; i++)
                {
                    if (gb.GetBool()) { segments[i].DeltaQ = (short)gb.GetSignedBits(9); hdr.SegmentationData.LastActiveSegId = (sbyte)i; }
                    if (gb.GetBool()) { segments[i].DeltaLfYV = (sbyte)gb.GetSignedBits(7); hdr.SegmentationData.LastActiveSegId = (sbyte)i; }
                    if (gb.GetBool()) { segments[i].DeltaLfYH = (sbyte)gb.GetSignedBits(7); hdr.SegmentationData.LastActiveSegId = (sbyte)i; }
                    if (gb.GetBool()) { segments[i].DeltaLfU = (sbyte)gb.GetSignedBits(7); hdr.SegmentationData.LastActiveSegId = (sbyte)i; }
                    if (gb.GetBool()) { segments[i].DeltaLfV = (sbyte)gb.GetSignedBits(7); hdr.SegmentationData.LastActiveSegId = (sbyte)i; }
                    if (gb.GetBool())
                    {
                        segments[i].Ref = (sbyte)gb.GetBits(3);
                        hdr.SegmentationData.LastActiveSegId = (sbyte)i;
                        hdr.SegmentationData.Preskip = 1;
                    }
                    else
                    {
                        segments[i].Ref = -1;
                    }
                    if ((segments[i].Skip = (byte)(gb.GetBool() ? 1 : 0)) != 0)
                    {
                        hdr.SegmentationData.LastActiveSegId = (sbyte)i;
                        hdr.SegmentationData.Preskip = 1;
                    }
                    if ((segments[i].GlobalMv = (byte)(gb.GetBool() ? 1 : 0)) != 0)
                    {
                        hdr.SegmentationData.LastActiveSegId = (sbyte)i;
                        hdr.SegmentationData.Preskip = 1;
                    }
                }
            }
            // else: keep segmentation data from primary reference frame (handled by caller)
        }
        else
        {
            var segments = hdr.SegmentationData.Segments;
            for (int i = 0; i < Av1Constants.MaxSegments; i++)
                segments[i].Ref = -1;
        }
    }

    // ========================================================================
    // Loop filter parsing
    // ========================================================================

    private static void ParseLoopFilter(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames,
        ref Av1GetBits gb)
    {
        if (hdr.AllLossless || hdr.AllowIntraBc)
        {
            hdr.LfModeRefDeltaEnabled = true;
            hdr.LfModeRefDeltaUpdate = true;
            hdr.LfModeRefDeltas = DefaultModeRefDeltas;
            return;
        }

        hdr.LfLevelY0 = (byte)gb.GetBits(6);
        hdr.LfLevelY1 = (byte)gb.GetBits(6);
        if (!seqHdr.Monochrome && (hdr.LfLevelY0 != 0 || hdr.LfLevelY1 != 0))
        {
            hdr.LfLevelU = (byte)gb.GetBits(6);
            hdr.LfLevelV = (byte)gb.GetBits(6);
        }
        hdr.LfSharpness = (byte)gb.GetBits(3);

        if (hdr.PrimaryRefFrame == Av1Constants.PrimaryRefNone)
        {
            hdr.LfModeRefDeltas = DefaultModeRefDeltas;
        }
        else
        {
            // Copy from primary reference frame's loop filter deltas
            // In a full decoder, we'd copy from the stored frame header.
            // For now, use defaults as fallback.
            hdr.LfModeRefDeltas = DefaultModeRefDeltas;
        }

        hdr.LfModeRefDeltaEnabled = gb.GetBool();
        if (hdr.LfModeRefDeltaEnabled)
        {
            hdr.LfModeRefDeltaUpdate = gb.GetBool();
            if (hdr.LfModeRefDeltaUpdate)
            {
                for (int i = 0; i < 8; i++)
                    if (gb.GetBool())
                        hdr.LfModeRefDeltas.SetRefDelta(i, (sbyte)gb.GetSignedBits(7));
                for (int i = 0; i < 2; i++)
                    if (gb.GetBool())
                        hdr.LfModeRefDeltas.SetModeDelta(i, (sbyte)gb.GetSignedBits(7));
            }
        }
    }

    // ========================================================================
    // Global motion parsing
    // ========================================================================

    private static void ParseGlobalMotion(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames,
        ref Av1GetBits gb)
    {
        for (int i = 0; i < 7; i++)
        {
            hdr.Gmv[i].Type = !gb.GetBool() ? Av1WarpedMotionType.Identity
                : gb.GetBool() ? Av1WarpedMotionType.RotZoom
                : gb.GetBool() ? Av1WarpedMotionType.Translation
                : Av1WarpedMotionType.Affine;

            if (hdr.Gmv[i].Type == Av1WarpedMotionType.Identity) continue;

            // Reference global motion params
            var refGmv = DefaultWmParams;
            // In a full decoder with stored frame headers, we'd read from primary ref.
            // This will be refined when we have full frame header storage.

            int bits, shift;
            if (hdr.Gmv[i].Type >= Av1WarpedMotionType.RotZoom)
            {
                hdr.Gmv[i][2] = (1 << 16) + 2 * gb.GetBitsSubexp((refGmv[2] - (1 << 16)) >> 1, 12);
                hdr.Gmv[i][3] = 2 * gb.GetBitsSubexp(refGmv[3] >> 1, 12);
                bits = 12;
                shift = 10;
            }
            else
            {
                bits = 9 - (hdr.Hp ? 0 : 1);
                shift = 13 + (hdr.Hp ? 0 : 1);
            }

            if (hdr.Gmv[i].Type == Av1WarpedMotionType.Affine)
            {
                hdr.Gmv[i][4] = 2 * gb.GetBitsSubexp(refGmv[4] >> 1, 12);
                hdr.Gmv[i][5] = (1 << 16) + 2 * gb.GetBitsSubexp((refGmv[5] - (1 << 16)) >> 1, 12);
            }
            else
            {
                hdr.Gmv[i][4] = -hdr.Gmv[i][3];
                hdr.Gmv[i][5] = hdr.Gmv[i][2];
            }

            hdr.Gmv[i][0] = gb.GetBitsSubexp(refGmv[0] >> shift, (uint)bits) * (1 << shift);
            hdr.Gmv[i][1] = gb.GetBitsSubexp(refGmv[1] >> shift, (uint)bits) * (1 << shift);
        }
    }

    // ========================================================================
    // Film grain parsing
    // ========================================================================

    private static unsafe void ParseFilmGrain(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames,
        ref Av1GetBits gb)
    {
        hdr.FilmGrainPresent = gb.GetBool();
        if (!hdr.FilmGrainPresent) return;

        uint seed = gb.GetBits(16);
        hdr.FilmGrainUpdate = hdr.FrameType != Av1FrameType.Inter || gb.GetBool();

        if (!hdr.FilmGrainUpdate)
        {
            // Copy film grain data from a reference frame
            // (deferred to when we have full reference frame header storage)
            hdr.FilmGrain.Seed = seed;
            return;
        }

        ref var fgd = ref hdr.FilmGrain;
        fgd.Seed = seed;

        fgd.NumYPoints = (int)gb.GetBits(4);
        for (int i = 0; i < fgd.NumYPoints; i++)
        {
            fgd.YPoints[i * 2] = (byte)gb.GetBits(8);
            fgd.YPoints[i * 2 + 1] = (byte)gb.GetBits(8);
        }

        if (!seqHdr.Monochrome)
            fgd.ChromaScalingFromLuma = gb.GetBool() ? 1 : 0;

        if (seqHdr.Monochrome || fgd.ChromaScalingFromLuma != 0 ||
            (seqHdr.SubsamplingX == 1 && seqHdr.SubsamplingY == 1 && fgd.NumYPoints == 0))
        {
            fgd.NumUvPoints0 = fgd.NumUvPoints1 = 0;
        }
        else
        {
            for (int pl = 0; pl < 2; pl++)
            {
                int numUvPoints = (int)gb.GetBits(4);
                if (pl == 0) fgd.NumUvPoints0 = numUvPoints;
                else fgd.NumUvPoints1 = numUvPoints;
                for (int i = 0; i < numUvPoints; i++)
                {
                    fgd.UvPoints[(pl * 10 + i) * 2] = (byte)gb.GetBits(8);
                    fgd.UvPoints[(pl * 10 + i) * 2 + 1] = (byte)gb.GetBits(8);
                }
            }
        }

        fgd.ScalingShift = (int)(gb.GetBits(2) + 8);
        fgd.ArCoeffLag = (int)gb.GetBits(2);
        int numYPos = 2 * fgd.ArCoeffLag * (fgd.ArCoeffLag + 1);

        if (fgd.NumYPoints != 0)
            for (int i = 0; i < numYPos; i++)
                fgd.ArCoeffsY[i] = (sbyte)((int)gb.GetBits(8) - 128);

        for (int pl = 0; pl < 2; pl++)
        {
            int numUvPts = pl == 0 ? fgd.NumUvPoints0 : fgd.NumUvPoints1;
            if (numUvPts != 0 || fgd.ChromaScalingFromLuma != 0)
            {
                int numUvPos = numYPos + (fgd.NumYPoints != 0 ? 1 : 0);
                for (int i = 0; i < numUvPos; i++)
                    fgd.ArCoeffsUv[pl * 28 + i] = (sbyte)((int)gb.GetBits(8) - 128);
                if (fgd.NumYPoints == 0)
                    fgd.ArCoeffsUv[pl * 28 + numUvPos] = 0;
            }
        }

        fgd.ArCoeffShift = (long)(gb.GetBits(2) + 6);
        fgd.GrainScaleShift = (int)gb.GetBits(2);

        for (int pl = 0; pl < 2; pl++)
        {
            int numUvPts = pl == 0 ? fgd.NumUvPoints0 : fgd.NumUvPoints1;
            if (numUvPts != 0)
            {
                int uvMult = (int)gb.GetBits(8) - 128;
                int uvLumaMult = (int)gb.GetBits(8) - 128;
                int uvOffset = (int)gb.GetBits(9) - 256;
                if (pl == 0) { fgd.UvMult0 = uvMult; fgd.UvLumaMult0 = uvLumaMult; fgd.UvOffset0 = uvOffset; }
                else { fgd.UvMult1 = uvMult; fgd.UvLumaMult1 = uvLumaMult; fgd.UvOffset1 = uvOffset; }
            }
        }

        fgd.OverlapFlag = gb.GetBool() ? 1 : 0;
        fgd.ClipToRestrictedRange = gb.GetBool() ? 1 : 0;
    }

    // ========================================================================
    // Helper: POC difference (order hint distance)
    // ========================================================================

    /// <summary>
    /// Compute the signed distance between two order hints.
    /// Ported from dav1d get_poc_diff() in env.h.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPocDiff(int orderHintNBits, int poc0, int poc1)
    {
        if (orderHintNBits == 0) return 0;
        int mask = 1 << (orderHintNBits - 1);
        int diff = poc0 - poc1;
        return (diff & (mask - 1)) - (diff & mask);
    }

    // ========================================================================
    // Helper: derive short ref idx signaling
    // ========================================================================

    private static void DeriveShortRefIdxs(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames)
    {
        // This implements the reference frame short signaling derivation
        // from AV1 spec section 7.8 and dav1d obu.c.
        // Uses frame offsets to assign the remaining 5 ref indices (1,2,4,5,6).

        Span<int> frameOffset = stackalloc int[9]; // index [0..7] = refs, [-1] accessed via [8]
        int earliestRef = -1;
        int earliestOffset = int.MaxValue;

        for (int i = 0; i < 8; i++)
        {
            var refFrame = refFrames[i];
            if (!refFrame.Valid) return; // error case, but we'll handle gracefully
            int diff = GetPocDiff(seqHdr.OrderHintNBits, refFrame.OrderHint, hdr.FrameOffset);
            frameOffset[i] = diff;
            if (diff < earliestOffset)
            {
                earliestOffset = diff;
                earliestRef = i;
            }
        }

        frameOffset[hdr.GetRefIdx(0)] = int.MinValue;
        frameOffset[hdr.GetRefIdx(3)] = int.MinValue;

        // refidx[6] = latest forward ref
        int refIdx = -1;
        int latestOffset = 0;
        for (int i = 0; i < 8; i++)
        {
            if (frameOffset[i] >= latestOffset)
            {
                latestOffset = frameOffset[i];
                refIdx = i;
            }
        }
        if (refIdx >= 0) frameOffset[refIdx] = int.MinValue;
        hdr.SetRefIdx(6, (sbyte)refIdx);

        // refidx[4], refidx[5] = two earliest backward refs
        for (int k = 4; k < 6; k++)
        {
            uint bestOffset = 0xFF; // unsigned compare for negatives
            refIdx = -1;
            for (int j = 0; j < 8; j++)
            {
                uint hint = (uint)frameOffset[j];
                if (hint < bestOffset)
                {
                    bestOffset = hint;
                    refIdx = j;
                }
            }
            if (refIdx >= 0) frameOffset[refIdx] = int.MinValue;
            hdr.SetRefIdx(k, (sbyte)refIdx);
        }

        // Fill remaining unset indices (1, 2)
        for (int k = 1; k < 7; k++)
        {
            if (hdr.GetRefIdx(k) >= 0) continue;
            uint bestLatest = unchecked((uint)(~0xFF));
            refIdx = -1;
            for (int j = 0; j < 8; j++)
            {
                uint hint = (uint)frameOffset[j];
                if (hint >= bestLatest)
                {
                    bestLatest = hint;
                    refIdx = j;
                }
            }
            if (refIdx >= 0) frameOffset[refIdx] = int.MinValue;
            hdr.SetRefIdx(k, refIdx >= 0 ? (sbyte)refIdx : (sbyte)earliestRef);
        }
    }

    // ========================================================================
    // Helper: derive skip mode references
    // ========================================================================

    private static void DeriveSkipMode(
        Av1DecoderFrameHeader hdr,
        Av1DecoderSequenceHeader seqHdr,
        Av1ReferenceFrame[] refFrames)
    {
        int poc = hdr.FrameOffset;
        int offBefore = -1, offAfter = -1;
        int offBeforeIdx = 0, offAfterIdx = 0;

        for (int i = 0; i < 7; i++)
        {
            var refIdx = hdr.GetRefIdx(i);
            if (refIdx < 0 || refIdx >= 8 || !refFrames[refIdx].Valid) continue;
            int refPoc = refFrames[refIdx].OrderHint;

            int diff = GetPocDiff(seqHdr.OrderHintNBits, refPoc, poc);
            if (diff > 0)
            {
                if (offAfter < 0 || GetPocDiff(seqHdr.OrderHintNBits, offAfter, refPoc) > 0)
                {
                    offAfter = refPoc;
                    offAfterIdx = i;
                }
            }
            else if (diff < 0)
            {
                if (offBefore < 0 || GetPocDiff(seqHdr.OrderHintNBits, refPoc, offBefore) > 0)
                {
                    offBefore = refPoc;
                    offBeforeIdx = i;
                }
            }
        }

        if ((offBefore | offAfter) >= 0)
        {
            hdr.SkipModeRef0 = (sbyte)Math.Min(offBeforeIdx, offAfterIdx);
            hdr.SkipModeRef1 = (sbyte)Math.Max(offBeforeIdx, offAfterIdx);
            hdr.SkipModeAllowed = true;
        }
        else if (offBefore >= 0)
        {
            int offBefore2 = -1;
            int offBefore2Idx = 0;
            for (int i = 0; i < 7; i++)
            {
                var refIdx = hdr.GetRefIdx(i);
                if (refIdx < 0 || refIdx >= 8 || !refFrames[refIdx].Valid) continue;
                int refPoc = refFrames[refIdx].OrderHint;
                if (GetPocDiff(seqHdr.OrderHintNBits, refPoc, offBefore) < 0)
                {
                    if (offBefore2 < 0 || GetPocDiff(seqHdr.OrderHintNBits, refPoc, offBefore2) > 0)
                    {
                        offBefore2 = refPoc;
                        offBefore2Idx = i;
                    }
                }
            }

            if (offBefore2 >= 0)
            {
                hdr.SkipModeRef0 = (sbyte)Math.Min(offBeforeIdx, offBefore2Idx);
                hdr.SkipModeRef1 = (sbyte)Math.Max(offBeforeIdx, offBefore2Idx);
                hdr.SkipModeAllowed = true;
            }
        }
    }
}
