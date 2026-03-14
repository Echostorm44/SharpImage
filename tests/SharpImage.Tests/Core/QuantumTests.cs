using System.Runtime.CompilerServices;
using SharpImage.Core;

namespace SharpImage.Tests.Core;

public class QuantumTests
{
    // Prevents the compiler from const-folding, so TUnit doesn't flag these as constant assertions
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T Eval<T>(T value) => value;

    [Test]
    public async Task MaxValue_Is65535()
    {
        await Assert.That(Eval(Quantum.MaxValue)).IsEqualTo((ushort)65535);
    }

    [Test]
    public async Task Depth_Is16()
    {
        await Assert.That(Eval(Quantum.Depth)).IsEqualTo(16);
    }

    [Test]
    public async Task Scale_IsInverseOfMaxValue()
    {
        double expected = 1.0 / 65535.0;
        await Assert.That(Eval(Quantum.Scale)).IsEqualTo(expected);
    }

    [Test]
    public async Task Opaque_EqualsMaxValue()
    {
        await Assert.That(Eval(Quantum.Opaque)).IsEqualTo(Eval(Quantum.MaxValue));
    }

    [Test]
    public async Task Transparent_IsZero()
    {
        await Assert.That(Eval(Quantum.Transparent)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Clamp_NegativeValue_ReturnsZero()
    {
        await Assert.That(Quantum.Clamp(-1.0)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Clamp_ValueAboveMax_ReturnsMax()
    {
        await Assert.That(Quantum.Clamp(70000.0)).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task Clamp_NaN_ReturnsZero()
    {
        await Assert.That(Quantum.Clamp(double.NaN)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Clamp_Zero_ReturnsZero()
    {
        await Assert.That(Quantum.Clamp(0.0)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Clamp_MidValue_RoundsCorrectly()
    {
        // 32767.5 should round to 32768
        await Assert.That(Quantum.Clamp(32767.5)).IsEqualTo((ushort)32768);
    }

    [Test]
    public async Task Clamp_ExactMax_ReturnsMax()
    {
        await Assert.That(Quantum.Clamp(65535.0)).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task ScaleToByte_Zero_ReturnsZero()
    {
        await Assert.That(Quantum.ScaleToByte(0)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task ScaleToByte_MaxValue_Returns255()
    {
        await Assert.That(Quantum.ScaleToByte(Quantum.MaxValue)).IsEqualTo((byte)255);
    }

    [Test]
    public async Task ScaleToByte_HalfValue_Returns128()
    {
        // 65535/2 = 32767.5, so ushort value 32768 should map to ~128
        ushort halfQuantum = 32896; // 128 * 257 = 32896
        await Assert.That(Quantum.ScaleToByte(halfQuantum)).IsEqualTo((byte)128);
    }

    [Test]
    public async Task ScaleFromByte_Zero_ReturnsZero()
    {
        await Assert.That(Quantum.ScaleFromByte(0)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task ScaleFromByte_255_ReturnsMaxValue()
    {
        await Assert.That(Quantum.ScaleFromByte(255)).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task ScaleFromByte_128_Returns32896()
    {
        // 128 * 257 = 32896
        await Assert.That(Quantum.ScaleFromByte(128)).IsEqualTo((ushort)32896);
    }

    [Test]
    public async Task ScaleToByte_And_ScaleFromByte_RoundTrip()
    {
        // Every byte value should survive a round-trip
        for (int b = 0; b <= 255; b++)
        {
            ushort quantum = Quantum.ScaleFromByte((byte)b);
            byte result = Quantum.ScaleToByte(quantum);
            await Assert.That(result).IsEqualTo((byte)b);
        }
    }

    [Test]
    public async Task ScaleToDouble_Zero_ReturnsZero()
    {
        await Assert.That(Quantum.ScaleToDouble(0)).IsEqualTo(0.0);
    }

    [Test]
    public async Task ScaleToDouble_MaxValue_ReturnsOne()
    {
        await Assert.That(Quantum.ScaleToDouble(Quantum.MaxValue)).IsEqualTo(1.0);
    }

    [Test]
    public async Task ScaleFromDouble_Zero_ReturnsZero()
    {
        await Assert.That(Quantum.ScaleFromDouble(0.0)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task ScaleFromDouble_One_ReturnsMaxValue()
    {
        await Assert.That(Quantum.ScaleFromDouble(1.0)).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task ScaleToDouble_And_ScaleFromDouble_RoundTrip()
    {
        ushort original = 42000;
        double normalized = Quantum.ScaleToDouble(original);
        ushort result = Quantum.ScaleFromDouble(normalized);
        await Assert.That(result).IsEqualTo(original);
    }

    [Test]
    public async Task ScaleToDepth_SameDepth_ReturnsSameValue()
    {
        ushort value = 30000;
        await Assert.That(Quantum.ScaleToDepth(value, 16)).IsEqualTo(value);
    }

    [Test]
    public async Task ScaleToDepth_8bit_MatchesScaleToByte()
    {
        ushort value = 32896;
        byte expected = Quantum.ScaleToByte(value);
        ushort result = Quantum.ScaleToDepth(value, 8);
        await Assert.That(result).IsEqualTo((ushort)expected);
    }

    [Test]
    public async Task ScaleFromDepth_8bit_MatchesScaleFromByte()
    {
        uint byteValue = 200;
        ushort expected = Quantum.ScaleFromByte((byte)byteValue);
        ushort result = Quantum.ScaleFromDepth(byteValue, 8);
        await Assert.That(result).IsEqualTo(expected);
    }
}
