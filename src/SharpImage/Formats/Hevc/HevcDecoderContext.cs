// HEVC Decoder Context - manages VPS/SPS/PPS state
// Similar to H264DecoderContext but with VPS support

using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// Manages the state of an HEVC decoder, including active parameter sets.
/// </summary>
public sealed class HevcDecoderContext
{
    private readonly Dictionary<int, HevcVideoParameterSet> vpsMap = new();
    private readonly Dictionary<int, HevcSequenceParameterSet> spsMap = new();
    private readonly Dictionary<int, HevcPictureParameterSet> ppsMap = new();
    private HevcSliceSegmentHeader? lastIndependentSliceHeader;

    /// <summary>Gets the currently active VPS.</summary>
    public HevcVideoParameterSet? ActiveVps { get; private set; }

    /// <summary>Gets or sets the currently active SPS. Internal set for multi-layer restore.</summary>
    public HevcSequenceParameterSet? ActiveSps { get; internal set; }

    /// <summary>Gets or sets the currently active PPS. Internal set for multi-layer restore.</summary>
    public HevcPictureParameterSet? ActivePps { get; internal set; }

    /// <summary>Gets the configuration version, incremented when parameter sets change.</summary>
    public int ConfigurationVersion { get; private set; }

    // Derived properties from active SPS

    /// <summary>Display width in pixels.</summary>
    public int Width => ActiveSps?.DisplayWidth ?? 0;

    /// <summary>Display height in pixels.</summary>
    public int Height => ActiveSps?.DisplayHeight ?? 0;

    /// <summary>Coded width in pixels (before cropping).</summary>
    public int CodedWidth => ActiveSps?.PictureWidthInLumaSamples ?? 0;

    /// <summary>Coded height in pixels (before cropping).</summary>
    public int CodedHeight => ActiveSps?.PictureHeightInLumaSamples ?? 0;

    /// <summary>CTB size in luma samples (16, 32, or 64).</summary>
    public int CtbSizeY => ActiveSps?.CtbSizeY ?? 64;

    /// <summary>Picture width in CTBs.</summary>
    public int PicWidthInCtbsY => ActiveSps?.PicWidthInCtbsY ?? 0;

    /// <summary>Picture height in CTBs.</summary>
    public int PicHeightInCtbsY => ActiveSps?.PicHeightInCtbsY ?? 0;

    /// <summary>HEVC profile.</summary>
    public HevcProfile Profile => ActiveSps?.ProfileTierLevel.GeneralProfileIdc ?? HevcProfile.None;

    /// <summary>HEVC level.</summary>
    public HevcLevel Level => ActiveSps?.ProfileTierLevel.GeneralLevelIdc ?? HevcLevel.Level1;

    /// <summary>Chroma format.</summary>
    public HevcChromaFormat ChromaFormat => ActiveSps?.ChromaFormatIdc ?? HevcChromaFormat.Chroma420;

    /// <summary>Luma bit depth.</summary>
    public int BitDepthLuma => ActiveSps?.BitDepthLuma ?? 8;

    /// <summary>Chroma bit depth.</summary>
    public int BitDepthChroma => ActiveSps?.BitDepthChroma ?? 8;

    /// <summary>Frame rate from VUI or VPS timing info.</summary>
    public double FrameRate => ActiveSps?.FrameRate ?? ActiveVps?.FrameRate ?? 0;

    /// <summary>Maximum number of reference frames.</summary>
    public int MaxRefFrames => ActiveSps?.MaxDecPicBufferingMinus1[ActiveSps.MaxSubLayersMinus1] + 1 ?? 1;

    /// <summary>
    /// Processes a NAL unit and updates decoder state.
    /// </summary>
    /// <returns>True if this NAL unit changed the active parameter sets.</returns>
    public bool ProcessNalUnit(HevcNalUnit nalUnit)
    {
        return nalUnit.Type switch
        {
            HevcNalUnitType.VideoParameterSet => ProcessVps(nalUnit.Payload.Span),
            HevcNalUnitType.SequenceParameterSet => ProcessSps(nalUnit.Payload.Span, nalUnit.LayerId),
            HevcNalUnitType.PictureParameterSet => ProcessPps(nalUnit.Payload.Span),
            _ when nalUnit.IsVcl => ActivatePpsFromSlice(nalUnit.Payload.Span, nalUnit.Type),
            _ => false
        };
    }

