using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Ksud;

/// <summary>闂傚倸鍊风粈渚€骞夐敓鐘偓鍐幢濡炴洘妞介獮瀣晜閻ｅ苯甯撴繝鐢靛Т閿曘倝鎮ч崱娆戠焼闁告劦鍠楅悡鏇熺箾閹存繂鑸规鐐达耿閺屾洟宕奸姀鈩冩瘓闂佽鍠氶、濠囁囬弻銉﹀€垫慨妯煎帶楠炴﹢鏌ゅú顏冩喚妞ゃ垺宀搁崺鈧い鎺嗗亾妞ゆ洩缍佸畷濂稿即閻斿憡鐝梻浣告啞濞诧箓宕滃☉銏″仒闁靛鏅滈埛鎴︽煙缁嬫寧鎹ｉ柍钘夘樀閺岋絽螖閳ь剙螞濠靛鈧礁顫濇０婵囨櫓闂佸吋浜介崕閬嶅礉?Python 闂備浇顕у锕傦綖婢舵劕绠栭柛顐ｆ礀绾惧潡姊洪鈧粔鎾儗?result 闂傚倷娴囬褏鈧稈鏅濈划娆撳箳濡炲皷鍋撻崘顔煎窛闁哄鍨艰闂備線鈧偛鑻晶鎾煛鐏炶濡奸柍钘夘槸椤粓宕卞Ο鐐€濆?/summary>
public sealed class Asset
{
    public string Name { get; set; } = string.Empty;
    public ulong FnVa { get; set; }
    public long TableOff { get; set; }
    public int? DataOff { get; set; }
    public int? CompLen { get; set; }
    public int? Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public string Method { get; set; } = string.Empty;
    public ulong? HashRefVa { get; set; }
    public bool HashInBinary { get; set; }
    public string OutFile { get; set; } = string.Empty;

    public Asset() { }

    /// <summary>濠电姷鏁告慨浼村垂閻撳簶鏋栨繛鎴炴皑閻捇鏌熺紒銏犳灈闁哄嫨鍎叉穱濠囧Χ閸涱喖娅ｉ梺鍛婂灩婵炩偓闁哄本鐩鎾Ω閵壯傜泊闂備胶顭堥鍡涘箲閸ヮ剙绠栨い蹇撶墕瀹告繃銇勯弽銊х煠婵℃煡浜跺缁樻媴閸涘﹤鏆堥梺鍛婃⒐閻楃娀骞冮悜钘壩ㄩ柍杞扮閹偛顪冮妶鍡樺蔼闁搞劎鎳撹灋婵せ鍋撻柡灞剧洴婵＄兘顢涢悙鎼偓宥咁渻閵堝棗濮冪紒顔界懇瀵鏁撻悩鑼槰闂侀潧顭堥崕娲汲閵忋倖鈷戦弶鐐村閸斿秹鏌熼悷鐗堝枠闁绘侗鍠氶埀顒婄秵閸犳牜绮诲☉娆嶄簻闁圭儤鍨甸埀顒€缍婂�?OutFile 闂傚倸鍊烽悞锕傛儑瑜版帒绀夌€光偓閳ь剟鍩€椤掍礁鍤柛娆忓暙椤曪絿绱掑Ο鑲╃槇闂佸憡娲忛崝灞剧妤ｅ啯鐓曢柨鏃囶嚙楠炴牕霉閻樺啿绗х紒杈ㄥ浮閻擃剟骞撻幒鎴澬曢梻浣芥〃缁€浣虹矓闂堟侗鍤楅柛鏇ㄥ灠闁卞洦鎱ㄥ鍡楀幐濠?/summary>
    public Asset(Asset other)
    {
        Name = other.Name; FnVa = other.FnVa; TableOff = other.TableOff;
        DataOff = other.DataOff; CompLen = other.CompLen; Size = other.Size;
        Sha256 = other.Sha256; Verified = other.Verified; Method = other.Method;
        HashRefVa = other.HashRefVa; HashInBinary = other.HashInBinary; OutFile = other.OutFile;
    }
}

