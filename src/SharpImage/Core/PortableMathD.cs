using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Core;

/// <summary>
/// Double <c>pow</c> / <c>exp</c> / <c>log</c> / <c>log2</c> / <c>log10</c> evaluated with IEEE basic arithmetic and
/// fused multiply-add only (both exactly specified by IEEE 754), so the result is bit-identical on every OS and CPU.
/// <see cref="Math.Pow(double, double)"/> and friends call the C runtime (UCRT on Windows, glibc on Linux), and those
/// disagree in the last bit for about 1 input in 1000. The float counterparts are in <see cref="PortableMath"/>.
/// <para>Each function works in double-double (about 2^-100 relative before the final rounding; table-driven log and exp
/// in the style of fdlibm / Tang), so the result is the correctly rounded double except when the exact value lies
/// within that error of a rounding boundary.</para>
/// </summary>
internal static class PortableMathD
{
    // ln 2 as double-double (hi + lo).
    private const double Ln2Hi = 0.6931471805599453094172321;
    private const double Ln2Lo = 2.319046813846299558417771e-17;

    // log: the mantissa m in [1, 2) split by its top 7 bits into 128 buckets. Inv ~ 1 / bucket start (10 significant
    // bits) so that z = m Inv - 1 is small; buckets from sqrt(2) up stand for m / 2 (EAdj = 1, Hi + Lo = log(1 / (2 Inv)))
    // so log of values just below 1 never cancels against ln 2. Inv is exactly 1 for the first bucket and 1/2 for the
    // last, so log of values near 1 never cancels either.
    // Literal data (no static initialisation, so hot callers carry no class-initialisation checks). Rows of
    // LogTableBits: Inv, Hi, Lo (double bit patterns), EAdj; rows of ExpTableBits: Hi, Lo of 2^(j/64). Both are
    // reproduced from the slow double-double series by ComputeLogTableBits / ComputeExpTableBits (PortableMathTests).
    private const int LogBuckets = 128;
    private const double ExpN = 64.0 / 0.6931471805599453094172321;   // 64 / ln 2 (rounding only steers j)
    private const double Ln2By64Hi = Ln2Hi / 64.0;                     // exact scalings by 2^-6
    private const double Ln2By64Lo = Ln2Lo / 64.0;

