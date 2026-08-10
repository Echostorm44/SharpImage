// JPEG XL ANS/prefix symbol reader with hybrid-uint tokenisation and LZ77 back-references.
// Ported from libjxl (dec_ans.h ANSSymbolReader) and validated bit-exactly against a Python
// prototype on libjxl-produced lossless files.
namespace SharpImage.Formats.Jxl;

internal sealed class JxlAnsReader
{
    private const int WindowMask = (1 << 20) - 1;

    private readonly JxlAnsCode code;
    private readonly JxlBitReader br;
    private readonly bool usePrefix;
    private readonly int logEntrySize;
    private readonly int entrySizeMinus1;
    private uint state;

    private readonly bool usesLz77;
    private readonly uint lz77Threshold;
    private readonly uint lz77MinLength;
    private readonly int lz77Ctx;
    private readonly int numSpecial;
    private readonly int[] special;
    private readonly int[]? window;
    private int numDecoded;
    private int numToCopy;
    private int copyPos;

    public JxlAnsReader(JxlAnsCode code, JxlBitReader br, int distanceMultiplier = 0)
    {
        this.code = code;
        this.br = br;
        usePrefix = code.UsePrefix;
        if (!usePrefix)
        {
            logEntrySize = JxlBits.AnsLogTabSize - code.LogAlpha;
            entrySizeMinus1 = (1 << logEntrySize) - 1;
        }

        state = usePrefix ? (uint)(JxlBits.AnsSignature << 16) : br.ReadBits(32);
        usesLz77 = code.Lz77Enabled;
        lz77Threshold = code.Lz77MinSymbol;
        lz77MinLength = code.Lz77MinLength;
        lz77Ctx = code.DistanceContext;
        numSpecial = distanceMultiplier > 0 ? JxlEntropy.NumSpecialDistances : 0;
        special = new int[numSpecial];
        for (int i = 0; i < numSpecial; i++)
        {
            special[i] = JxlEntropy.SpecialDistances[i, 0] + (distanceMultiplier * JxlEntropy.SpecialDistances[i, 1]);
        }

        window = usesLz77 ? new int[1 << 20] : null;
    }

    private int ReadSymbol(int histo)
    {
        if (usePrefix)
        {
            return code.Huffs![histo].ReadSymbol(br);
        }

        uint res = state & (JxlBits.AnsTabSize - 1);
        AliasEntry[] table = code.AliasTables![histo];
        int i = (int)(res >> logEntrySize);
        int pos = (int)(res & (uint)entrySizeMinus1);
        AliasEntry e = table[i];
        bool greater = pos >= e.Cutoff;
        int freq = e.Freq0 ^ (greater ? e.Freq1XorFreq0 : 0);
        int offset = (greater ? e.Offsets1 : 0) + pos;
        int value = greater ? e.Right : i;
        state = (uint)((freq * (state >> JxlBits.AnsLogTabSize)) + offset);
        if (state < (1u << 16))
        {
            state = (state << 16) | br.PeekBits(16);
            br.Consume(16);
        }

        return value;
    }

    public uint ReadHybridUintClustered(int histo)
    {
        if (usesLz77 && numToCopy > 0)
        {
            int ret = window![copyPos++ & WindowMask];
            numToCopy--;
            window[numDecoded++ & WindowMask] = ret;
            return (uint)ret;
        }

        int token = ReadSymbol(histo);
        if (usesLz77 && token >= lz77Threshold)
        {
            numToCopy = (int)JxlEntropy.ReadHybridUintConfig(code.Lz77LengthConfig, (uint)(token - lz77Threshold), br) + (int)lz77MinLength;
            int dtok = ReadSymbol(lz77Ctx);
            int dist = (int)JxlEntropy.ReadHybridUintConfig(code.UintConfigs[lz77Ctx], (uint)dtok, br);
            if (dist < numSpecial)
            {
                dist = special[dist];
            }
            else
            {
                dist = dist + 1 - numSpecial;
            }

            if (dist < 1)
            {
                dist = 1;
            }

            if (dist > numDecoded)
            {
                dist = numDecoded;
            }

            copyPos = numDecoded - dist;
            return ReadHybridUintClustered(histo);
        }

        uint val = JxlEntropy.ReadHybridUintConfig(code.UintConfigs[histo], (uint)token, br);
        if (usesLz77)
        {
            window![numDecoded++ & WindowMask] = (int)val;
        }

        return val;
    }

    public uint ReadHybridUintCtx(int ctx) => ReadHybridUintClustered(code.ContextMap[ctx]);

    public bool CheckFinal() => usePrefix || state == (uint)(JxlBits.AnsSignature << 16);
}
