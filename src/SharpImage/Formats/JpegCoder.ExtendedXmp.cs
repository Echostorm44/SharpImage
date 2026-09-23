using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace SharpImage.Formats;

public static partial class JpegCoder
{
    private const string XmpNsNote = "http://ns.adobe.com/xmp/note/";

    // Adobe XMP Specification Part 3 §1.1.3.1 ExtendedXMP: reassembles the APP1 "http://ns.adobe.com/xmp/extension/"
    // segments (GUID, total length, offset; any order, no gaps or overlaps) whose GUID the standard packet names in
    // xmpNote:HasExtendedXMP, then merges them as libavif does (avifJPEGMergeXMP): the HasExtendedXMP property is
    // removed and the extension's rdf:RDF children are appended to the standard rdf:RDF, the result serialized like
    // libxml2's xmlDocDumpFormatMemory. Returns null (keep the standard packet) when anything does not check out.
    private static string? MergeExtendedXmp(string standard, List<byte[]> segments)
    {
        const int tagLen = 35, guidLen = 32, headLen = tagLen + guidLen + 8;
        byte[]? ext = null;
        bool[]? seen = null;
        string? guid = null;
        foreach (var seg in segments)
        {
            if (seg.Length < headLen) return null;
            string g = Encoding.ASCII.GetString(seg, tagLen, guidLen);
            if (!g.All(Uri.IsHexDigit)) return null;
            uint total = BinaryPrimitives.ReadUInt32BigEndian(seg.AsSpan(tagLen + guidLen));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(seg.AsSpan(tagLen + guidLen + 4));
            int size = seg.Length - headLen;
            if (size == 0 || (ulong)offset + (ulong)size > total || total > 64u << 20) return null;
            if (ext == null)
            {
                guid = g;
                ext = new byte[total];
                seen = new bool[total];
            }
            else if (g != guid || total != ext.Length) return null;
            for (int i = 0; i < size; i++)
            {
                if (seen![offset + i]) return null;
                seen[offset + i] = true;
            }
            seg.AsSpan(headLen, size).CopyTo(ext.AsSpan((int)offset));
        }
        if (ext == null || seen!.Contains(false)) return null;
        bool alternative;
        if (standard.Contains("xmpNote:HasExtendedXMP=\"" + guid, StringComparison.Ordinal)) alternative = false;
        else if (standard.Contains("<xmpNote:HasExtendedXMP>" + guid, StringComparison.Ordinal)) alternative = true;
        else return null;

        var doc = LoadXmpPreservingSpace(standard);
        var extDoc = LoadXmpPreservingSpace(Encoding.UTF8.GetString(ext));
        var rdf = doc == null ? null : FirstElement(doc, "RDF", XmpNsRdf);
        var extRdf = extDoc == null ? null : FirstElement(extDoc, "RDF", XmpNsRdf);
        if (rdf == null || extRdf == null) return null;

        bool removed = false;
        foreach (XmlNode n in rdf.ChildNodes)
        {
            if (n is not XmlElement desc || desc.NamespaceURI != XmpNsRdf || desc.LocalName != "Description") continue;
            if (alternative)
            {
                foreach (XmlNode c in desc.ChildNodes)
                    if (c is XmlElement ce && ce.NamespaceURI == XmpNsNote && ce.LocalName == "HasExtendedXMP")
                    {
                        desc.RemoveChild(ce);
                        removed = true;
                        break;
                    }
            }
            else if (desc.GetAttributeNode("HasExtendedXMP", XmpNsNote) is { } attr)
            {
                desc.Attributes.Remove(attr);
                removed = true;
            }
            if (removed) break;
        }
        if (!removed) return null;

        var copied = new List<(XmlNode Node, List<(string Prefix, string Uri)> ExtraNs)>();
        foreach (XmlNode n in extRdf.ChildNodes) copied.Add((doc!.ImportNode(n, true), NamespacesToDeclare(n)));
        var extraNs = new Dictionary<XmlNode, List<(string, string)>>();
        foreach (var (node, ns) in copied)
        {
            rdf.AppendChild(node);
            if (ns.Count > 0) extraNs[node] = ns;
        }
        return LibxmlSerialize(doc!, extraNs);
    }

