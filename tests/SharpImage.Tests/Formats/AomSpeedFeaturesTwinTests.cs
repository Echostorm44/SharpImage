using System.Runtime.InteropServices;
using System.Text;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twin of the speed-feature port (src/SharpImage/Formats/Av1/Aom/AomSpeedFeatures.cs) against libaom 3.14.1's own
// av1_set_speed_features_framesize_independent / _dependent / _qindex_dependent, through aomtwin_sf.dll (a harness
// built against libaom.a that runs them on a calloc'd AV1_COMP and dumps every SPEED_FEATURES field, WinnerModeParams
// and the sequence tool flags as path=value lines; tests/native/aomtwin). Opt-in: point SHARPIMAGE_AOMTWIN_SF at
// aomtwin_sf.dll; without it the tests report Skipped.
[NotInParallel]
public sealed class AomSpeedFeaturesTwinTests
{
    private const string EnvVar = "SHARPIMAGE_AOMTWIN_SF";
    private static readonly string? DllPath = AomTwinNative.PathFromEnv(EnvVar);
    private static readonly bool Available = DllPath != null && Load();

    private static bool Load()
    {
        AomTwinNative.Register("aomtwin_sf", DllPath!);
        return true;
    }

    private static unsafe class Native
    {
        [DllImport("aomtwin_sf")] public static extern int sf_twin_run(int* p, int* ops, int nops, byte* outp, int cap);
    }

    private sealed record Config(AomSpeedFeatureInputs In, int Speed, AomSpeedFeatureSeqFlags Seq, int[] Ops)
    {
        public override string ToString() =>
            $"speed={Speed} {In.Width}x{In.Height} q={In.BaseQindex} hbd={In.UseHighBitDepth} allowSct={In.AllowScreenContentTools} " +
            $"useSct={In.UseScreenContentTools} fc={In.FrContentType} ft={In.FrameType} upd={In.UpdateType} trellis={In.DisableTrellisQuant} " +
            $"q=[{In.BestAllowedQ},{In.WorstAllowedQ}] txsz={In.EnableTxSizeSearch} workers={In.NumWorkers} rowmt={In.RowMt} " +
            $"pass={In.Pass} lap={In.LapEnabled} stage={In.CompressorStage} gfcbr={In.GfCbrBoostPct} seqLocked={Seq.SeqParamsLocked} " +
            $"ops=[{string.Join(",", Ops)}]";
    }

    private static int B(bool b) => b ? 1 : 0;

    private static string Libaom(Config c)
    {
        var i = c.In;
        var s = c.Seq;
        int[] p =
        [
            c.Speed, i.Width, i.Height, B(i.UseHighBitDepth), B(i.AllowScreenContentTools), B(i.UseScreenContentTools),
            B(i.IsScreenContentType), i.FrContentType, i.BaseQindex, i.FrameType, i.UpdateType, i.DisableTrellisQuant,
            i.BestAllowedQ, i.WorstAllowedQ, B(i.EnableTxSizeSearch), i.NumWorkers, i.RowMt, i.Pass, B(i.LapEnabled),
            i.CompressorStage, i.GfCbrBoostPct, i.Tuning, B(s.SeqParamsLocked), s.enable_dist_wtd_comp, s.enable_dual_filter,
            s.enable_restoration, s.enable_interintra_compound, s.enable_masked_compound,
        ];
        var buf = new byte[1 << 16];
        int len;
        unsafe
        {
            fixed (int* pp = p)
            fixed (int* po = c.Ops)
            fixed (byte* pb = buf)
                len = Native.sf_twin_run(pp, po, c.Ops.Length, pb, buf.Length);
        }
        if (len < 0) throw new InvalidOperationException($"sf_twin_run failed ({len})");
        return Encoding.ASCII.GetString(buf, 0, len);
    }

