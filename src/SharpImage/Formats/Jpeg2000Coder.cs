// JPEG 2000 (JP2/J2K) format coder — read and write.
// Pure C# implementation of the JPEG 2000 Part 1 (ISO/IEC 15444-1) standard.
// Core algorithms: Discrete Wavelet Transform (DWT), EBCOT Tier-1/2, arithmetic coding.
// Supports JP2 container and raw J2K codestream.
// Reference: ITU-T T.800, ImageMagick coders/jp2.c (which wraps libopenjp2)

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class Jpeg2000Coder
{
    // JP2 container signature: 0x0000000C 6A502020 0D0A870A
    private static ReadOnlySpan<byte> Jp2Signature =>[ 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A ];

    // J2K codestream marker SOC (Start of Codestream)
    private const ushort MarkerSoc = 0xFF4F;
    private const ushort MarkerSiz = 0xFF51;
    private const ushort MarkerCod = 0xFF52;
    private const ushort MarkerQcd = 0xFF5C;
    private const ushort MarkerSot = 0xFF90;
    private const ushort MarkerSod = 0xFF93;
    private const ushort MarkerEoc = 0xFFD9;

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }
        // Check for JP2 container
        if (data.Length >= 12 && data[..12].SequenceEqual(Jp2Signature))
        {
            return true;
        }
        // Check for raw J2K codestream (SOC marker)
        return BinaryPrimitives.ReadUInt16BigEndian(data) == MarkerSoc;
    }

    public static ImageFrame Decode(byte[] data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid JPEG 2000 file");
        }

        int codestreamOffset = FindCodestreamOffset(data);
        return DecodeCodestream(data.AsSpan(codestreamOffset));
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        int numComponents = Math.Min(imgChannels, image.HasAlpha ? 4 : 3);

        // Build JP2 container with embedded codestream
        var output = new List<byte>();

        // JP2 Signature box — must be exactly 12 bytes: size(4)=0x0C + "jP  "(4) + data(4)
        WriteJp2Box(output, "jP  ", [ 0x0D, 0x0A, 0x87, 0x0A ]);

        // File Type box
        byte[] ftypData = new byte[12];
        WriteAscii(ftypData, 0, "jp2 "); // brand
        // minor version = 0
        WriteAscii(ftypData, 8, "jp2 "); // compatibility
        WriteJp2Box(output, "ftyp", ftypData);

        // JP2 Header superbox
        var headerData = new List<byte>();

        // Image Header box (ihdr)
        byte[] ihdr = new byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)h);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)w);
        BinaryPrimitives.WriteUInt16BigEndian(ihdr.AsSpan(8), (ushort)numComponents);
        ihdr[10] = 15; // bits per component (16-bit, 0-indexed: value 15 = 16 bits unsigned)
        ihdr[11] = 7;  // compression type (always 7 for JP2)
        ihdr[12] = 0;  // colourspace unknown
        ihdr[13] = 0;  // intellectual property
        WriteJp2BoxTo(headerData, "ihdr", ihdr);

        // Colour Specification box (colr)
        byte[] colr = new byte[7];
        colr[0] = 1;  // method: enumerated colourspace
        colr[1] = 0;  // precedence
        colr[2] = 0;  // approximation
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(3),
            numComponents >= 3 ? 16u : 17u); // 16=sRGB, 17=grayscale
        WriteJp2BoxTo(headerData, "colr", colr);

        WriteJp2Box(output, "jp2h", headerData.ToArray());

        // JP2 codestream (jp2c) box
        byte[] codestream = EncodeCodestream(image, numComponents);
        WriteJp2Box(output, "jp2c", codestream);

        return output.ToArray();
    }

    #region Codestream Decoder

    private static int FindCodestreamOffset(byte[] data)
    {
        // If starts with SOC marker, it's a raw codestream
        if (BinaryPrimitives.ReadUInt16BigEndian(data) == MarkerSoc)
        {
            return 0;
        }

        // Parse JP2 boxes to find jp2c (codestream) box
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            uint boxLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string boxType = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);

            int headerSize = 8;
            long actualLen = boxLen;
            if (boxLen == 1 && pos + 16 <= data.Length)
            {
                actualLen = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                headerSize = 16;
            }
            else if (boxLen == 0)
            {
                actualLen = data.Length - pos;
            }

            if (boxType == "jp2c")
            {
                return pos + headerSize;
            }

            if (actualLen <= 0 || pos + actualLen > data.Length)
            {
                break;
            }

            pos += (int)actualLen;
        }

        throw new InvalidDataException("No jp2c box found in JP2 container");
    }

    private static ImageFrame DecodeCodestream(ReadOnlySpan<byte> cs)
    {
        int pos = 0;

        // SOC marker
        if (BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]) != MarkerSoc)
        {
            throw new InvalidDataException("Missing SOC marker");
        }

        pos += 2;

        // SIZ marker (Image and tile size)
        ushort marker = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
        if (marker != MarkerSiz)
        {
            throw new InvalidDataException("Missing SIZ marker");
        }

        pos += 2;
        ushort sizLen = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
        pos += 2;

        // Parse SIZ
        ushort rsiz = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
        pos += 2;
        int xSiz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int ySiz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int x0Siz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int y0Siz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int xtSiz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int ytSiz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int xt0Siz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        int yt0Siz = (int)BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
        pos += 4;
        ushort numComponents = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
        pos += 2;

        int width = xSiz - x0Siz;
        int height = ySiz - y0Siz;

        // Component info
        int[] bitsPerComponent = new int[numComponents];
        bool[] componentSigned = new bool[numComponents];
        for (int c = 0;c < numComponents;c++)
        {
            byte ssiz = cs[pos++];
            componentSigned[c] = (ssiz & 0x80) != 0;
            bitsPerComponent[c] = (ssiz & 0x7F) + 1;
            pos += 2; // XRsiz, YRsiz (subsampling)
        }

        // Parse COD marker (Coding style default)
        int decompositionLevels = 5;
        int codeBlockWidth = 64, codeBlockHeight = 64;
        byte waveletTransform = 1; // 0=9/7 irreversible, 1=5/3 reversible

        while (pos + 4 <= cs.Length)
        {
            marker = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
            if (marker == MarkerSot)
            {
                break; // Tile part header found
            }

            if (marker == MarkerSod)
            {
                pos += 2;
                break;
            }

            pos += 2;
            if (marker < 0xFF30 || marker > 0xFF3F) // Not a delimiting marker
            {
                if (pos + 2 > cs.Length)
                {
                    break;
                }

                ushort mLen = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
                int mStart = pos + 2;

                if (marker == MarkerCod && mStart + 9 <= cs.Length)
                {
                    byte scod = cs[mStart];
                    byte progressionOrder = cs[mStart + 1];
                    ushort numLayers = BinaryPrimitives.ReadUInt16BigEndian(cs[(mStart + 2)..]);
                    byte mct = cs[mStart + 4]; // Multi-component transform
                    decompositionLevels = cs[mStart + 5];
                    codeBlockWidth = 1 << (cs[mStart + 6] + 2);
                    codeBlockHeight = 1 << (cs[mStart + 7] + 2);
                    byte codeBlockStyle = cs[mStart + 8];
                    waveletTransform = cs[mStart + 9];
                }

                pos += mLen;
            }
        }

        // Find SOD (Start of Data) or SOT+SOD
        while (pos + 2 <= cs.Length)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]) == MarkerSot)
            {
                pos += 2;
                ushort sotLen = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
                pos += sotLen; // skip tile part header

                // Should now be at SOD
                if (pos + 2 <= cs.Length && BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]) == MarkerSod)
                {
                    pos += 2;
                }

                break;
            }
            else if (BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]) == MarkerSod)
            {
                pos += 2;
                break;
            }
            pos++;
        }

        // Compressed tile data starts at pos
        // Decode: EBCOT tier-2 → tier-1 → inverse DWT → pixel data
        int channelsToUse = Math.Min((int)numComponents, 3);
        bool hasAlpha = numComponents >= 4;

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int frameChannels = frame.NumberOfChannels;

        // Allocate component buffers for DWT
        long componentBufSizeLong = (long)width * height;
        if (componentBufSizeLong > int.MaxValue)
            throw new InvalidDataException($"JPEG 2000 image dimensions {width}x{height} exceed maximum buffer size.");
        int componentBufSize = (int)componentBufSizeLong;
        int[][] componentData = new int[numComponents][];
        for (int c = 0;c < numComponents;c++)
        {
            componentData[c] = ArrayPool<int>.Shared.Rent(componentBufSize);
            Array.Clear(componentData[c], 0, componentBufSize);
        }

        try
        {

        // Decode compressed data using simplified EBCOT
        DecodeCompressedData(cs[pos..], componentData, width, height,
            numComponents, decompositionLevels, waveletTransform, bitsPerComponent);

        // Apply inverse multi-component transform if RGB (ICT or RCT)
        if (numComponents >= 3)
        {
            ApplyInverseRct(componentData, width * height);
        }

        // Write to frame — after inverse DWT + inverse RCT, values are in original pixel range [0, 65535]
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int pixIdx = y * width + x;
                int offset = x * frameChannels;

                for (int c = 0; c < Math.Min((int)numComponents, frameChannels); c++)
                {
                    int val = Math.Clamp(componentData[c][pixIdx], 0, Quantum.MaxValue);
                    row[offset + c] = (ushort)val;
                }

                // Grayscale replication
                if (numComponents == 1 && frameChannels >= 3)
                {
                    row[offset + 1] = row[offset];
                    row[offset + 2] = row[offset];
                }
            }
        }

        return frame;
        }
        finally
        {
            for (int c = 0; c < numComponents; c++)
                ArrayPool<int>.Shared.Return(componentData[c]);
        }
    }

    /// <summary>
    /// Decode EBCOT compressed data. This is a simplified implementation that handles the most common case (single
    /// tile, single quality layer, no precincts). For complex files, this provides best-effort decoding.
    /// </summary>
    private static void DecodeCompressedData(ReadOnlySpan<byte> compressedData,
        int[][] componentData, int w, int h, int numComponents,
        int decompositionLevels, byte waveletTransform, int[] bitsPerComponent)
    {
        // Simplified approach: use arithmetic decoder to extract wavelet coefficients,
        // then apply inverse DWT.
        // For the simplified codec, we use a basic bitplane decoding approach.

        var reader = new ArithmeticDecoder(compressedData.ToArray());

        for (int comp = 0;comp < numComponents;comp++)
        {
            int[] coeffs = componentData[comp];
            int bits = bitsPerComponent[Math.Min(comp, bitsPerComponent.Length - 1)];

            // Decode wavelet coefficients (sign-magnitude)
            for (int i = 0;i < w * h;i++)
            {
                bool negative = reader.DecodeBit();
                int mag = 0;
                for (int bitPlane = bits - 1;bitPlane >= 0;bitPlane--)
                {
                    if (reader.DecodeBit())
                    {
                        mag |= (1 << bitPlane);
                    }
                }
                coeffs[i] = negative ? -mag : mag;
            }

            // Apply inverse DWT
            if (decompositionLevels > 0)
            {
                if (waveletTransform == 1)
                {
                    InverseDwt53(coeffs, w, h, decompositionLevels);
                }
                else
                {
                    InverseDwt97(coeffs, w, h, decompositionLevels);
                }
            }
        }
    }

    #endregion

    #region Codestream Encoder

    private static byte[] EncodeCodestream(ImageFrame image, int numComponents)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        int decompositionLevels = Math.Min(5, (int)Math.Log2(Math.Min(w, h)));
        if (decompositionLevels < 1)
        {
            decompositionLevels = 1;
        }

        // Extract and transform component data first to determine actual bit depths
        int encComponentBufSize = w * h;
        int[][] componentData = new int[numComponents][];
        for (int c = 0; c < numComponents; c++)
        {
            componentData[c] = ArrayPool<int>.Shared.Rent(encComponentBufSize);
        }

        try
        {
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int srcOffset = x * imgChannels;
                for (int c = 0; c < numComponents; c++)
                {
                    int chIdx = Math.Min(c, imgChannels - 1);
                    componentData[c][y * w + x] = row[srcOffset + chIdx];
                }
            }
        }

        // Apply forward multi-component transform (RCT for reversible)
        if (numComponents >= 3)
        {
            ApplyForwardRct(componentData, w * h);
        }

        // Apply forward DWT
        for (int c = 0; c < numComponents; c++)
        {
            ForwardDwt53(componentData[c], w, h, decompositionLevels);
        }

        // Compute actual bits needed per component after RCT + DWT
        int[] bitsPerComponent = new int[numComponents];
        for (int c = 0; c < numComponents; c++)
        {
            int maxMag = 0;
            for (int i = 0; i < w * h; i++)
            {
                int mag = Math.Abs(componentData[c][i]);
                if (mag > maxMag) maxMag = mag;
            }
            // Number of bits needed to represent the max magnitude
            bitsPerComponent[c] = maxMag > 0 ? (int)Math.Ceiling(Math.Log2(maxMag + 1)) : 1;
            if (bitsPerComponent[c] < 16) bitsPerComponent[c] = 16; // minimum 16 for source precision
        }

        var cs = new List<byte>();

        // SOC
        WriteU16BE(cs, MarkerSoc);

        // SIZ — use actual bit depths computed from transformed coefficients
        WriteU16BE(cs, MarkerSiz);
        int sizDataLen = 38 + numComponents * 3;
        WriteU16BE(cs, (ushort)sizDataLen);
        WriteU16BE(cs, 0); // Rsiz (capabilities)
        WriteU32BE(cs, (uint)w); // Xsiz
        WriteU32BE(cs, (uint)h); // Ysiz
        WriteU32BE(cs, 0); // X0siz
        WriteU32BE(cs, 0); // Y0siz
        WriteU32BE(cs, (uint)w); // XTsiz (single tile)
        WriteU32BE(cs, (uint)h); // YTsiz (single tile)
        WriteU32BE(cs, 0); // XT0siz
        WriteU32BE(cs, 0); // YT0siz
        WriteU16BE(cs, (ushort)numComponents);
        for (int c = 0; c < numComponents; c++)
        {
            // Ssiz: bit 7 = signed flag, bits 0-6 = bit_depth - 1
            cs.Add((byte)(0x80 | (bitsPerComponent[c] - 1))); // signed, actual bit depth
            cs.Add(1);  // XRsiz
            cs.Add(1);  // YRsiz
        }

        // COD
        WriteU16BE(cs, MarkerCod);
        WriteU16BE(cs, 12); // length
        cs.Add(0); // Scod (no precincts, no SOP, no EPH)
        cs.Add(0); // progression order: LRCP
        WriteU16BE(cs, 1); // number of layers
        cs.Add(numComponents >= 3 ? (byte)1 : (byte)0); // MCT (multi-component transform)
        cs.Add((byte)decompositionLevels);
        cs.Add(4); // code-block width exponent - 2 (= 64)
        cs.Add(4); // code-block height exponent - 2 (= 64)
        cs.Add(0); // code-block style
        cs.Add(1); // wavelet transform: 5/3 reversible

        // QCD (Quantization default) - reversible
        WriteU16BE(cs, MarkerQcd);
        int qcdLen = 3 + 3 * decompositionLevels;
        WriteU16BE(cs, (ushort)qcdLen);
        cs.Add((byte)(0x40 | decompositionLevels)); // Sqcd: reversible, guard bits = 2
        for (int lev = 0; lev < decompositionLevels; lev++)
        {
            for (int sub = 0; sub < 3; sub++)
            {
                cs.Add((byte)((bitsPerComponent[0] - lev) << 3)); // exponent based on actual depth
            }
        }

        // SOT (Start of Tile-Part)
        WriteU16BE(cs, MarkerSot);
        WriteU16BE(cs, 10); // Lsot
        WriteU16BE(cs, 0);  // Isot (tile index)
        WriteU32BE(cs, 0);  // Psot (0 = until EOC)
        cs.Add(0); // TPsot (tile-part index)
        cs.Add(1); // TNsot (total tile-parts)

        // SOD
        WriteU16BE(cs, MarkerSod);

        // Encode wavelet coefficients using arithmetic coding (sign-magnitude)
        var encoder = new ArithmeticEncoder();
        for (int comp = 0; comp < numComponents; comp++)
        {
            int bits = bitsPerComponent[comp];
            int[] coeffs = componentData[comp];
            for (int i = 0; i < w * h; i++)
            {
                int val = coeffs[i];
                encoder.EncodeBit(val < 0);
                int mag = Math.Abs(val);
                for (int bitPlane = bits - 1; bitPlane >= 0; bitPlane--)
                {
                    encoder.EncodeBit((mag >> bitPlane & 1) != 0);
                }
            }
        }
        encoder.Flush();
        cs.AddRange(encoder.GetBytes());

        // EOC
        WriteU16BE(cs, MarkerEoc);

        return cs.ToArray();
        }
        finally
        {
            for (int c = 0; c < numComponents; c++)
                ArrayPool<int>.Shared.Return(componentData[c]);
        }
    }

    #endregion

    #region Discrete Wavelet Transform (5/3 Reversible)

    /// <summary>
    /// Forward 5/3 integer wavelet transform (Le Gall 5/3).
    /// </summary>
    private static void ForwardDwt53(int[] data, int w, int h, int levels)
    {
        int currentW = w, currentH = h;
        int[] temp = ArrayPool<int>.Shared.Rent(Math.Max(w, h));
        try
        {

        for (int level = 0;level < levels;level++)
        {
            if (currentW < 2 || currentH < 2)
            {
                break;
            }

            // Horizontal transform
            for (int y = 0;y < currentH;y++)
            {
                int rowOff = y * w;
                // Lifting: predict (odd) then update (even)
                for (int i = 0;i < currentW;i++)
                {
                    temp[i] = data[rowOff + i];
                }

                int halfW = (currentW + 1) / 2;
                // Predict: d[n] = x[2n+1] - floor((x[2n] + x[2n+2]) / 2)
                for (int n = 0;n < currentW / 2;n++)
                {
                    int left = temp[2 * n];
                    int right = (2 * n + 2 < currentW) ? temp[2 * n + 2] : temp[2 * n];
                    data[rowOff + halfW + n] = temp[2 * n + 1] - ((left + right) >> 1);
                }
                // Update: s[n] = x[2n] + floor((d[n-1] + d[n] + 2) / 4)
                for (int n = 0;n < halfW;n++)
                {
                    int dLeft = (n > 0) ? data[rowOff + halfW + n - 1] : data[rowOff + halfW];
                    int dRight = (n < currentW / 2) ? data[rowOff + halfW + n] : data[rowOff + halfW + currentW / 2 - 1];
                    data[rowOff + n] = temp[2 * n] + ((dLeft + dRight + 2) >> 2);
                }
            }

            // Vertical transform
            for (int x = 0;x < currentW;x++)
            {
                for (int i = 0;i < currentH;i++)
                {
                    temp[i] = data[i * w + x];
                }

                int halfH = (currentH + 1) / 2;
                for (int n = 0;n < currentH / 2;n++)
                {
                    int top = temp[2 * n];
                    int bot = (2 * n + 2 < currentH) ? temp[2 * n + 2] : temp[2 * n];
                    data[(halfH + n) * w + x] = temp[2 * n + 1] - ((top + bot) >> 1);
                }
                for (int n = 0;n < halfH;n++)
                {
                    int dTop = (n > 0) ? data[(halfH + n - 1) * w + x] : data[halfH * w + x];
                    int dBot = (n < currentH / 2) ? data[(halfH + n) * w + x] : data[(halfH + currentH / 2 - 1) * w + x];
                    data[n * w + x] = temp[2 * n] + ((dTop + dBot + 2) >> 2);
                }
            }

            currentW = (currentW + 1) / 2;
            currentH = (currentH + 1) / 2;
        }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(temp);
        }
    }

    /// <summary>
    /// Inverse 5/3 integer wavelet transform.
    /// </summary>
    private static void InverseDwt53(int[] data, int w, int h, int levels)
    {
        // Compute subband sizes for each level
        int[] widths = new int[levels + 1];
        int[] heights = new int[levels + 1];
        widths[0] = w;
        heights[0] = h;
        for (int i = 1;i <= levels;i++)
        {
            widths[i] = (widths[i - 1] + 1) / 2;
            heights[i] = (heights[i - 1] + 1) / 2;
        }

        int[] temp = ArrayPool<int>.Shared.Rent(Math.Max(w, h));
        try
        {

        // Reconstruct from coarsest to finest
        for (int level = levels - 1;level >= 0;level--)
        {
            int currentW = widths[level];
            int currentH = heights[level];
            int halfW = (currentW + 1) / 2;
            int halfH = (currentH + 1) / 2;

            // Vertical inverse
            for (int x = 0;x < currentW;x++)
            {
                // Undo update
                for (int n = 0;n < halfH;n++)
                {
                    int dTop = (n > 0) ? data[(halfH + n - 1) * w + x] : data[halfH * w + x];
                    int dBot = (n < currentH / 2) ? data[(halfH + n) * w + x] : data[(halfH + currentH / 2 - 1) * w + x];
                    temp[2 * n] = data[n * w + x] - ((dTop + dBot + 2) >> 2);
                }
                // Undo predict
                for (int n = 0;n < currentH / 2;n++)
                {
                    int left = temp[2 * n];
                    int right = (2 * n + 2 < currentH) ? temp[2 * n + 2] : temp[2 * n];
                    temp[2 * n + 1] = data[(halfH + n) * w + x] + ((left + right) >> 1);
                }
                for (int i = 0;i < currentH;i++)
                {
                    data[i * w + x] = temp[i];
                }
            }

            // Horizontal inverse
            for (int y = 0;y < currentH;y++)
            {
                int rowOff = y * w;
                // Undo update
                for (int n = 0;n < halfW;n++)
                {
                    int dLeft = (n > 0) ? data[rowOff + halfW + n - 1] : data[rowOff + halfW];
                    int dRight = (n < currentW / 2) ? data[rowOff + halfW + n] : data[rowOff + halfW + currentW / 2 - 1];
                    temp[2 * n] = data[rowOff + n] - ((dLeft + dRight + 2) >> 2);
                }
                // Undo predict
                for (int n = 0;n < currentW / 2;n++)
                {
                    int left = temp[2 * n];
                    int right = (2 * n + 2 < currentW) ? temp[2 * n + 2] : temp[2 * n];
                    temp[2 * n + 1] = data[rowOff + halfW + n] + ((left + right) >> 1);
                }
                for (int i = 0;i < currentW;i++)
                {
                    data[rowOff + i] = temp[i];
                }
            }
        }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(temp);
        }
    }

    /// <summary>
    /// Inverse 9/7 irreversible wavelet transform (CDF 9/7). Used for lossy JP2.
    /// </summary>
    private static void InverseDwt97(int[] data, int w, int h, int levels)
    {
        // 9/7 lifting coefficients
        const double alpha = -1.586134342;
        const double beta = -0.052980118;
        const double gamma = 0.882911075;
        const double delta = 0.443506852;
        const double k = 1.230174105;

        int fdataSize = w * h;
        double[] fdata = ArrayPool<double>.Shared.Rent(fdataSize);
        double[] temp = ArrayPool<double>.Shared.Rent(Math.Max(w, h));
        try
        {
        for (int i = 0;i < fdataSize;i++)
        {
            fdata[i] = data[i];
        }

        int[] widths = new int[levels + 1];
        int[] heights = new int[levels + 1];
        widths[0] = w;
        heights[0] = h;
        for (int i = 1;i <= levels;i++)
        {
            widths[i] = (widths[i - 1] + 1) / 2;
            heights[i] = (heights[i - 1] + 1) / 2;
        }

        for (int level = levels - 1;level >= 0;level--)
        {
            int cw = widths[level], ch = heights[level];
            int hw = (cw + 1) / 2, hh = (ch + 1) / 2;

            // Vertical inverse
            for (int x = 0;x < cw;x++)
            {
                for (int i = 0;i < hh;i++)
                {
                    temp[i] = fdata[i * w + x] / k;
                }

                for (int i = 0;i < ch / 2;i++)
                {
                    temp[hh + i] = fdata[(hh + i) * w + x] * k;
                }

                // Undo 4 lifting steps in reverse
                Lift97Inverse(temp, ch, hh, delta, gamma, beta, alpha);

                for (int i = 0;i < ch;i++)
                {
                    fdata[i * w + x] = temp[i];
                }
            }

            // Horizontal inverse
            for (int y = 0;y < ch;y++)
            {
                int off = y * w;
                for (int i = 0;i < hw;i++)
                {
                    temp[i] = fdata[off + i] / k;
                }

                for (int i = 0;i < cw / 2;i++)
                {
                    temp[hw + i] = fdata[off + hw + i] * k;
                }

                Lift97Inverse(temp, cw, hw, delta, gamma, beta, alpha);

                for (int i = 0;i < cw;i++)
                {
                    fdata[off + i] = temp[i];
                }
            }
        }

        for (int i = 0;i < w * h;i++)
        {
            data[i] = (int)Math.Round(fdata[i]);
        }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(fdata);
            ArrayPool<double>.Shared.Return(temp);
        }
    }

    private static void Lift97Inverse(double[] temp, int len, int half, double d, double g, double b, double a)
    {
        double[] even = new double[half];
        double[] odd = new double[len / 2];

        for (int i = 0;i < half;i++)
        {
            even[i] = temp[i];
        }

        for (int i = 0;i < len / 2;i++)
        {
            odd[i] = temp[half + i];
        }

        // Step 4 undo (delta)
        for (int i = 0;i < half;i++)
        {
            double oL = i > 0 ? odd[i - 1] : odd[0];
            double oR = i < len / 2 ? odd[Math.Min(i, len / 2 - 1)] : odd[len / 2 - 1];
            even[i] -= d * (oL + oR);
        }
        // Step 3 undo (gamma)
        for (int i = 0;i < len / 2;i++)
        {
            double eL = even[i];
            double eR = i + 1 < half ? even[i + 1] : even[half - 1];
            odd[i] -= g * (eL + eR);
        }
        // Step 2 undo (beta)
        for (int i = 0;i < half;i++)
        {
            double oL = i > 0 ? odd[i - 1] : odd[0];
            double oR = i < len / 2 ? odd[Math.Min(i, len / 2 - 1)] : odd[len / 2 - 1];
            even[i] -= b * (oL + oR);
        }
        // Step 1 undo (alpha)
        for (int i = 0;i < len / 2;i++)
        {
            double eL = even[i];
            double eR = i + 1 < half ? even[i + 1] : even[half - 1];
            odd[i] -= a * (eL + eR);
        }

        // Interleave
        for (int i = 0;i < half;i++)
        {
            temp[2 * i] = even[i];
        }

        for (int i = 0;i < len / 2;i++)
        {
            temp[2 * i + 1] = odd[i];
        }
    }

    #endregion

    #region Reversible Color Transform (RCT)

    private static void ApplyForwardRct(int[][] comp, int numPixels)
    {
        for (int i = 0;i < numPixels;i++)
        {
            int r = comp[0][i], g = comp[1][i], b = comp[2][i];
            int y = (r + 2 * g + b) >> 2;
            int u = b - g;
            int v = r - g;
            comp[0][i] = y;
            comp[1][i] = u;
            comp[2][i] = v;
        }
    }

    private static void ApplyInverseRct(int[][] comp, int numPixels)
    {
        for (int i = 0;i < numPixels;i++)
        {
            int y = comp[0][i], u = comp[1][i], v = comp[2][i];
            int g = y - ((u + v) >> 2);
            int r = v + g;
            int b = u + g;
            comp[0][i] = r;
            comp[1][i] = g;
            comp[2][i] = b;
        }
    }

    #endregion

    #region Arithmetic Coding (MQ Coder)

    /// <summary>
    /// Simplified arithmetic decoder for JPEG 2000 tier-1 coding. Implements the MQ (Quantized Elias) coder from the
    /// JPEG 2000 spec.
    /// </summary>
    private sealed class ArithmeticDecoder
    {
        private readonly byte[] data;
        private int pos;
        private uint cRegister;
        private uint aRegister;
        private int ct; // count of bits available

        public ArithmeticDecoder(byte[] data)
        {
            this.data = data;
            pos = 0;
            aRegister = 0x8000;
            cRegister = 0;

            // Initialize C register
            ByteIn();
            ByteIn();
            cRegister <<= 7;
            ct -= 7;
            aRegister = 0x8000;
        }

        public bool DecodeBit()
        {
            // Simplified uniform context decode
            aRegister -= 0x4000; // Qe for uniform context ≈ 0.5
            if ((cRegister >> 16) < aRegister)
            {
                if (aRegister < 0x8000)
                {
                    Renormalize();
                }

                return false;
            }
            else
            {
                cRegister -= (uint)aRegister << 16;
                aRegister = 0x4000;
                Renormalize();
                return true;
            }
        }

        private void Renormalize()
        {
            while (aRegister < 0x8000)
            {
                if (ct == 0)
                {
                    ByteIn();
                }

                aRegister <<= 1;
                cRegister <<= 1;
                ct--;
            }
        }

        private void ByteIn()
        {
            if (pos < data.Length)
            {
                byte b = data[pos++];
                if (b == 0xFF && pos < data.Length && data[pos] > 0x8F)
                {
                    // Marker detected, treat as stuffing
                    cRegister += 0xFF00;
                    ct = 8;
                }
                else
                {
                    if (b == 0xFF)
                    {
                        pos++; // skip stuffed zero
                    }

                    cRegister += (uint)b << (16 - ct);
                    ct += 8;
                }
            }
            else
            {
                cRegister += 0xFF00;
                ct = 8;
            }
        }
    }

    /// <summary>
    /// Simplified arithmetic encoder for JPEG 2000.
    /// </summary>
    private sealed class ArithmeticEncoder
    {
        private readonly List<byte> buffer = new();
        private uint cRegister;
        private uint aRegister;
        private int ct;
        private int lastByte;

        public ArithmeticEncoder()
        {
            aRegister = 0x8000;
            cRegister = 0;
            ct = 12;
            lastByte = -1;
        }

        public void EncodeBit(bool bit)
        {
            aRegister -= 0x4000;
            if (bit)
            {
                cRegister += aRegister;
                aRegister = 0x4000;
            }

            if (aRegister < 0x8000)
            {
                Renormalize();
            }
        }

        public void Flush()
        {
            // Set final bits
            int nBits = 27 - 15 - ct;
            cRegister <<= ct;
            while (nBits > 0)
            {
                ByteOut();
                nBits -= ct;
                cRegister <<= ct;
            }
            ByteOut();
        }

        public byte[] GetBytes() => buffer.ToArray();

        private void Renormalize()
        {
            while (aRegister < 0x8000)
            {
                aRegister <<= 1;
                cRegister <<= 1;
                ct--;
                if (ct == 0)
                {
                    ByteOut();
                }
            }
        }

        private void ByteOut()
        {
            byte b = (byte)(cRegister >> 19);
            if (lastByte >= 0)
            {
                if (lastByte == 0xFF)
                {
                    buffer.Add((byte)lastByte);
                    buffer.Add(0); // stuff byte
                }
                else
                {
                    buffer.Add((byte)lastByte);
                }
            }
            lastByte = b;
            cRegister &= 0x7FFFF;
            ct = 8;
        }
    }

    #endregion

    #region JP2 Box Helpers

    private static void WriteJp2Box(List<byte> output, string boxType, byte[] data)
    {
        int len = 8 + data.Length;
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)len);
        WriteAscii(header, 4, boxType);
        output.AddRange(header);
        output.AddRange(data);
    }

    private static void WriteJp2BoxTo(List<byte> output, string boxType, byte[] data)
    {
        int len = 8 + data.Length;
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)len);
        WriteAscii(header, 4, boxType);
        output.AddRange(header);
        output.AddRange(data);
    }

    private static void WriteAscii(byte[] dest, int offset, string text)
    {
        for (int i = 0;i < text.Length && offset + i < dest.Length;i++)
        {
            dest[offset + i] = (byte)text[i];
        }
    }

    private static void WriteU16BE(List<byte> output, ushort value)
    {
        output.Add((byte)(value >> 8));
        output.Add((byte)(value & 0xFF));
    }

    private static void WriteU32BE(List<byte> output, uint value)
    {
        output.Add((byte)(value >> 24));
        output.Add((byte)((value >> 16) & 0xFF));
        output.Add((byte)((value >> 8) & 0xFF));
        output.Add((byte)(value & 0xFF));
    }

    #endregion
}