    private static ReadOnlySpan<ulong> LogTableBits =>
    [
        0x3FF0000000000000, 0x0000000000000000, 0x0000000000000000, 0,
        0x3FEFA00000000000, 0x3F882448A388A2AA, 0x3C104B16137F09A0, 0,
        0x3FEF600000000000, 0x3F9432A925980CC1, 0xBC38CDAF39004192, 0,
        0x3FEF280000000000, 0x3F9B5CC258B718E6, 0x3C11B8AFBFE81960, 0,
        0x3FEEE80000000000, 0x3FA1CE5A62BC353A, 0xBC4C39390333B61C, 0,
        0x3FEEB00000000000, 0x3FA5715C4C03CEEF, 0xBC2BBF88EC501B54, 0,
        0x3FEE780000000000, 0x3FA91B073EFD7314, 0x3C2D60449AB527C4, 0,
        0x3FEE380000000000, 0x3FAD52ED6405D86F, 0x3C416AEB2214C8C0, 0,
        0x3FEE000000000000, 0x3FB08598B59E3A07, 0xBC5DD7009902BF34, 0,
        0x3FEDC80000000000, 0x3FB26536C3D8C369, 0x3C5D604BE2DD16F0, 0,
        0x3FED900000000000, 0x3FB4485E03DBDFAD, 0x3C51BA349AADBC6E, 0,
        0x3FED600000000000, 0x3FB5E95A4D9791CB, 0x3C5F38745C5C450A, 0,
        0x3FED280000000000, 0x3FB7D33687C293C9, 0xBC5CF063E63E7076, 0,
        0x3FECF00000000000, 0x3FB9C0C32D4D2548, 0x3C4FB0BE3CCC1534, 0,
        0x3FECC00000000000, 0x3FBB6AC88DAD5B1C, 0xBC40057EED1CA5A0, 0,
        0x3FEC880000000000, 0x3FBD5F55659210E2, 0x3C4CE60C2A34A8FA, 0,
        0x3FEC580000000000, 0x3FBF0F70CDD992E3, 0x3C3F6C272C1DCA70, 0,
        0x3FEC280000000000, 0x3FC06135354D4B18, 0x3C518A0D03BA5397, 0,
        0x3FEBF80000000000, 0x3FC13C2605C398C3, 0xBC6FDD94F6508B87, 0,
        0x3FEBC80000000000, 0x3FC2188FD9807263, 0xBC3E7F50C7012690, 0,
        0x3FEB980000000000, 0x3FC2F677CBBC0A96, 0xBC69FBD3E17E5527, 0,
        0x3FEB680000000000, 0x3FC3D5E3126BC27F, 0x3C697C284B6258A9, 0,
        0x3FEB380000000000, 0x3FC4B6D6FEFE22A4, 0x3C6767AB73CA8D5E, 0,
        0x3FEB080000000000, 0x3FC59958FF1D52F1, 0x3C6F4D12C6BF5A88, 0,
        0x3FEAD80000000000, 0x3FC67D6E9D785771, 0xBC610614E0DA5FB8, 0,
        0x3FEAB00000000000, 0x3FC73CB9074FD14D, 0xBC6521A000B4CF00, 0,
        0x3FEA800000000000, 0x3FC823C16551A3C2, 0xBC61232CE70BE782, 0,
        0x3FEA580000000000, 0x3FC8E588EBAC2DBF, 0xBC646A9A5DD7FF10, 0,
        0x3FEA300000000000, 0x3FC9A8778DEBAA38, 0x3C6F47DFD871F87E, 0,
        0x3FEA000000000000, 0x3FCA93ED3C8AD9E3, 0x3C6BCAFA9DE97204, 0,
        0x3FE9D80000000000, 0x3FCB5971A213ACDB, 0xBC6E2F8AADC42F8E, 0,
        0x3FE9B00000000000, 0x3FCC2028AB17F9B4, 0x3C6F11AA3853A5F2, 0,
        0x3FE9880000000000, 0x3FCCE816157F1988, 0xBC55744132A297B0, 0,
        0x3FE9600000000000, 0x3FCDB13DB0D48940, 0x3C5AA11D49F96CB8, 0,
        0x3FE9380000000000, 0x3FCE7BA35EB77E2A, 0x3C411DC86C9B7564, 0,
        0x3FE9100000000000, 0x3FCF474B134DF229, 0xBC527C77DED76AAC, 0,
        0x3FE8E80000000000, 0x3FD00A1C6ADDA473, 0x3C78D688B9E17A8A, 0,
        0x3FE8C00000000000, 0x3FD07138604D5862, 0x3C7CDB16ED4E9138, 0,
        0x3FE8980000000000, 0x3FD0D8FB813EB1EF, 0xBC7CDDE2B0172BD4, 0,
        0x3FE8780000000000, 0x3FD12C77CD00713B, 0x3C64A4508FBCBA26, 0,
        0x3FE8500000000000, 0x3FD1956D3B9BC2FA, 0x3C77B9D68D50A15C, 0,
        0x3FE8280000000000, 0x3FD1FF0FE7CF47A7, 0x3C75B513FF0C1450, 0,
        0x3FE8080000000000, 0x3FD25410494E56C7, 0x3C77AC0EF77F2528, 0,
        0x3FE7E00000000000, 0x3FD2BEF07CDC9354, 0xBC782DAD7FD86088, 0,
        0x3FE7C00000000000, 0x3FD314F1E1D35CE4, 0xBC73D69909E5C3DC, 0,
        0x3FE7980000000000, 0x3FD3811728564CB2, 0xBC6E493A0702B238, 0,
        0x3FE7780000000000, 0x3FD3D81FB5946DBA, 0x3C7C1EAB1642E36C, 0,
        0x3FE7580000000000, 0x3FD42F9F3FF62642, 0xBC7BBF082CCABBAE, 0,
        0x3FE7380000000000, 0x3FD487970E958770, 0x3C7B8465CF25F4C6, 0,
        0x3FE7100000000000, 0x3FD4F637EBBA9810, 0xBC758CB3124B9246, 0,
        0x3FE6F00000000000, 0x3FD54F431B7BE1A9, 0xBC7AACFDBBDAB914, 0,
        0x3FE6D00000000000, 0x3FD5A8CADBBEDFA1, 0xBC5E6C2BDFB3E040, 0,
        0x3FE6B00000000000, 0x3FD602D08AF091EC, 0xBC56E8920C09B73C, 0,
        0x3FE6900000000000, 0xBFD5FF3070A793D4, 0x3C6BC60EFAFC6F70, 1,
        0x3FE6700000000000, 0xBFD5A42AB0F4CFE2, 0x3C78EBCB7DEE9A3C, 1,
        0x3FE6500000000000, 0xBFD548A2C3ADD263, 0x3C6819CF7E308DDC, 1,
        0x3FE6300000000000, 0xBFD4EC973260026A, 0x3C742A87D977DC60, 1,
        0x3FE6180000000000, 0xBFD4A7373CECF997, 0xBC7CB140CABB6BDC, 1,
        0x3FE5F80000000000, 0xBFD44A41B463C47C, 0x3C7D70C8309EDCFB, 1,
        0x3FE5D80000000000, 0xBFD3ECC460EF5F50, 0x3C54313E09807B00, 1,
        0x3FE5B80000000000, 0xBFD38EBDB38ED321, 0x3C73E8CC159AFD10, 1,
        0x3FE5A00000000000, 0xBFD347DD9A987D55, 0x3C64DD4C580919F8, 1,
        0x3FE5800000000000, 0xBFD2E8E2BAE11D31, 0x3C78F4CDB95EBDF8, 1,
        0x3FE5600000000000, 0xBFD2895A13DE86A3, 0xBC77AD24C13F040E, 1,
        0x3FE5480000000000, 0xBFD241558BFD1404, 0x3BE9BAE06A5C8800, 1,
        0x3FE5280000000000, 0xBFD1E0D0C33716BE, 0xBC6E55361A93FE60, 1,
        0x3FE5100000000000, 0xBFD1980D2DD4236F, 0xBC79D3D1B0E4D146, 1,
        0x3FE4F00000000000, 0xBFD136870293A8B0, 0xBC77B66298EDD24A, 1,
        0x3FE4D80000000000, 0xBFD0ED005F657DA4, 0xBC7C56BD2ABFE82A, 1,
        0x3FE4C00000000000, 0xBFD0A324E27390E3, 0xBC77DCFDE8061C04, 1,
        0x3FE4A00000000000, 0xBFD0402594B4D041, 0x3C628EC217A5022E, 1,
        0x3FE4880000000000, 0xBFCFEB0233E607CC, 0xBC66E32D5E8C7080, 1,
        0x3FE4700000000000, 0xBFCF550A564B7B37, 0xBC2C5F6DFD018C40, 1,
        0x3FE4500000000000, 0xBFCE8C0252AA5A60, 0x3C46E03A39BFC8A0, 1,
        0x3FE4380000000000, 0xBFCDF46C0C722D2F, 0xBC605616F20722E8, 1,
        0x3FE4200000000000, 0xBFCD5C216B4FBB91, 0xBC66E443597E4D40, 1,
        0x3FE4080000000000, 0xBFCCC320C0176502, 0xBC6039A653793A84, 1,
        0x3FE3F00000000000, 0xBFCC2968558C18C1, 0x3C673DEE38A3FB6C, 1,
        0x3FE3D80000000000, 0xBFCB8EF670420C3B, 0x3C6999BD0EE3FE88, 1,
        0x3FE3C00000000000, 0xBFCAF3C94E80BFF3, 0x3C5398CFF3641985, 1,
        0x3FE3A80000000000, 0xBFCA57DF28244DCD, 0x3C4B9AF132A24E40, 1,
        0x3FE3900000000000, 0xBFC9BB362E7DFB83, 0xBC6575E31F003E0C, 1,
        0x3FE3780000000000, 0xBFC91DCC8C340BDE, 0xBC5AAF77BFD17182, 1,
        0x3FE3600000000000, 0xBFC87FA06520C911, 0x3C6BF7FDBFA08D9A, 1,
        0x3FE3480000000000, 0xBFC7E0AFD630C274, 0x3C583E270EFCC373, 1,
        0x3FE3300000000000, 0xBFC740F8F54037A5, 0x3C5B264062A84CDC, 1,
        0x3FE3180000000000, 0xBFC6A079D0F7AAD2, 0x3C1EEDCBAC2A7EE0, 1,
        0x3FE3000000000000, 0xBFC5FF3070A793D4, 0x3C5BC60EFAFC6F70, 1,
        0x3FE2E80000000000, 0xBFC55D1AD4232D6F, 0x3C5AC8966E06083C, 1,
        0x3FE2D80000000000, 0xBFC4F099F4A230B2, 0xBC2A0A02A1B24790, 1,
        0x3FE2C00000000000, 0xBFC44D2B6CCB7D1E, 0xBC69F4F6543E1F88, 1,
        0x3FE2A80000000000, 0xBFC3A8EB2D31A376, 0x3C3220A8ABF098F0, 1,
        0x3FE2900000000000, 0xBFC303D718E47FD3, 0x3C06B9C7D9609200, 1,
        0x3FE2800000000000, 0xBFC29552F81FF523, 0xBC6301771C407DBE, 1,
        0x3FE2680000000000, 0xBFC1EED90E2DC2C3, 0x3C64E47B44DB8540, 1,
        0x3FE2500000000000, 0xBFC14785846742AC, 0xBC6A28813E3A7F08, 1,
        0x3FE2400000000000, 0xBFC0D77E7CD08E59, 0xBC69A5DC5E9030AC, 1,
        0x3FE2280000000000, 0xBFC02EBB42BF3D4B, 0x3C4F4B9C01CB92D0, 1,
        0x3FE2180000000000, 0xBFBF7B79FEC37DDF, 0x3C487E897ED01784, 1,
        0x3FE2000000000000, 0xBFBE27076E2AF2E6, 0x3C361578001E0160, 1,
        0x3FE1F00000000000, 0xBFBD4313D66CB35D, 0xBC5790DD951D90FA, 1,
        0x3FE1D80000000000, 0xBFBBEBA818146765, 0x3C5E2DB7C7D5A130, 1,
        0x3FE1C80000000000, 0xBFBB05B49BEE43FE, 0xBC5160C7C252F298, 1,
        0x3FE1B00000000000, 0xBFB9AB42462033AD, 0x3C42099E1C184E90, 1,
        0x3FE1A00000000000, 0xBFB8C345D6319B21, 0x3C24A697AB3424C0, 1,
        0x3FE1880000000000, 0xBFB765BF23A6BE13, 0xBC50FF28EF6A592F, 1,
        0x3FE1780000000000, 0xBFB67BB0726EC0FC, 0x3C5B692C214DDBEC, 1,
        0x3FE1680000000000, 0xBFB590CAFDF01C28, 0xBC53D5C8AAEA76D2, 1,
        0x3FE1500000000000, 0xBFB42EDCBEA646F0, 0xBC4DDD4F935996C8, 1,
        0x3FE1400000000000, 0xBFB341D7961BD1D1, 0x3C5B599F227BECBC, 1,
        0x3FE1300000000000, 0xBFB253F62F0A1417, 0x3C1C125963FC4CF8, 1,
        0x3FE1180000000000, 0xBFB0ED839B5526FE, 0xBC27256EA8988A60, 1,
        0x3FE1080000000000, 0xBFAFFAE9119B9303, 0xBC3BA13162A9C448, 1,
        0x3FE0F80000000000, 0xBFAE19070C276016, 0x3C419918A7A17DC4, 1,
        0x3FE0E80000000000, 0xBFAC355DD0921F2D, 0x3C39B2A03E3BE3A8, 1,
        0x3FE0D00000000000, 0xBFA95C830EC8E3EB, 0xBC4F5A0E80520BF2, 1,
        0x3FE0C00000000000, 0xBFA77458F632DCFC, 0xBC418D3CA87B9296, 1,
        0x3FE0B00000000000, 0xBFA58A5BAFC8E4D5, 0x3C4CE55C2B4E2B72, 1,
        0x3FE0A00000000000, 0xBFA39E87B9FEBD60, 0x3C45BFA937F551BC, 1,
        0x3FE0900000000000, 0xBFA1B0D98923D980, 0x3C3E9AE889BAC480, 1,
        0x3FE0780000000000, 0xBF9D91A66C543CC4, 0x3C1D34E608CBDAAC, 1,
        0x3FE0680000000000, 0xBF99ACE7551CC514, 0xBC33409C1DF8167F, 1,
        0x3FE0580000000000, 0xBF95C45A51B8D389, 0x3C3B10B6C3EC21B4, 1,
        0x3FE0480000000000, 0xBF91D7F7EB9EEBE7, 0x3C2D41FE63D2DC00, 1,
        0x3FE0380000000000, 0xBF8BCF712C74384C, 0x3C1F6842688F4998, 1,
        0x3FE0280000000000, 0xBF83E7295D25A7D9, 0x3BEFF29A11443A10, 1,
        0x3FE0180000000000, 0xBF77EE11EBD82E94, 0x3C161E96E2FC5D90, 1,
        0x3FE0000000000000, 0x0000000000000000, 0x0000000000000000, 1,
    ];
    private static ReadOnlySpan<ulong> ExpTableBits =>
    [
        0x3FF0000000000000, 0x0000000000000000,
        0x3FF02C9A3E778061, 0xBC719083535B085A,
        0x3FF059B0D3158574, 0x3C8D73E2A475B465,
        0x3FF0874518759BC8, 0x3C6186BE4BB28501,
        0x3FF0B5586CF9890F, 0x3C98A62E4ADC6108,
        0x3FF0E3EC32D3D1A2, 0x3C403A1727C57B2A,
        0x3FF11301D0125B51, 0xBC96C51039449B3C,
        0x3FF1429AAEA92DE0, 0xBC932FBF9AF1369F,
        0x3FF172B83C7D517B, 0xBC819041B9D78A78,
        0x3FF1A35BEB6FCB75, 0x3C8E5B4C7B4968E5,
        0x3FF1D4873168B9AA, 0x3C9E016E00A2643D,
        0x3FF2063B88628CD6, 0x3C8DC775814A8494,
        0x3FF2387A6E756238, 0x3C99B07EB6C70572,
        0x3FF26B4565E27CDD, 0x3C82BD339940E9D7,
        0x3FF29E9DF51FDEE1, 0x3C8612E8AFAD1250,
        0x3FF2D285A6E4030B, 0x3C90024754DB41D5,
        0x3FF306FE0A31B715, 0x3C86F46AD23182E5,
        0x3FF33C08B26416FF, 0x3C932721843659A7,
        0x3FF371A7373AA9CB, 0xBC963AEABF42EAE3,
        0x3FF3A7DB34E59FF7, 0xBC75E436D661F5DB,
        0x3FF3DEA64C123422, 0x3C8ADA0911F09EB9,
        0x3FF4160A21F72E2A, 0xBC5EF3691C309269,
        0x3FF44E086061892D, 0x3C489B7A04EF80F1,
        0x3FF486A2B5C13CD0, 0x3C73C1A3B69062EA,
        0x3FF4BFDAD5362A27, 0x3C7D4397AFEC42E2,
        0x3FF4F9B2769D2CA7, 0xBC94B309D25957E5,
        0x3FF5342B569D4F82, 0xBC807ABE1DB13CB4,
        0x3FF56F4736B527DA, 0x3C99BB2C011D93AD,
        0x3FF5AB07DD485429, 0x3C96324C054647AE,
        0x3FF5E76F15AD2148, 0x3C9BA6F93080E65F,
        0x3FF6247EB03A5585, 0xBC9383C17E40B496,
        0x3FF6623882552225, 0xBC9BB60987591C33,
        0x3FF6A09E667F3BCD, 0xBC9BDD3413B26455,
        0x3FF6DFB23C651A2F, 0xBC6BBE3A683C88A6,
        0x3FF71F75E8EC5F74, 0xBC816E4786887A9A,
        0x3FF75FEB564267C9, 0xBC90245957316DD2,
        0x3FF7A11473EB0187, 0xBC841577EE04992B,
        0x3FF7E2F336CF4E62, 0x3C705D02BA15797E,
        0x3FF82589994CCE13, 0xBC9D4C1DD41532D9,
        0x3FF868D99B4492ED, 0xBC9FC6F89BD4F6BC,
        0x3FF8ACE5422AA0DB, 0x3C96E9F156864B29,
        0x3FF8F1AE99157736, 0x3C85CC13A2E3976B,
        0x3FF93737B0CDC5E5, 0xBC675FC781B57ED4,
        0x3FF97D829FDE4E50, 0xBC9D185B7C1B85D1,
        0x3FF9C49182A3F090, 0x3C7C7C46B071F2B6,
        0x3FFA0C667B5DE565, 0xBC9359495D1CD532,
        0x3FFA5503B23E255D, 0xBC9D2F6EDB8D41E2,
        0x3FFA9E6B5579FDBF, 0x3C90FAC90EF7FD30,
        0x3FFAE89F995AD3AD, 0x3C97A1CD345DCC80,
        0x3FFB33A2B84F15FB, 0xBC62805E3084D6F7,
        0x3FFB7F76F2FB5E47, 0xBC75584F7E54AC44,
        0x3FFBCC1E904BC1D2, 0x3C823DD07A2D9E81,
        0x3FFC199BDD85529C, 0x3C811065895048D8,
        0x3FFC67F12E57D14B, 0x3C92884DFF483CAE,
        0x3FFCB720DCEF9069, 0x3C7503CBD1E949D8,
        0x3FFD072D4A07897C, 0xBC9CBC3743797A9E,
        0x3FFD5818DCFBA487, 0x3C82ED02D75B3707,
        0x3FFDA9E603DB3285, 0x3C9C2300696DB534,
        0x3FFDFC97337B9B5F, 0xBC91A5CD4F184B59,
        0x3FFE502EE78B3FF6, 0x3C839E8980A9CC8D,
        0x3FFEA4AFA2A490DA, 0xBC9E9C23179C2893,
        0x3FFEFA1BEE615A27, 0x3C9DC7F486A4B6AF,
        0x3FFF50765B6E4540, 0x3C99D3E12DD8A18B,
        0x3FFFA7C1819E90D8, 0x3C874853F3A59318,
    ];