    private static string Ours(Config c)
    {
        var i = c.In;
        var sf = new AomSpeedFeatures();
        var wmp = new AomWinnerModeParams();
        // the seq flags are mutated: work on a copy so the libaom run sees the original values
        var seq = new AomSpeedFeatureSeqFlags
        {
            SeqParamsLocked = c.Seq.SeqParamsLocked, enable_dist_wtd_comp = c.Seq.enable_dist_wtd_comp,
            enable_dual_filter = c.Seq.enable_dual_filter, enable_restoration = c.Seq.enable_restoration,
            enable_interintra_compound = c.Seq.enable_interintra_compound, enable_masked_compound = c.Seq.enable_masked_compound,
        };
        int savedQ = i.BaseQindex;
        for (int k = 0; k < c.Ops.Length; k++)
        {
            switch (c.Ops[k])
            {
                case 1: sf.SetFramesizeIndependent(i, seq, wmp, c.Speed); break;
                case 2: sf.SetFramesizeDependent(i, seq, c.Speed); break;
                case 3: sf.SetQindexDependent(i, wmp, c.Speed); break;
                case 4: i.BaseQindex = c.Ops[++k]; break;
            }
        }
        i.BaseQindex = savedQ;

        var sb = new StringBuilder();
        AomSpeedFeaturesDump.Fields(sf, sb);
        for (int a = 0; a < MODE_EVAL_TYPES; a++)
            for (int b = 0; b < 2; b++) P(sb, $"winner_mode_params.coeff_opt_thresholds[{a}][{b}]", wmp.coeff_opt_thresholds[a * 2 + b]);
        for (int a = 0; a < MODE_EVAL_TYPES; a++) P(sb, $"winner_mode_params.tx_size_search_methods[{a}]", wmp.tx_size_search_methods[a]);
        for (int a = 0; a < MODE_EVAL_TYPES; a++) P(sb, $"winner_mode_params.use_transform_domain_distortion[{a}]", wmp.use_transform_domain_distortion[a]);
        for (int a = 0; a < MODE_EVAL_TYPES; a++) P(sb, $"winner_mode_params.tx_domain_dist_threshold[{a}]", wmp.tx_domain_dist_threshold[a]);
        for (int a = 0; a < MODE_EVAL_TYPES; a++) P(sb, $"winner_mode_params.skip_txfm_level[{a}]", wmp.skip_txfm_level[a]);
        for (int a = 0; a < MODE_EVAL_TYPES; a++) P(sb, $"winner_mode_params.predict_dc_level[{a}]", wmp.predict_dc_level[a]);
        P(sb, "seq_params.enable_dist_wtd_comp", seq.enable_dist_wtd_comp);
        P(sb, "seq_params.enable_dual_filter", seq.enable_dual_filter);
        P(sb, "seq_params.enable_restoration", seq.enable_restoration);
        P(sb, "seq_params.enable_interintra_compound", seq.enable_interintra_compound);
        P(sb, "seq_params.enable_masked_compound", seq.enable_masked_compound);
        return sb.ToString();
    }

    private static void P(StringBuilder sb, string key, long v) => sb.Append(key).Append('=').Append(v).Append('\n');

    private static List<(string Key, string Value)> Parse(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l =>
        {
            int e = l.IndexOf('=');
            return (l[..e], l[(e + 1)..]);
        }).ToList();

    // Compares every field; returns the description of the mismatches (empty when all equal).
    private static string Compare(Config c, ref int fieldsCompared)
    {
        var lib = Parse(Libaom(c));
        var ours = Parse(Ours(c));
        var diffs = new List<string>();
        if (lib.Count != ours.Count) diffs.Add($"field count libaom={lib.Count} ours={ours.Count}");
        for (int k = 0; k < Math.Min(lib.Count, ours.Count); k++)
        {
            if (lib[k].Key != ours[k].Key) { diffs.Add($"field order: libaom {lib[k].Key} vs ours {ours[k].Key}"); break; }
            if (lib[k].Value != ours[k].Value) diffs.Add($"{lib[k].Key}: libaom={lib[k].Value} ours={ours[k].Value}");
        }
        fieldsCompared += Math.Min(lib.Count, ours.Count);
        return diffs.Count == 0 ? "" : $"[{c}]\n    " + string.Join("\n    ", diffs);
    }

    private static AomSpeedFeatureInputs Inputs(int w, int h, int q, bool hbd = false, bool sct = false) => new()
    {
        Width = w, Height = h, BaseQindex = q, UseHighBitDepth = hbd,
        AllowScreenContentTools = sct, UseScreenContentTools = sct, IsScreenContentType = sct,
    };

