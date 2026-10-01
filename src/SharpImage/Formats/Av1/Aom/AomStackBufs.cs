using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

// Fixed-size scratch arrays as struct locals for the hot search paths. A method with a stackalloc cannot be compiled
// with on-stack replacement, so the JIT compiles it once fully optimised but without the tier-1 profile (dynamic PGO);
// a struct local of the same size keeps the method on the normal Tier0 -> instrumented -> Tier1 path.
[InlineArray(3)] internal struct StackArr3<T> { private T e; }
[InlineArray(4)] internal struct StackArr4<T> { private T e; }
[InlineArray(7)] internal struct StackArr7<T> { private T e; }
[InlineArray(10)] internal struct StackArr10<T> { private T e; }
[InlineArray(13)] internal struct StackArr13<T> { private T e; }
[InlineArray(14)] internal struct StackArr14<T> { private T e; }
[InlineArray(16)] internal struct StackArr16<T> { private T e; }
[InlineArray(32)] internal struct StackArr32<T> { private T e; }
[InlineArray(48)] internal struct StackArr48<T> { private T e; }
[InlineArray(64)] internal struct StackArr64<T> { private T e; }
[InlineArray(117)] internal struct StackArr117<T> { private T e; }
[InlineArray(129)] internal struct StackArr129<T> { private T e; }
[InlineArray(160)] internal struct StackArr160<T> { private T e; }
[InlineArray(163)] internal struct StackArr163<T> { private T e; }
[InlineArray(1024)] internal struct StackArr1024<T> { private T e; }
[InlineArray(1089)] internal struct StackArr1089<T> { private T e; }