    /// <summary>The log table from its definition: Inv rounded to 10 bits (1 and 1/2 for the end buckets), Hi + Lo =
    /// log(1 / Inv) or, from sqrt(2) up, log(1 / (2 Inv)) with EAdj = 1.</summary>
    internal static ulong[] ComputeLogTableBits()
    {
        var bits = new ulong[LogBuckets * 4];
        for (int i = 0; i < LogBuckets; i++)
        {
            double centre = 1.0 + (i + 0.5) / 128.0;
            double inv = i == 0 ? 1.0 : i == LogBuckets - 1 ? 0.5 : Math.Round(1024.0 / centre) / 1024.0;
            bool upper = centre > 1.4142135623730951;
            SlowLog(upper ? 2.0 * inv : inv, out double h, out double l);
            bits[i * 4] = BitConverter.DoubleToUInt64Bits(inv);
            bits[i * 4 + 1] = BitConverter.DoubleToUInt64Bits(0.0 - h);
            bits[i * 4 + 2] = BitConverter.DoubleToUInt64Bits(0.0 - l);
            bits[i * 4 + 3] = upper ? 1UL : 0UL;
        }
        return bits;
    }

    /// <summary>The exp table from its definition: 2^(j/64) as double-double.</summary>
    internal static ulong[] ComputeExpTableBits()
    {
        var bits = new ulong[128];
        for (int j = 0; j < 64; j++)
        {
            SlowExp2Frac(j, out double h, out double l);
            bits[j * 2] = BitConverter.DoubleToUInt64Bits(h);
            bits[j * 2 + 1] = BitConverter.DoubleToUInt64Bits(l);
        }
        return bits;
    }

