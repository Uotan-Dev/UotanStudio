using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// Selects which kinds of <c>debug_info_item</c> content survive <see cref="DexPostProcessor.StripDebugInfo"/>.
/// <para>选择 <see cref="DexPostProcessor.StripDebugInfo"/> 保留哪类 <c>debug_info_item</c> 内容。</para>
/// </summary>
public sealed class StripDebugOptions
{
    /// <summary>Keeps the <c>.line</c> position events (dropped when <see langword="false"/>).
    /// <para>保留 <c>.line</c> 位置事件（为 <see langword="false"/> 时删除）。</para></summary>
    public bool KeepLineInfo { get; init; } = true;

    /// <summary>Keeps the <c>.prologue</c>/<c>.epilogue</c> markers (dropped when <see langword="false"/>).
    /// <para>保留 <c>.prologue</c>/<c>.epilogue</c> 标记（为 <see langword="false"/> 时删除）。</para></summary>
    public bool KeepPrologueMarkers { get; init; } = true;

    /// <summary>Keeps the <c>.local</c>/<c>.end local</c>/<c>.restart local</c> events (dropped when <see langword="false"/>).
    /// <para>保留 <c>.local</c>/<c>.end local</c>/<c>.restart local</c> 事件（为 <see langword="false"/> 时删除）。</para></summary>
    public bool KeepLocalInfo { get; init; } = true;

    /// <summary>Keeps the <c>.source</c> file-name events (dropped when <see langword="false"/>).
    /// <para>保留 <c>.source</c> 文件名事件（为 <see langword="false"/> 时删除）。</para></summary>
    public bool KeepSourceFile { get; init; } = true;

    /// <summary>Keeps the <c>.param</c> parameter-name entries in the debug header (zeroed when <see langword="false"/>).
    /// <para>保留 debug 头部的 <c>.param</c> 参数名条目（为 <see langword="false"/> 时清零）。</para></summary>
    public bool KeepParamNames { get; init; } = true;

    /// <summary>Keeps nothing: every debug event and name entry is removed while the debug stream itself stays present.
    /// <para>什么都不保留：删除全部调试事件与名称条目，但调试流本身仍存在。</para></summary>
    public static StripDebugOptions None { get; } = new()
    {
        KeepLineInfo = false,
        KeepPrologueMarkers = false,
        KeepLocalInfo = false,
        KeepSourceFile = false,
        KeepParamNames = false
    };
}

/// <summary>
/// Summary of the parsed <c>hiddenapi_class_data_item</c> flag distribution.
/// <para>解析出的 <c>hiddenapi_class_data_item</c> 标志分布摘要。</para>
/// </summary>
public sealed class HiddenApiFlagsReport
{
    /// <summary>Number of classes whose offsets entry is non-zero.
    /// <para>偏移条目非零的类数量。</para></summary>
    public int ClassesWithFlags { get; init; }

    /// <summary>Number of members (fields and methods) covered by a class flag block.
    /// <para>被类标志块覆盖的成员（字段与方法）数量。</para></summary>
    public int FlaggedMembers { get; init; }

    /// <summary>Flag value histogram (whitelist=0, greylist=1, blacklist=2, greylist-max-o…r=3…6 per the format spec).
    /// <para>标志值直方图（按格式规范：whitelist=0、greylist=1、blacklist=2、greylist-max-o…r=3…6）。</para></summary>
    public IReadOnlyDictionary<uint, int> DomainCounts { get; init; } = new Dictionary<uint, int>();

    /// <summary>Whether the section could not be parsed consistently.
    /// <para>该段是否无法一致地解析。</para></summary>
    /// <value><see langword="true"/> with the reason in <see cref="ParseError"/>; the flag distribution is then not reported.
    /// <para>为 <see langword="true"/> 时原因见 <see cref="ParseError"/>，此时不报告标志分布。</para></value>
    public bool ParseFailed { get; init; }

    /// <summary>The parse failure reason; empty on success.
    /// <para>解析失败原因；成功时为空。</para></summary>
    public string ParseError { get; init; } = string.Empty;
}

/// <summary>
/// The actions <see cref="DexPostProcessor.Repair"/> took while normalizing a damaged DEX image.
/// <para><see cref="DexPostProcessor.Repair"/> 规范化受损 DEX 镜像时采取的动作。</para>
/// </summary>
public sealed class DexRepairReport
{
    /// <summary>Whether the declared checksum disagreed with the image bytes.
    /// <para>声明的校验和是否与镜像字节不一致。</para></summary>
    public bool ChecksumMismatched { get; init; }

