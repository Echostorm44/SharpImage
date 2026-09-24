// HEIF/AVIF (ISOBMFF) item model: the 'meta' box parsed into items (type, properties, references, data). Item data
// is assembled from every iloc extent (versions 0-2, base offsets, construction_method 0 = file offsets and 1 = the
// 'idat' box), properties come from every 'ipma' (with their essential flags), references from 'iref' (v0/v1).
// Mirrors what libavif's read.c accepts; construction_method 2 (item offsets) is rejected, as in libavif.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpImage.Formats;

internal sealed class HeifItem
{
    public int Id;
    public string Type = "";
    public string ContentType = "";
    public int ConstructionMethod;
    public long BaseOffset;
    public readonly List<(long Offset, long Length)> Extents = [];
    public readonly List<(string Type, int Off, int Len, bool Essential)> Properties = [];
}

internal sealed class HeifContainer
{
    private readonly byte[] data;
    private int idatOff = -1, idatLen;

    public int PrimaryId { get; private set; } = 1;
    public Dictionary<int, HeifItem> Items { get; } = [];
    public List<(string Type, int From, List<int> To)> References { get; } = [];
    /// <summary>Entity groups from 'grpl' (grouping type, group id, entity ids in order), e.g. 'altr'.</summary>
    public List<(string Type, uint GroupId, List<uint> Entities)> Groups { get; } = [];
    /// <summary>ftyp major + compatible brands.</summary>
    public List<string> Brands { get; } = [];

    private HeifContainer(byte[] data) => this.data = data;

    public byte[] Data => data;

    public static HeifContainer Parse(byte[] data)
    {
        var c = new HeifContainer(data);
        foreach (var (type, off, len) in Children(data, 0, data.Length))
        {
            if (type == "ftyp" && c.Brands.Count == 0 && len >= 8)
            {
                c.Brands.Add(Encoding.ASCII.GetString(data, off, 4));
                for (int p = off + 8; p + 4 <= off + len; p += 4) c.Brands.Add(Encoding.ASCII.GetString(data, p, 4));
            }
            if (type == "meta") { c.ParseMeta(off + 4, len - 4); break; }
        }
        return c;
    }

    public HeifItem? Primary => Items.GetValueOrDefault(PrimaryId);

    /// <summary>Item payload: all extents concatenated (file offsets, or offsets into 'idat'). Null when the item has
    /// no data, uses an unsupported construction method, or an extent falls outside the file.</summary>
    public byte[]? ItemData(int id)
    {
        if (!Items.TryGetValue(id, out var item) || item.Extents.Count == 0) return null;
        if (item.ConstructionMethod is not (0 or 1)) return null;
        long total = 0;
        foreach (var (_, len) in item.Extents) total += len;
        if (total <= 0 || total > int.MaxValue) return null;
        var buf = new byte[total];
        int o = 0;
        foreach (var (eOff, eLen) in item.Extents)
        {
            long start = item.BaseOffset + eOff;
            long srcStart, srcLimit;
            if (item.ConstructionMethod == 0) { srcStart = start; srcLimit = data.Length; }
            else
            {
                if (idatOff < 0) return null;
                srcStart = idatOff + start; srcLimit = idatOff + idatLen;
            }
            if (srcStart < 0 || eLen < 0 || srcStart + eLen > srcLimit) return null;
            Buffer.BlockCopy(data, (int)srcStart, buf, o, (int)eLen);
            o += (int)eLen;
        }
        return buf;
    }

    /// <summary>First associated property of the given type (payload offset/length), optionally filtered.</summary>
    public (int Off, int Len, bool Essential)? Property(int itemId, string type, Func<int, int, bool>? match = null)
    {
        if (!Items.TryGetValue(itemId, out var item)) return null;
        foreach (var (t, off, len, ess) in item.Properties)
            if (t == type && (match == null || match(off, len))) return (off, len, ess);
        return null;
    }

    /// <summary>Items referencing <paramref name="toId"/> with the given reference type (e.g. auxl, cdsc).</summary>
    public IEnumerable<int> ReferencesTo(int toId, string type)
    {
        foreach (var (t, from, to) in References)
            if (t == type && to.Contains(toId)) yield return from;
    }

    /// <summary>The ordered targets of <paramref name="fromId"/>'s reference of the given type (e.g. a grid's dimg).</summary>
    public List<int> ReferencesFrom(int fromId, string type)
    {
        foreach (var (t, from, to) in References)
            if (t == type && from == fromId) return to;
        return [];
    }