    internal static bool TablesMatchDefinition() =>
        LogTableBits.SequenceEqual(ComputeLogTableBits()) && ExpTableBits.SequenceEqual(ComputeExpTableBits());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Bits(ReadOnlySpan<ulong> table, int index) =>
        BitConverter.UInt64BitsToDouble(Unsafe.Add(ref MemoryMarshal.GetReference(table), index));
    // ---------------------------------------------------------------- public API

    /// <summary>Natural logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Log(double x)
    {
        if (!(x > 0.0 && x <= double.MaxValue) && !TryLogSpecial(x, out double special))
        {
            return special;
        }
        LogDD(x, out double h, out double l);
        return h + l;
    }

    /// <summary>
    /// Natural logarithm to within about 2 ulp, about twice as fast as <see cref="Log(double)"/>: still bit-identical
    /// on every OS (same tables, double arithmetic only), for heuristics such as entropy cost estimates where the last
    /// bit does not need to be the correctly rounded one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double LogFast(double x)
    {
        if (!(x > 0.0 && x <= double.MaxValue) && !TryLogSpecial(x, out double special))
        {
            return special;
        }
        int e = 0;
        if (x < 2.2250738585072014e-308)
        {
            x *= 18014398509481984.0;
            e = -54;
        }
        long bits = BitConverter.DoubleToInt64Bits(x);
        int ci = (int)((bits >> 45) & 127) * 4;
        e += (int)((bits >> 52) & 0x7FF) - 1023 + (int)Unsafe.Add(ref MemoryMarshal.GetReference(LogTableBits), ci + 3);
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        double z = Math.FusedMultiplyAdd(m, Bits(LogTableBits, ci), -1.0);
        double z2 = z * z, z4 = z2 * z2;
        double a0 = Math.FusedMultiplyAdd(z, 1.0 / 3.0, -0.5), a1 = Math.FusedMultiplyAdd(z, 1.0 / 5.0, -1.0 / 4.0);
        double a2 = Math.FusedMultiplyAdd(z, 1.0 / 7.0, -1.0 / 6.0), a3 = Math.FusedMultiplyAdd(z, 1.0 / 9.0, -1.0 / 8.0);
        double p = Math.FusedMultiplyAdd(z4, Math.FusedMultiplyAdd(z2, a3, a2), Math.FusedMultiplyAdd(z2, a1, a0));
        double l1p = Math.FusedMultiplyAdd(z2, p, z);   // log1p(z) to z^9
        return Math.FusedMultiplyAdd(e, Ln2Hi, Math.FusedMultiplyAdd(e, Ln2Lo, Bits(LogTableBits, ci + 1)) + l1p);
    }

