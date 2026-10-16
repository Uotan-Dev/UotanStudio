using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Benchmarks.Inputs;

/// <summary>Enumerates the synthetic payload characters the benchmark corpus can produce.
/// <para>枚举基准语料可以生成的合成载荷特征类型。</para></summary>
public enum PayloadKind
{
    /// <summary>Log- and XML-shaped English lines with repetitive structure; compresses roughly 4x to 8x.
    /// <para>结构重复的日志与 XML 形式英文行，压缩率约为 4 到 8 倍。</para></summary>
    Text,

    /// <summary>C#-like source text with heavy symbol repetition; compresses well.
    /// <para>符号高度重复的类 C# 源码文本，压缩率很高。</para></summary>
    Source,

    /// <summary>x86-64-like machine code with repeated prologues, epilogues and operand forms; medium compressibility.
    /// <para>带有重复序言、收尾与操作数形式的类 x86-64 机器码，压缩率中等。</para></summary>
    Executable,

    /// <summary>An all-zero fill; compresses to almost nothing.
    /// <para>全零填充，压缩后几乎不占空间。</para></summary>
    ZeroFill,

    /// <summary>High-entropy XorShift output; deliberately incompressible.
    /// <para>高熵 XorShift 输出，刻意保持不可压缩。</para></summary>
    AlreadyCompressed,

    /// <summary>High-entropy bytes with no exploitable local structure; deliberately incompressible.
    /// <para>没有可利用局部结构的高熵字节，刻意保持不可压缩。</para></summary>
    Encrypted
}

/// <summary>The single source of deterministic, cached benchmark input for repository benchmarks, so codec
/// throughput is measured against realistic payloads instead of incompressible pseudo-random bytes.
/// <para>仓库基准测试唯一的确定性缓存输入来源，使编解码吞吐针对真实载荷测量，而不是针对不可压缩的伪随机字节。</para>
/// <para>The corpus is generated from fixed arithmetic constants only — no ambient randomness, clock, path,
/// culture or machine identity reaches the payload bytes — so two machines produce byte-identical corpora for
/// the same <see cref="PayloadKind"/> and size. The same <c>(kind, mebibytes)</c> pair is materialized at most
/// once per process: buffers are cached in memory, and the files under <see cref="HostRoot"/> are written once
/// and deliberately survive process exit so repeated BenchmarkDotNet runs reuse them. At process exit each
/// payload file is removed exactly once, and only after a sibling <c>.claim</c> marker proves this class wrote
/// it, so a file that pre-existed the process is never deleted.</para>
/// <para>语料仅由固定算术常量生成：环境随机源、时钟、路径、区域性与机器标识都不会进入载荷字节，因此两台机器对相同的
/// <see cref="PayloadKind"/> 与尺寸会产生逐字节一致的语料。同一 <c>(kind, mebibytes)</c> 组合在每个进程内至多实体化一次：
/// 缓冲区常驻内存缓存，<see cref="HostRoot"/> 下的文件只写入一次并刻意在进程退出后保留，以便重复运行 BenchmarkDotNet 时复用。
/// 进程退出时每个载荷文件只被删除一次，且必须先由同名 <c>.claim</c> 标记证明是本类写入的，因此进程启动前已存在的文件绝不会被删除。</para>
/// </summary>
internal static class BenchCorpus
{
    /// <summary>The smallest payload size the corpus accepts, in mebibytes.
    /// <para>语料接受的最小载荷尺寸，单位为兆字节（MiB）。</para></summary>
    internal const int MinMebibytes = 1;

    /// <summary>The largest payload size a single entry may materialize, in mebibytes.
    /// <para>单个条目允许实体化的最大载荷尺寸，单位为兆字节（MiB）。</para></summary>
    internal const int MaxMebibytes = 256;

