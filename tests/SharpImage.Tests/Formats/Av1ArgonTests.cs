using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpImage.Tests.Formats;

// The AOM Argon conformance suite (every stream of dav1d's tests/dav1d_argon.bash default set: profiles 0-2, core +
// special, Annex B and Section 5, profile switching) against its reference MD5s, hashed as dav1d's md5 muxer does
// with film grain applied, operating point 0 and all layers output. The streams are not redistributable here, so
// the test is opt-in: point SHARPIMAGE_ARGON_DIR at an unpacked Argon tree (the directory holding profile0_core
// etc.); without it the test is skipped.
public sealed class Av1ArgonTests
{
    private static readonly string[] Dirs =
    [
        "profile0_core", "profile0_core_special", "profile0_not_annexb", "profile0_not_annexb_special",
        "profile1_core", "profile1_core_special", "profile1_not_annexb", "profile1_not_annexb_special",
        "profile2_core", "profile2_core_special", "profile2_not_annexb", "profile2_not_annexb_special", "profile_switching",
    ];

    private static string? Root => Environment.GetEnvironmentVariable("SHARPIMAGE_ARGON_DIR");

    public static IEnumerable<string> Streams()
    {
        if (Root is not { } root || !Directory.Exists(root)) yield break;
        foreach (var dir in Dirs)
        {
            string sd = Path.Combine(root, dir, "streams");
            if (!Directory.Exists(sd)) continue;
            foreach (var f in Directory.GetFiles(sd, "*.obu").Order())
                yield return dir + "/" + Path.GetFileName(f);
        }
    }

    [Test]
    [MethodDataSource(nameof(Streams), SkipIfEmpty = true)]
    public async Task Decode_MatchesArgonMd5(string stream)
    {
        string dir = stream[..stream.IndexOf('/')], file = stream[(dir.Length + 1)..];
        string result = Av1Conformance.CheckArgon(Path.Combine(Root!, dir, "streams", file),
            Path.Combine(Root!, dir, "md5_ref", Path.GetFileNameWithoutExtension(file) + ".md5"), !dir.Contains("not_annexb"));
        await Assert.That(result).StartsWith("ok");
    }
}