    /// <summary>Base-10 logarithm to within about 2 ulp (see <see cref="LogFast"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Log10Fast(double x) => LogFast(x) * Log10EHi;

    /// <summary>e^x to within about 1 ulp, bit-identical on every OS (see <see cref="LogFast"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double ExpFast(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }
        if (x > 709.8)
        {
            return double.PositiveInfinity;
        }
        if (x < -745.2)
        {
            return 0.0;
        }
        double n = Math.Round(x * ExpN);
        int ni = (int)n;
        int j = ni & 63;
        double r = Math.FusedMultiplyAdd(-n, Ln2By64Lo, Math.FusedMultiplyAdd(-n, Ln2By64Hi, x));
        double r2 = r * r;
        double b0 = Math.FusedMultiplyAdd(r, 1.0 / 6.0, 0.5), b1 = Math.FusedMultiplyAdd(r, 1.0 / 120.0, 1.0 / 24.0);
        double em1 = Math.FusedMultiplyAdd(r2, Math.FusedMultiplyAdd(r2, Math.FusedMultiplyAdd(r, 1.0 / 5040.0, 1.0 / 720.0), b1) * r2 + b0, r);
        double t = Bits(ExpTableBits, j * 2);
        return ScaleB(Math.FusedMultiplyAdd(t, em1, t + Bits(ExpTableBits, j * 2 + 1)), (ni - j) >> 6);
    }

    /// <summary>x^y for positive finite x to within about (2 + |y ln x|) ulp (exp(y log x) from the fast kernels, so the
    /// error grows with the size of the result's exponent; other arguments go
    /// to <see cref="Pow(double, double)"/>), bit-identical on every OS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double PowFast(double x, double y)
    {
        if (x > 0.0 && x <= double.MaxValue && Math.Abs(y) <= double.MaxValue && x != 1.0 && y != 0.0 && y != 1.0)
        {
            return ExpFast(y * LogFast(x));
        }
        return Pow(x, y);
    }

    /// <summary>Base-2 logarithm to within about 2 ulp (see <see cref="LogFast"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Log2Fast(double x) => LogFast(x) * 1.4426950408889634074;

    /// <summary>Base-2 logarithm (exact for powers of two).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Log2(double x)
    {
        if (!(x > 0.0 && x <= double.MaxValue) && !TryLogSpecial(x, out double special))
        {
            return special;
        }
        LogDD(x, out double h, out double l);
        return MulDDRound(h, l, Log2EHi, Log2ELo);   // (h + l) * log2(e)
    }

    /// <summary>Base-10 logarithm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Log10(double x)
    {
        if (!(x > 0.0 && x <= double.MaxValue) && !TryLogSpecial(x, out double special))
        {
            return special;
        }
        LogDD(x, out double h, out double l);
        return MulDDRound(h, l, Log10EHi, Log10ELo);   // (h + l) * log10(e)
    }

    /// <summary>Logarithm of <paramref name="x"/> in base <paramref name="newBase"/> (as <see cref="Math.Log(double, double)"/>:
    /// log x / log newBase).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Log(double x, double newBase) => Log(x) / Log(newBase);

    /// <summary>Cube root (exact for perfect cubes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Cbrt(double x)
    {
        if (x == 0.0 || double.IsNaN(x) || double.IsInfinity(x))
        {
            return x;
        }
        double ax = Math.Abs(x);
        // y ~ ax^(1/3) within an ulp or so (FreeBSD s_cbrt.c's exponent / 3 bit estimate and polynomial to 23 bits, then a
        // Halley step), then one Newton step in double-double on y^3 = ax for the last bit.
        long bits = BitConverter.DoubleToInt64Bits(ax);
        double t;
        if (bits < 0x0010000000000000L)
        {
            double s = ax * 18014398509481984.0;   // 2^54: subnormal -> normal
            long sb = BitConverter.DoubleToInt64Bits(s);
            t = BitConverter.Int64BitsToDouble((long)((ulong)(sb >> 32) / 3 + 696219795) << 32);
        }
        else
        {
            t = BitConverter.Int64BitsToDouble((long)((ulong)(bits >> 32) / 3 + 715094163) << 32);
        }
        double q = (t * t) * (t / ax);
        t *= (1.87595182427177009643 + q * (-1.88497979543377169875 + q * 1.621429720105354466140))
            + ((q * q) * q) * (-0.758397934778766047437 + q * 0.145996192886612446982);
        // one Halley step (cubic convergence): from 23 bits to within an ulp or so
        double t3 = t * t * t;
        double y = t * ((t3 + 2.0 * ax) / (2.0 * t3 + ax));
        // y3 = y^3 as double-double
        double y2h = y * y;
        double y2l = Math.FusedMultiplyAdd(y, y, -y2h);
        double y3h = y2h * y;
        double y3l = Math.FusedMultiplyAdd(y2h, y, -y3h) + y2l * y;
        // correction (ax - y^3) / (3 y^2)
        double dh = (ax - y3h) - y3l;
        double r = y + dh / (3.0 * y2h);
        return x < 0.0 ? -r : r;
    }

    /// <summary>e^x.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Exp(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }
        return ExpDD(x, 0.0);
    }

    /// <summary>x^y with C99 pow special cases.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // math kernels: optimised code from the first call (no tier 0)
    public static double Pow(double x, double y)
    {
        if (x > 0.0 && x <= double.MaxValue && Math.Abs(y) <= double.MaxValue && x != 1.0 && y != 0.0 && y != 1.0 && y != 2.0
            && y != 0.5)
        {
            // the common case: positive finite base, finite exponent with no exact shortcut
            LogDD(x, out double ph, out double pl);
            double qh = y * ph;
            double qe = Math.FusedMultiplyAdd(y, ph, -qh);
            return ExpDD(qh, qe + y * pl);
        }
        if (y == 0.0 || x == 1.0)
        {
            return 1.0;
        }
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return double.NaN;
        }

        bool yIsInt = double.IsInteger(y);
        bool yIsOdd = yIsInt && Math.Abs(y) < 9007199254740992.0 && ((long)y & 1) != 0;
        double ax = Math.Abs(x);

        if (double.IsInfinity(y))
        {
            if (ax == 1.0)
            {
                return 1.0;
            }
            return (ax < 1.0) == (y > 0.0) ? 0.0 : double.PositiveInfinity;
        }
        if (x == 0.0 || double.IsInfinity(x))
        {
            bool huge = (x == 0.0) == (y < 0.0);
            double mag = huge ? double.PositiveInfinity : 0.0;
            return double.IsNegative(x) && yIsOdd ? -mag : mag;
        }
        if (x < 0.0 && !yIsInt)
        {
            return double.NaN;
        }
        if (y == 1.0)
        {
            return x;
        }
        if (y == 2.0)
        {
            return x * x;
        }
        if (y == 0.5 && x > 0.0)
        {
            return Math.Sqrt(x);   // IEEE square root: correctly rounded everywhere
        }

        LogDD(ax, out double lh, out double ll);
        // t = y * log|x| in double-double
        double th = y * lh;
        double te = Math.FusedMultiplyAdd(y, lh, -th);
        double tl = te + y * ll;
        double r = ExpDD(th, tl);
        return x < 0.0 && yIsOdd ? -r : r;
    }

    // ---------------------------------------------------------------- cores

    private static bool TryLogSpecial(double x, out double result)
    {
        result = 0.0;
        if (double.IsNaN(x) || x < 0.0)
        {
            result = double.NaN;
            return false;
        }
        if (x == 0.0)
        {
            result = double.NegativeInfinity;
            return false;
        }
        if (double.IsPositiveInfinity(x))
        {
            result = double.PositiveInfinity;
            return false;
        }
        if (x == 1.0)
        {
            return false;   // result 0
        }
        return true;
    }

    /// <summary>log(x) as hi + lo for positive finite x.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void LogDD(double x, out double hi, out double lo)
    {
        int e = 0;
        if (x < 2.2250738585072014e-308)
        {
            x *= 18014398509481984.0;   // 2^54: subnormal -> normal
            e = -54;
        }
        long bits = BitConverter.DoubleToInt64Bits(x);
        e += (int)((bits >> 52) & 0x7FF) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);   // [1, 2)
        int ci = (int)((bits >> 45) & 127) * 4;
        e += (int)Unsafe.Add(ref MemoryMarshal.GetReference(LogTableBits), ci + 3);
        double inv = Bits(LogTableBits, ci);
        // z = m * inv - 1 exactly as a double-double: p + pe = m * inv, p - 1 is exact (p within 2^-7 of 1).
        double p = m * inv;
        double pe = Math.FusedMultiplyAdd(m, inv, -p);
        double zh = p - 1.0;
        FastTwoSum(zh, pe, out zh, out double zl);   // |pe| <= ulp(p) / 2 <= |zh| unless zh == 0
        // log1p(z) = z - z^2/2 + z^3/3 - ..., |z| < 2^-7: the first two terms in double-double, the rest in double.
        double z2h = zh * zh;
        double z2l = Math.FusedMultiplyAdd(zh, zh, -z2h) + 2.0 * zh * zl;
        // z^3 (1/3 - z/4 + ... - z^9/12) by Estrin's scheme (short dependency chains), fused multiply-adds throughout
        double z4 = z2h * z2h;
        double a0 = Math.FusedMultiplyAdd(zh, -1.0 / 4.0, 1.0 / 3.0), a1 = Math.FusedMultiplyAdd(zh, -1.0 / 6.0, 1.0 / 5.0);
        double a2 = Math.FusedMultiplyAdd(zh, -1.0 / 8.0, 1.0 / 7.0), a3 = Math.FusedMultiplyAdd(zh, -1.0 / 10.0, 1.0 / 9.0);
        double a4 = Math.FusedMultiplyAdd(zh, -1.0 / 12.0, 1.0 / 11.0);
        double poly = Math.FusedMultiplyAdd(z4, Math.FusedMultiplyAdd(z4, a4, Math.FusedMultiplyAdd(z2h, a3, a2)), Math.FusedMultiplyAdd(z2h, a1, a0));
        double tail = zh * z2h * poly;
        // s = z - z2 / 2 + tail
        FastTwoSum(zh, -0.5 * z2h, out double sh, out double sl);   // |z| > z^2 / 2
        sl += zl - 0.5 * z2l + tail;
        // + log c_i
        TwoSum(Bits(LogTableBits, ci + 1), sh, out double ah, out double al);
        al += Bits(LogTableBits, ci + 2) + sl;
        // + e * ln 2
        double eh = e * Ln2Hi;
        double ee = Math.FusedMultiplyAdd(e, Ln2Hi, -eh) + e * Ln2Lo;
        FastTwoSum(eh, ah, out double bh, out double bl);   // e == 0, or |e ln2| >= ln2 > |log c + log1p z|
        bl += ee + al;
        FastTwoSum(bh, bl, out hi, out lo);
    }

    /// <summary>e^(xh + xl), |xl| tiny relative to xh.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double ExpDD(double xh, double xl)
    {
        if (xh > 709.8)
        {
            return double.PositiveInfinity;
        }
        if (xh < -745.2)
        {
            return 0.0;
        }
        double n = Math.Round(xh * ExpN);
        int ni = (int)n;
        int j = ni & 63;
        int k = (ni - j) >> 6;
        // r = x - n * ln2/64 in double-double (n * Ln2By64Hi via FMA remainder).
        double ph = n * Ln2By64Hi;
        double pe = Math.FusedMultiplyAdd(n, Ln2By64Hi, -ph);
        double rh = xh - ph;                          // exact: xh and ph are within a factor of 2 (or rh is tiny)
        double rl = xl - pe - n * Ln2By64Lo;
        FastTwoSum(rh, rl, out rh, out rl);
        // expm1(r) = r + r^2/2 + r^3/6 + ..., |r| <= ln2/128: first two terms double-double, the rest double.
        double r2h = rh * rh;
        double r2l = Math.FusedMultiplyAdd(rh, rh, -r2h) + 2.0 * rh * rl;
        double b0 = Math.FusedMultiplyAdd(rh, 1.0 / 24.0, 1.0 / 6.0), b1 = Math.FusedMultiplyAdd(rh, 1.0 / 720.0, 1.0 / 120.0);
        double b2 = Math.FusedMultiplyAdd(rh, 1.0 / 40320.0, 1.0 / 5040.0);
        double tail = rh * r2h * Math.FusedMultiplyAdd(r2h * r2h, b2, Math.FusedMultiplyAdd(r2h, b1, b0));
        FastTwoSum(rh, 0.5 * r2h, out double qh, out double ql);   // |r| > r^2 / 2
        ql += rl + 0.5 * r2l + tail;
        // T * (1 + q) = T + T * q
        double th = Bits(ExpTableBits, j * 2), tl = Bits(ExpTableBits, j * 2 + 1);

        double mh = th * qh;
        double ml = Math.FusedMultiplyAdd(th, qh, -mh) + th * ql + tl * qh;
        FastTwoSum(th, mh, out double sh, out double sl);   // T >= 1 > |T q|
        sl += ml + tl;
        double s = sh + sl;
        return ScaleB(s, k);
    }

    /// <summary>s * 2^k with one rounding, also when the result is subnormal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ScaleB(double s, int k)
    {
        if (k > -1000 && k < 1000)
        {
            return s * BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
        }
        return Math.ScaleB(s, k);   // managed and exact (a single rounding for subnormal results)
    }

    // log2(e) and log10(e) as double-double
    private const double Log2EHi = 1.4426950408889634074;
    private const double Log2ELo = 2.0355273740931033e-17;
    private const double Log10EHi = 0.43429448190325182765;
    private const double Log10ELo = 1.0983196502167651e-17;

    /// <summary>(h + l) * (ch + cl) rounded to double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double MulDDRound(double h, double l, double ch, double cl)
    {
        double q = h * ch;
        double e = Math.FusedMultiplyAdd(h, ch, -q) + (h * cl + l * ch);
        return q + e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TwoSum(double a, double b, out double s, out double e)
    {
        s = a + b;
        double bb = s - a;
        e = (a - (s - bb)) + (b - bb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FastTwoSum(double a, double b, out double s, out double e)
    {
        s = a + b;
        e = b - (s - a);
    }

    // ---------------------------------------------------------------- start-up only (slow, double-double series)

    /// <summary>log(x) for x in [0.7, 1.43] by 2 atanh(s), s = (x - 1) / (x + 1), in double-double.</summary>
    private static void SlowLog(double x, out double hi, out double lo)
    {
        if (x == 1.0)
        {
            hi = 0.0;
            lo = 0.0;
            return;
        }
        // s = (x - 1) / (x + 1); x - 1 exact, x + 1 as double-double.
        double nh = x - 1.0;
        TwoSum(x, 1.0, out double dh, out double dl);
        DivDD(nh, 0.0, dh, dl, out double sh, out double sl);
        MulDD(sh, sl, sh, sl, out double zh, out double zl);
        // sum_{k>=0} z^k / (2k + 1), 40 terms (z < 0.0313: far below 2^-110)
        double ah = 0.0, al = 0.0;
        for (int k = 40; k >= 0; k--)
        {
            MulDD(ah, al, zh, zl, out ah, out al);
            DivDD(1.0, 0.0, 2 * k + 1, 0.0, out double ch, out double cl);
            AddDD(ah, al, ch, cl, out ah, out al);
        }
        MulDD(ah, al, sh, sl, out ah, out al);
        hi = 2.0 * ah;
        lo = 2.0 * al;
    }

    /// <summary>2^(j/64) = e^(j/64 ln 2) by its Taylor series in double-double.</summary>
    private static void SlowExp2Frac(int j, out double hi, out double lo)
    {
        MulDD(j / 64.0, 0.0, Ln2Hi, Ln2Lo, out double xh, out double xl);   // in [0, ln2)
        double sh = 1.0, sl = 0.0, th = 1.0, tl = 0.0;
        for (int k = 1; k <= 30; k++)
        {
            MulDD(th, tl, xh, xl, out th, out tl);
            DivDD(th, tl, k, 0.0, out th, out tl);
            AddDD(sh, sl, th, tl, out sh, out sl);
        }
        hi = sh;
        lo = sl;
    }

    private static void AddDD(double ah, double al, double bh, double bl, out double h, out double l)
    {
        TwoSum(ah, bh, out double s, out double e);
        e += al + bl;
        FastTwoSum(s, e, out h, out l);
    }

    private static void MulDD(double ah, double al, double bh, double bl, out double h, out double l)
    {
        double p = ah * bh;
        double e = Math.FusedMultiplyAdd(ah, bh, -p) + (ah * bl + al * bh);
        FastTwoSum(p, e, out h, out l);
    }

    private static void DivDD(double ah, double al, double bh, double bl, out double h, out double l)
    {
        double q1 = ah / bh;
        MulDD(q1, 0.0, bh, bl, out double ph, out double pl);
        AddDD(ah, al, -ph, -pl, out double rh, out double rl);
        double q2 = rh / bh;
        MulDD(q2, 0.0, bh, bl, out ph, out pl);
        AddDD(rh, rl, -ph, -pl, out rh, out _);
        double q3 = rh / bh;
        FastTwoSum(q1, q2, out h, out l);
        AddDD(h, l, q3, 0.0, out h, out l);
    }
}