    private static XmlDocument? LoadXmpPreservingSpace(string xml)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(reader);
            return doc;
        }
        catch (XmlException) { return null; }
    }

    private static XmlElement? FirstElement(XmlNode root, string local, string ns)
    {
        foreach (XmlNode n in root.ChildNodes)
        {
            if (n is XmlElement e && e.LocalName == local && e.NamespaceURI == ns) return e;
            if (FirstElement(n, local, ns) is { } found) return found;
        }
        return null;
    }

    // libxml2 xmlStaticCopyNode / xmlCopyProp: a copied subtree keeps its own xmlns declarations; each namespace an
    // element or attribute of it uses that none of its copied ancestors declares is declared again on the copy's root
    // (appended after its own declarations, in order of first use: element before its attributes, then children).
    private static List<(string Prefix, string Uri)> NamespacesToDeclare(XmlNode root)
    {
        var added = new List<(string Prefix, string Uri)>();
        if (root is not XmlElement) return added;
        void Walk(XmlElement e, List<string> inScope)
        {
            var scope = new List<string>(inScope);
            foreach (XmlAttribute a in e.Attributes)
                if (a.Prefix == "xmlns") scope.Add(a.LocalName);
                else if (a.Prefix == "" && a.LocalName == "xmlns") scope.Add("");
            void Use(string prefix, string uri)
            {
                if (prefix == "xml" || (prefix == "" && uri == "")) return;
                if (scope.Contains(prefix) || added.Any(x => x.Prefix == prefix)) return;
                added.Add((prefix, uri));
            }
            Use(e.Prefix, e.NamespaceURI);
            foreach (XmlAttribute a in e.Attributes)
                if (a.Prefix != "xmlns" && !(a.Prefix == "" && a.LocalName == "xmlns") && a.Prefix != "") Use(a.Prefix, a.NamespaceURI);
            foreach (XmlNode c in e.ChildNodes)
                if (c is XmlElement ce) Walk(ce, scope);
        }
        Walk((XmlElement)root, []);
        return added;
    }

    // xmlDocDumpFormatMemory(doc, format = 1) for a document parsed with default options: XML declaration (the
    // original version / encoding / standalone), each top-level node followed by a newline, namespace declarations
    // before attributes, empty elements as "<x/>", indentation only inside elements without text children, and —
    // without a declared encoding — every non-ASCII character in text and attribute values as a hex character
    // reference.
    private static string LibxmlSerialize(XmlDocument doc, Dictionary<XmlNode, List<(string Prefix, string Uri)>> extraNs)
    {
        var sb = new StringBuilder();
        var decl = doc.FirstChild as XmlDeclaration;
        string? encoding = string.IsNullOrEmpty(decl?.Encoding) ? null : decl!.Encoding;
        bool escapeNonAscii = encoding == null;
        sb.Append("<?xml version=\"").Append(string.IsNullOrEmpty(decl?.Version) ? "1.0" : decl!.Version).Append('"');
        if (encoding != null) sb.Append(" encoding=\"").Append(encoding).Append('"');
        if (decl?.Standalone == "no") sb.Append(" standalone=\"no\"");
        else if (decl?.Standalone == "yes") sb.Append(" standalone=\"yes\"");
        sb.Append("?>\n");

        void Escape(string s, bool attr)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); continue;
                    case '<': sb.Append("&lt;"); continue;
                    case '>': sb.Append("&gt;"); continue;
                    case '\r': sb.Append("&#13;"); continue;
                    case '"' when attr: sb.Append("&quot;"); continue;
                    case '\t' when attr: sb.Append("&#9;"); continue;
                    case '\n' when attr: sb.Append("&#10;"); continue;
                }
                if (ch < 0x20 && ch != '\t' && ch != '\n') { sb.Append("&#xFFFD;"); continue; }
                if (ch < 0x80 || !escapeNonAscii) { sb.Append(ch); continue; }
                int cp = ch;
                if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(ch, s[++i]);
                if (cp is 0xFFFE or 0xFFFF) cp = 0xFFFD;
                sb.Append("&#x").Append(HexCharRef(cp)).Append(';');
            }
        }

        void Node(XmlNode n, int level, bool format)
        {
            switch (n)
            {
                case XmlElement e:
                    sb.Append('<').Append(e.Name);
                    foreach (XmlAttribute a in e.Attributes)
                        if (a.Prefix == "xmlns" || (a.Prefix == "" && a.LocalName == "xmlns"))
                        {
                            sb.Append(' ').Append(a.Name).Append("=\"");
                            Escape(a.Value, true);
                            sb.Append('"');
                        }
                    if (extraNs.TryGetValue(e, out var extra))
                        foreach (var (prefix, uri) in extra)
                        {
                            sb.Append(prefix == "" ? " xmlns" : " xmlns:" + prefix).Append("=\"");
                            Escape(uri, true);
                            sb.Append('"');
                        }
                    foreach (XmlAttribute a in e.Attributes)
                        if (!(a.Prefix == "xmlns" || (a.Prefix == "" && a.LocalName == "xmlns")))
                        {
                            sb.Append(' ').Append(a.Name).Append("=\"");
                            Escape(a.Value, true);
                            sb.Append('"');
                        }
                    if (!e.HasChildNodes) { sb.Append("/>"); break; }
                    bool childFormat = format;
                    foreach (XmlNode c in e.ChildNodes)
                        if (c is XmlText or XmlWhitespace or XmlSignificantWhitespace or XmlCDataSection or XmlEntityReference) { childFormat = false; break; }
                    sb.Append('>');
                    if (childFormat) sb.Append('\n');
                    foreach (XmlNode c in e.ChildNodes)
                    {
                        if (childFormat && c is XmlElement or XmlProcessingInstruction or XmlComment) sb.Append(' ', 2 * (level + 1));
                        Node(c, level + 1, childFormat);
                        if (childFormat) sb.Append('\n');
                    }
                    if (childFormat) sb.Append(' ', 2 * level);
                    sb.Append("</").Append(e.Name).Append('>');
                    break;
                case XmlText or XmlWhitespace or XmlSignificantWhitespace:
                    Escape(n.Value ?? "", false);
                    break;
                case XmlCDataSection cd:
                    string v = cd.Value ?? "";
                    if (v.Length == 0) { sb.Append("<![CDATA[]]>"); break; }
                    int start = 0;
                    for (int i = 0; i + 2 < v.Length; i++)
                        if (v[i] == ']' && v[i + 1] == ']' && v[i + 2] == '>')
                        {
                            sb.Append("<![CDATA[").Append(v, start, i + 2 - start).Append("]]>");
                            start = i + 2;
                        }
                    if (start < v.Length) sb.Append("<![CDATA[").Append(v, start, v.Length - start).Append("]]>");
                    break;
                case XmlProcessingInstruction pi:
                    sb.Append("<?").Append(pi.Name);
                    if (pi.Value != null) sb.Append(' ').Append(pi.Value);
                    sb.Append("?>");
                    break;
                case XmlComment cm:
                    sb.Append("<!--").Append(cm.Value).Append("-->");
                    break;
            }
        }

        foreach (XmlNode n in doc.ChildNodes)
        {
            if (n is XmlDeclaration or XmlWhitespace or XmlSignificantWhitespace or XmlDocumentType) continue;
            Node(n, 0, true);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // libxml2 xmlSerializeHexCharRef digits: uppercase, starting from the highest non-zero nibble of the highest
    // non-zero byte.
    private static string HexCharRef(int val)
    {
        int shift = 0, bits = val;
        if ((bits & 0xFF0000) != 0) { shift = 16; bits &= 0xFF0000; }
        else if ((bits & 0x00FF00) != 0) { shift = 8; bits &= 0x00FF00; }
        if ((bits & 0xF0F0F0) != 0) shift += 4;
        var s = new StringBuilder();
        do
        {
            int d = (val >> shift) & 0xF;
            s.Append((char)(d < 10 ? '0' + d : 'A' + d - 10));
            shift -= 4;
        } while (shift >= 0);
        return s.ToString();
    }
}
