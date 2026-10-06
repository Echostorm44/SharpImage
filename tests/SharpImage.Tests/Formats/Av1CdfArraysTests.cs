using System.Reflection;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>The CDF contexts' explicit array walk (CollectArrays, used by the row-MT CDF copies / averages and the CDF
/// index; reflection over the fields is not reliable under Native AOT) against the declaration-order reflection walk it
/// replaced: the same arrays, in the same order, every one of them.</summary>
public sealed class Av1CdfArraysTests
{
    [Test]
    public async Task CollectArrays_MatchesTheDeclarationOrderReflectionWalk()
    {
        var ctx = new Av1CdfContext();
        var explicitList = new List<ushort[]>();
        ctx.CollectArrays(explicitList);
        var reflected = new List<ushort[]>();
        Walk(ctx, reflected);
        await Assert.That(explicitList.Count).IsEqualTo(reflected.Count);
        int same = 0;
        for (int i = 0; i < reflected.Count; i++)
            if (ReferenceEquals(explicitList[i], reflected[i])) same++;
        await Assert.That(same).IsEqualTo(reflected.Count);
    }

    [Test]
    public async Task PinArrays_ReplacesEveryArrayKeepingTheSizes()
    {
        var ctx = new Av1CdfContext();
        var before = new List<ushort[]>();
        ctx.CollectArrays(before);
        ctx.PinArrays();
        var after = new List<ushort[]>();
        ctx.CollectArrays(after);
        int replaced = 0, sized = 0;
        for (int i = 0; i < before.Count; i++)
        {
            if (!ReferenceEquals(before[i], after[i])) replaced++;
            if (before[i].Length == after[i].Length) sized++;
        }
        await Assert.That(replaced).IsEqualTo(before.Count);
        await Assert.That(sized).IsEqualTo(before.Count);
    }

    private static void Walk(object o, List<ushort[]> into)
    {
        foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.MetadataToken))
        {
            object? v = f.GetValue(o);
            if (v is ushort[] a) into.Add(a);
            else if (v is ushort[][] aa) into.AddRange(aa);
            else if (v != null && f.FieldType.IsClass && f.FieldType.Namespace == typeof(Av1CdfContext).Namespace) Walk(v, into);
        }
    }
}