public sealed class SkippedAsset
{
    public string Name { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>闂傚倸鍊风粈浣革耿鏉堚晛鍨濇い鏍仜缁€澶愭煛閸ゅ爼顣﹀Ч妤呮⒑閹肩偛鍔撮柛鎾寸懅缁顢氶埀顒勫蓟閺囩喎绶為柛顐ｇ箓婵洟姊洪崫銉ユ灁闁稿鍊濆�?/summary>
public sealed class Report
{
    public string Source { get; set; } = string.Empty;
    public ushort Machine { get; set; }
    public int TotalEntries { get; set; }
    public int Extracted { get; set; }
    public List<Asset> Assets { get; set; } = new();
    public List<SkippedAsset> Skipped { get; set; } = new();
    public List<string> Log { get; set; } = new();
}

/// <summary>
/// �?rust-embed 8.x闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟杈鹃檮閸嬪鏌涢埄鍐槈缂?compression / include-flate闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟娆¤娲弻鍡楊吋閸涱喚鈧剟姊虹憴鍕妞ゆ泦鍛笉闁挎繂顦伴悡娆撴⒑椤撱劎鐣遍柡瀣枛閺?ELF 濠电姷鏁搁崑娑㈡偤閵娧冨灊鐎光偓閸曨剙鍓规繛杈剧悼閸庛倕鐣烽崣澶嬪弿婵＄偠顕ф禍楣冩倵鐟欏嫭绀冪€光偓缁嬫鍤曢柛顭戝亝婵挳鏌涢幇鈺佸�?
/// 闂傚倸鍊搁崐鎼佸磹閹间焦鍋嬪┑鐘叉处閸嬶繝鏌ㄩ弴鐐测偓鎼佹倿閸偁浜滈柟瀵稿仜椤曟粎绱掓担鐟邦棆缂佽鲸甯炵槐鎺懳熼懡銈庢Ч闁诲氦顫夊ú蹇涘垂娴犲绠栭柕蹇曞Х閺嗗鏌ｅΔ鈧悧濠傗枔娴犲鐓熼幖娣€ゅ鎰版煙閸涘﹥鍊愰柟顖氭湰閹峰懘鎼归崷顓犲姼婵＄偑鍊栭崝蹇涘箠閸ヮ剙缁╅柨鏇炲€归悡娆撴煛婢跺﹦浠㈡い锝呯－缁辨帡鍩€椤掑嫬绀嬫い鏍ㄧ☉閳ь剛鏁婚弻锝夊閻樺吀绮甸梺鎼炲€愰崑鎾剁�?Python 闂備浇顕у锕傦綖婢舵劕绠栭柛顐ｆ礀绾惧潡姊洪鈧粔鎾儗?extract_rustembed.py 闂傚倸鍊烽悞锕傛儑瑜版帒绀夌€光偓閳ь剟鍩€椤掍礁鍤柛娆忓暣楠炲啳顦撮柍褜鍓欏﹢杈ㄦ叏閻戣姤鍋柍褜鍓熷娲川婵犲倸顫囬梺鍛婃煥閼活垵顣鹃梺瑙勫婢ф鎮″☉妯忓綊鏁愰崶鍓佸姼缂備降鍔忓畷鐢垫閹烘挻缍囬柕濞垮劚閹藉灚�?/// </summary>
public static partial class Extractor
{
    // -------------------------------------------------- 闂傚倷绀侀幖顐λ囬柆宥呯；婵炴垟鎳囬埀顒婄畵閹粓鎸婃径鎰紬婵犵數鍋涘Ο濠冪濠婂牊鍎楁俊銈呭暊閸嬫捇鐛崹顔煎濠碘槅鍋呯粙鎴︽晝閵忋倖鍋╃€光偓閳ь剛澹曟禒瀣厱閻忕偛澧介妴鎺戭熆瑜滄禍鐐哄箟閹间礁绾ч柟绋垮閹瑥顪冮妶鍡樼┛缂傚秳绀侀锝夊箻椤旇棄浜归梺鎯ф禋閸嬪懘顢?--

    /// <summary>
    /// 闂傚倸鍊峰ù鍥綖婢舵劦鏁婇柡宥庡幖绾惧鏌涘☉鍗炲季�?aarch64 闂傚倸鍊风粈渚€骞夐敓鐘插瀭闁稿繐鍚嬮崣蹇涙煏閸繍妲告慨瑙勭叀閺岋綁寮崹顔藉€梺鍝勬媼閸撶喖骞冨鈧幃娆撴濞戞顥氶梻浣告惈濡厽绂嶉鍕垫綎?ADRP+ADD 闂傚倷娴囧畷鍨叏瀹曞洦顐介柕鍫濇处椤洟鏌￠崶銉ョ仾闁稿鏅涢埞鎴︽偐鐎圭姴顥濈紓浣插亾闁告洦鍨遍悡锝夌叓閸ャ劌鍤繛鍏煎姍閺岋綁鏁愭径澶嬪枤闂佸搫澶囬崜婵嬪箯閸涙潙浼犻柛鏇炵仛濮ｅ鏌ｆ惔銈庢綈闁规悂顥撶划鍫熺瑹閳ь剟銆佸鑸垫櫜濠㈣泛锕ゅ畵鍡涙⒑缂佹﹩鐒炬繛鍛礀閳绘捇濮€閵堝棌鎷洪梻鍌氱墐閺呯姴顬婇鐣岀瘈闁靛繆鍩楅鍫氣偓?VA)�?    /// 闂傚倸鍊风粈渚€骞夐敓鐘冲仭妞ゆ牗绋撻々鍙夌節婵犲倻澧曢柣顓燁殘閹叉悂骞嬮悩鍏哥瑝濠电偞鍨崹鍦矆閸愵亝鍠愰柟杈剧畱閻撴洟鏌涢鐘茬伄缁惧墽鍘ч…璺ㄦ崉閻氭潙濮涢柡宥忕節閺岋絾鎯旈敍鍕驳缂佺偓婢樼粔鍓佸垝鐎ｎ喖绠虫俊銈傚亾閸ユ挳妫呴銏″婵炲弶绮撻幃?
    ///   ADRP  Xd, label        (0x90000000 / mask 0x9F000000)
    ///   ADD   Xd, Xn, #imm     (0x91000000 / mask 0xFF800000, 64 �?
    /// 闂傚倷绀侀幖顐λ囬锕€鐤炬繝濠傜墕閽冪喖鏌曟繛鍨壄婵炲樊浜滈崘鈧銈嗘尵閸嬬喖顢欓崶銊х瘈闁靛骏绲剧涵鐐亜閹存繃鍤囬柟顖欑窔閹兘骞婃繝鍌滅Ш闁诡喒鏅犻、娆戜焊閺嵮傚闂佸搫娲㈤崹褰掑触鐟欏嫨浜滈柟鎯у船閻忣亪鏌涚€ｎ偆绠撻棁澶愭煕韫囨艾浜归柡鍡橈耿閺屽秷顧侀柛鎾寸懅閳ь剚绋堥弲鐘荤嵁閸℃稑绫嶉柛銉㈡櫇閺夋悂姊洪崫鍕偓钘夆枖閺囥垺鍋￠柍鍝勬噺閳锋垿鏌涘☉姗堝伐缂佹纰嶉妵鍕Ω閵壯冾暫闂佸磭绮幑鍥嵁鐎ｎ亖鏀介柟閭﹀墯椤撳潡姊绘担鍛婃儓婵炲眰鍔嶉幈銊╂偨缁嬫寧杈堥棅顐㈡处缁嬫帡鎮￠弴銏＄厵闁绘垶蓱閹嫭绻涢崼鐔烘噰闁哄本娲熷畷鍗炍旈埀顒勫汲閿濆鐓涘ù锝呮憸鏁堥悗瑙勬礈閸樠囧煘閹达箑骞㈡繛鍡樏弬鈧梻鍌氬€搁崐鐑芥倿閿曚降浜归柛鎰典簽閻瑩鏌涢弴銊ュ箺妞ゎ偅娲橀幈銊ノ熼幐搴ｃ€愰弶?    /// </summary>
    public static List<ulong> ScanAarch64Refs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        byte[] data = elf.Data;
        long? fnOff = elf.VaToOff(fnVa);
        if (fnOff is null)
            return new List<ulong>();
        var refs = new List<ulong>();
        long end = Math.Min(data.Length - 3, fnOff.Value + window);
        long i = fnOff.Value;
        while (i + 4 <= end)
        {
            uint insn = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)i));
            if ((insn & 0x9F000000) == 0x90000000)               // ADRP
            {
                uint rd = insn & 0x1F;
                uint immlo = (insn >> 29) & 0x3;
                uint immhi = (insn >> 5) & 0x7FFFF;
                int imm = (int)((immhi << 2) | immlo);
                if ((imm & 0x80000) != 0)
                    imm -= 0x100000;
                ulong pcVa = elf.OffToVa(i) ?? fnVa;
                ulong page = (pcVa >> 12) << 12;
                ulong target = (page + ((ulong)imm << 12)) & 0xFFFFFFFFFFFFFFFFUL;
                // 缂傚倸鍊搁崐鐑芥倿閿曞倹鍎婇梺顒€绉撮悙濠冦亜椤撶喎鐏ラ柟顔兼嚇濮婄粯鎷呮笟顖滃姼缂備胶绮〃鍛村煝閺傚簱妲堥柕蹇曞Х閻ｅ搫鈹戞幊閸婃洟骞婅箛娑樼�?ADD闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟杈鹃檮閸嬪鏌涢埄鍐槈闁稿被鍔戦弻鐔衡偓鐢殿焾鏍℃繝娈垮灠閵堢顫忔繝姘劦妞ゆ帒瀚粻娑欍亜閺嶃劏澹樼紒鐘冲灴濮婂宕掑顑藉亾閹间礁纾圭憸鐗堝笒缁犱即鏌涢幇顓犮偞�?1 闂傚倸鍊风粈渚€骞栭位鍥敃閿曗偓缁€鍫熺節闂堟稓澧愰柛瀣崌瀹曞綊顢曢妶搴㈡缂傚倷绀侀崐鍦暜閳╁啩绻嗛柟顖涘閻も偓闂佸搫娲ㄦ刊顓炍?
                for (long j = i + 4; j <= Math.Min(end, i + 12); j += 4)
                {
                    uint a = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)j));
                    if ((a & 0xFF800000) == 0x91000000)          // ADD (immediate, 64-bit)
                    {
                        uint rn = (a >> 5) & 0x1F;
                        if (rn == rd)
                        {
                            uint imm12 = (a >> 10) & 0xFFF;
                            uint shift = (a >> 22) & 1;
                            ulong final = (target + ((ulong)imm12 << (shift == 1 ? 12 : 0))) & 0xFFFFFFFFFFFFFFFFUL;
                            long? ro = elf.ResolveRef(final);
                            ulong? rva = ro is not null ? elf.OffToVa(ro.Value) : null;
                            if (rva is not null && !elf.IsExecVa(rva.Value))
                                refs.Add(final);
                            break;
                        }
                    }
                    else if ((a & 0x7F000000) is 0x91000000 or 0x93000000)
                    {
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            i += 4;
        }
        var seen = new HashSet<ulong>();
        var outRefs = new List<ulong>(refs.Count);
        foreach (ulong r in refs)
            if (seen.Add(r))
                outRefs.Add(r);
        return outRefs;
    }

    /// <summary>
    /// 闂傚倸鍊峰ù鍥綖婢舵劦鏁婇柡宥庡幖绾惧鏌涘☉鍗炲季�?x86_64 闂傚倸鍊风粈渚€骞夐敓鐘插瀭闁稿繐鍚嬮崣蹇涙煏閸繍妲告慨瑙勭叀閺岋綁寮崹顔藉€梺鍝勬媼閸撶喖骞冨鈧幃娆撴濞戞顥氶梻浣告惈濡厽绂嶉鍕垫綎?RIP 闂傚倸鍊烽懗鍫曞磿閻㈢鐤炬繝闈涱儌閳ь剨绠撳畷濂稿Ψ椤旇姤娅旀俊鐐€栧Λ鍐极椤曗偓瀹曟垿骞樼€涙ê顎撻梺鍛婂姈閸庡啿鈻撻鐐╂斀闁绘劙娼х敮鍫曟煕閻曚礁浜版い銏″哺閺佹劖寰勬繝鍌氱ギ闂備胶绮…鍫濃枍閺囩偐鏋旈柕蹇嬪€栭埛鎴炴叏閻熺増鎼愬┑鈥炽偢閹顫濋悡搴＄睄閻庢鍠楁繛濠囧箖閵忋倖鍋傞幖杈剧秵閸炶淇婇悙顏勨偓鏍蓟閵娾晛瑙﹂悗锝庡枛缁€瀣旈敐鍛殲闁绘挻娲滈幉鍛婂緞閹邦剛锛涢梺鐟板⒔缁垶鎮?    /// 闂傚倷娴囧畷鐢稿窗閹邦優娲冀椤剚绋戦埥澶娢?LEA r64,[rip+disp32] (48/4C 8D) �?MOV r64,[rip+disp32] (48/4C 8B)�?    /// </summary>
    public static List<ulong> ScanX86_64Refs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        byte[] data = elf.Data;
        long? fnOff = elf.VaToOff(fnVa);
        if (fnOff is null)
            return new List<ulong>();
        var refs = new List<ulong>();
        long end = Math.Min(data.Length - 7, fnOff.Value + window);
        long i = fnOff.Value;
        while (i + 7 <= end)
        {
            if (data[i] is 0x48 or 0x4C && data[i + 1] is 0x8D or 0x8B)
            {
                byte modrm = data[i + 2];
                if ((modrm & 0xC7) == 0x05)                      // mod=00, rm=101 -> RIP+disp32
                {
                    int disp = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)i + 3));
                    ulong nextVa = elf.OffToVa(i + 7) ?? (fnVa + (ulong)(i + 7 - (elf.VaToOff(fnVa) ?? 0)));
                    ulong target = (nextVa + (ulong)disp) & 0xFFFFFFFFFFFFFFFFUL;
                    long? ro = elf.ResolveRef(target);
                    ulong? rva = ro is not null ? elf.OffToVa(ro.Value) : null;
                    if (rva is not null && !elf.IsExecVa(rva.Value))
                        refs.Add(target);
                    i += 7;
                    continue;
                }
            }
            i += 1;
        }
        var seen = new HashSet<ulong>();
        var outRefs = new List<ulong>(refs.Count);
        foreach (ulong r in refs)
            if (seen.Add(r))
                outRefs.Add(r);
        return outRefs;
    }

    /// <summary>闂傚倸鍊风粈浣革耿闁秮鈧箓宕奸妷瀣喘瀵埖鎯旈幘瀛樻殜闂備線鈧偛鑻晶鎾煛鐏炲墽娲撮柡浣瑰姍瀹曟﹢濡搁妷顔锯偓鐑芥⒑閼姐倕浠﹂柛鎴濈秺瀹曟垶绻濋崒婊勬闂侀潻瀵岄崢鎼佸磻閹剧粯鏅查幖瀛樼箘閻╁酣姊虹拠鏌ヮ€楁い顓犲厴瀵鈽夊Ο閿嬬€婚梺褰掑亰閸撴稒绂掗幘顔藉€垫繛鍫濈仢閺嬶附淇婇銏狀仾闁哄懌鍎靛娲箹閻愭彃濮夐梺鍝勬噺缁诲倿鈥旈崘鈺冾浄閻庯綆鍋嗛崢鍗炩攽閻愬弶顥為柛鏃€顨嗛弲鍫曨敂閸℃瑧锛滈梺鐟板悁閻掞箑鈻嶆繝鍥ㄧ厵妞ゆ棁宕甸惌娆忊攽閳╁啯鍊愰柡浣瑰姍瀹曟帒顫濋銏╂�?/summary>
    public static List<ulong> ScanRefs(ElfFile elf, ulong fnVa, int window = 0x1000)
    {
        return elf.Machine switch
        {
            183 => ScanAarch64Refs(elf, fnVa, window),           // EM_AARCH64
            62 => ScanX86_64Refs(elf, fnVa, window),             // EM_X86_64
            _ => throw new InvalidOperationException($"濠电姷鏁搁崑鐐哄垂閸洖绠伴柛婵勫劤閻捇鏌ｉ姀銏╃劸闁哄鐒﹂妵鍕即濡も偓娴滄儳螖閻橀潧浠﹂柛鏃€鐗犻獮蹇涘川鐎涙ê鈧粯淇婇姘倯婵炲牊鎮傚缁樻媴閸涘﹥鍎撻梺缁橆殔濡繈鐛弽顓ф晝闁冲灈鏅滈悗娲⒑閸︻厼鍔嬫慨濠呭吹娴滄悂鎮介崨濠備化闂佹悶鍎烘禍婊堟儍濞差亝�?machine={elf.Machine}"),
        };
    }

    // ------------------------------------------------------------ ENTRIES �?--

    /// <summary>
    /// 闂傚倷娴囬褍顫濋敃鍌︾稏濠㈣埖鍔栭崕妤併亜閺傚灝鈷旂紒�?rust-embed �?ENTRIES 闂傚倷娴囧畷鐢稿磻閻愬鐟规俊銈呮噺閸嬪倸顭跨捄鐑樻拱妞ゎ偅娲樼换娑㈠幢濡纰嶉梺鍦嚀閻栧ジ寮婚弴鐔虹闁割煈鍠掗崑鎾诲冀椤撶偟鐓戦棅顐㈡处缁嬫帡宕戦悩宕囩闁瑰鍋熸晶顏堟煕閺冣偓閻熝呮閹烘せ鈧箓骞掗弮鍌滄闂備礁鎼懟顖炴嚐椤栫儐鏁囧┑鍌滎焾瀹告繃銇勯弽銊р姇闁伙妇鍋涢埞鎴︽倷閼搁潧娑х紓浣藉皺閺咁偆鍒掗弮鍌涘磯閻炴稈鍓濋悘渚€姊洪悷閭﹀殶濠殿喚鏁诲畷浼村幢濞戞瑧鍘遍梺鏂ユ櫅閸犳艾鈻撻弴鐘电＜闁稿本姘ㄦ晶鏇㈡煃閽樺妯€妤犵偞顭囩划浣冦亹閹烘柨寮界紓鍌氬€峰ù鍥敋瑜忕划鏃堝醇閺囩偟鐣洪梺鍝勵槼濞夋洟寮抽崱娑欏€甸柨婵嗛婢х増銇勯敂璇蹭喊婵?    /// 婵犵數濮甸鏍闯椤栨粌绶ら柣锝呮湰瀹曟煡鏌熺€涙绠ラ柣鎺曞Г閵囧嫰寮介顫勃�?3 �?qword�?闂傚倸鍊风粈渚€骞夐敓鐘冲殞闁告挆鍛厠闂佸湱铏庨崰妤呭磻閵堝洦鍠愰柡鍐ㄧ墢瀹撲線鏌熼悧鍫熺凡闁绘劕锕ラ妵鍕箳瀹ュ浂妲�? 闂傚倸鍊风粈渚€骞夐敓鐘冲殞闁告挆鍛厠闂佸湱铏庨崰妤呭磻閵堝洦鍠愰柡鍌涱儥濞兼牜鎲搁悧鍫濈瑨闁搞劌鍊归妵鍕籍閸屾瀚涢�? 闂傚倸鍊搁崐鎼佸磹閹间礁纾婚柛鈩冪☉閸屻劑鏌ゅù瀣澒闁稿鎹囧畷褰掝敃椤掑啠鍋撶捄銊㈠亾鐟欏嫭绀冮柣鐔叉櫊楠炲﹪鎮欓崫鍕庛劑鏌ㄩ弮鍌滄憘闁哥啿鍋撻梻鍌氬€风粈浣革耿闁秮鈧箓宕煎婵囨そ椤㈡﹢濮€閻樻�?�?    /// �?qword �?+ 闂傚倸鍊搁崐鎼佸磹閻㈢纾婚柟鍓х帛閻撴洘銇勯鐔风仴闁哄绋掗妵?+ 闂傚倸鍊风粈渚€骞夐敓鐘插瀭闁稿繐鍚嬮崣蹇涙煏閸繍妲告慨瑙勭叀閺岋綁寮崒姘粯缂備胶濮抽崡鎶藉蓟濞戙垹唯妞ゆ梻鍘ч～顏堟煟鎼淬垻顣茬紒瀣灴閸┿儲寰勯幇顒傤啋闂佸綊顣﹂悞锔芥櫏濠碉紕鍋戦崐鏍蓟閵娾晜鍤愭い鏍剱閺佸鏌ㄥ┑鍡欏ⅱ闁汇倐鍋撻梻浣告惈閸燁偊宕愰幖浣哥劵闁绘鐗呯换鍡涙煏閸繃鎼愮€涙繈姊哄ú璇插箹妞わ箑宕嵄闁归偊鍏橀弨浠嬫倵閿濆簼绨芥い锔哄姂閹鐛崹顔煎濠电偠顕滅粻鎾愁�?闂傚倷绀侀幖顐λ囬锕€鐤炬繝闈涱儏绾惧鏌ｉ弬鍨倯闁?24 闂傚倷娴囬褏鈧稈鏅濈划娆撳箳濡炲皷鍋撻崘鈺佺窞閻庯絺鏅濈粻姘渻閵堝棙灏甸柛搴㈠姇椤洦瀵肩€涙鍘繝銏ｆ硾濡瑥鈻嶉崱妯诲弿?缂傚倸鍊搁崐鐑芥⒔瀹ュ绀夐悘鐐电叓濞差亜鐓涢柛鎰ㄦ櫅鎼村﹪姊洪崨濠傚Е闁绘挸鐗撻幃锟犲Ψ閳哄倻鍘梺鍓插亝缁诲倿寮虫繝鍥ㄧ�?    /// 闂傚倷绀侀幖顐λ囬锕€鐤炬繝濠傜墕閽冪喖鏌曟繛鍨�?[(闂傚倸鍊风粈渚€骞夐敓鐘冲殞闁告挆鍛厠闂佸湱铏庨崰妤呭�? 闂傚倸鍊搁崐鎼佸磹閹间礁纾婚柛鈩冪☉閸屻劑鏌ゅù瀣澒闁稿鎹囧畷褰掝敃椤愶絿鐓�? 闂傚倷娴囧畷鐢稿磻閻愮數鐭欓煫鍥ㄧ☉缁€澶愭煙鏉堝墽鐣辨潻婵嬫倵楠炲灝鍔氭い锔诲灣婢规洘绻濆鍗炲絼闂佹悶鍎崝宥夋⒐?]闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟杈鹃檮閸嬪鈹戦悩鎻掝仱闁稿鎹囧畷褰掝敃閵堝洨鍘掗梻浣筋嚃閸犳洟宕￠搹顐＄箚闁归棿绀侀悡姗€骞栫€涙ɑ灏ù婊冨⒔閳ь剙绠嶉崕閬嵥囬婊冾棜濠电姵姘ㄩ崣鎾绘煕閵夛絽濡介梻鈧崸妤佺厓闁告繂瀚崳鍦磼閻樺啿鈻曢柡宀€鍠栭幃婊兾熼搹閫涙偅缂備胶鍋撳妯肩矓瑜版帒钃熼柕濞垮劗濡插牊鎱ㄥΔ鈧Λ娆掑€撮�?    /// </summary>
    public static List<(string Name, ulong FnVa, long TableOff)> FindEntries(
        byte[] view, ElfFile elf, int maxNameLen = 256, int minCluster = 3)
    {
        var hits = new List<(long P, string Name, ulong Fn)>();
        for (long p = 0; p <= view.Length - 24; p += 8)
        {
            ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)p));
            ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)p + 8));
            ulong v3 = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)p + 16));
            long? noff = elf.VaToOff(v1);
            if (noff is null || elf.IsWritableVa(v1) || elf.IsExecVa(v1))
                continue;
            byte nb = view[noff.Value];
            if (nb < 0x20 || nb > 0x7E)
                continue;
            if (v2 < 1 || v2 > (ulong)maxNameLen)
                continue;
            if (!elf.IsExecVa(v3))
                continue;
            if (noff.Value + (long)v2 > view.Length)
                continue;
            bool printable = true;
            for (long k = noff.Value; k < noff.Value + (long)v2; k++)
            {
                byte b = view[k];
                if (b < 0x20 || b > 0x7E) { printable = false; break; }
            }
            if (!printable)
                continue;
            var name = ElfFile.EncodingLatin1.GetString(view, (int)noff.Value, (int)v2);
            // rust-embed tables can sit next to unrelated (&str, value) data.
            // Keep only names that are provably KernelSU module assets.
            if (!name.EndsWith("_kernelsu.ko", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Regex.IsMatch(name, @"(?i)(?:^|/)(?:android\d+(?:\.\d+)?[-_]\d+\.\d+|\d+\.\d+(?:\.\d+)?[-_]android\d+).+_kernelsu\.ko$"))
                continue;
            hits.Add((p, name, v3));
        }

        // 闂傚倸鍊峰ù鍥ㄧ珶閸喆浠堢紒瀣儥濞尖晠鏌曟繛褍瀚禒蹇擃渻閵堝棗濮ч梻鍕瀵煡顢楁担鐟板伎濠碘槅鍨板锟犲传濞差亝鐓曢柍杞扮閳ь剙娼￠獮?ENTRIES �?(闂傚倸鍊风粈渚€骞夐敓鐘冲殞闁告挆鍛厠闂佸湱铏庨崰妤呭�?闂傚倸鍊搁崐鎼佸磹閻㈢纾婚柟鍓х帛閻撴洘銇勯鐔风仴闁哄绋掗妵?闂傚倸鍊风粈渚€骞夐敓鐘插瀭闁稿繐鍚嬮崣蹇涙煏閸繍妲告慨? 濠电姷鏁搁崑鐐哄垂閸洖绠伴柟闂寸蹈閸ヮ剦鏁嶉柣鎰絻閸嬪秹姊虹憴鍕姢濠⒀冩捣婢规洘绺介崨濠勫幈闂佸搫娲㈤崝宀勭嵁濡ゅ懏鐓熼柨婵嗩槷閹插墽鈧鍠栭悥濂稿春閿熺姴绀冮柕濞垮€栭崺娑㈡⒒娴ｅ湱婀介柛銊ョ秺閳ワ箑鐣￠柇锕€娈ㄥ銈嗗姧缁犳垹绮婚妷锔轰簻闁哄倸鐏濋埛鏃堟煟閹捐尙绐旀慨濠冩そ楠炴劖鎯旈敐鍌涱潔闂備胶绮〃鍛存晝椤忓牄鈧礁螖閸涱喖鈧兘鏌ｉ幋鐐冩岸骞?24�?        hits.Sort((a, b) => a.P.CompareTo(b.P));
        var clusters = new List<List<(long P, string Name, ulong Fn)>>();
        var cur = new List<(long P, string Name, ulong Fn)>();
        foreach (var h in hits)
        {
            if (cur.Count > 0 && h.P == cur[^1].P + 24)
                cur.Add(h);
            else
            {
                if (cur.Count > 0)
                    clusters.Add(cur);
                cur = new List<(long P, string Name, ulong Fn)> { h };
            }
        }
        if (cur.Count > 0)
            clusters.Add(cur);
        var big = clusters.Where(c => c.Count >= minCluster).ToList();
        if (big.Count > 0)
            clusters = big;

        var entries = new List<(string, ulong, long)>();
        var seen = new HashSet<string>();
        foreach (var c in clusters)
            foreach (var (p, name, fn) in c)
                if (seen.Add(name))
                    entries.Add((name, fn, p));
        entries.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return entries;
    }

    // ------------------------------------------------------- LazyLock 闂傚倸鍊搁崐鎼佸磹閹间焦鍋嬪┑鐘叉处閸嬶繝鏌ㄩ弴鐐测偓鎼佹倿閸偁浜滈柟杈剧稻绾埖銇勯敂鑲╃暤闁哄矉缍侀弫鎰板炊瑜屾竟鏇㈡⒑?--

    /// <summary>
    /// 闂傚倷绀侀幖顐λ囬锕€鐤炬繝濠傜墕閽冪喖鏌曟繛鍨�?LazyLock 闂傚倸鍊搁崐鎼佸磹閹间焦鍋嬪┑鐘叉处閸嬶繝鏌ㄩ弴鐐测偓鎼佹倿閸偁浜滈柟杈剧稻绾埖銇勯敂鑲╃暤闁哄矉缍侀崺濠傗枎韫囨柨顦╅梺鎼炲€曠粔褰掑蓟濞戞矮娌柛鎾楀懐鏆ら梻浣风串缁犳挻绂嶉崼鏇炶摕婵炴垯鍨瑰敮闂佹寧姊婚弲顐ょ矙閸ヮ剚鈷戦柛婵勫劚娴狅箓鏌熼崨濠傗枙闁绘侗鍠栭鍏煎緞婵犲嫬濮︽俊鐐€栫敮鎺斺偓姘煎墰閳ь剚鑹鹃ˇ顖炩€︾捄銊﹀磯濞撴凹鍨伴崜浼存⒑娴兼瑧鐣辩紓宥咃躬瀵鈽夊鍡樺兊濡炪倖鎸鹃崑鐔哥閹绢喗鍋℃繝濠傛噹椤ｅジ鏌ｉ幙鍕瘈闁绘侗鍠栭濂稿醇椤愶絺鏋嗗┑锛勫亼閸婃垿宕瑰ú顏勫瀭闁汇垻顭堥悡婵嬫煙閸撗呭笡闁稿鍔戦弻娑⑩€﹂幋婵呯敖闂佸搫妫涙繛鈧慨濠冩そ瀹曘劍绻濋崒婊€绱樺┑鐘愁問閸犳捇宕愬┑鍡╁殨闁规儼濮ら崑鍕煟閹捐櫕鎹ｆい鏂挎喘濮婃椽骞栭悙鎻掑Ф闂佸憡鎸诲銊╁焵?VA)闂傚倸鍊风粈渚€骞夐敍鍕殰婵°倕鎳岄埀顒€鍟鍕箛椤戔斂鍎甸弻娑㈠箛闂堟稒鐏堥�?    /// rust-embed 濠电姷鏁搁崑鐐哄垂閸洖绠伴柛婵勫劤閻捇鏌熺紒銏犳灈缂佺姵鐗犻弻鐔煎箲閹伴潧娈梺鍛婂灩婵炩偓闁哄本鐩鎾Ω閵夈倗鏁栫紓鍌欑劍閸炶崵绮婚幘璇茶摕闁挎繂顦儫闂侀潧顦崕鐑樺閸愩劉鏀介柣鎰問閸庣娀鏌涙繝鍐疄闁绘侗鍣ｉ崺鈧い鎺嗗亾闂囧鏌ㄥ┑鍡樺櫣闁愁亜寮堕妵鍕箛閳轰讲鍋撻弽顓炴辈婵犲﹤鐗婇悡鏇㈡煙閹佃櫕娅呭┑鈩冨缁绘繈鍩€椤掑嫬绾ч幖瀛樻尰椤秹姊虹憴鍕姢缁剧虎鍘奸悺顓㈡⒒娴ｇ瓔鍤冮柛鐘愁殜楠炴垿宕堕鈧�?    ///   v3.1.0 �?      [thunk, 0, 0, 3]
    ///   v1.0.2/v2.1.2 �?[0, 8, 8, thunk, helper, 0]   闂傚倸鍊烽懗鍫曞磻閵娾晛纾块柡灞诲劚閽冪喖鏌ㄩ悢鍝勑㈤柣?helper 闂傚倸鍊风粈浣虹礊婵犲偆鐒界憸蹇曞垝閺冨牊顥堟繛瀵割劜缁茶法鍒掑▎蹇婃瀻闁诡垎鍕靛悪闂傚倷绀侀幖顐⒚洪妸锔藉弿闁汇垻顭堝Ч鏌ョ叓閸ャ劎鈯曢柣鎾寸☉闇夐柨婵嗘噺閹牊銇勯敐鍫濅汗闁逞屽墲椤骞愭搴㈩偨婵﹩鍓涢弳锔剧磼鐎ｎ収鍤﹂柡鍐ㄧ墕缁€瀣煏婵炲灝鍔欏�?    ///   v3.0.0 �?      闂傚倸鍊风粈渚€骞夐垾鎰佹綎缂備焦蓱閸欏繘鏌熼锝囦汗鐟滅増甯掗悙濠冦亜閹哄棗浜鹃梻鍌氬亞閸ㄥ爼寮婚弴鐔虹闁割煈鍠栨儫闂備線鈧偛鑻晶顕€鏌涢悤浣哥仸濠碉紕鏁诲畷鐔碱敍濞戞ê澹冮梻浣告啞缁哄潡宕曢幎鍓垮洭顢氶埀顒€顫忓ú顏勫窛濠电姴鍟犻幏缁樼節濞堝灝鏋熼柛搴㈠▕濠€渚€姊洪幖鐐插姶闁告挻鐟╁濠氼敍閻愬鍘繝鐢靛仜閻忔繈宕濆顓熷弿闁挎繂妫楁慨宥夋煛鐏炵澧查柟宄版噽閹叉挳宕熼鐐叉濠电姷鏁告慨顓㈠磻閹炬番浜滈煫鍥ㄦ尰椤ョ偤鏌涚€ｎ偓鑰块柡灞炬礃缁绘盯鎮欓浣圭翻�?+0x20 �?    /// </summary>
    public static List<ulong> FindStaticFns(byte[] view, ElfFile elf, ulong staticVa, int scanBytes = 0x40)
    {
        long? off = elf.VaToOff(staticVa);
        if (off is null)
            return new List<ulong>();
        long end = Math.Min(view.Length - 8, off.Value + scanBytes);
        var fns = new List<ulong>();
        for (long q = off.Value; q <= end; q += 8)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(view.AsSpan((int)q));
            if (elf.IsExecVa(v))
                fns.Add(v);
        }
        return fns;
    }

    // ------------------------------------------------------------ DEFLATE 闂傚倷娴囧畷鐢稿窗閹扮増鍋￠弶鍫氭櫅缁躲倝鏌ｉ敐鍛拱闁?--

    private static byte[] ReadCapped(Stream s, int maxLen)
    {
        using var outMs = new MemoryStream();
        var buf = new byte[8192];
        int total = 0;
        while (total <= maxLen)
        {
            int n = s.Read(buf, 0, buf.Length);
            if (n <= 0)
                break;
            total += n;
            outMs.Write(buf, 0, n);
        }
        return outMs.ToArray();
    }

    private static int ProduceLength(byte[] data, int off, int len, int capOut)
    {
        try
        {
            using var ms = new MemoryStream(data, off, len);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            return ReadCapped(ds, capOut).Length;
        }
        catch (InvalidDataException)
        {
            return -1;
        }
    }

    /// <summary>濠电姷鏁搁崑娑㈡偤閵娧冨灊鐎光偓閸曨剙娈ｅ銈嗙墱閸嬫盯宕归崒鐐茬婵烇綆鍓欐俊濂告煢閸愵亜鏋涢柡灞炬礃瀵板嫬鈽夊杈ㄦ婵＄偑鍊戦崕鑼崲閸儱钃熼柨鐔哄Т闁卞洦鎱ㄥ鍡楀幐濠㈣娲熷铏规嫚閼碱剛顔囩紓浣虹帛閸旀骞戦姀鐘栨棃宕橀埡濠備缓婵＄偑鍊栭幐鍫曞垂閸︻厾鐭嗛柛宀€鍋為悡蹇擃熆鐠鸿櫣澧曢柛鏃€纰嶉妵鍕Χ閸涱喖娅х紓浣介哺閹瑰洤鐣烽幒鎳虫棃鍩€椤掍胶顩插Δ锝呭暞閳锋帡鏌涚仦缁㈡當濞存粓绠栧娲礈閼碱剙甯ラ梺鍝ュУ閻楃娀銆佸▎鎰瘈闁搞儯鍔庨崣鍡椻攽閻愭潙鐏﹂柨姘亜閵婏妇鎳冮柍瑙勫灴閹瑩宕ｆ径濠勭崺闂備胶鎳撳鍫曞箰閹惰棄鏋侀柟鐗堟緲瀹告繃銇勯幘璺侯€屽ù鍏肩箞濮婃椽宕崟鍨﹂梺缁橆殔閿曨亪鐛幇鏉跨濞达絿顭堥幆鐐差渻閵堝棙灏甸柛鐘冲姉缁辩偤宕堕浣哄幍濡炪倖鐗曞Λ妤呭疮娴兼潙鍌ㄩ柟鐑橆殕閻撶喖骞栨潏鍓хɑ闁搞倐鍋撻梻浣告惈閻楁粓宕滈悢濂夊殨闁规儼濮ら弲鎻掝熆鐠轰警鍎岄柟宄扮秺濮婃椽宕滈懠顒€甯ラ梺鍝ュУ閻楃娀銆佸▎鎰瘈闁搞儯鍔庨崣鍡椻攽閻愭潙鐏︽い顓炴处閺呭爼寮撮悢缈犵盎闂佸搫娲㈤崝宀勫几閻斿吋鐓?zlib �?consumed闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟娆¤娲、姗€濮€閻橀潧濮?/summary>
    private static int FindConsumed(byte[] data, int off, int chunkLen, int outputLen, int capOut)
    {
        int lo = 0, hi = chunkLen;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ProduceLength(data, off, mid, capOut) >= outputLen)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// 闂傚倷娴囬褏鎹㈤幇顔藉床闁瑰濮靛畷鏌ユ煕閳╁啰鈯曢柛搴★攻閵囧嫰寮介妸褋鈧帗銇?off 濠电姷鏁告慨浼村垂閻撳簶鏋栨繛鎴炲焹閸嬫挸顫濋悡搴＄闂侀€炲苯澧柣蹇旀皑閸掓帡骞樺畷鍥ㄦ闂侀潧绻堥崐鏍磻閸曨偀鏀介柛灞惧嚬濡插湱�?DEFLATE(RFC1951, �?zlib/gzip �? 闂傚倷娴囧畷鐢稿窗閹扮増鍋￠弶鍫氭櫅缁躲倝鏌ｉ敐鍛拱闁哄棙绮撻弻娑㈠Ψ椤旂厧顫╅�?    /// 闂傚倸鍊烽懗鍫曞箠閹剧粯鍋ら柕濞炬櫅缁€澶愭煛閸モ晛鏋戦柛娆忕箲缁绘盯骞嬮悙鍨櫗婵炲瓨鍤庨崐婵嬪蓟閿熺姴纾兼繛鎴烇供閸ゅ绱撴担鎻掍壕�?(闂傚倸鍊风粈渚€骞夐敓鐘虫櫔婵＄偑鍊栭崹闈浢洪妸褎顫曢柟鐐綑缁剁偤鏌熼柇锕€骞愰柟宄扮秺濮婃椽宕滈懠顒€甯ラ梺鍝ュУ閻楃娀�? 闂傚倷娴囧畷鐢稿窗閹扮増鍋￠弶鍫氭櫅缁躲倝鏌ｉ敐鍛拱闁哄棙绮撻弻娑㈠Ψ椤旂厧顫╅柣鐔哥懕缁犳捇鐛弽銊︾秶闁告挆鍜冪吹闂備礁婀遍弲顐﹀窗閺嶎厼�?闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟鐑樻⒒閻瑩鏌熸潏鍓х暠濡楀懘姊洪悷閭﹀殶濠殿喚鏁搁埀顒佽壘椤兘寮婚弴鐔风窞闁割偅绻傞‖瀣磽娴ｅ搫校闁绘搫绻濆�?null�?    /// </summary>
    public static (int Consumed, byte[] Output)? TryDeflate(byte[] data, int off, int cap = 32 * 1024 * 1024)
    {
        int chunkLen = Math.Min(cap, data.Length - off);
        if (chunkLen <= 0)
            return null;
        byte[] output;
        try
        {
            using var ms = new MemoryStream(data, off, chunkLen);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            output = ReadCapped(ds, cap + 1);
            if (output.Length > cap)
                return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (output.Length == 0)
            return null;
        int consumed = FindConsumed(data, off, chunkLen, output.Length, cap + 1);
        if (consumed <= 0)
            return null;
        return (consumed, output);
    }

    // ------------------------------------------------------------ 闂備浇顕у锕傦綖婢舵劕绠栭柛顐ｆ礀绾惧潡姊洪鈧粔鎾儗濡ゅ懏鐓曠憸搴ㄣ€冮崨顖滅焼闁告洦鍨遍悡鐘绘煙椤撶喎绗掗柛鏃€绮嶇换?--

    /// <summary>闂傚倷娴囧畷鐢稿窗閹扮増鍋￠弶鍫氭櫅缁躲倝鏌ｉ敐鍛拱闁哄棙绮撻弻娑㈠Ψ椤旂厧顫╅梺鍝勵儎缁舵岸寮婚敐鍛傛棃鍩€椤掑嫭鍋嬮柛鈩冪懅閻牓鏌ㄩ弴鐐测偓褰掓偂閵夛妇绠鹃柟瀵稿仧閹冲啴鏌涘┑鍫濆妺缂佺粯鐩獮姗€宕樺顔芥闂備胶顢婄亸娆愭櫠濡ゅ懎绠柛娑卞灡閸犲棝鏌涢弴鐐典粵鐞氀囨⒒閸屾艾鈧兘鎮為敃浣典汗闁告劦浜為惌鎾绘煟閵忋埄鐒鹃柦鍐枎閳规垿鎮╅幓鎺濅痪闂佸摜濮村Λ婵嬪蓟閵娿儮鏀介柛鈩兠▍锝囩磽娴ｈ鈷愰柛搴㈠▕閸┿儲寰勯幇顒傤啋濡炪倖鐗楅惌顔界珶閺囩喓绡€闁汇垽娼ф禒婊堟煛鐎ｎ偅灏︽鐐茬Ч瀹曟﹢鐓鐑嗘�?/summary>
    public static byte[] GetOutBytes(byte[] data, Asset asset)
    {
        if (asset.DataOff is null)
            return Array.Empty<byte>();
        var got = TryDeflate(data, asset.DataOff.Value);
        return got?.Output ?? Array.Empty<byte>();
    }

    /// <summary>闂傚倸鍊烽懗鍫曘€佹繝鍌楁灃婵炴垯鍨洪弲婵嬫煏婵炑冩噽閻掓儳顪冮妶鍡楀闁瑰啿娲銊╂晝閸屾稓鍘甸梻渚囧弿缁犳垶鏅堕鐐寸厱濠电姴鍟埢鏇㈡煙椤旂厧妲婚柍璇叉唉缁犳盯骞欓崘褏纾介梻浣筋嚙妤犳悂鎮樺鑸靛亱闁绘灏欓弳锕傛煟濡鍤欓柛鎰ㄥ亾闂備焦瀵уú鎴︽嚄閸泙鍥ㄥ鐎涙鍘介柟鑹版彧缁查箖宕甸埀顒€顪冮妶蹇撶槣闁搞劏娉涢悾鐑芥晲閸垻鏉稿┑鐐村灦钃辨い銉︾箞濮婃椽骞栭悙鎻掑Х闂傚倸瀚€氫即銆侀弴鐔侯浄閻庯綆鍋嗛崢鍗炩攽椤旀枻渚涢柛鎾寸閳綊�?/summary>
    public static string SanitizeName(string name)
    {
        name = name.Replace('\\', '/').TrimStart('/');
        return InvalidCharsRegex().Replace(name, "_");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[^A-Za-z0-9._+@%\-]")]
    private static partial System.Text.RegularExpressions.Regex InvalidCharsRegex();

    // ------------------------------------------------------------ 濠电姷鏁搁崑鐐哄垂閸洖绠伴柛鎾楀啫鐏婂銈嗙墬缁烘寧绂嶉悷鑸偓鎺戭潩閿濆懍澹曢柣搴ゎ潐濞测晝绱炴担鍓插殨濞寸姴顑呮儫閻熸粌顑夊畷鎴炵節閸曨厾锛?--

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));



    /// <summary>
    /// 闂傚倷娴囬褍霉閻戣棄绠犻柟鎹愵嚙鐎氬銇勯幒鎴濐仼婵鐓￠弻銊モ攽閸♀晜笑缂備胶濮烽弫濠氬蓟閻斿吋鐒介柨鏇楀亾濠⒀屽枤缁辨帡骞撻幒鎾充淮闂佸搫琚崝宀勫煡婢跺ň鏋庨柟瀵稿Т瑜板海绱撻崒娆戝妽闁靛洨鍋ら幊妤呭醇閺囩偟顔夐梺鍛婃处閸ㄥジ寮鍡樺弿婵☆垰銇橀崥顐︽煟濞戝崬娅嶆慨濠冩そ楠炴劖鎯旈敐鍌涱潔婵犵數鍋熼埛鍫澪ｉ崬鐎ock / table-linked references only�? 闂傚倸鍊风粈渚€骞栭銈囩煓闁告洦鍘藉畷鍙夌節闂堟侗鍎愰柛瀣戠换娑㈠幢濡纰嶉�?    /// heuristic fallback removed 闂傚倸鍊风粈渚€骞栭锕€鐤い鏍仜绾惧灝鈹戦崒姘暈闁稿﹤顭烽弻銊╁籍閸ヮ灝鎾趁瑰┃鍨偓婵嬪蓟濞戙垹唯妞ゆ棁宕甸幐澶愭⒑缁嬫鍎愰柟鍛婄摃閻忓啯绻涙潏鍓хК妞ゆ洘鐗曞嵄妞ゆ牜鍋為埛鎴炴叏閻熺増鎼愬┑鈥炽偢閹顫濋悡搴♀拫閻?rodata DEFLATE 闂傚倸鍊峰ù鍥綖婢舵劦鏁婇柡宥庡幖绾惧鏌涘☉鍗炲季闁搞倖娲熼弻娑滎槼妞ゃ劌妫濆鏌ヮ敆閸曨剛鍘卞銈嗗姂閸婃洟寮告惔顫簻闁靛瀵屽▓婊勬叏婵犲啯銇濇鐐存崌楠炴帡寮崒銈呮暪濠电姷鏁告慨宥夊焵椤掑嫬纾婚柟鍓х帛閳锋垿鏌涘┑鍡楊仾濠殿垰銈搁弻娑㈠Ω閵娧呯勘闂侀潧娲﹂崝鏍箔閻旂厧鐒垫い鎺戝閽冪喖鏌涘☉妯兼憼闁稿妫濋弻娑氫沪閸撗呯厒濡炪倧瀵岄崑濠傤潖?    /// </summary>
    public static (List<Asset> Assets, List<string> Log) ExtractAssets(
        ElfFile elf, bool verbose = false)
    {
        byte[] view = elf.PatchView() ?? elf.Data;
        byte[] data = view;                      // 闂傚倷娴囧畷鐢稿窗閹扮増鍋￠弶鍫氭櫅缁躲倝鏌ｉ敐鍛拱闁哄棙绮撻弻娑㈠Ψ閵忊剝鐝栨俊鐐垫嚀閸熷潡鈥︾捄銊﹀磯闁绘艾鐡ㄩ弫鍓х磽閸屾氨孝婵☆偅顨婂﹢渚€姊洪幐搴ｇ畵闁绘锕﹀☉鐢稿醇閺囩喓鍘告繛杈剧秮濞佳囧焵椤掍胶澧辩紒顔藉哺閺屽棗顓兼惔銏℃珦闂備胶顭堢换鎰邦敄閸涙鍥濞戞碍娈鹃梺闈涱檧闂勫嫰宕曢悢鍏肩叆婵犻潧妫欓ˉ鐘绘煛閸℃瑥鈻堥柡宀嬬秮閹垽宕妷锕€娅戞繝纰樻閸嬪懘銆冮崨瀛樼�?
        var log = new List<string>();
        var entries = FindEntries(view, elf);
        if (verbose)
            log.Add($"ENTRIES 闂傚倷娴囧畷鐢稿磻閻愮數鐭欓煫鍥ㄧ☉缁€澶屸偓鍏夊亾闁告洦鍋嗛悿鍥⒑閺傘儲娅呴柛鐔跺嵆�?{entries.Count} �? [{string.Join(", ", entries.Select(e => e.Name))}]");

        var assets = new List<Asset>();
        var entryFns = entries.Select(e => e.FnVa).OrderBy(f => f).ToList();
        foreach (var (name, fnVa, tableOff) in entries)
        {
            // 濠电姷鏁告慨浼村垂婵傜鏄ラ柡宥庡幗閸嬪鏌￠崶銉ョ仾闁稿孩鍨甸湁闁绘挸娴烽幗鐘绘煃闁垮绗氶柕鍥у楠炴鎹勬潪鎵崟闂備椒绱粻鎴炴櫠鎼淬劌鐒垫い鎺嶇贰閸熷繘鏌涢悩鎰佹當閻撱倖绻濇繝鍌滃闁哄绶氶弻鏇㈠醇濠垫劖效闂佸搫鎷嬮崜姘跺箞閵娿儺娼ㄩ柛鈩冾殔缁犲綊姊?濠电姷鏁搁崑鐐哄垂閸洖绠伴柟闂寸贰閺佸嫰鏌涢弴銊ュ箻闁告宀搁弻鐔虹磼閵忕姵鐏嶉梺鎶芥敱鐢濡甸崟顖氱閻犻缚妗ㄧ划鍫曟⒑?濠电姷鏁搁崑鐐哄垂閸洖绠插ù锝囩《閺嬪秹鏌ㄥ┑鍡╂Ц闁汇倗鍋撻妵鍕箛閸撲胶鏆犵紓浣哄О閸庢煡濡甸崟顖氱疀闁告挷绀侀崺灞剧箾鐎电甯堕梺甯秮瀵濡搁妷銏☆潔濠碘槅鍨抽崢褔路閳ь剚绻濋悽闈浶涢柛瀣尭闇夐柨婵嗙墱濞兼劙鏌涙惔锝呮灈闁绘搩鍋婂畷鍫曞Ω閿曗偓閺嗘绱撴担姝屽婵炵》绻濆濠氭晲婢舵稓绋忓銈嗗坊閸嬫挸鈹戦檱濞咃絿妲愰幒鏃傜＜婵☆垵宕靛▓銈夋⒑瑜版帩妫戝┑顔炬暬楠炲繘宕ㄧ€涙ê鐧勬繝銏ｆ硾椤戞劙宕崼鏇熲拻闁稿本鍑归悡顒勬煕閹垮嫮鐣电€规洘鍨垮畷鐔碱敇閻曚焦缍楅梻浣稿暱閹碱偊骞婅箛娑樼；闁靛ň鏅滈悡蹇撯攽閻愯尙浠㈤柛鏃€绮撳Λ鍛搭敆婢跺﹤鈷嬮梺鍝勬湰閻╊垶鐛Ο铏规殾闁搞儻绲鹃悗鍝ョ�?
            ulong nxt = entryFns.FirstOrDefault(f => f > fnVa);
            if (nxt == 0)
                nxt = fnVa + 0x200;
            int extWindow = (int)Math.Min(0x400UL, nxt - fnVa);
            var refs = ScanRefs(elf, fnVa, extWindow);
            var hashRefs = refs.Where(r => !elf.IsWritableVa(r)).ToList();
            var dataRefs = refs.Where(elf.IsWritableVa).ToList();

            var result = new Asset { Name = name, FnVa = fnVa, TableOff = tableOff };

            // 1) 闂傚倷鐒﹂惇褰掑春閸曨垰鍨傞梺顒€绉甸崑銈夋煛閸ヨ埖绶涚紓宥嗙墪椤法鎹勯悜妯绘嫳閻庤娲栭ˇ浼村Φ閸曨垰鍐€闁靛濡囧▓銈囩磽娴ｇ懓濮夐柛瀣ㄥ€濆濠氬Ω閵夈垺顫嶅┑鐐叉鐢帡藟濠靛棭娓婚柕鍫濈箳閻ｅ磭鈧娲栭悘姘辩矉閹烘鏅濋柛灞炬皑閿涙粓姊虹紒妯哄闁规椿浜炲Σ鎰板Ω閳哄倵鎷?-> LazyLock 闂傚倸鍊搁崐鎼佸磹閹间焦鍋嬪┑鐘叉处閸嬶繝鏌ㄩ弴鐐测偓鎼佹�?-> thunk/helper -> 闂傚倸鍊风粈渚€骞夐敓鐘虫櫔婵＄偑鍊栭崹闈浢洪妸褎顫曢柟鐐綑缁剁偤鏌熼柇锕€澧柣搴墴濮婅櫣绱掑鍡樼暥闂佺粯顨呭Λ娑氬垝?
            //    闂傚倸鍊烽悞锔锯偓绗涘懐鐭欓柟瀛樼箥閻掔晫鈧鍠栭鍗瞨ession 闂傚倸鍊风粈渚€骞栭锔绘晞闁告侗鍨崑鎾愁潩閻撳骸顫紓浣介哺閹瑰洭鐛Ο灏栧亾閻㈢數銆婇柡瀣墵閺岋絾鎯旈婊呅ｉ梺鍝ュУ椤ㄥ﹪寮澶婄睄闁割偆鍠撻崢閬嶆⒑閸濆嫯鐧侀柛鏇ㄥ亝椤忊剝绻濋悽闈浶涢柛瀣尭闇夐柣鎾冲瘨閸ょ喖鏌涘▎鎰闁靛洤瀚伴獮鍥礈娴ｇ尨绱甸梻浣藉吹閸犳劗鎹㈤崼銉ヨ摕闁挎繂顦悡娑樏归敐鍥у妺闁规彃銈搁弻锝夊閳轰胶浠┑鐐插悑閻熲晠銆佸鑸垫櫜濠㈣泛锕ゅ畵鍡涙⒑缂佹﹩鐒炬繛鍛礀閳绘捇濮€閵堝棌鎷洪柡澶屽仦婵粙宕楃仦绛嬬唵鐟滃酣銆冮崨瀛樺仼闁割煈鍋嗛悷褰掓煃瑜滈崜鐔奉嚕椤愩埄鍚嬮柛銉檮閸曞啰绱撻崒娆撴闁搞劏顫夋穱濠冨鐎涙鍘介梺缁橆焾缁箖顢楅悢鍏肩厱闁靛绠戦ˉ瀣箾閸℃劕鐏查柟顔界懇瀹曨偊宕熼銈呯秮闂傚倷绶氶埀顒傚仜閼活垱鏅剁€电硶鍋撶憴鍕┛缂傚秳绶氶妴浣糕槈閵忊€斥偓鐑芥倵濞戞瑯鐒介柕鍫櫍濮婄粯鎷呴悷閭﹀殝缂備礁顑嗛崹鍓佺紦娴犲绫嶉柛灞句緱濞肩喎顪冮妶鍡樺蔼闁搞劌婀辩划鍫ュ磼閻愬鍘卞銈嗗姦閸嬪嫭鏅堕幍顔剧＜闁?
            foreach (var r in dataRefs)
            {
                foreach (var thunk in FindStaticFns(view, elf, r))
                {
                    foreach (var r2 in ScanRefs(elf, thunk, 0x40))
                    {
                        long? off2 = elf.ResolveRef(r2);
                        if (off2 is null)
                            continue;
                        var got = TryDeflate(data, (int)off2.Value);
                        if (got is not null && got.Value.Output.Length >= 4096)
                        {
                            result.DataOff = (int)off2.Value;
                            result.CompLen = got.Value.Consumed;
                            result.Size = got.Value.Output.Length;
                            result.Sha256 = Sha256Hex(got.Value.Output);
                            result.Method = "entry-factory";
                            break;
                        }
                    }
                    if (result.DataOff is not null)
                        break;
                }
                if (result.DataOff is not null)
                    break;
            }

            // 2) 闂傚倸鍊搁崐鎼佸磹閹间礁纾婚柛鈩冪☉閸屻劑鏌ゅù瀣澒闁稿鎹囧畷褰掝敃椤掑啠鍋撻崸妤佺厵闁惧浚鍋嗗ú瀛樸亜閵忊剝顥堢€规洖鐖奸、妤呭磼濮橆剛鍊掗梻鍌氬€风粈渚€骞夐敓鐘冲亱婵°倐鍋撴い顏勫暣閹稿﹥绔熼埡鍌滄创鐎规洜顭堣灃闁逞屽墰缁濡烽敂杞扮盎闂佽婢樻晶搴ㄥ礆閺夋５褰掓偂鎼淬劌寮伴梺鍝勮閸斿矂鍩ユ径濞㈢喖鎮℃惔妯哄綒濠碉紕鍋戦崐鎰板疾閳哄懎纾诲┑鐘叉搐缁?v0.9.2~v2.1.2 缂傚倸鍊搁崐鐑芥倿閿斿墽鐭欓柟娆¤娲鍓佹�?thunk 闂傚倸鍊烽悞锕傛儑瑜版帒绀夌€光偓閳ь剟鍩€椤掍礁鍤柛娆忓暣楠炲啫螖閸滀焦鏅┑顔筋焽閸樠冣枔濮椻偓濮婅櫣绱掑Ο鍨棟濡炪倖娲﹂崢鍓у垝?
            //    闂傚倸鍊搁崐鎼佸磹閹间礁纾婚柛鈩冪☉閸屻劑鏌ゅù瀣澒闁稿鎹囧畷褰掝敃椤掑啠鍋撶捄銊㈠亾鐟欏嫭绀堥柛蹇斏戞穱濠囧箹娴ｈ娅嗛梺浼欑到閼活垶宕垫潏銊х瘈闁汇垽娼ч崜宕囩磼閹绘巻鍋撻悢缈犳睏婵犮垼娉涢惉鑲╃矆婵犲洦鐓曟繛鎴濆船閻忥絿鐥崜褍浜鹃柕鍥у瀵噣宕掑顐㈩棜缂傚倷鑳堕搹搴ㄥ垂閸洖钃熼柣鏃傚帶缁犳帡鏌熼悜妯虹仴濠殿喖娴风槐鎾存媴閻熼偊鏆㈤梺鍝勬噽婵炩偓妤犵偛鍟粋鎺斺偓锝庘偓顓涙櫊閺屾洘寰勯崼婵嗗Б濡炪倕绻楀畷鐢垫閹捐纾兼慨姗嗗厴閸嬫捇鎮界粙璺ㄦ煣濠电姴锕ら悧鍡椥ч弻銉︾厽闁哄啫鍊哥敮鍓佺磼閻樻彃绾ч柕鍥у瀵剟宕归钘夆偓顖炴⒑缁嬫鍎嶉柛搴ｆ暬瀵濡搁妷銏☆潔濠电偛妫欓崹鐔肺ｉ崼婵愭富闁靛牆绻樺顕€鏌ｉ幙鍕瘈妤犵偛鍟粋鎺斺偓锝庘偓顓涙櫊閺屾洟宕煎┑鍥х獩闂佺懓妯婃禍顏勵潖婵犳艾纾兼慨妯哄船椤ｅ爼姊洪幖鐐插缂傚秴锕悰顕€宕橀鑲╊啋闁诲海鏁哥涵鍫曞几閺嶎厽鈷戦梺顐ゅ仜閼活垱鏅堕婊呯＜闁稿本绋戝ù顕€鏌熺粙鍖℃敾鐎垫澘瀚换婵嬪炊椤垵鏁梻鍌氬€搁崐鎼佸磹閹间礁纾归柣鐔煎亰閻掕姤绻涢崱妯虹仸妞ゎ偅娲熼弻宥夊传閸曨剙娅ら梺绋款儍閸斿秹濡甸崟顖ｆ晣闁绘棃顥撻鍌滅磽娴ｆ彃浜?
            if (result.DataOff is null)
            {
                foreach (var r in refs)
                {
                    long? off = elf.ResolveRef(r);
                    if (off is null)
                        continue;
                    var got = TryDeflate(data, (int)off.Value);
                    if (got is not null && got.Value.Output.Length >= 4096)
                    {
                        result.DataOff = (int)off.Value;
                        result.CompLen = got.Value.Consumed;
                        result.Size = got.Value.Output.Length;
                        result.Sha256 = Sha256Hex(got.Value.Output);
                        result.Method = "entry-ref";
                        break;
                    }
                }
            }

            // 3) 闂傚倸鍊风粈渚€骞夐敓鐘冲殞闁绘劦鍓﹀▓浠嬫煙闂傚韫堥柛銉墻閺佸棝鏌涢弴銊ュ婵炲懏鐗曢埞鎴︻敊閺傘倓绶甸梺鍛婃尰閻熴儵�?rodata DEFLATE 闂傚倸鍊峰ù鍥綖婢舵劦鏁婇柡宥庡幖绾惧鏌涘☉鍗炲季�?+ SHA-256 闂傚倸鍊风粈渚€骞夐敓鐘冲仭闁靛／鍛厠闂佹眹鍨归幉锟犲磻閸岀偞鐓ラ柣鏂挎惈鏍￠梺鍝勬媼閸撶喖寮诲☉銏╂晝闁靛牆鎳忛悗濠氭⒑娴兼瑧鐣辨い顓犲厴瀵鈽夊鍡樺兊濡炪倖甯掗崐鎰板閵堝棛鍘介梺鎰佸灡閻熴儱煤閿曞倸鐓曢柟鐑樻尪娴滄粓鏌熼幑鎰【濞寸姵绮撻弻锝夋晝娴ｅ湱銆愰梺瀹犳椤︾敻鐛▎鎾崇闁靛鍎抽鎺撶節閻㈤潧浠滈柣妤€锕畷褰掑垂椤旂偓娈惧銈嗗姧缁犳垿鏌嬮崶銊х瘈闂傚牊绋撴晶銏ゆ煙閺屻儰鎲鹃柟顔筋殔閳绘捇宕归鐣屼憾婵犵數鍋涘鍓佸垝閹炬剚鍤曟い鎰剁畱鍥存繝銏ｆ硾閿曪箓藝闁秵鍋℃繝濠傛噹椤ｅジ鎮介娑樼闁轰緡鍣ｉ獮搴ㄦ嚍閵壯冨�?

            // 闂傚倸鍊风粈渚€骞栭銈囩煓闁告洦鍘藉畷鍙夌節闂堟侗鍎愰柛瀣戠换娑㈠幢濡闉嶉梺鍝勬媼閸撴氨鎹㈠☉銏犲耿婵炲棙蓱閳荤硥 濠电姵顔栭崰妤冩暜閹烘柡鍋撳鐓庡箹妞ゎ偄绻楅妵鎰板箳閹寸偠�?+ SHA-256 闂傚倸鍊风粈渚€骞夐敓鐘插瀭闁绘挸绨堕弸宥夋煥濠靛棭妲虹€规挷绶氶弻娑㈠Ψ椤旂厧顫╅梺鍦嚀閻栧ジ寮婚弴鐔虹闁割煈鍠掗崑鎾澄旈崨顔煎壃閻庡厜鍋撻柛鏇ㄥ亞妤犲洭姊洪崘鍙夋儓闁稿﹤顭烽崺銉﹀緞閹邦厾鍘遍梺鍐叉惈閸嬪棗鈻撻弴鐘电＜閻庯綆浜跺Ο鈧銈冨灪閿曘垺淇婂宀婃Ь濡炪倧璐熺敮銆唗adata 闂傚倸鍊风粈渚€骞夐敓鐘茬闁哄洢鍨圭粻鐘诲箹鏉堝墽鎮奸柣顓炵墦閺岋綁濮€閵忊晝鍔告繛纾嬪亹婵炩偓闁哄本鐩、鏇㈡晲閸モ晝鏆梻浣藉吹閸犳劙鎮ч悩璇茶�?            if (result.DataOff is not null && result.Sha256 is not null)
            {
                byte[] hb = Convert.FromHexString(result.Sha256);
                ulong? hashHit = null;
                foreach (var hr in hashRefs)
                {
                    long? hoff = elf.ResolveRef(hr);
                    if (hoff is not null && hoff + 32 <= data.Length &&
                        Convert.ToHexStringLower(data.AsSpan((int)hoff.Value, 32)) == result.Sha256)
                    {
                        hashHit = hr;
                        break;
                    }
                }
                result.HashRefVa = hashHit;
                // A digest merely occurring somewhere in the ELF is not enough
                // to associate it with this resource. It must be reached by a
                // reference from the same resource factory.
                result.HashInBinary = false;
                byte[] outBytes = GetOutBytes(data, result);
                result.Verified =
                    hashHit is not null &&
                    outBytes.Length >= 4 &&
                    outBytes[0] == 0x7F && outBytes[1] == (byte)'E' &&
                    outBytes[2] == (byte)'L' && outBytes[3] == (byte)'F';
            }
            assets.Add(result);
        }

        return (assets, log);
    }
}