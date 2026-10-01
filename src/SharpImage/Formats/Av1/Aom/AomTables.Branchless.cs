using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

internal static partial class AomTables
{
    /// <summary>abs() without a branch: gcc compiles the C's abs() / ternaries on data-random signs branch-free, while
    /// Math.Abs (with its overflow check) is a conditional jump that mispredicts on them. Not for int.MinValue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AbsI(int v)
    {
        int m = v >> 31;
        return (v ^ m) - m;
    }
}