    private const int Mebibyte = 1024 * 1024;
    private const string CorpusDirectoryName = "uotan-bench-corpus";
    private const string ExitMarkerExtension = ".claim";
    private const int HighEntropyPrefixLength = 64;
    private const int ForwardMixMultiplier = 0x9D;
    private const int TextTickStride = 4099;
    private const int TextValueModulus = 4096;
    private const int SourceIndexModulus = 8192;
    private const int InstructionCount = 54;
    private const int OperandAlphabetSize = 16;
    private const int ImmediateStride = 64;
    private const int ImmediateSeed = 0x7F30;
    private const ulong SeedSalt = 0x9E37_79B9_7F4A_7C15UL;
    private const ulong CompressedSeedTag = 0x243F_6A88_85A3_08D3UL;
    private const ulong EncryptedSeedTag = 0x1319_8A2E_0370_7344UL;

    private static readonly ConcurrentDictionary<(PayloadKind Kind, int Mebibytes), byte[]> Buffers = new();
    private static readonly ConcurrentDictionary<string, string> Files = new();
    private static readonly ConcurrentDictionary<string, string> RecordedFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> RecordedDirectories = new(StringComparer.Ordinal);
    private static readonly Lock FileGate = new();
    private static readonly string CorpusRootPath = ComputeCorpusRoot();
    private static int _exitHookRegistered;

    /// <summary>Gets the absolute directory that hosts every corpus payload file.
    /// <para>获取承载所有语料载荷文件的绝对目录。</para>
    /// </summary>
    /// <value>The absolute corpus root path, which is not created until the first <see cref="File"/> call.
    /// <para>语料根目录的绝对路径；在首次调用 <see cref="File"/> 之前不会创建。</para></value>
    internal static string HostRoot => CorpusRootPath;

    /// <summary>Returns the cached payload buffer for one <paramref name="kind"/> at <paramref name="mebibytes"/> MiB.
    /// <para>返回某个 <paramref name="kind"/> 在 <paramref name="mebibytes"/> MiB 下的缓存载荷缓冲区。</para>
    /// </summary>
    /// <param name="kind">The payload character to generate.
    /// <para>要生成的特征类型。</para></param>
    /// <param name="mebibytes">The payload size in mebibytes, between <see cref="MinMebibytes"/> and <see cref="MaxMebibytes"/>.
    /// <para>以 MiB 表示的载荷尺寸，取值在 <see cref="MinMebibytes"/> 与 <see cref="MaxMebibytes"/> 之间。</para></param>
    /// <returns>An exactly <paramref name="mebibytes"/> MiB buffer; the same instance is returned on every call with the same arguments.
    /// <para>长度恰为 <paramref name="mebibytes"/> MiB 的缓冲区；相同参数在每次调用时返回同一实例。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mebibytes"/> is outside the accepted range, or when <paramref name="kind"/> is not a defined <see cref="PayloadKind"/> value.
    /// <para>当 <paramref name="mebibytes"/> 超出接受范围，或 <paramref name="kind"/> 不是已定义的 <see cref="PayloadKind"/> 值时抛出。</para></exception>
    internal static ReadOnlySpan<byte> Bytes(PayloadKind kind, int mebibytes)
    {
        Validate(kind, mebibytes);
        return Buffers.GetOrAdd((kind, mebibytes), static key => Generate(key.Kind, key.Mebibytes));
    }

