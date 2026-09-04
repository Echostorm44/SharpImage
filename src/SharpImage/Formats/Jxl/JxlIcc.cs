// JPEG XL ICC profile compression codec (ISO/IEC 18181 Annex C). An embedded ICC profile is stored in two
// nested layers: an outer ENTROPY layer (an ANS/prefix stream over 41 byte-contexts, get_icc_ctx) whose
// output is the "encoded" ICC stream, and an inner TRANSFORM layer that turns that stream (a header-delta
// section + a command list + a data section) back into the raw ICC bytes. This file implements the transform
// layer (DecodeTransform, the exact port of jxl-oxide's decode_icc, and EncodeTransform, a simple but valid
// command stream); the entropy layer lives in JxlIccEntropy. The two decoders (libjxl, jxl-oxide) are the
// authority — decode_icc.rs in jxl-color is the reference.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Jxl;

internal static class JxlIcc
{
    // The 19 common ICC tag signatures (tagcode 2..20 in the command stream).
    private static readonly byte[][] CommonTags =
    {
        B("rTRC"), B("rXYZ"), B("cprt"), B("wtpt"), B("bkpt"), B("rXYZ"), B("gXYZ"), B("bXYZ"), B("kXYZ"),
        B("rTRC"), B("gTRC"), B("bTRC"), B("kTRC"), B("chad"), B("desc"), B("chrm"), B("dmnd"), B("dmdd"), B("lumi"),
    };

    // The 8 common tag-data signatures (main command 16..23).
    private static readonly byte[][] CommonData =
    {
        B("XYZ "), B("desc"), B("text"), B("mluc"), B("para"), B("curv"), B("sf32"), B("gbd "),
    };