    /// <summary>
    /// Parses and stores a VPS.
    /// </summary>
    private bool ProcessVps(ReadOnlySpan<byte> payload)
    {
        byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(payload);
        var vps = HevcParameterSetParser.ParseVps(rbsp);
        if (vps == null)
            return false;

        int vpsId = vps.VideoParameterSetId;
        bool changed = !vpsMap.ContainsKey(vpsId);

        vpsMap[vpsId] = vps;

        if (changed)
            ConfigurationVersion++;

        return changed;
    }

    /// <summary>
    /// Parses and stores an SPS.
    /// </summary>
    private bool ProcessSps(ReadOnlySpan<byte> payload, byte nuhLayerId)
    {
        byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(payload);
        if (DiagParseLog != null)
        {
            var hexBytes = string.Join(" ", rbsp.AsSpan().Slice(0, Math.Min(20, rbsp.Length)).ToArray().Select(b => b.ToString("X2")));
            DiagParseLog.Add($"ProcessSps: payloadLen={payload.Length}, rbspLen={rbsp.Length}, first20=[{hexBytes}]");
        }
        
        // Peek VPS ID from the first 4 bits to look up VPS for multi-layer SPS
        HevcVideoParameterSet? vps = null;
        if (rbsp.Length >= 1)
        {
            int vpsId = (rbsp[0] >> 4) & 0x0F;
            vpsMap.TryGetValue(vpsId, out vps);
        }
        
        var sps = HevcParameterSetParser.ParseSps(rbsp, nuhLayerId, vps);
        if (sps == null)
        {
            DiagParseLog?.Add($"ProcessSps: ParseSps returned null");
            return false;
        }

        int spsId = sps.SequenceParameterSetId;
        DiagParseLog?.Add($"ProcessSps: spsId={spsId}, vpsId={sps.VideoParameterSetId}, maxSubLayers={sps.MaxSubLayersMinus1}, chromaFmt={sps.ChromaFormatIdc}");
        bool changed = !spsMap.ContainsKey(spsId);

        spsMap[spsId] = sps;

        if (changed)
            ConfigurationVersion++;

        return changed;
    }

    /// <summary>
    /// Parses and stores a PPS.
    /// </summary>
    private bool ProcessPps(ReadOnlySpan<byte> payload)
    {
        byte[] rbsp = HevcNalParser.RemoveEmulationPreventionBytes(payload);
        
        // Pre-parse to get SPS ID for chroma format (needed for scaling list 4:4:4 copy)
        var tempReader = new BitstreamReader(rbsp);
        tempReader.ReadExpGolombUnsigned(); // pps_pic_parameter_set_id (skip, needed to reach sps_id)
        int spsId = (int)tempReader.ReadExpGolombUnsigned(); // pps_seq_parameter_set_id
        int chromaFmt = spsMap.TryGetValue(spsId, out var referencedSps)
            ? (int)referencedSps.ChromaFormatIdc
            : 1;
        var profileIdc = referencedSps?.ProfileTierLevel.GeneralProfileIdc ?? HevcProfile.None;
        
        var pps = HevcParameterSetParser.ParsePps(rbsp, chromaFmt, profileIdc);
        if (pps == null)
            return false;

        int ppsId = pps.PictureParameterSetId;
        bool changed = !ppsMap.ContainsKey(ppsId);

        ppsMap[ppsId] = pps;

        if (changed)
            ConfigurationVersion++;

        return changed;
    }

    /// <summary>
    /// Activates VPS/SPS/PPS chain from a slice NAL unit.
    /// </summary>
    private bool ActivatePpsFromSlice(ReadOnlySpan<byte> payload, HevcNalUnitType nalType)
    {
        byte? ppsId = HevcSliceHeaderParser.PeekSlicePpsId(payload, nalType);
        if (ppsId == null)
            return false;

        return ActivatePps(ppsId.Value);
    }