    /// <summary>Returns the absolute cached path of the on-disk payload file for one <paramref name="kind"/> at <paramref name="mebibytes"/> MiB.
    /// <para>返回某个 <paramref name="kind"/> 在 <paramref name="mebibytes"/> MiB 下磁盘载荷文件的绝对缓存路径。</para>
    /// </summary>
    /// <param name="kind">The payload character to materialize.
    /// <para>要实体化的特征类型。</para></param>
    /// <param name="mebibytes">The payload size in mebibytes, between <see cref="MinMebibytes"/> and <see cref="MaxMebibytes"/>.
    /// <para>以 MiB 表示的载荷尺寸，取值在 <see cref="MinMebibytes"/> 与 <see cref="MaxMebibytes"/> 之间。</para></param>
    /// <returns>The file path under <see cref="HostRoot"/>, named <c>&lt;kind&gt;-&lt;mib&gt;m/&lt;kind&gt;.bin</c>. The file is written once per process and survives process exit.
    /// <para><see cref="HostRoot"/> 下的文件路径，命名为 <c>&lt;kind&gt;-&lt;mib&gt;m/&lt;kind&gt;.bin</c>；文件每个进程只写入一次，并在进程退出后保留。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mebibytes"/> is outside the accepted range, or when <paramref name="kind"/> is not a defined <see cref="PayloadKind"/> value.
    /// <para>当 <paramref name="mebibytes"/> 超出接受范围，或 <paramref name="kind"/> 不是已定义的 <see cref="PayloadKind"/> 值时抛出。</para></exception>
    /// <exception cref="IOException">Thrown when the payload file exists with a different length and cannot be replaced.
    /// <para>当载荷文件已存在但长度不同且无法替换时抛出。</para></exception>
    internal static string File(PayloadKind kind, int mebibytes)
    {
        Validate(kind, mebibytes);
        var name = kind.ToString().ToLowerInvariant();
        var directory = Path.Combine(CorpusRootPath, FormattableString.Invariant($"{name}-{mebibytes}m"));
        var path = Path.Combine(directory, FormattableString.Invariant($"{name}.bin"));
        var cached = Files.GetOrAdd(path, _ => path);
        var expectedLength = (long)mebibytes * Mebibyte;
        if (System.IO.File.Exists(path) && new FileInfo(path).Length == expectedLength)
        {
            return cached;
        }

        lock (FileGate)
        {
            if (System.IO.File.Exists(path) && new FileInfo(path).Length == expectedLength)
            {
                return cached;
            }

            Directory.CreateDirectory(directory);
            var buffer = Buffers.GetOrAdd((kind, mebibytes), static key => Generate(key.Kind, key.Mebibytes));
            var temporary = FormattableString.Invariant($"{path}.{mebibytes}.tmp");
            System.IO.File.WriteAllBytes(temporary, buffer);
            var claim = Path.Combine(directory, ExitMarkerFileName(name));
            System.IO.File.WriteAllBytes(claim, []);
            System.IO.File.Move(temporary, path, overwrite: true);
            RecordCreatedFile(path);
            Record(claim);
            RegisterExitHook();
            return cached;
        }
    }