    /// <summary>Whether trailing bytes beyond the declared file size were cut away.
    /// <para>是否切除了超出声明文件大小的尾部字节。</para></summary>
    public bool TruncatedTrailingBytes { get; init; }

    /// <summary>How many trailing bytes were cut away.
    /// <para>被切除的尾部字节数。</para></summary>
    public int TruncatedByteCount { get; init; }

    /// <summary>Whether an existing SHA-1 signature field disagreed with the rebuilt value.
    /// <para>既有 SHA-1 签名字段是否与重建值不一致。</para></summary>
    public bool SignatureMismatched { get; init; }

    /// <summary>The number of dex images produced (always 1 for a repair).
    /// <para>产出的 dex 镜像数（修复总是 1）。</para></summary>
    public int RepairedImages { get; init; }
}

/// <summary>
/// Post-processing operations over a parsed <see cref="DexFile"/>: debug-info stripping, hidden-API
/// flag handling and a lenient rebuild used as a one-key repair.
/// <para>对已解析 <see cref="DexFile"/> 的后处理操作：调试信息剥离、hidden API 标志处理，以及作为一键修复的宽松重建。</para>
/// </summary>
public static class DexPostProcessor
{
    private const int HashChunkSize = 64 * 1024;

    /// <summary>Strips debug content from every code item according to the options and returns how many code items changed.
    /// <para>按选项剥离所有 code item 的调试内容，返回发生变化的 code item 数量。</para></summary>
    /// <param name="dex">Model to modify in place; versions and other tables are untouched.
    /// <para>就地修改的模型；版本与其他表不受影响。</para></param>
    /// <param name="options">What to keep; <see langword="null"/> removes every debug stream entirely (the MT-style full strip).
    /// <para>保留项；为 <see langword="null"/> 时整体删除每条调试流（MT 式全量剥离）。</para></param>
    /// <param name="clearSourceFile">Also clears the per-class <c>.source</c> file name (<c>SourceFileIndex</c> → <c>-1</c>).
    /// <para>是否同时清除每类的 <c>.source</c> 文件名（<c>SourceFileIndex</c> → <c>-1</c>）。</para></param>
    /// <returns>The number of code items whose <see cref="CodeItem.DebugInfo"/> changed.
    /// <para><see cref="CodeItem.DebugInfo"/> 发生变化的 code item 数量。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    public static int StripDebugInfo(DexFile dex, StripDebugOptions? options = null, bool clearSourceFile = false)
    {
        ArgumentNullException.ThrowIfNull(dex);
        var changed = 0;
        if (options is null)
        {
            foreach (var cls in dex.Classes)
            {
                if (clearSourceFile) cls.SourceFileIndex = -1;
                foreach (var method in cls.AllMethods)
                {
                    if (method.Code?.DebugInfo is not null)
                    {
                        method.Code.DebugInfo = null;
                        changed++;
                    }
                }
            }
            return changed;
        }

        foreach (var cls in dex.Classes)
        {
            if (clearSourceFile) cls.SourceFileIndex = -1;
            foreach (var method in cls.AllMethods)
            {
                var code = method.Code;
                if (code?.DebugInfo is not { } debug) continue;
                var rewritten = DebugInfoRewriter.Rewrite(debug, code, options);
                if (!rewritten.SequenceEqual(debug))
                {
                    code.DebugInfo = rewritten;
                    changed++;
                }
            }
        }
        return changed;
    }

    /// <summary>Strips debug content from an image and rebuilds it through the disassembler/assembler pair, so strings referenced only by the removed debug content leave the string pool and the image shrinks accordingly.
    /// <para>剥离镜像的调试内容并经反汇编/汇编对重建，使仅被已删调试内容引用的字符串离开字符串池，镜像随之缩小。</para></summary>
    /// <param name="data">Complete DEX image bytes. <para>完整的 DEX 镜像字节。</para></param>
    /// <param name="options">What to keep; <see langword="null"/> removes every debug stream entirely.
    /// <para>保留项；为 <see langword="null"/> 时整体删除每条调试流。</para></param>
    /// <param name="clearSourceFile">Also clears the per-class <c>.source</c> file name.
    /// <para>是否同时清除每类的 <c>.source</c> 文件名。</para></param>
    /// <returns>The rebuilt image bytes, canonical identifier tables included.
    /// <para>重建后的镜像字节，包含规范化排序的标识符表。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FormatException">The rebuilt text cannot be re-assembled (a round-trip failure would be a bug).
    /// <para>重建文本无法重新汇编（往返失败属于缺陷）。</para></exception>
    public static byte[] StripDebugInfoRebuilt(byte[] data, StripDebugOptions? options = null, bool clearSourceFile = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dex = DexFile.Load(data);
        StripDebugInfo(dex, options, clearSourceFile);
        var text = Disassembler.DisassembleAll(dex);
        return Smali.SmaliAssembler.Assemble(text).ToArray();
    }

