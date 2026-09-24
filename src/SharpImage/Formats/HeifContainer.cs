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

/// <summary>A cursor over one ISO BMFF box payload whose reads are bounded by the box (libavif avifROStream): reading
/// past its end throws <see cref="InvalidDataException"/> ("Truncated Box[type]").</summary>
internal struct BoxReader(byte[] d, int off, int len, string name)
{
    private int pos = off;
    private readonly int end = off + len;

    private readonly void Need(long n)
    {
        if (n < 0 || pos > end - n) throw new InvalidDataException($"Truncated Box[{name}].");
    }
    public readonly int Pos => pos;
    public readonly int Remaining => end - pos;
    public void Skip(int n) { Need(n); pos += n; }
    public byte U8() { Need(1); return d[pos++]; }
    public uint U24() { Need(3); uint v = (uint)((d[pos] << 16) | (d[pos + 1] << 8) | d[pos + 2]); pos += 3; return v; }
    public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos)); pos += 2; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos)); pos += 4; return v; }
    public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(pos)); pos += 8; return v; }
    /// <summary>An unsigned big-endian field of 0, 4 or 8 bytes (iloc offset / length sizes); 0 bytes reads 0.</summary>
    public ulong UN(int size) => size switch
    {
        0 => 0,
        1 => U8(),
        2 => U16(),
        4 => U32(),
        8 => U64(),
        _ => throw new InvalidDataException($"Box[{name}] has an invalid field size {size}."),
    };
    public (byte Version, uint Flags) FullBox() => (U8(), U24());
}

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
            ? (Dim(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p.Off + 4))), Dim(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p.Off + 8))))
            : null;

    private static int Dim(uint v) => v is > 0 and <= int.MaxValue ? (int)v : throw new InvalidDataException($"Invalid ispe dimension {v}.");

    private void ParseMeta(int off, int len)
    {
        var kids = Children(data, off, len);
        foreach (var (type, bOff, bLen) in kids)
        {
            switch (type)
            {
                case "pitm":
                {
                    var b = new BoxReader(data, bOff, bLen, "pitm");
                    PrimaryId = b.FullBox().Version == 0 ? b.U16() : ItemId(b.U32());
                    break;
                }
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

    // Item ids are 16 or 32 bits; ids past int.MaxValue cannot name anything we could reference.
    private static int ItemId(uint id) => id <= int.MaxValue ? (int)id : throw new InvalidDataException($"Item ID {id} is out of range.");

    private void ParseIinf(int off, int len)
    {
        var b = new BoxReader(data, off, len, "iinf");
        if (b.FullBox().Version == 0) b.U16(); else b.U32();   // entry_count (the infe children are what count)
        int pos = b.Pos;
        foreach (var (type, pOff, pLen) in Children(data, pos, off + len - pos))
        {
            if (type != "infe" || pLen < 4) continue;
            int v = data[pOff], q = pOff + 4, qEnd = pOff + pLen;
            if (v < 2 || q + (v == 2 ? 2 : 4) + 6 > qEnd) continue;
            int id = v == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(q)) : ItemId(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(q)));
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
        var b = new BoxReader(data, off, len, "iloc");
        int version = b.FullBox().Version;
        if (version > 2) throw new InvalidDataException($"Box[iloc] version {version} is not supported.");
        byte sizes = b.U8(), sizes2 = b.U8();
        int offsetSize = sizes >> 4, lengthSize = sizes & 0xF;
        int baseOffsetSize = sizes2 >> 4, indexSize = version >= 1 ? sizes2 & 0xF : 0;
        foreach (int sz in (int[])[offsetSize, lengthSize, baseOffsetSize, indexSize])
            if (sz is not (0 or 4 or 8)) throw new InvalidDataException($"Box[iloc] has an invalid field size {sz}.");
        uint count = version < 2 ? b.U16() : b.U32();
        for (uint i = 0; i < count; i++)
        {
            int id = version < 2 ? b.U16() : ItemId(b.U32());
            var item = Item(id);
            if (version >= 1) item.ConstructionMethod = b.U16() & 0xF;
            b.U16();   // data_reference_index
            item.BaseOffset = (long)Math.Min(b.UN(baseOffsetSize), long.MaxValue);
            int extents = b.U16();
            for (int e = 0; e < extents; e++)
            {
                b.UN(indexSize);   // extent_index (only meaningful for construction_method 2)
                long eOff = (long)Math.Min(b.UN(offsetSize), long.MaxValue);
                long eLen = (long)Math.Min(b.UN(lengthSize), long.MaxValue);
                item.Extents.Add((eOff, eLen));
            }
        }
    }

    private void ParseIref(int off, int len)
    {
        var hb = new BoxReader(data, off, len, "iref");
        bool wide = hb.FullBox().Version != 0;
        foreach (var (type, pOff, pLen) in Children(data, off + 4, len - 4))
        {
            var b = new BoxReader(data, pOff, pLen, type);
            int from = wide ? ItemId(b.U32()) : b.U16();
            int n = b.U16();
            var to = new List<int>(n);
            for (int k = 0; k < n; k++) to.Add(wide ? ItemId(b.U32()) : b.U16());
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
            if (type != "ipma") continue;
            var b = new BoxReader(data, bOff, bLen, "ipma");
            var (version, flags) = b.FullBox();
            bool wideIdx = (flags & 1) != 0;
            uint entries = b.U32();
            for (uint e = 0; e < entries; e++)
            {
                int id = version < 1 ? b.U16() : ItemId(b.U32());
                int n = b.U8();
                var item = Item(id);
                for (int k = 0; k < n; k++)
                {
                    int raw = wideIdx ? b.U16() : b.U8();
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
