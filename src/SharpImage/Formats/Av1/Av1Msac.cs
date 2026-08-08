// AV1 Multi-Symbol Arithmetic Coder (MSAC)
// Ported from dav1d: src/msac.c, src/msac.h
// This is the entropy coder for AV1 tile data — the critical decoding path.
// Uses CDF (Cumulative Distribution Function) tables with adaptive updates.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 multi-symbol arithmetic coder.
/// Decodes entropy-coded symbols from tile data using CDF probability tables.
/// </summary>
/// <remarks>
/// Ported from dav1d msac.c. The coder maintains a 64-bit difference value
/// (dif) and a 16-bit range (rng). Symbols are decoded by partitioning the
/// range according to CDF probabilities, then normalizing.
///
/// CDF tables are Q15 inverse CDFs with an adaptation counter stored at
/// index [n_symbols]. The adaptation rate starts fast and slows as more
/// symbols are coded.
/// </remarks>
public ref struct Av1Msac
{
    private const int ProbShift = 6;
    private const int MinProb = 4;
    private const int WinSize = 64; // sizeof(ulong) * 8

    /// <summary>Debug label for tracking which code path called DecodeBoolAdapt.</summary>
    public static string? DbgLabel;

    /// <summary>Label attached to trace entries for code path identification.</summary>
    public static string? TraceLabel;

    /// <summary>Optional callback for DLL comparison at each DecodeBoolEqui call.
    /// Takes (rng, difLo, difHi, cnt, dataSpan, dataOffset, dataEnd) and returns DLL's result (0 or 1).</summary>
    public static Func<uint, uint, uint, int, ReadOnlySpan<byte>, int, int, int>? DllCompareFunc;

    /// <summary>Optional callback for DLL comparison at each DecodeBool call.
    /// Takes (rng, difLo, difHi, cnt, f, dataSpan, dataOffset, dataEnd) and returns DLL's result (0 or 1).</summary>
    public static Func<uint, uint, uint, int, uint, ReadOnlySpan<byte>, int, int, int>? DllCompareBoolFunc;

    /// <summary>Optional callback for DLL comparison at each DecodeSymbolAdapt call.
    /// Takes (rng, difLo, difHi, cnt, cdf, nSym, dataSpan, dataOffset, dataEnd) and returns DLL's result.</summary>
    public static Func<uint, uint, uint, int, ushort[], int, ReadOnlySpan<byte>, int, int, int>? DllCompareSymFunc;

    /// <summary>Optional callback for DLL comparison at each DecodeBools call.
    /// Takes (rng, difLo, difHi, cnt, n, dataSpan, dataOffset, dataEnd) and returns DLL's result uint.</summary>
    public static Func<uint, uint, uint, int, int, ReadOnlySpan<byte>, int, int, uint>? DllCompareBoolsFunc;

    /// <summary>Optional callback for full palette index decode comparison.
    /// Takes (rng, difLo, difHi, cnt, palSize, w4, h4, bw4, bh4, isLuma, colorMapCdf, dataSpan, dataOffset, dataEnd, ourIndices)
    /// Returns DLL's index array (or null to skip).</summary>
    public static Func<uint, uint, uint, int, int, int, int, int, int, bool, ushort[], ReadOnlySpan<byte>, int, int, byte[], byte[]>? DllComparePalIdxFunc;

    // ── Dav1d-format MSAC trace output ──
    // Emits every decode op in dav1d's trace format for diff comparison.
    private static int _davTraceSeq;
    private static System.IO.TextWriter? _davTraceWriter;
    private int _davTraceFrame;  // per-instance frame tag (set via SetDav1dTraceFrame)
    private bool _davTraceEnabled;

    public static void StartDav1dTrace(string filePath)
    {
        _davTraceSeq = 0;
        _davTraceWriter = System.IO.File.CreateText(filePath);
        _davTraceWriter.Flush();
    }

    /// <summary>Set the per-instance frame number for dav1d trace output.</summary>
    public void SetDav1dTraceFrame(int frame) => _davTraceFrame = frame;

    /// <summary>Enable trace for this MSAC instance.</summary>
    public void EnableDav1dTrace() => _davTraceEnabled = true;

    public static void StopDav1dTrace()
    {
        _davTraceWriter?.Close();
        _davTraceWriter = null;
    }

    private void EmitDav1dBE(uint bit)
    {
        if (!_davTraceEnabled || _davTraceWriter == null) return;
        int seq = _davTraceSeq++;
        _davTraceWriter.WriteLine(
            $"{seq} F{_davTraceFrame} BE pre_cnt={cnt} pre_dif={dif} pre_rng={rng} bit={bit} # {DbgLabel}");
        _davTraceWriter.Flush();
    }

    private void EmitDav1dBA(Span<ushort> cdf, uint bit)
    {
        if (!_davTraceEnabled || _davTraceWriter == null) return;
        int seq = _davTraceSeq++;
        _davTraceWriter.WriteLine(
            $"{seq} F{_davTraceFrame} BA cdf0={cdf[0]} cdf1={cdf[1]} pre_cnt={cnt} pre_dif={dif} pre_rng={rng} bit={bit} # {DbgLabel}");
        _davTraceWriter.Flush();
    }

    private void EmitDav1dSA(Span<ushort> cdf, int nSymbols, uint val)
    {
        if (!_davTraceEnabled || _davTraceWriter == null) return;
        int seq = _davTraceSeq++;
        var sb = new System.Text.StringBuilder();
        sb.Append($"{seq} F{_davTraceFrame} SA nsym={nSymbols} val={val} pre_cnt={cnt} pre_dif={dif} pre_rng={rng}");
        int maxCdf = nSymbols < cdf.Length ? nSymbols : cdf.Length - 1;
        for (int t = 0; t <= maxCdf; t++)
            sb.Append($" cdf[{t}]={cdf[t]}");
        sb.Append($" # {DbgLabel}");
        _davTraceWriter.WriteLine(sb.ToString());
        _davTraceWriter.Flush();
    }

    // MSAC tracing
    private static int _traceCounter;
    private static System.IO.TextWriter? _traceWriter;
    private bool _traceEnabled;

    /// <summary>Enable MSAC tracing to a file. Call before decoding starts.</summary>
    public static void StartTrace(string filePath)
    {
        _traceCounter = 0;
        _traceWriter = System.IO.File.CreateText(filePath);
        _traceWriter.Flush();
    }

    /// <summary>Stop MSAC tracing.</summary>
    public static void StopTrace()
    {
        _traceWriter?.Close();
        _traceWriter = null;
    }

    private void TraceDecode(string op, int result, string detail = "")
    {
        if (!_traceEnabled || _traceWriter == null) return;
        _traceCounter++;

        // Verify traced value against actual state for the divergent case
        if (cnt == 3 && rng == 0xE108 && (dif >> 16) == (0xCD59400000000000UL >> 16))
        {
            AvDbg.W($"[DBG-TRACE] counter={_traceCounter} op={op} result={result} detail={detail} _inDecodeBools={_inDecodeBools} dif=0x{dif:X16} rng=0x{rng:X4} cnt={cnt}");
        }

        string label = TraceLabel ?? "";
        _traceWriter.WriteLine($"{_traceCounter,-6} {op,-18} result={result,-4} dif={dif:X16} rng={rng:X4} cnt={cnt,4} pos={position,4} {label,-10} {detail}");
        _traceWriter.Flush();
    }

    private ReadOnlySpan<byte> data;
    private int position;
    private int dataEnd;
    private ulong dif;
    private uint rng;
    private int cnt;
    private bool allowUpdateCdf;
    private int _boolAdaptCounter;
    private bool _inDecodeBools;
    private int _debugCount;
    private static int _debugStep;

    /// <summary>Whether CDF table updates are enabled.</summary>
    public readonly bool AllowUpdateCdf => allowUpdateCdf;

    /// <summary>Current count value (for checking overread: cnt &lt;= -15 means error).</summary>
    public readonly int Cnt => cnt;

    // Debug accessors
    public readonly ulong DebugDif => dif;
    public readonly uint DebugRng => rng;
    public readonly int DebugPos => position;
    public readonly ReadOnlySpan<byte> DebugData => data;
    public readonly int DebugDataEnd => dataEnd;

    /// <summary>Whether trace output is enabled for this instance.</summary>
    public readonly bool TraceEnabled => _traceEnabled;

    /// <summary>Log a trace operation from external call sites.</summary>
    public void TraceOp(string op, int result, string detail = "")
    {
        if (_traceEnabled) TraceDecode(op, result, detail);
    }

    /// <summary>
    /// Persistable MSAC state for saving/restoring across method boundaries
    /// where the ref struct cannot be stored.
    /// </summary>
    public struct SavedState
    {
        public int Position;
        public int DataEnd;
        public ulong Dif;
        public uint Rng;
        public int Cnt;
        public bool AllowUpdateCdf;
        public bool TraceEnabled;
    }

    /// <summary>Save current MSAC state for later restoration.</summary>
    public readonly SavedState Save() => new()
    {
        Position = position,
        DataEnd = dataEnd,
        Dif = dif,
        Rng = rng,
        Cnt = cnt,
        AllowUpdateCdf = allowUpdateCdf,
        TraceEnabled = _traceEnabled,
    };

    /// <summary>
    /// Restore a previously saved MSAC state. The data span must be the same
    /// buffer that was used when the state was saved.
    /// </summary>
    public Av1Msac(ReadOnlySpan<byte> data, SavedState state)
    {
        this.data = data;
        position = state.Position;
        dataEnd = state.DataEnd;
        dif = state.Dif;
        rng = state.Rng;
        cnt = state.Cnt;
        allowUpdateCdf = state.AllowUpdateCdf;
        _traceEnabled = state.TraceEnabled;
    }

    /// <summary>
    /// Initializes the MSAC state for decoding.
    /// </summary>
    /// <param name="data">Tile data to decode.</param>
    /// <param name="disableCdfUpdate">If true, CDF tables are not updated during decoding.</param>
    public Av1Msac(ReadOnlySpan<byte> data, bool disableCdfUpdate = false, bool traceEnabled = false)
    {
        this.data = data;
        position = 0;
        dataEnd = data.Length;
        dif = 0;
        rng = 0x8000;
        cnt = -15;
        allowUpdateCdf = !disableCdfUpdate;
        _traceEnabled = traceEnabled;
        Refill();
        if (_traceEnabled)
            TraceDecode("INIT", 0, $"len={data.Length} dif={dif:X16} rng={rng:X4} cnt={cnt}");
    }

    /// <summary>
    /// Decodes a boolean with equal probability (50/50).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBoolEqui()
    {
        uint r = rng;
        ulong d = dif;
        uint origRng = r;
        ulong origDif = d;
        int origCnt = cnt;
        uint v = ((r >> 8) << 7) + MinProb;
        ulong vw = (ulong)v << (WinSize - 16);
        uint ret = d >= vw ? 1u : 0u;
        d -= ret * vw;
        v += ret * (r - 2 * v);
        Normalize(d, v);
        uint result = ret == 0 ? 1u : 0u;
        EmitDav1dBE(result);

        // Targeted diagnostic for the divergent state
        if (origCnt == 3 && origRng == 0xE108 && (origDif >> 16) == (0xCD59400000000000UL >> 16))
        {
            AvDbg.W($"[DBG-DECEQ] targeted hit!");
            AvDbg.W($"[DBG-DECEQ]   pre: dif=0x{origDif:X16} rng=0x{origRng:X4} cnt={origCnt}");
            AvDbg.W($"[DBG-DECEQ]   v=(({origRng}>>8)<<7)+4 = {v} (0x{v:X})");
            AvDbg.W($"[DBG-DECEQ]   vw={v}ull<<{WinSize-16} = 0x{vw:X16}");
            AvDbg.W($"[DBG-DECEQ]   dif>=vw? 0x{origDif:X16} >= 0x{vw:X16} = {origDif >= vw}");
            AvDbg.W($"[DBG-DECEQ]   ret={ret}");
            AvDbg.W($"[DBG-DECEQ]   d -= ret*vw = 0x{origDif:X16} - 0x{ret*vw:X16} = 0x{d:X16}");
            AvDbg.W($"[DBG-DECEQ]   v += ret*(r-2*v) = {v + ret * (r - 2 * v)} (0x{v + ret * (r - 2 * v):X})");
            int clzRng = BitOperations.LeadingZeroCount(rng);
            AvDbg.W($"[DBG-DECEQ]   post-Normalize: dif=0x{dif:X16} rng=0x{rng:X4} cnt={cnt} shiftTmp: clz({origRng})={clzRng}");
            AvDbg.W($"[DBG-DECEQ]   result=ret==0?1:0 = {ret}==0?1:0 = {result}");
        }
        
        // DLL comparison if callback registered
        var cmp = DllCompareFunc;
        if (cmp != null)
        {
            int dllResult = cmp(r, (uint)origDif, (uint)(origDif >> 32), origCnt, data, position, dataEnd);
            if ((int)result != dllResult)
            {
                AvDbg.W($"[BOOLEQUI-DLL-MISMATCH] our={result} dll={dllResult} pre: difHi={(uint)(origDif>>32):X8} difLo={(uint)origDif:X8} rng={r:X4} cnt={origCnt}");
            }
        }
        
        // NOTE: _inDecodeBools prevents duplicate trace when called from DecodeBools loop
        if (_traceEnabled && !_inDecodeBools)
            TraceDecode("DecodeBoolEqui", (int)result, "direct");
        return result;
    }

    /// <summary>
    /// Decodes a boolean with the given probability (f is Q15, 0..32768).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBool(uint f)
    {
        uint r = rng;
        ulong d = dif;
        ulong origDif = d;
        int origCnt = cnt;
        uint v = ((r >> 8) * (f >> ProbShift) >> (7 - ProbShift)) + MinProb;
        ulong vw = (ulong)v << (WinSize - 16);
        uint ret = d >= vw ? 1u : 0u;
        
        // Trace the ref r2 DecodeBool (f=7995)
        bool dbgRefr2 = (f == 7995);
        if (dbgRefr2) {
            AvDbg.W($"[DB-BOOL] ref-r2 f={f} rng={rng:X4}({rng}) r={r>>8} fps={(f>>ProbShift)} v={v} c={d>>48} ret={ret} preCnt={origCnt}");
        }
        
        d -= ret * vw;
        v += ret * (r - 2 * v);
        Normalize(d, v);
        
        if (dbgRefr2 && ret == 0) {
            AvDbg.W($"[DB-BOOL] POST ret=0 newRng(v)={v} normalized={rng:X4}({rng}) cnt={cnt}");
        }
        
        uint result = ret == 0 ? 1u : 0u;

        var cmpBool = DllCompareBoolFunc;
        if (cmpBool != null)
        {
            int dllResult = cmpBool(r, (uint)origDif, (uint)(origDif >> 32), origCnt, f, data, position, dataEnd);
            if ((int)result != dllResult)
            {
                AvDbg.W($"[DECODEBOOL-DLL-MISMATCH] our={result} dll={dllResult} pre: difHi={(uint)(origDif>>32):X8} difLo={(uint)origDif:X8} rng={r:X4} cnt={origCnt} f={f}");
            }
        }
        if (_traceEnabled) TraceDecode("DecodeBool", (int)result, $"f={f}");
        return result;
    }

    /// <summary>
    /// Decodes a boolean with adaptive CDF update.
    /// cdf[0] is the probability, cdf[1] is the adaptation counter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBoolAdapt(Span<ushort> cdf)
    {
        ushort preCdf0 = cdf[0];
        ushort preCdf1 = cdf[1];
        uint bit = DecodeBool(preCdf0);

        if (allowUpdateCdf)
        {
            uint count = cdf[1];
            int rate = 4 + (int)(count >> 4);
            if (bit != 0)
                cdf[0] += (ushort)((32768 - cdf[0]) >> rate);
            else
                cdf[0] -= (ushort)(cdf[0] >> rate);
            cdf[1] = (ushort)(count + (count < 32 ? 1u : 0u));
        }

        EmitDav1dBA(cdf, bit);

        // Diagnostic: first ref decision for frame 1 (cdf[0] ~15795)
        if (preCdf0 == 15795 || preCdf0 == 27871)
        {
            AvDbg.W($"[BA-REF-DIAG] cdf0={preCdf0} cdf1={preCdf1} bit={bit} rate={(preCdf1>0 ? 4+(preCdf1>>4) : 4)} postCdf0={cdf[0]} rng={rng:X4} dif_lo={(uint)dif:X8}");
        }

        if (_traceEnabled)
        {
            if (_boolAdaptCounter < 50)
                AvDbg.W($"[BA-TRACE] #{_boolAdaptCounter} pre: dif=0x{dif:X16} rng=0x{rng:X4} cnt={cnt}  bit={bit}  cdf0={preCdf0} cdf1={preCdf1}  label={DbgLabel}");
            _boolAdaptCounter++;
            TraceDecode("DecodeBoolAdapt", (int)bit, $"preCdf0={preCdf0} preCdf1={preCdf1}");
        }
        return bit;
    }

    /// <summary>
    /// Decodes a multi-symbol value using an adaptive CDF table.
    /// CDF is an inverse cumulative distribution in Q15.
    /// cdf[nSymbols] holds the adaptation counter.
    /// </summary>
    /// <param name="cdf">CDF table (length = nSymbols + 1).</param>
    /// <param name="nSymbols">Number of symbols (1..15).</param>
    /// <returns>Decoded symbol index (0..nSymbols-1).</returns>
    public uint DecodeSymbolAdapt(Span<ushort> cdf, int nSymbols)
    {
        // Save pre-state for DLL comparison
        ulong dif_start = dif;
        uint rng_start = rng;
        int cnt_start = cnt;

        // Diagnostic: log exact pre-state for partition divergence investigation
        bool isTarget = (nSymbols >= 3 && cdf.Length > 0 && (cdf[0] == 13260 || cdf[0] == 16056));
        ulong preDif = 0;
        uint preRng = 0;
        int preCnt = 0;
        if (isTarget)
        {
            preDif = dif;
            preRng = rng;
            preCnt = cnt;
        }

        uint c = (uint)(dif >> (WinSize - 16));
        uint r = rng >> 8;
        uint u, v = rng;
        int val = -1;

        if (isTarget)
        {
            AvDbg.W($"[SYM-TRACE] cdf0={cdf[0]} c=0x{c:X4}={c} r=0x{r:X2}={r} rng=0x{rng:X4} vStart=0x{v:X4}={v} nSym={nSymbols}");
        }

        do
        {
            val++;
            u = v;
            v = r * (uint)(cdf[val] >> ProbShift);
            uint vBeforeShift = v;
            v >>= 7 - ProbShift;
            v += MinProb * (uint)(nSymbols - val);
            if (isTarget) {
                AvDbg.W($"[SYM-TRACE]   val={val} cdf[{val}]={cdf[val]} >>6={cdf[val]>>ProbShift} vMul={vBeforeShift} vShifted={v - MinProb * (uint)(nSymbols - val)} v={v} c={c} c<v={c < v} u={u}");
            }
            
            // DEBUG: dump v for 16x16 partition (nSym=9, cdf[0]=17171)
            if (nSymbols == 9 && cdf.Length > 0 && cdf[0] == 17171) {
                uint vVal = v;
                uint vMul = vBeforeShift;
                AvDbg.W($"[V-LOOP] val={val} cdf={cdf[val]} fps={cdf[val]>>ProbShift} r={r} vMul={vMul} vShifted={vVal - MinProb * (uint)(nSymbols - val)} v={vVal} c={c} c<v={c < vVal}");
                if (c >= vVal || val >= nSymbols) {
                    AvDbg.W($"[V-LOOP] EXIT val={val} u={u} v={vVal} u-v={u-vVal}");
                }
            }
            
            // DEBUG: dump v for 32x32 partition (nSym=9, cdf[0]=14306)
            if (nSymbols == 9 && cdf.Length > 0 && cdf[0] == 14306) {
                uint vVal = v;
                uint vMul = vBeforeShift;
                AvDbg.W($"[V-LOOP32] val={val} cdf={cdf[val]} fps={cdf[val]>>ProbShift} r={r} vMul={vMul} vShifted={vVal - MinProb * (uint)(nSymbols - val)} v={vVal} c={c} c<v={c < vVal}");
                if (c >= vVal || val >= nSymbols) {
                    AvDbg.W($"[V-LOOP32] EXIT val={val} u={u} v={vVal} u-v={u-vVal}");
                }
            }
        } while (c < v);

        Normalize(dif - ((ulong)v << (WinSize - 16)), u - v);
        
        // DLL comparison for DecodeSymbolAdapt — also check adaptation
        var cmpSym = DllCompareSymFunc;
        ushort[]? cdfCopyForAdaptCheck = null;
        if (cmpSym != null)
        {
            ushort[] cdfCopy = new ushort[cdf.Length];
            for (int ci = 0; ci < cdf.Length; ci++) cdfCopy[ci] = cdf[ci];
            int dllResult = cmpSym(rng_start, (uint)dif_start, (uint)(dif_start >> 32), cnt_start, cdfCopy, nSymbols, data, position, dataEnd);
            if (val != dllResult)
            {
                AvDbg.W($"[SYMADAPT-DLL-MISMATCH] our={val} dll={dllResult} nSym={nSymbols} pre: difHi={(uint)(dif_start>>32):X8} difLo={(uint)dif_start:X8} rng={rng_start:X4} cnt={cnt_start}");
            }
            cdfCopyForAdaptCheck = cdfCopy;
        }
        
        // Note: dif and rng are now POST-normalize values
        // The trace records these post-normalize values

        // Save pre-update CDF for tracing
        string? preCdfVals = null;
        uint preCount = 0;
        if (_traceEnabled)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < nSymbols; i++) sb.Append($"{cdf[i]} ");
            preCdfVals = sb.ToString().Trim();
            preCount = cdf[nSymbols];
        }

        if (allowUpdateCdf)
        {
            uint count = cdf[nSymbols];
            uint rate = 4 + (count >> 4) + (nSymbols > 2 ? 1u : 0u);
            int i;
            for (i = 0; i < val; i++)
                cdf[i] += (ushort)((32768 - cdf[i]) >> (int)rate);
            for (; i < nSymbols; i++)
                cdf[i] -= (ushort)(cdf[i] >> (int)rate);
            cdf[nSymbols] = (ushort)(count + (count < 32 ? 1u : 0u));
            
            // Compare adaptation against DLL result
            if (cdfCopyForAdaptCheck != null)
            {
                for (int ci = 0; ci < nSymbols; ci++)
                {
                    if (cdf[ci] != cdfCopyForAdaptCheck[ci])
                    {
                        AvDbg.W($"[ADAPT-DLL-MISMATCH] idx={ci} our={cdf[ci]} dll={cdfCopyForAdaptCheck[ci]} nSym={nSymbols} val={val} rate={rate} preCdf0={cdfCopyForAdaptCheck[0]}");
                    }
                }
                if (cdf[nSymbols] != cdfCopyForAdaptCheck[nSymbols])
                    AvDbg.W($"[ADAPT-DLL-MISMATCH] counter our={cdf[nSymbols]} dll={cdfCopyForAdaptCheck[nSymbols]}");
            }
        }

        if (_traceEnabled && preCdfVals != null)
        {
            TraceDecode("DecodeSymbolAdapt", val, $"nSym={nSymbols} preCdf=[{preCdfVals}] preCnt={preCount}");
        }

        if (isTarget)
        {
            AvDbg.W($"[SYM-TARGET] pre: dif=0x{preDif:X16} rng=0x{preRng:X4} cnt={preCnt}  post: dif=0x{dif:X16} rng=0x{rng:X4} cnt={cnt}  result={val}  nSym={nSymbols}");
        }

        EmitDav1dSA(cdf, nSymbols, (uint)val);

        return (uint)val;
    }

    /// <summary>
    /// Convenience wrapper for 4-symbol CDF decode (nSymbols 1..3).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeSymbolAdapt4(Span<ushort> cdf, int nSymbols) =>
        DecodeSymbolAdapt(cdf, nSymbols);

    /// <summary>
    /// Convenience wrapper for 8-symbol CDF decode (nSymbols 1..7).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeSymbolAdapt8(Span<ushort> cdf, int nSymbols) =>
        DecodeSymbolAdapt(cdf, nSymbols);

    /// <summary>
    /// Convenience wrapper for 16-symbol CDF decode (nSymbols 3..15).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeSymbolAdapt16(Span<ushort> cdf, int nSymbols) =>
        DecodeSymbolAdapt(cdf, nSymbols);

    /// <summary>
    /// Decodes a high-token value (used for coefficient level coding).
    /// Cascades through 4 stages of 4-symbol CDFs.
    /// </summary>
    public uint DecodeHiTok(Span<ushort> cdf)
    {
        uint tokBr = DecodeSymbolAdapt4(cdf, 3);
        uint tok = 3 + tokBr;
        if (tokBr == 3)
        {
            tokBr = DecodeSymbolAdapt4(cdf, 3);
            tok = 6 + tokBr;
            if (tokBr == 3)
            {
                tokBr = DecodeSymbolAdapt4(cdf, 3);
                tok = 9 + tokBr;
                if (tokBr == 3)
                    tok = 12 + DecodeSymbolAdapt4(cdf, 3);
            }
        }
        return tok;
    }

    /// <summary>
    /// Decodes n bits with equal probability (MSB first).
    /// </summary>
    public uint DecodeBools(int n)
    {
        _inDecodeBools = true;
        ulong origDif = dif;
        uint origRng = rng;
        int origCnt = cnt;
        uint v = 0;
        for (int i = 0; i < n; i++)
        {
            uint b = DecodeBoolEqui();
            if (_traceEnabled) TraceDecode($"DecodeBoolEqui", (int)b, $"bit[{i}]");
            v = (v << 1) | b;
        }
        _inDecodeBools = false;
        if (_traceEnabled) TraceDecode("DecodeBools", (int)v, $"n={n}");

        var cmpBools = DllCompareBoolsFunc;
        if (cmpBools != null)
        {
            uint dllResult = cmpBools(origRng, (uint)origDif, (uint)(origDif >> 32), origCnt, n,
                data, position, dataEnd);
            if (v != dllResult)
            {
                AvDbg.W($"[BOOLS-DLL-MISMATCH] n={n} ours={v}(0x{v:x}) dll={dllResult}(0x{dllResult:x}) pre: difHi={(uint)(origDif>>32):X8} difLo={(uint)origDif:X8} rng={origRng:X4} cnt={origCnt}");
            }
        }

        return v;
    }

    /// <summary>
    /// Decodes a uniform-distributed value in range [0, n-1].
    /// n must be > 0.
    /// </summary>
    public int DecodeUniform(uint n)
    {
        int l = 31 - BitOperations.LeadingZeroCount(n) + 1;
        uint m = (1u << l) - n;
        uint v = 0;
        for (int i = 0; i < l - 1; i++)
        {
            uint b = DecodeBoolEqui();
            if (_traceEnabled) TraceDecode($"DecodeBoolEqui", (int)b, $"bit[{i}]");
            v = (v << 1) | b;
        }
        int result;
        if (v < m)
        {
            result = (int)v;
        }
        else
        {
            uint b = DecodeBoolEqui();
            if (_traceEnabled) TraceDecode("DecodeBoolEqui", (int)b, "uniTail");
            result = (int)((v << 1) - m + b);
        }
        if (_traceEnabled) TraceDecode("DecodeUniform", result, $"n={n}");
        return result;
    }

    /// <summary>
    /// Decodes a subexponential coded value with reference.
    /// Used for global motion parameters.
    /// </summary>
    public int DecodeSubexp(int reference, int n, uint k)
    {
        uint a = 0;
        if (DecodeBoolEqui() != 0)
        {
            if (_traceEnabled) TraceDecode("DecodeBoolEqui", 1, "subexp1");
            if (DecodeBoolEqui() != 0)
            {
                if (_traceEnabled) TraceDecode("DecodeBoolEqui", 1, "subexp2");
                k += DecodeBoolEqui() + 1;
                if (_traceEnabled) TraceDecode("DecodeBoolEqui", (int)(k - 1), "subexp3");
            }
            else
            {
                if (_traceEnabled) TraceDecode("DecodeBoolEqui", 0, "subexp2");
            }
            a = 1u << (int)k;
        }
        else
        {
            if (_traceEnabled) TraceDecode("DecodeBoolEqui", 0, "subexp1");
        }
        uint v = DecodeBools((int)k) + a;
        return reference * 2 <= n
            ? (int)InvRecenter((uint)reference, v)
            : n - 1 - (int)InvRecenter((uint)(n - 1 - reference), v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize(ulong d, uint r)
    {
        int shift = 15 ^ (31 ^ BitOperations.LeadingZeroCount(r));
        int c = cnt;
        
        // DEBUG: trace partition normalization
        if (c == 35 && rng == 0xDD10) {
            ulong preDif = d;
            AvDbg.W($"[NORM-DIFF] r={r}(0x{r:X4}) clz={BitOperations.LeadingZeroCount(r)} shift={shift} preDif=0x{preDif:X16} postDif=0x{(preDif<<shift):X16}");
        }
        
        if (c == 38 && rng == 0xCA20) {
            ulong preDif = d;
            AvDbg.W($"[NORM-32x32] r={r}(0x{r:X4}) clz={BitOperations.LeadingZeroCount(r)} shift={shift} preDif=0x{preDif:X16} postDif=0x{(preDif<<shift):X16} postRng={rng:X4}");
        }

        dif = d << shift;
        rng = r << shift;
        cnt = c - shift;
        
        if (c == 35 && rng == 0xDD10) {
            AvDbg.W($"[NORM-DBG] post rng={rng:X4}({rng}) cnt={cnt}");
        }

        if (c == 4 && rng == 0xE108 && (dif >> 16) == (0xCD59400000000000UL >> 16))
        {
            AvDbg.W($"[DBG-NORM-POST2] shift={shift} post: dif=0x{dif:X16} rng=0x{rng:X4} cnt={cnt}");
        }

        if (rng < 0x8000 && r > 0)
            AvDbg.W($"[NORM-ERR] rng={rng:X4} < 0x8000, r={r:X4}, shift={shift}");
        if ((uint)c < (uint)shift)
            Refill();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill()
    {
        int c = WinSize - cnt - 24;
        ulong d = dif;
        while (c >= 0)
        {
            if (position >= dataEnd)
            {
                // Set remaining bits to 1 (EOF padding)
                d |= ~(~0xFFul << c);
                break;
            }
            d |= (ulong)(data[position++] ^ 0xFF) << c;
            c -= 8;
        }
        dif = d;
        cnt = WinSize - c - 24;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint InvRecenter(uint r, uint v)
    {
        if (v > (r << 1))
            return v;
        else if ((v & 1) == 0)
            return (v >> 1) + r;
        else
            return r - ((v + 1) >> 1);
    }
}