    /// <summary>Replaces the <c>hiddenapi_class_data_item</c> section with one whose offsets table is all zero, which the format spec defines as "no data, all flags zero" — every member falls back to the whitelist domain.
    /// <para>把 <c>hiddenapi_class_data_item</c> 段替换为偏移表全零的版本；格式规范将零偏移定义为"无数据、全部标志为零"，即所有成员回落到 whitelist 域。</para></summary>
    /// <param name="dex">Model to modify in place. <para>就地修改的模型。</para></param>
    /// <returns><see langword="true"/> when the model carried a section and it was replaced; <see langword="false"/> when there was no section to clear.
    /// <para>模型带段且已被替换时为 <see langword="true"/>；没有可清除的段时为 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    public static bool ClearHiddenApiFlags(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        if (dex.HiddenApiClassData is null) return false;
        var zeroed = BuildZeroedHiddenApiSection(dex.Classes.Count);
        if (dex.HiddenApiClassData.AsSpan().SequenceEqual(zeroed)) return false;
        dex.HiddenApiClassData = zeroed;
        return true;
    }

    /// <summary>Removes the <c>hiddenapi_class_data_item</c> section entirely; a rebuilt image without the section loads with every member treated as unflagged.
    /// <para>整体移除 <c>hiddenapi_class_data_item</c> 段；重建出的镜像没有该段，加载时所有成员按无标志处理。</para></summary>
    /// <param name="dex">Model to modify in place. <para>就地修改的模型。</para></param>
    /// <returns><see langword="true"/> when a section was removed. <para>移除了段时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    public static bool StripHiddenApiSection(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        if (dex.HiddenApiClassData is null) return false;
        dex.HiddenApiClassData = null;
        dex.HiddenApiMapSize = 0;
        return true;
    }

    /// <summary>Parses the captured <c>hiddenapi_class_data_item</c> section per the format spec (a leading total-size uint, a uint[] offsets table indexed by class, then concatenated per-class uleb128 flag arrays in class-data member order) and reports the flag distribution.
    /// <para>按格式规范解析捕获的 <c>hiddenapi_class_data_item</c> 段（开头为总大小 uint、按类索引的 uint[] 偏移表、随后按类数据成员顺序级联的各类 uleb128 标志数组），并报告标志分布。</para></summary>
    /// <param name="dex">Model whose captured section is analyzed. <para>对其捕获段做分析的模型。</para></param>
    /// <returns>The report; <see langword="null"/> when the model carries no section.
    /// <para>报告；模型没有段时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="dex"/> is <see langword="null"/>.
    /// <para><paramref name="dex"/> 为 <see langword="null"/>。</para></exception>
    public static HiddenApiFlagsReport? AnalyzeHiddenApiFlags(DexFile dex)
    {
        ArgumentNullException.ThrowIfNull(dex);
        if (dex.HiddenApiClassData is not { } section) return null;
        try
        {
            return ParseHiddenApiSection(section, dex);
        }
        catch (EndOfStreamException ex)
        {
            return new HiddenApiFlagsReport { ParseFailed = true, ParseError = ex.Message };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new HiddenApiFlagsReport { ParseFailed = true, ParseError = ex.Message };
        }
    }

    /// <summary>Re-parses a damaged image leniently (checksum and file-size disagreements tolerated, trailing bytes cut) and rebuilds it with fresh tables, signature and checksum.
    /// <para>宽松地重新解析受损镜像（容忍校验和与文件大小不一致、切除尾部字节），并以全新表、签名与校验和重建。</para></summary>
    /// <param name="data">Damaged DEX image bytes; magic, version, endian tag and table ranges are still validated.
    /// <para>受损的 DEX 镜像字节；魔数、版本、字节序标记与表范围仍会校验。</para></param>
    /// <param name="report">Receives the actions taken. <para>接收所采取动作的报告。</para></param>
    /// <returns>The rebuilt image bytes. <para>重建后的镜像字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DexException">The image is too damaged to parse even leniently (bad magic, unsupported version, truncated tables).
    /// <para>镜像受损到宽松解析也无法进行（魔数非法、版本不支持、表被截断）。</para></exception>
    public static byte[] Repair(byte[] data, out DexRepairReport report)
    {
        ArgumentNullException.ThrowIfNull(data);
        var declaredChecksum = data.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) : 0u;
        var computedChecksum = Hash.Adler32.Adler32.Compute(data.AsSpan(12));
        var checksumMismatched = declaredChecksum != computedChecksum;

        var dex = DexFile.Load(data, new DexParseOptions { VerifyChecksum = false, StrictFileSize = false });
        var truncated = data.Length - checked((int)dex.Header.FileSize);

        var declaredSignature = data.Length >= 32 ? data[12..32].ToArray() : [];
        var signatureMismatched = false;
        if (declaredSignature.Length > 0)
        {
            Span<byte> computedSignature = stackalloc byte[Sha1.HashSizeInBytes];
            using (var sha1 = Sha1.CreateAccumulator())
            {
                for (var pos = 32; pos < data.Length; pos += HashChunkSize)
                {
                    sha1.Append(data.AsSpan(pos, Math.Min(HashChunkSize, data.Length - pos)));
                }
                sha1.GetHashAndReset(computedSignature);
            }
            signatureMismatched = !declaredSignature.AsSpan().SequenceEqual(computedSignature);
        }
        var rebuilt = dex.ToArray();

        report = new DexRepairReport
        {
            ChecksumMismatched = checksumMismatched,
            TruncatedTrailingBytes = truncated > 0,
            TruncatedByteCount = Math.Max(0, truncated),
            SignatureMismatched = signatureMismatched,
            RepairedImages = 1
        };
        return rebuilt;
    }

    /// <summary>Parses a zeroed section: leading total size, then one all-zero uint offset per class.
    /// <para>构造零化段：开头为总大小，随后每类一个全零的 uint 偏移。</para></summary>
    private static byte[] BuildZeroedHiddenApiSection(int classCount)
    {
        var totalSize = 4 + 4 * classCount;
        var buffer = new byte[totalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)totalSize);
        return buffer;
    }

    /// <summary>Walks the section layout; throws tolerant-parse errors that surface as a failed report.
    /// <para>按段布局走读；抛出的宽容解析错误会表现为失败的报告。</para></summary>
    private static HiddenApiFlagsReport ParseHiddenApiSection(byte[] section, DexFile dex)
    {
        if (section.Length < 8) throw new EndOfStreamException("hiddenapi section shorter than size + one offset entry.");
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(section);
        if (declaredSize != section.Length) throw new EndOfStreamException("hiddenapi section size field disagrees with the captured bytes.");
        var classCount = dex.Classes.Count;
        var offsetsRegionEnd = 4 + 4 * classCount;
        if (offsetsRegionEnd > section.Length) throw new EndOfStreamException("hiddenapi offsets table exceeds the section.");
        var offsets = new uint[classCount];
        for (var i = 0; i < classCount; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(4 + 4 * i));
        }
        var sorted = offsets.Where(o => o != 0).Distinct().OrderBy(o => o).ToArray();
        if (sorted.Length > 0 && (sorted[0] < offsetsRegionEnd || sorted[^1] > declaredSize))
        {
            throw new EndOfStreamException("hiddenapi flag block offsets fall outside the section.");
        }

        var classesWithFlags = 0;
        var flaggedMembers = 0;
        var domainCounts = new Dictionary<uint, int>();
        var boundsEnd = declaredSize;
        for (var i = 0; i < classCount; i++)
        {
            if (offsets[i] == 0) continue;
            var cursor = (int)offsets[i];
            var next = 0u;
            foreach (var candidate in sorted)
            {
                if (candidate > offsets[i])
                {
                    next = candidate;
                    break;
                }
            }
            var blockEnd = next != 0 ? (int)next : (int)boundsEnd;
            var cls = dex.Classes[i];
            var memberCount = cls.StaticFields.Count + cls.InstanceFields.Count
                + cls.DirectMethods.Count + cls.VirtualMethods.Count;
            for (var m = 0; m < memberCount; m++)
            {
                if (cursor >= blockEnd) throw new EndOfStreamException("hiddenapi flag block ends before the class member count.");
                var flag = Leb128.ReadUleb128(section, ref cursor);
                domainCounts.TryGetValue(flag, out var count);
                domainCounts[flag] = count + 1;
                flaggedMembers++;
            }
            classesWithFlags++;
        }
        return new HiddenApiFlagsReport
        {
            ClassesWithFlags = classesWithFlags,
            FlaggedMembers = flaggedMembers,
            DomainCounts = domainCounts
        };
    }
}