    private static readonly (int W, int H)[] Sizes =
    [
        (64, 64), (352, 288), (512, 384), (640, 480), (480, 640), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160),
        (4096, 2304),
    ];

    private static readonly int[] Qindexes = [0, 40, 70, 96, 97, 108, 112, 128, 140, 141, 170, 180, 200, 255];

    private static async Task Run(IEnumerable<Config> configs)
    {
        int n = 0, fields = 0;
        var failures = new List<string>();
        foreach (var c in configs)
        {
            n++;
            string d = Compare(c, ref fields);
            if (d.Length > 0 && failures.Count < 20) failures.Add(d);
        }
        Console.WriteLine($"speed-feature twin: {n} configurations, {fields} field values compared, {failures.Count} mismatching");
        await Assert.That(string.Join("\n", failures)).IsEqualTo("");
    }

    // The per-frame all-intra sequence (framesize_independent, framesize_dependent, qindex_dependent) over speeds,
    // frame sizes, qindexes, screen content on/off and 8/high bit depth.
    [Test]
    public async Task AllIntra_FrameSequence_Matrix()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        IEnumerable<Config> Configs()
        {
            for (int speed = 0; speed <= 9; speed++)
                foreach (var (w, h) in Sizes)
                    foreach (int q in Qindexes)
                        foreach (bool sct in new[] { false, true })
                            foreach (bool hbd in new[] { false, true })
                                yield return new Config(Inputs(w, h, q, hbd, sct), speed, new AomSpeedFeatureSeqFlags(), [1, 2, 3]);
        }
        await Run(Configs());
    }

    // The individual stages and repeated calls (qindex_dependent twice at different q, as the screen-content quick run
    // and the final encode do; framesize_dependent repeated as av1_check_initial_width + av1_set_size_dependent_vars do).
    [Test]
    public async Task AllIntra_Stages_And_Repeats()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        int[][] opsList = [[1], [1, 2], [1, 2, 2, 3], [1, 2, 3, 4, 200, 3], [1, 2, 3, 4, 20, 3], [1, 2, 3, 1, 2, 3]];
        IEnumerable<Config> Configs()
        {
            for (int speed = 0; speed <= 9; speed++)
                foreach (var (w, h) in Sizes)
                    foreach (int q in new[] { 0, 112, 255 })
                        foreach (bool sct in new[] { false, true })
                            foreach (var ops in opsList)
                                yield return new Config(Inputs(w, h, q, false, sct), speed, new AomSpeedFeatureSeqFlags(), ops);
        }
        await Run(Configs());
    }

    // The other inputs the functions read: screen-content flags independently, graphics/animation content type, frame
    // and update types, trellis modes and lossless, tx size search off, multi-threading, stats stages, gf_cbr boost,
    // locked sequence parameters.
    [Test]
    public async Task AllIntra_OtherInputs()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var variants = new List<(string Name, Action<AomSpeedFeatureInputs> Set, bool SeqLocked)>
        {
            ("allowSctOnly", i => { i.AllowScreenContentTools = true; }, false),
            ("useSctOnly", i => { i.UseScreenContentTools = true; }, false),
            ("graphics", i => { i.FrContentType = FC_GRAPHICS_ANIMATION; }, false),
            ("intraOnly", i => { i.FrameType = INTRA_ONLY_FRAME; }, false),
            ("interLf", i => { i.FrameType = INTER_FRAME; i.UpdateType = LF_UPDATE; }, false),
            ("interGf", i => { i.FrameType = INTER_FRAME; i.UpdateType = GF_UPDATE; }, false),
            ("interArf2", i => { i.FrameType = INTER_FRAME; i.UpdateType = INTNL_ARF_UPDATE; }, false),
            ("trellis0", i => { i.DisableTrellisQuant = 0; }, false),
            ("trellis1", i => { i.DisableTrellisQuant = 1; }, false),
            ("trellis2", i => { i.DisableTrellisQuant = 2; }, false),
            ("lossless3", i => { i.BestAllowedQ = 0; i.WorstAllowedQ = 0; }, false),
            ("lossless0", i => { i.BestAllowedQ = 0; i.WorstAllowedQ = 0; i.DisableTrellisQuant = 0; }, false),
            ("lossless2", i => { i.BestAllowedQ = 0; i.WorstAllowedQ = 0; i.DisableTrellisQuant = 2; }, false),
            ("noTxSizeSearch", i => { i.EnableTxSizeSearch = false; }, false),
            ("mt4", i => { i.NumWorkers = 4; }, false),
            ("mt8rowmt0", i => { i.NumWorkers = 8; i.RowMt = 0; }, false),
            ("firstPass", i => { i.Pass = AOM_RC_FIRST_PASS; }, false),
            ("secondPass", i => { i.Pass = AOM_RC_SECOND_PASS; }, false),
            ("lap", i => { i.LapEnabled = true; }, false),
            ("lapStage", i => { i.LapEnabled = true; i.CompressorStage = LAP_STAGE; }, false),
            ("gfcbr", i => { i.GfCbrBoostPct = 50; }, false),
            ("seqLocked", i => { }, true),
        };
        IEnumerable<Config> Configs()
        {
            foreach (var v in variants)
                for (int speed = 0; speed <= 9; speed++)
                    foreach (var (w, h) in Sizes)
                        foreach (int q in new[] { 0, 60, 112, 150, 190, 255 })
                            foreach (bool hbd in new[] { false, true })
                            {
                                var i = Inputs(w, h, q, hbd);
                                v.Set(i);
                                var seq = new AomSpeedFeatureSeqFlags { SeqParamsLocked = v.SeqLocked };
                                yield return new Config(i, speed, seq, [1, 2, 3]);
                            }
        }
        await Run(Configs());
    }
}