    private static void Validate(PayloadKind kind, int mebibytes)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Payload kind is not a defined PayloadKind value.");
        }

        if (mebibytes is < MinMebibytes or > MaxMebibytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mebibytes),
                mebibytes,
                FormattableString.Invariant($"Corpus payload size must be between {MinMebibytes} and {MaxMebibytes} MiB."));
        }
    }

    private static byte[] Generate(PayloadKind kind, int mebibytes)
    {
        var buffer = new byte[(long)mebibytes * Mebibyte];
        switch (kind)
        {
            case PayloadKind.Text:
                FillText(buffer, mebibytes);
                break;
            case PayloadKind.Source:
                FillSource(buffer, mebibytes);
                break;
            case PayloadKind.Executable:
                FillExecutable(buffer, mebibytes);
                break;
            case PayloadKind.ZeroFill:
                break;
            case PayloadKind.AlreadyCompressed:
                FillHighEntropy(buffer, mebibytes, alreadyCompressed: true);
                break;
            case PayloadKind.Encrypted:
                FillHighEntropy(buffer, mebibytes, alreadyCompressed: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Payload kind is not a defined PayloadKind value.");
        }

        return buffer;
    }

    private static void FillText(byte[] buffer, int mebibytes)
    {
        var units = BuildTextUnits(mebibytes);
        var writer = new SpanSegmentWriter(buffer);
        var line = 0;
        while (true)
        {
            var block = BuildTextBlockKey(line);
            if (!writer.TryWrite(units[block]))
            {
                break;
            }

            foreach (var leaf in TextLeaves)
            {
                var key = FormattableString.Invariant($"{block}#{leaf}#{line % TextValueModulus}");
                if (!writer.TryWrite(units[key]))
                {
                    return;
                }
            }

            var tail = FormattableString.Invariant($"{block}#tail");
            if (!writer.TryWrite(units[tail]))
            {
                return;
            }

            line++;
        }
    }

    private static Dictionary<string, (byte[] Bytes, int TextByteCount)> BuildTextUnits(int mebibytes)
    {
        var units = new Dictionary<string, (byte[] Bytes, int TextByteCount)>(StringComparer.Ordinal);
        var writer = new SpanSegmentWriter(new byte[LineBudget(mebibytes)]);
        for (var line = 0; ; line++)
        {
            var block = BuildTextBlockKey(line);
            AddTextUnit(units, block, TextBlockBody(line));

            foreach (var leaf in TextLeaves)
            {
                AddTextUnit(units, FormattableString.Invariant($"{block}#{leaf}#{line % TextValueModulus}"), TextLeafBody(leaf, line));
            }

            AddTextUnit(units, FormattableString.Invariant($"{block}#tail"), "</log>");
            if (!writer.TryWrite(units[block]))
            {
                return units;
            }
        }
    }

    private static string BuildTextBlockKey(int line)
    {
        return FormattableString.Invariant($"<log level=\"{TextLevels[line % TextLevels.Length]}\" unit=\"{TextUnits[line % TextUnits.Length]}\" seq=\"{line}\" tick=\"{line * TextTickStride}\">");
    }

    private static string TextBlockBody(int line)
    {
        return BuildTextBlockKey(line);
    }

    private static string TextLeafBody(string leaf, int line)
    {
        var level = TextLevels[line % TextLevels.Length];
        var unit = TextUnits[(line / TextLevels.Length) % TextUnits.Length];
        return FormattableString.Invariant($"<entry level=\"{level}\" tag=\"{leaf}\" unit=\"{unit}\" seq=\"{line}\" offset=\"0x{line * TextTickStride:X8}\" value=\"{line % TextValueModulus}\"/>");
    }

    private static void AddTextUnit(
        Dictionary<string, (byte[] Bytes, int TextByteCount)> units,
        string key,
        string body)
    {
        if (units.ContainsKey(key))
        {
            return;
        }

        var line = FormattableString.Invariant($"       \"{body}\",\n");
        units[key] = (Encoding.UTF8.GetBytes(line), line.Length);
    }

    private static void FillSource(byte[] buffer, int mebibytes)
    {
        var units = BuildSourceUnits(mebibytes);
        var writer = new SpanSegmentWriter(buffer);
        for (var chunk = 0; ; chunk++)
        {
            var block = BuildSourceBlockKey(chunk);
            if (!writer.TryWrite(units[block]))
            {
                break;
            }

            foreach (var template in SourceMergeTemplates)
            {
                var key = FormattableString.Invariant($"{block}#{template}");
                if (!writer.TryWrite(units[key]))
                {
                    return;
                }
            }

            if (!writer.TryWrite(units[FormattableString.Invariant($"{block}#return")]))
            {
                return;
            }
        }
    }

    private static Dictionary<string, byte[]> BuildSourceUnits(int mebibytes)
    {
        var units = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var writer = new SpanSegmentWriter(new byte[LineBudget(mebibytes)]);
        for (var chunk = 0; ; chunk++)
        {
            var block = BuildSourceBlockKey(chunk);
            AddSourceUnit(units, block, FormattableString.Invariant($"        var {SourceLocals[chunk % SourceLocals.Length]} = buffer.Slice({SourceLocals[(chunk / 3) % SourceLocals.Length]}, {chunk % SourceIndexModulus});"));

            foreach (var template in SourceMergeTemplates)
            {
                AddSourceUnit(units, FormattableString.Invariant($"{block}#{template}"), SourceMergeBody(template, chunk));
            }

            AddSourceUnit(units, FormattableString.Invariant($"{block}#return"), FormattableString.Invariant($"        return new {SourceResultTypes[chunk % SourceResultTypes.Length]}({SourceLocals[chunk % SourceLocals.Length]}, {chunk % SourceIndexModulus});"));
            if (!writer.TryWrite(units[block]))
            {
                return units;
            }
        }
    }

    private static string BuildSourceBlockKey(int chunk)
    {
        return FormattableString.Invariant($"// region {chunk} kind={SourceKinds[chunk % SourceKinds.Length]} index={chunk % SourceIndexModulus}");
    }

    private static string SourceMergeBody(string template, int chunk)
    {
        return FormattableString.Invariant($"        {SourceLocals[chunk % SourceLocals.Length]} = {template}({SourceLocals[(chunk + 1) % SourceLocals.Length]}, {SourceKinds[chunk % SourceKinds.Length]}[{chunk % SourceIndexModulus}], {chunk % SourceIndexModulus});");
    }

    private static void AddSourceUnit(Dictionary<string, byte[]> units, string key, string body)
    {
        if (units.ContainsKey(key))
        {
            return;
        }

        var line = FormattableString.Invariant($"       \"{body}\",\n");
        units[key] = Encoding.UTF8.GetBytes(line);
    }

    private static void FillExecutable(byte[] buffer, int mebibytes)
    {
        var window = new byte[InstructionCount * 4];
        var immediate = ImmediateSeed;
        var function = 0;
        var offset = 0;
        while (offset + window.Length <= buffer.Length)
        {
            for (var index = 0; index < InstructionCount; index++)
            {
                var at = index * 4;
                switch (index)
                {
                    case 0:
                    case 20:
                        window[at] = 0x55;
                        window[at + 1] = 0x48;
                        window[at + 2] = 0x89;
                        window[at + 3] = 0xE5;
                        break;
                    case 10:
                    case 38:
                        window[at] = 0x48;
                        window[at + 1] = 0x81;
                        window[at + 2] = 0xEC;
                        window[at + 3] = (byte)(0x20 + (operandBits(function, OperandAlphabetSize) << 4));
                        break;
                    case 12:
                        window[at] = 0x48;
                        window[at + 1] = 0x8B;
                        window[at + 2] = 0x44;
                        window[at + 3] = (byte)(0x24 + operandBits(function, OperandAlphabetSize));
                        break;
                    case 33:
                        window[at] = 0xB8;
                        window[at + 1] = (byte)immediate;
                        window[at + 2] = (byte)(immediate >> 8);
                        window[at + 3] = 0x90;
                        break;
                    case 41:
                        window[at] = 0x85;
                        window[at + 1] = 0xC0;
                        window[at + 2] = 0x0F;
                        window[at + 3] = 0x84;
                        break;
                    case 44:
                        window[at] = 0xE8;
                        window[at + 1] = (byte)operandBits(function, OperandAlphabetSize);
                        window[at + 2] = (byte)(operandBits(function + 1, OperandAlphabetSize) << 4);
                        window[at + 3] = 0x90;
                        break;
                    case 52:
                        window[at] = 0x5D;
                        window[at + 1] = 0xC3;
                        window[at + 2] = 0xCC;
                        window[at + 3] = 0xCC;
                        break;
                    default:
                        window[at] = 0x48;
                        window[at + 1] = 0x89;
                        window[at + 2] = (byte)(0xC0 | (operandBits(function + index, OperandAlphabetSize) << 3) | operandBits(function + index + 1, OperandAlphabetSize));
                        window[at + 3] = 0x90;
                        break;
                }
            }

            Buffer.BlockCopy(window, 0, buffer, offset, window.Length);
            offset += window.Length;
            immediate += ImmediateStride;
            function++;
        }

        if (offset < buffer.Length)
        {
            Array.Clear(buffer, offset, buffer.Length - offset);
        }
    }

    private static readonly byte[] HighEntropyPrefix = BuildHighEntropyPrefix();

    private static void FillHighEntropy(byte[] buffer, int mebibytes, bool alreadyCompressed)
    {
        ref var destination = ref MemoryMarshal.GetArrayDataReference(buffer);
        Unsafe.CopyBlockUnaligned(ref destination, ref HighEntropyPrefix[0], (uint)HighEntropyPrefixLength);
        var state = HighEntropySeed(mebibytes, alreadyCompressed);
        var index = HighEntropyPrefixLength;
        while (index < buffer.Length)
        {
            var random = XorShift64(ref state);
            for (var shift = 0; shift < 8 && index < buffer.Length; shift++)
            {
                buffer[index] = alreadyCompressed
                    ? (byte)(random >> (shift * 8))
                    : (byte)((random >> (shift * 8)) ^ (byte)(index * ForwardMixMultiplier));
                index++;
            }
        }
    }

    private static byte[] BuildHighEntropyPrefix()
    {
        var prefix = new byte[HighEntropyPrefixLength];
        var state = HighEntropySeed(0, alreadyCompressed: false);
        for (var i = 0; i < prefix.Length; i++)
        {
            prefix[i] = (byte)(XorShift64(ref state) >> 56);
        }

        return prefix;
    }

    private static ulong HighEntropySeed(int mebibytes, bool alreadyCompressed)
    {
        var mixed = ((ulong)(uint)mebibytes * SeedSalt) ^ (alreadyCompressed ? 0x243F_6A88_85A3_08D3UL : 0x1319_8A2E_0370_7344UL);
        var state = mixed == 0 ? 1UL : mixed;
        for (var round = 0; round < 8; round++)
        {
            state = XorShift64(ref state);
        }

        return state;
    }

    private static ulong XorShift64(ref ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }

    private static byte operandBits(int function, int alphabetSize)
    {
        return (byte)(function % alphabetSize);
    }

    private static int LineBudget(int mebibytes)
    {
        return mebibytes * Mebibyte;
    }

    private static string ComputeCorpusRoot()
    {
        var baseDirectory = Path.GetTempPath();
        if (!baseDirectory.EndsWith(Path.DirectorySeparatorChar))
        {
            baseDirectory += Path.DirectorySeparatorChar;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, CorpusDirectoryName));
    }

    private static void RecordCreatedFile(string path)
    {
        Record(path);
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            RecordedDirectories.TryAdd(directory, directory);
        }
    }

    private static void Record(string path)
    {
        RecordedFiles.TryAdd(path, path);
    }

    private static string ExitMarkerFileName(string name)
    {
        return FormattableString.Invariant($"{name}{ExitMarkerExtension}");
    }

    private static readonly string[] TextLeaves =
    [
        "boot",
        "recovery",
        "vendor",
        "system",
        "odm",
        "cache"
    ];

    private static readonly string[] TextLevels =
    [
        "debug",
        "info",
        "warn",
        "error"
    ];

    private static readonly string[] TextUnits =
    [
        "payload",
        "sparse",
        "erofs",
        "ext4",
        "hash",
        "verify"
    ];

    private static readonly string[] SourceMergeTemplates =
    [
        "merge",
        "combine",
        "reduce"
    ];

    private static readonly string[] SourceLocals =
    [
        "slice",
        "merged",
        "window",
        "cursor"
    ];

    private static readonly string[] SourceKinds =
    [
        "table",
        "offsets",
        "lengths",
        "hashes"
    ];

    private static readonly string[] SourceResultTypes =
    [
        "Result",
        "Extent",
        "Chunk"
    ];

    private static void RegisterExitHook()
    {
        if (Interlocked.Exchange(ref _exitHookRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => CleanupCreatedFiles();
    }

    private static void CleanupCreatedFiles()
    {
        foreach (var pair in RecordedFiles)
        {
            TryDeleteFile(pair.Key);
        }

        foreach (var pair in RecordedDirectories)
        {
            TryDeleteDirectory(pair.Key);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private struct SpanSegmentWriter(byte[] buffer)
    {
        private readonly byte[] _buffer = buffer;
        private int _offset;

        internal bool TryWrite((byte[] Bytes, int TextByteCount) unit)
        {
            return TryWrite(unit.Bytes, unit.TextByteCount);
        }

        internal bool TryWrite(byte[] bytes)
        {
            return TryWrite(bytes, bytes.Length);
        }

        private bool TryWrite(byte[] bytes, int byteCount)
        {
            if (_offset + byteCount > _buffer.Length)
            {
                return false;
            }

            bytes.AsSpan(0, byteCount).CopyTo(_buffer.AsSpan(_offset));
            _offset += byteCount;
            return true;
        }
    }
}