/// <summary>
/// Rewrites a <c>debug_info_item</c> byte stream in place semantics: events are filtered per the
/// <see cref="StripDebugOptions"/> while the uleb128 header (line start, parameter names) is rebuilt.
/// <para>按 <see cref="StripDebugOptions"/> 过滤事件地重写 <c>debug_info_item</c> 字节流，
/// uleb128 头部（起始行号、参数名）同步重建。</para>
/// </summary>
/// <remarks>
/// Special opcodes (≥0x0A) fuse a PC advance with a line advance; when line events are dropped the
/// PC component survives as an explicit <c>ADVANCE_PC</c> so addresses stay monotonic.
/// <para>特殊操作码（≥0x0A）把 PC 前进与行号前进合并；删除行事件时 PC 分量改写为显式
/// <c>ADVANCE_PC</c>，保证地址单调。</para>
/// </remarks>
internal static class DebugInfoRewriter
{
    /// <summary>Applies the options to one debug stream.
    /// <para>对一条调试流应用选项。</para></summary>
    /// <param name="debug">The raw stream bytes including the terminating <c>END_SEQUENCE</c>.
    /// <para>含结尾 <c>END_SEQUENCE</c> 的原始流字节。</para></param>
    /// <param name="code">Owning code item; only its register sizes validate the parameter table.
    /// <para>所属 code item；仅其寄存器规模用于校验参数表。</para></param>
    /// <param name="options">What to keep. <para>保留项。</para></param>
    /// <returns>The rewritten stream bytes. <para>重写后的流字节。</para></returns>
    /// <exception cref="EndOfStreamException">The stream is truncated or an opcode's operands run past the end.
    /// <para>流被截断或某操作码的操作数越出结尾。</para></exception>
    public static byte[] Rewrite(ReadOnlySpan<byte> debug, CodeItem code, StripDebugOptions options)
    {
        var source = debug.ToArray();
        using var output = new MemoryStream(source.Length);
        var position = 0;
        var lineStart = Leb128.ReadUleb128(source, ref position);
        var parameterCount = Leb128.ReadUleb128(source, ref position);
        void U(uint value)
        {
            Span<byte> bytes = stackalloc byte[5];
            output.Write(bytes[..Leb128.WriteUleb128(bytes, 0, value)]);
        }
        void S(int value)
        {
            Span<byte> bytes = stackalloc byte[5];
            output.Write(bytes[..Leb128.WriteSleb128(bytes, 0, value)]);
        }
        U(lineStart);
        U(parameterCount);
        for (var i = 0; i < parameterCount; i++)
        {
            var name = Leb128.ReadUleb128(source, ref position);
            U(options.KeepParamNames ? name : 0);
        }

        var finished = false;
        while (!finished)
        {
            if (position >= source.Length) throw new EndOfStreamException("debug stream ends without END_SEQUENCE.");
            var opcode = source[position++];
            switch (opcode)
            {
                case 0x00:
                    output.WriteByte(0);
                    finished = true;
                    break;
                case 0x01:
                    if (!options.KeepLineInfo)
                    {
                        _ = Leb128.ReadUleb128(source, ref position);
                        break;
                    }
                    output.WriteByte(1);
                    U(Leb128.ReadUleb128(source, ref position));
                    break;
                case 0x02:
                    if (!options.KeepLineInfo)
                    {
                        _ = Leb128.ReadSleb128(source, ref position);
                        break;
                    }
                    output.WriteByte(2);
                    S(Leb128.ReadSleb128(source, ref position));
                    break;
                case 0x03 or 0x04 or 0x05 or 0x06:
                    if (!options.KeepLocalInfo)
                    {
                        var operandCount = opcode == 0x04 ? 4 : opcode == 0x03 ? 3 : 1;
                        for (var i = 0; i < operandCount; i++) _ = Leb128.ReadUleb128(source, ref position);
                        break;
                    }
                    output.WriteByte(opcode);
                    var operands = opcode == 0x04 ? 4 : opcode == 0x03 ? 3 : 1;
                    for (var i = 0; i < operands; i++) U(Leb128.ReadUleb128(source, ref position));
                    break;
                case 0x07 or 0x08:
                    if (options.KeepPrologueMarkers) output.WriteByte(opcode);
                    break;
                case 0x09:
                    if (!options.KeepSourceFile)
                    {
                        _ = Leb128.ReadUleb128(source, ref position);
                        break;
                    }
                    output.WriteByte(9);
                    U(Leb128.ReadUleb128(source, ref position));
                    break;
                default:
                    var adjusted = opcode - 0x0A;
                    var pcDelta = adjusted / 15;
                    if (options.KeepLineInfo)
                    {
                        output.WriteByte(opcode);
                        break;
                    }
                    if (pcDelta > 0)
                    {
                        output.WriteByte(1);
                        U((uint)pcDelta);
                    }
                    break;
            }
        }
        return output.ToArray();
    }
}