    private static byte[] B(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    // The per-byte context for the entropy layer (idx into the ICC output stream, previous two bytes).
    // Ported verbatim from jxl-color decode_icc get_icc_ctx; the encoder and decoder MUST agree.
    public static uint GetCtx(int idx, byte b1, byte b2)
    {
        if (idx <= 128)
        {
            return 0;
        }

        uint p1 = b1 switch
        {
            >= (byte)'a' and <= (byte)'z' => 0,
            >= (byte)'A' and <= (byte)'Z' => 0,
            >= (byte)'0' and <= (byte)'9' => 1,
            (byte)'.' or (byte)',' => 1,
            <= 1 => 2u + b1,
            <= 15 => 4,
            >= 241 and <= 254 => 5,
            255 => 6,
            _ => 7,
        };
        uint p2 = b2 switch
        {
            >= (byte)'a' and <= (byte)'z' => 0,
            >= (byte)'A' and <= (byte)'Z' => 0,
            >= (byte)'0' and <= (byte)'9' => 1,
            (byte)'.' or (byte)',' => 1,
            <= 15 => 2,
            >= 241 => 3,
            _ => 4,
        };
        return 1 + p1 + (8 * p2);
    }

    public const int NumCtx = 41;

    // LEB128 varint read from a byte stream (position advanced).
    private static ulong ReadVarint(byte[] s, ref int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (shift < 63)
        {
            if (pos >= s.Length)
            {
                throw new InvalidDataException("ICC stream too short (varint)");
            }

            byte b = s[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return value;
    }

    private static void WriteVarint(List<byte> s, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            s.Add(b);
        }
        while (value != 0);
    }

    // Header byte prediction (idx in [0,128)). `header` is the reconstructed-so-far header (bytes at the
    // referenced positions have prediction 0, so delta == value there and reads are consistent).
    private static byte PredictHeader(int idx, uint outputSize, byte[] header)
    {
        switch (idx)
        {
            case >= 0 and <= 3:
                return (byte)(outputSize >> (24 - (8 * idx)));
            case 8:
                return 4;
            case >= 12 and <= 23:
                return B("mntrRGB XYZ ")[idx - 12];
            case >= 36 and <= 39:
                return B("acsp")[idx - 36];
            case 41 when header[40] == (byte)'A':
            case 42 when header[40] == (byte)'A':
                return (byte)'P';
            case 43 when header[40] == (byte)'A':
                return (byte)'L';
            case 41 when header[40] == (byte)'M':
                return (byte)'S';
            case 42 when header[40] == (byte)'M':
                return (byte)'F';
            case 43 when header[40] == (byte)'M':
                return (byte)'T';
            case 42 when header[40] == (byte)'S' && header[41] == (byte)'G':
                return (byte)'I';
            case 43 when header[40] == (byte)'S' && header[41] == (byte)'G':
                return (byte)' ';
            case 42 when header[40] == (byte)'S' && header[41] == (byte)'U':
                return (byte)'N';
            case 43 when header[40] == (byte)'S' && header[41] == (byte)'U':
                return (byte)'W';
            case 70:
                return 246;
            case 71:
                return 214;
            case 73:
                return 1;
            case 78:
                return 211;
            case 79:
                return 45;
            case >= 80 and <= 83:
                return header[4 + idx - 80];
            default:
                return 0;
        }
    }

    private static byte[] Shuffle2(byte[] bytes)
    {
        int len = bytes.Length;
        var outp = new List<byte>(len);
        int height = len / 2;
        int odd = len % 2;
        for (int idx = 0; idx < height; idx++)
        {
            outp.Add(bytes[idx]);
            outp.Add(bytes[idx + height + odd]);
        }

        if (odd != 0)
        {
            outp.Add(bytes[height]);
        }

        return outp.ToArray();
    }

    private static byte[] Shuffle4(byte[] bytes)
    {
        int len = bytes.Length;
        var outp = new List<byte>(len);
        int step = len / 4;
        int wideCount = len % 4;
        for (int idx = 0; idx < step; idx++)
        {
            int baseI = idx;
            for (int k = 0; k < wideCount; k++)
            {
                outp.Add(bytes[baseI]);
                baseI += step + 1;
            }

            for (int k = wideCount; k < 4; k++)
            {
                outp.Add(bytes[baseI]);
                baseI += step;
            }
        }

        for (int idx = 1; idx <= wideCount; idx++)
        {
            outp.Add(bytes[((step + 1) * idx) - 1]);
        }

        return outp.ToArray();
    }

    // Reconstruct the raw ICC profile from the transform-layer stream (exact port of decode_icc). `stream` is
    // the entropy-decoded ICC stream: varint(output_size) varint(commands_size) commands[] data[].
    public static byte[] DecodeTransform(byte[] stream)
    {
        int pos = 0;
        ulong outputSize = ReadVarint(stream, ref pos);
        ulong commandsSize = ReadVarint(stream, ref pos);
        int streamOffset = pos;
        if ((ulong)streamOffset + commandsSize > (ulong)stream.Length)
        {
            throw new InvalidDataException("ICC: invalid commands_size");
        }

        if (outputSize > (1 << 28))
        {
            throw new InvalidDataException("ICC: output_size too large");
        }

        int cmdEnd = streamOffset + (int)commandsSize;
        byte[] commands = stream[streamOffset..cmdEnd];
        // data is the remaining stream; we track a moving offset into it.
        int dataPos = cmdEnd;
        int headerSize = (int)Math.Min(outputSize, 128);
        if (stream.Length - dataPos < headerSize)
        {
            throw new InvalidDataException("ICC: invalid output_size");
        }

        var outp = new List<byte>((int)outputSize);

        // Header: reconstructed[i] = predict(i) + delta[i]. Build into a 128-buffer for prediction reads.
        var headerBuf = new byte[headerSize];
        for (int i = 0; i < headerSize; i++)
        {
            headerBuf[i] = stream[dataPos + i];
        }

        for (int idx = 0; idx < headerSize; idx++)
        {
            byte p = PredictHeader(idx, (uint)outputSize, headerBuf);
            outp.Add((byte)(p + headerBuf[idx]));
        }

        dataPos += headerSize;
        if (outputSize <= 128)
        {
            return outp.ToArray();
        }

        int cpos = 0;

        // Tag section.
        ulong v = ReadVarint(commands, ref cpos);
        if (v >= 1)
        {
            ulong numTags = v - 1;
            if ((outputSize - 128) / 12 < numTags)
            {
                throw new InvalidDataException("ICC: num_tags too large");
            }

            AppendBE(outp, (uint)numTags);
            uint prevTagStart = ((uint)numTags * 12) + 128;
            uint prevTagSize = 0;

            while (true)
            {
                if (cpos >= commands.Length)
                {
                    return outp.ToArray();
                }

                byte command = commands[cpos++];
                byte tagcode = (byte)(command & 63);
                byte[] tag;
                if (tagcode == 0)
                {
                    break;
                }
                else if (tagcode == 1)
                {
                    if (stream.Length - dataPos < 4)
                    {
                        throw new InvalidDataException("ICC: unexpected end of data stream");
                    }

                    tag = stream[dataPos..(dataPos + 4)];
                    dataPos += 4;
                }
                else if (tagcode <= 20)
                {
                    tag = CommonTags[tagcode - 2];
                }
                else
                {
                    throw new InvalidDataException("ICC: invalid tagcode");
                }

                uint tagstart = (command & 64) == 0 ? prevTagStart + prevTagSize : (uint)ReadVarint(commands, ref cpos);
                uint tagsize;
                if ((command & 128) != 0)
                {
                    tagsize = (uint)ReadVarint(commands, ref cpos);
                }
                else if (Eq(tag, "rXYZ") || Eq(tag, "gXYZ") || Eq(tag, "bXYZ") || Eq(tag, "kXYZ") ||
                         Eq(tag, "wtpt") || Eq(tag, "bkpt") || Eq(tag, "lumi"))
                {
                    tagsize = 20;
                }
                else
                {
                    tagsize = prevTagSize;
                }

                if ((ulong)tagstart + tagsize > outputSize)
                {
                    throw new InvalidDataException("ICC: profile size mismatch");
                }

                prevTagStart = tagstart;
                prevTagSize = tagsize;

                outp.AddRange(tag);
                AppendBE(outp, tagstart);
                AppendBE(outp, tagsize);
                if (tagcode == 2)
                {
                    outp.AddRange(B("gTRC"));
                    AppendBE(outp, tagstart);
                    AppendBE(outp, tagsize);
                    outp.AddRange(B("bTRC"));
                    AppendBE(outp, tagstart);
                    AppendBE(outp, tagsize);
                }
                else if (tagcode == 3)
                {
                    outp.AddRange(B("gXYZ"));
                    AppendBE(outp, tagstart + tagsize);
                    AppendBE(outp, tagsize);
                    outp.AddRange(B("bXYZ"));
                    AppendBE(outp, tagstart + (tagsize * 2));
                    AppendBE(outp, tagsize);
                }
            }
        }

        // Main section.
        while (cpos < commands.Length)
        {
            byte command = commands[cpos++];
            switch (command)
            {
                case 1:
                {
                    int num = (int)ReadVarint(commands, ref cpos);
                    if (num > stream.Length - dataPos)
                    {
                        throw new InvalidDataException("ICC: stream too short (literal)");
                    }

                    outp.AddRange(stream[dataPos..(dataPos + num)]);
                    dataPos += num;
                    break;
                }

                case 2:
                case 3:
                {
                    int num = (int)ReadVarint(commands, ref cpos);
                    if (num > stream.Length - dataPos)
                    {
                        throw new InvalidDataException("ICC: stream too short (shuffle)");
                    }

                    byte[] chunk = stream[dataPos..(dataPos + num)];
                    dataPos += num;
                    outp.AddRange(command == 2 ? Shuffle2(chunk) : Shuffle4(chunk));
                    break;
                }

                case 4:
                {
                    if (cpos >= commands.Length)
                    {
                        throw new InvalidDataException("ICC: stream too short (predict flags)");
                    }

                    byte flags = commands[cpos++];
                    int width = (flags & 3) + 1;
                    int order = (flags >> 2) & 3;
                    if (width == 3 || order == 3)
                    {
                        throw new InvalidDataException("ICC: width==3 || order==3");
                    }

                    int stride;
                    if ((flags & 16) == 0)
                    {
                        stride = width;
                    }
                    else
                    {
                        stride = (int)ReadVarint(commands, ref cpos);
                        if (stride < width)
                        {
                            throw new InvalidDataException("ICC: stride < width");
                        }
                    }

                    if ((long)stride * 4 >= outp.Count)
                    {
                        throw new InvalidDataException("ICC: stride*4 >= out.len");
                    }

                    int num = (int)ReadVarint(commands, ref cpos);
                    if (stream.Length - dataPos < num)
                    {
                        throw new InvalidDataException("ICC: stream too short (predict data)");
                    }

                    byte[] raw = stream[dataPos..(dataPos + num)];
                    dataPos += num;
                    byte[] bytes = width switch
                    {
                        1 => raw,
                        2 => Shuffle2(raw),
                        4 => Shuffle4(raw),
                        _ => throw new InvalidDataException("ICC: bad width"),
                    };

                    for (int i = 0; i < num; i += width)
                    {
                        var prev = new uint[3];
                        for (int j = 0; j <= order; j++)
                        {
                            int offset = outp.Count - (stride * (j + 1));
                            var bb = new byte[4];
                            for (int k = 0; k < width; k++)
                            {
                                bb[(4 - width) + k] = outp[offset + k];
                            }

                            prev[j] = (uint)((bb[0] << 24) | (bb[1] << 16) | (bb[2] << 8) | bb[3]);
                        }

                        uint p = order switch
                        {
                            0 => prev[0],
                            1 => (2u * prev[0]) - prev[1],
                            2 => (3u * (prev[0] - prev[1])) + prev[2],
                            _ => 0,
                        };

                        int limit = Math.Min(width, num - i);
                        for (int j = 0; j < limit; j++)
                        {
                            uint val = bytes[i + j] + (p >> (8 * (width - 1 - j)));
                            outp.Add((byte)val);
                        }
                    }

                    break;
                }

                case 10:
                {
                    if (stream.Length - dataPos < 12)
                    {
                        throw new InvalidDataException("ICC: stream too short (XYZ)");
                    }

                    outp.AddRange(new byte[] { (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0 });
                    outp.AddRange(stream[dataPos..(dataPos + 12)]);
                    dataPos += 12;
                    break;
                }

                case >= 16 and <= 23:
                {
                    outp.AddRange(CommonData[command - 16]);
                    outp.AddRange(new byte[] { 0, 0, 0, 0 });
                    break;
                }

                default:
                    throw new InvalidDataException($"ICC: invalid command {command}");
            }
        }

        if (outp.Count != (int)outputSize)
        {
            throw new InvalidDataException("ICC: decoded profile size mismatch");
        }

        return outp.ToArray();
    }

    // Build a valid (but non-optimal) transform-layer stream for a raw ICC profile: predict the header, then
    // copy the entire body with a single literal command. decode_icc reconstructs this exactly. libjxl's
    // encoder additionally recognises tags/shuffles/deltas to shrink the stream; the entropy layer still gives
    // most of the compression, and this keeps the encoder simple and obviously correct.
    public static byte[] EncodeTransform(byte[] raw)
    {
        int outputSize = raw.Length;
        int headerSize = Math.Min(outputSize, 128);

        // Header deltas: delta[i] = raw[i] - predict(i). Prediction reads reconstructed header bytes, which at
        // the referenced positions (40..43, 80..83, 4..) have prediction 0, so delta == value there — pass the
        // raw header so the reads match the decoder.
        var headerDelta = new byte[headerSize];
        var headerRecon = new byte[headerSize];
        Array.Copy(raw, headerRecon, headerSize);
        for (int i = 0; i < headerSize; i++)
        {
            byte p = PredictHeader(i, (uint)outputSize, headerRecon);
            headerDelta[i] = (byte)(raw[i] - p);
        }

        var commands = new List<byte>();
        var data = new List<byte>();
        data.AddRange(headerDelta);

        if (outputSize > 128)
        {
            WriteVarint(commands, 0);                         // v=0 => no tag section, straight to Main
            commands.Add(1);                                  // Main command 1: literal
            WriteVarint(commands, (ulong)(outputSize - 128)); // copy the whole body
            data.AddRange(raw[128..]);
        }

        var stream = new List<byte>();
        WriteVarint(stream, (ulong)outputSize);
        WriteVarint(stream, (ulong)commands.Count);
        stream.AddRange(commands);
        stream.AddRange(data);
        return stream.ToArray();
    }

    // ---- Entropy layer: the full ICC blob as it sits in the codestream ----

    // Reads an embedded ICC profile from the bitstream (enc_size + a 41-context entropy stream) and returns
    // the raw profile bytes. Mirrors jxl-color read_icc + decode_icc.
    public static byte[] DecodeStream(JxlBitReader br)
    {
        ulong encSize = br.ReadU64();
        if (encSize > (1 << 28))
        {
            throw new InvalidDataException("ICC: encoded profile too large");
        }

        JxlAnsCode code = JxlEntropy.DecodeHistograms(NumCtx, br);
        var rd = new JxlAnsReader(code, br);
        var encoded = new byte[(int)encSize];
        byte b1 = 0, b2 = 0;
        for (int idx = 0; idx < encoded.Length; idx++)
        {
            uint sym = rd.ReadHybridUintCtx((int)GetCtx(idx, b1, b2));
            if (sym >= 256)
            {
                throw new InvalidDataException("ICC: decoded value out of range");
            }

            encoded[idx] = (byte)sym;
            b2 = b1;
            b1 = encoded[idx];
        }

        if (!rd.CheckFinal())
        {
            throw new InvalidDataException("ICC: ANS stream verification failed");
        }

        return DecodeTransform(encoded);
    }

    // Writes an embedded ICC profile into the bitstream: the transform stream, entropy-coded with the same
    // 41-context model and a single shared prefix histogram (valid + simple; the reference clusters contexts
    // for a little more compression, which our general decoder still reads).
    public static void EncodeStream(JxlBitWriter w, byte[] rawIcc)
    {
        byte[] encoded = EncodeTransform(rawIcc);
        w.WriteU64((ulong)encoded.Length);

        int maxSym = 0;
        var freq = new long[256];
        foreach (byte b in encoded)
        {
            freq[b]++;
            if (b > maxSym)
            {
                maxSym = b;
            }
        }

        var codeTab = new JxlPrefixCode(freq, maxSym + 1);

        // Entropy-config header: lz77 off, simple all-to-cluster-0 context map for 41 contexts, prefix code,
        // split_exp=15 uint config (values < 2^15 are their own token, so a byte encodes as itself).
        w.WriteBool(false);   // lz77 disabled
        w.WriteBool(true);    // context map: is_simple
        w.WriteBits(0, 2);    // nbits = 0 => every context -> cluster 0
        w.WriteBool(true);    // use_prefix_code
        WriteUintConfig(w, splitExp: 15, logAlpha: 15);
        w.WriteVarLenUint16(codeTab.AlphabetSize - 1);
        codeTab.WriteHeader(w);

        foreach (byte b in encoded)
        {
            codeTab.WriteSymbol(w, b);
        }
    }

    private static void WriteUintConfig(JxlBitWriter w, int splitExp, int logAlpha)
    {
        w.WriteBits((uint)splitExp, JxlBits.CeilLog2(logAlpha + 1));
        // splitExp == logAlpha => no msb/lsb fields.
    }

    private static void AppendBE(List<byte> outp, uint v)
    {
        outp.Add((byte)(v >> 24));
        outp.Add((byte)(v >> 16));
        outp.Add((byte)(v >> 8));
        outp.Add((byte)v);
    }

    private static bool Eq(byte[] tag, string s)
    {
        if (tag.Length != s.Length)
        {
            return false;
        }

        for (int i = 0; i < s.Length; i++)
        {
            if (tag[i] != (byte)s[i])
            {
                return false;
            }
        }

        return true;
    }
}