    /// <summary>ispe of an item (width, height), if any.</summary>
    public (int W, int H)? Ispe(int itemId)
        => Property(itemId, "ispe") is { Len: >= 12 } p
            ? ((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p.Off + 4)), (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p.Off + 8)))
            : null;

    private void ParseMeta(int off, int len)
    {
        var kids = Children(data, off, len);
        foreach (var (type, bOff, bLen) in kids)
        {
            switch (type)
            {
                case "pitm" when bLen >= 6:
                    PrimaryId = data[bOff] == 0 ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(bOff + 4))
                                                : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bOff + 4));
                    break;
                case "idat":
                    idatOff = bOff; idatLen = bLen;
                    break;
            }
        }
        // iinf before iloc/iprp so items exist; order in the file is free.
        foreach (var (type, bOff, bLen) in kids) if (type == "iinf") ParseIinf(bOff, bLen);
        foreach (var (type, bOff, bLen) in kids)
        {
            if (type == "iloc") ParseIloc(bOff, bLen);
            else if (type == "iref") ParseIref(bOff, bLen);
            else if (type == "iprp") ParseIprp(bOff, bLen);
            else if (type == "grpl") ParseGrpl(bOff, bLen);
        }
    }

    // GroupsListBox: EntityToGroupBoxes (full boxes named by grouping_type): group_id, num_entities, entity_ids.
    private void ParseGrpl(int off, int len)
    {
        foreach (var (type, gOff, gLen) in Children(data, off, len))
        {
            if (gLen < 12) throw new InvalidDataException("Truncated EntityToGroupBox.");
            uint groupId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(gOff + 4));
            uint n = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(gOff + 8));
            if (n > (uint)(gLen - 12) / 4) throw new InvalidDataException("Truncated EntityToGroupBox.");
            var ids = new List<uint>((int)n);
            for (int i = 0; i < n; i++) ids.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(gOff + 12 + 4 * i)));
            Groups.Add((type, groupId, ids));
        }
    }

    private HeifItem Item(int id)
    {
        if (!Items.TryGetValue(id, out var it)) Items[id] = it = new HeifItem { Id = id };
        return it;
    }

    private void ParseIinf(int off, int len)
    {
        if (len < 6) return;
        int pos = off + 4 + (data[off] == 0 ? 2 : 4);
        foreach (var (type, pOff, pLen) in Children(data, pos, off + len - pos))
        {
            if (type != "infe" || pLen < 4) continue;
            int v = data[pOff], q = pOff + 4, qEnd = pOff + pLen;
            if (v < 2 || q + (v == 2 ? 2 : 4) + 6 > qEnd) continue;
            int id = v == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q)) : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(q));
            q += (v == 2 ? 2 : 4) + 2;
            var item = Item(id);
            item.Type = Encoding.ASCII.GetString(data, q, 4);
            q += 4;
            int nameEnd = Array.IndexOf(data, (byte)0, q, qEnd - q);
            if (item.Type == "mime" && nameEnd >= 0 && nameEnd + 1 < qEnd)
            {
                int ctEnd = Array.IndexOf(data, (byte)0, nameEnd + 1, qEnd - nameEnd - 1);
                item.ContentType = Encoding.ASCII.GetString(data, nameEnd + 1, (ctEnd >= 0 ? ctEnd : qEnd) - nameEnd - 1);
            }
        }
    }

    private void ParseIloc(int off, int len)
    {
        int end = off + len;
        if (len < 8) return;
        int version = data[off];
        int offsetSize = data[off + 4] >> 4, lengthSize = data[off + 4] & 0xF;
        int baseOffsetSize = data[off + 5] >> 4, indexSize = version >= 1 ? data[off + 5] & 0xF : 0;
        int pos = off + 6;
        int count;
        if (version < 2) { count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2; }
        else { count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)); pos += 4; }
        for (int i = 0; i < count && pos < end; i++)
        {
            int id;
            if (version < 2) { id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2; }
            else { id = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)); pos += 4; }
            var item = Item(id);
            if (version >= 1) { item.ConstructionMethod = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) & 0xF; pos += 2; }
            pos += 2;   // data_reference_index
            item.BaseOffset = ReadN(pos, baseOffsetSize); pos += baseOffsetSize;
            int extents = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
            for (int e = 0; e < extents && pos <= end; e++)
            {
                pos += indexSize;   // extent_index (only meaningful for construction_method 2)
                long eOff = ReadN(pos, offsetSize); pos += offsetSize;
                long eLen = ReadN(pos, lengthSize); pos += lengthSize;
                item.Extents.Add((eOff, eLen));
            }
        }
    }

    private void ParseIref(int off, int len)
    {
        if (len < 4) return;
        bool wide = data[off] != 0;
        int idSize = wide ? 4 : 2;
        foreach (var (type, pOff, pLen) in Children(data, off + 4, len - 4))
        {
            int q = pOff, qEnd = pOff + pLen;
            if (q + idSize + 2 > qEnd) continue;
            int from = (int)ReadN(q, idSize); q += idSize;
            int n = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q)); q += 2;
            var to = new List<int>(n);
            for (int k = 0; k < n && q + idSize <= qEnd; k++, q += idSize) to.Add((int)ReadN(q, idSize));
            References.Add((type, from, to));
        }
    }

    // libavif avifParseItemPropertyAssociation: AVIF 2.3.2.3.2 ('a1lx' shall not be essential), AVIF 2.3.2.1.1 ('a1op'),
    // HEIF 6.5.11.1 ('lsel') and MIAF 7.3.9 (transformative 'clap' / 'irot' / 'imir') shall be. Enforced for AVIF files
    // only, as libavif does; HEIC files are read leniently.
    private static void CheckEssential(int id, string type, bool essential)
    {
        if (essential && type == "a1lx")
            throw new InvalidDataException($"Item ID [{id}] has a {type} property association which must not be marked essential, but is.");
        if (!essential && type is "a1op" or "lsel" or "clap" or "irot" or "imir")
            throw new InvalidDataException($"Item ID [{id}] has a {type} property association which must be marked essential, but is not.");
    }

    private bool IsAvifBrand => Brands.Exists(b => b is "avif" or "avis");

    private void ParseIprp(int off, int len)
    {
        bool avif = IsAvifBrand;
        var kids = Children(data, off, len);
        List<(string Type, int Off, int Len)> props = [];
        foreach (var (type, bOff, bLen) in kids) if (type == "ipco") props = Children(data, bOff, bLen);
        foreach (var (type, bOff, bLen) in kids)
        {
            if (type != "ipma" || bLen < 8) continue;
            int version = data[bOff];
            bool wideIdx = (data[bOff + 3] & 1) != 0;
            int pos = bOff + 4, end = bOff + bLen;
            uint entries = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)); pos += 4;
            for (uint e = 0; e < entries && pos < end; e++)
            {
                int id = version < 1 ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) : (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                pos += version < 1 ? 2 : 4;
                int n = data[pos++];
                var item = Item(id);
                for (int k = 0; k < n && pos < end; k++)
                {
                    int raw = wideIdx ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) : data[pos];
                    pos += wideIdx ? 2 : 1;
                    bool essential = (raw & (wideIdx ? 0x8000 : 0x80)) != 0;
                    int idx = raw & (wideIdx ? 0x7FFF : 0x7F);
                    if (idx == 0 && essential && avif)
                        throw new InvalidDataException($"Box[ipma] for item ID [{id}] contains an illegal essential property index 0.");
                    if (idx >= 1 && idx <= props.Count)
                    {
                        var (pt, po, pl) = props[idx - 1];
                        if (avif) CheckEssential(id, pt, essential);
                        item.Properties.Add((pt, po, pl, essential));
                    }
                }
            }
        }
    }

    private long ReadN(int pos, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | data[pos + i];
        return v;
    }

    /// <summary>Child boxes of a payload region: (type, payload offset, payload length). Handles 64-bit and to-end sizes.</summary>
    internal static List<(string Type, int Off, int Len)> Children(byte[] data, int off, int len)
    {
        var list = new List<(string, int, int)>();
        int pos = off, end = Math.Min(data.Length, off + len);
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = Encoding.ASCII.GetString(data, pos + 4, 4);
            int hdr = 8;
            if (size == 1 && pos + 16 <= end) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8)); hdr = 16; }
            else if (size == 0) size = end - pos;
            if (size < hdr || pos + size > end) break;
            list.Add((type, pos + hdr, (int)size - hdr));
            pos += (int)size;
        }
        return list;
    }
}
