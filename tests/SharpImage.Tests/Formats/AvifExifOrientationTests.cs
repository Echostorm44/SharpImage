using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>avifGetExifOrientationOffset: the Orientation byte avifenc resets to 1 in the Exif item.</summary>
public sealed class AvifExifOrientationTests
{
    // "Exif\0\0" + TIFF header + IFD0 with two entries (Make, then Orientation = value)
    private static byte[] Exif(bool le, ushort orientation)
    {
        var b = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        void U16(int v) { if (le) { b.Add((byte)v); b.Add((byte)(v >> 8)); } else { b.Add((byte)(v >> 8)); b.Add((byte)v); } }
        void U32(uint v) { if (le) { U16((int)(v & 0xffff)); U16((int)(v >> 16)); } else { U16((int)(v >> 16)); U16((int)(v & 0xffff)); } }
        b.AddRange(le ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42); U32(8);
        U16(2);
        U16(0x010f); U16(2); U32(4); b.AddRange("Cam\0"u8.ToArray());
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U32(0);
        return b.ToArray();
    }

    [Test]
    public async Task FindsTheOrientationValueByte_BothByteOrders()
    {
        foreach (bool le in new[] { true, false })
        {
            var e = Exif(le, 6);
            int o = Av1AvifWriter.ExifOrientationOffset(e);
            await Assert.That(o).IsGreaterThan(0);
            await Assert.That(e[o]).IsEqualTo((byte)6);
            await Assert.That(e[le ? o + 1 : o - 1]).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task ReservedValuesAndMissingHeader_AreLeftAlone()
    {
        await Assert.That(Av1AvifWriter.ExifOrientationOffset(Exif(true, 9))).IsEqualTo(-1);
        await Assert.That(Av1AvifWriter.ExifOrientationOffset("Exif\0\0nothing here"u8.ToArray())).IsEqualTo(-1);
        await Assert.That(Av1AvifWriter.ExifOrientationOffset(Exif(false, 0)[..20])).IsEqualTo(-1);
    }
}