    /// <summary>
    /// Activates a PPS by ID, which also activates its SPS and VPS.
    /// </summary>
    public bool ActivatePps(int ppsId)
    {
        if (!ppsMap.TryGetValue(ppsId, out var pps))
            return false;

        if (!spsMap.TryGetValue(pps.SequenceParameterSetId, out var sps))
            return false;

        HevcVideoParameterSet? vps = null;
        vpsMap.TryGetValue(sps.VideoParameterSetId, out vps);

        bool changed = ActivePps != pps || ActiveSps != sps || ActiveVps != vps;

        ActivePps = pps;
        ActiveSps = sps;
        ActiveVps = vps;

        // Compute tile-derived arrays when PPS is activated (needs SPS dimensions)
        if (pps.CtbAddrRsToTs == null || changed)
            pps.ComputeTileDerivedArrays(sps.PicWidthInCtbsY, sps.PicHeightInCtbsY);

        return changed;
    }

    /// <summary>
    /// Gets a VPS by ID.
    /// </summary>
    public HevcVideoParameterSet? GetVps(int vpsId)
    {
        vpsMap.TryGetValue(vpsId, out var vps);
        return vps;
    }

    /// <summary>
    /// Gets an SPS by ID.
    /// </summary>
    public HevcSequenceParameterSet? GetSps(int spsId)
    {
        spsMap.TryGetValue(spsId, out var sps);
        return sps;
    }

    /// <summary>
    /// Gets a PPS by ID.
    /// </summary>
    public HevcPictureParameterSet? GetPps(int ppsId)
    {
        ppsMap.TryGetValue(ppsId, out var pps);
        return pps;
    }

    /// <summary>
    /// Parses a slice segment header using the current active parameter sets.
    /// </summary>
    public HevcSliceSegmentHeader? ParseSliceHeader(HevcNalUnit nalUnit)
    {
        if (!nalUnit.IsVcl)
            return null;

        // Try to peek PPS ID and activate it
        byte? ppsId = HevcSliceHeaderParser.PeekSlicePpsId(nalUnit.Payload.Span, nalUnit.Type);
        if (ppsId.HasValue)
        {
            if (!ActivatePps(ppsId.Value))
            {
                // Debug: log why activation failed
                bool hasPps = ppsMap.ContainsKey(ppsId.Value);
                int spsId = hasPps ? ppsMap[ppsId.Value].SequenceParameterSetId : -1;
                bool hasSps = spsId >= 0 && spsMap.ContainsKey(spsId);
                DiagParseLog?.Add($"ActivatePps({ppsId.Value}) failed: hasPps={hasPps}, spsId={spsId}, hasSps={hasSps}, ppsMapKeys=[{string.Join(",", ppsMap.Keys)}], spsMapKeys=[{string.Join(",", spsMap.Keys)}]");
            }
        }
        else
        {
            DiagParseLog?.Add($"PeekSlicePpsId returned null for nalType={nalUnit.Type}, payloadLen={nalUnit.Payload.Length}");
        }

        if (ActiveSps == null || ActivePps == null)
            return null;

        var header = HevcSliceHeaderParser.ParseSliceSegmentHeader(
            nalUnit.Payload.Span,
            nalUnit.Type,
            ActivePps,
            ActiveSps,
            lastIndependentSliceHeader,
            ActiveVps,
            nalUnit.LayerId,
            DiagParseLog);

        // Track last independent slice for dependent slice inheritance
        if (header != null && !header.DependentSliceSegmentFlag)
            lastIndependentSliceHeader = header;

        return header;
    }
    
    /// <summary>Diagnostic log for slice header parsing issues.</summary>
    public List<string>? DiagParseLog;

    /// <summary>
    /// Resets the decoder context, clearing all parameter sets.
    /// </summary>
    public void Reset()
    {
        vpsMap.Clear();
        spsMap.Clear();
        ppsMap.Clear();
        ActiveVps = null;
        ActiveSps = null;
        ActivePps = null;
        ConfigurationVersion = 0;
        lastIndependentSliceHeader = null;
    }

    /// <summary>
    /// Returns true if all necessary parameter sets are available for decoding.
    /// </summary>
    public bool IsReady => ActiveSps != null && ActivePps != null;

    /// <summary>
    /// Gets a summary string of the current configuration.
    /// </summary>
    public string GetConfigurationSummary()
    {
        if (ActiveSps == null)
            return "No active configuration";

        return $"{Width}x{Height} @ {FrameRate:F2}fps, " +
               $"{HevcNalUtilities.GetProfileName(Profile)} Profile, " +
               $"Level {HevcNalUtilities.GetLevelString(Level)}, " +
               $"{BitDepthLuma}-bit {ChromaFormat}";
    }
}
