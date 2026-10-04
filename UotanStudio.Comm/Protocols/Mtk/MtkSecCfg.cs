using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>Byte-exact reader and writer for the on-flash secure-boot configuration.
/// <para>片上安全启动配置的字节级读取与写入器。</para></summary>
/// <remarks>
/// Every layout rule, constant and state transition reproduced here comes from
/// mtkclient Library/Hardware/seccfg.py:11-116 with the partition-level selection rules at
/// Library/DA/xflash/extension/xflash.py:564-602 and Library/DA/legacy/extension/legacy.py:165-200.
/// Reading is pure and allocates nothing on the device. Writing is limited to the two states the
/// evidence names. The software transform is reproduced off-device; the hardware SEJ variants
/// (seccfg.py:55-62 and :269-274) stay refused unless a caller supplies an
/// <see cref="IMtkSecCfgHashEngine"/> bound to the running target.
/// <para>此处复现的每条布局规则、常量与状态转换均来自 mtkclient Library/Hardware/seccfg.py:11-116，
/// 分区级选择规则见 Library/DA/xflash/extension/xflash.py:564-602 与
/// Library/DA/legacy/extension/legacy.py:165-200。读取是纯函数且不在设备上分配。写入只限于证据命名的
/// 两个状态。软件变换在设备外复现；硬件 SEJ 变体（seccfg.py:55-62 与 :269-274）在调用方未提供绑定到
/// 运行中目标的 <see cref="IMtkSecCfgHashEngine"/> 时仍被拒绝。</para>
/// </remarks>
public static class MtkSecCfg
{
    /// <summary>The structure magic dword shared by both layouts (seccfg.py:18, :45, :148 and :199).
    /// <para>两种布局共用的结构魔数双字（seccfg.py:18、:45、:148 与 :199）。</para></summary>
    public const uint Magic = 0x4D4D4D4D;

    /// <summary>The end marker dword shared by both layouts (seccfg.py:24, :45, :153 and :198).
    /// <para>两种布局共用的结束标志双字（seccfg.py:24、:45、:153 与 :198）。</para></summary>
    public const uint EndFlag = 0x45454545;

    /// <summary>The byte length of the version 4 header block (seccfg.py:48-49).
    /// <para>版本 4 头部的字节长度（seccfg.py:48-49）。</para></summary>
    public const int Version4HeaderLength = 7 * 4;

    /// <summary>The declared size a version 4 structure must carry for the documented reader and
    /// writer layouts to agree (seccfg.py:43 reads the hash at size minus 0x20, seccfg.py:99 writes
    /// it directly behind the 28-byte header).
    /// <para>版本 4 结构的声明大小，只有该值才能让已记载的读取与写入布局一致
    /// （seccfg.py:43 在“大小减 0x20”处读取摘要，seccfg.py:99 则把摘要直接写在 28 字节头部之后）。</para></summary>
    public const uint Version4AgreedSize = Version4HeaderLength + 0x20;

    /// <summary>The byte length of the version 3 plaintext banner (seccfg.py:147 and :187).
    /// <para>版本 3 明文标头的字节长度（seccfg.py:147 与 :187）。</para></summary>
    public const int Version3BannerLength = 0x10;

    /// <summary>The byte length of the version 3 plaintext header (seccfg.py:187-197).
    /// <para>版本 3 明文头部的字节长度（seccfg.py:187-197）。</para></summary>
    public const int Version3HeaderLength = 0x2C;

    /// <summary>The block size to which the documented writer zero-pads a created image
    /// (seccfg.py:100-101 and :281-282).
    /// <para>已记载的写入器对生成镜像补零对齐的块大小（seccfg.py:100-101 与 :281-282）。</para></summary>
    public const int ImageAlignment = 0x200;

    /// <summary>The encryption-length dword the documented writer stores for a closed
    /// configuration (seccfg.py:236).
    /// <para>已记载的写入器在关闭配置时存储的加密长度双字（seccfg.py:236）。</para></summary>
    public const uint Version3ClosedEncryptionLength = 0x01000000;

    /// <summary>The encryption-length dword the documented writer stores for an open
    /// configuration (seccfg.py:233).
    /// <para>已记载的写入器在打开配置时存储的加密长度双字（seccfg.py:233）。</para></summary>
    public const uint Version3OpenEncryptionLength = 0x07F20000;

    private const uint LockStateWrittenWhenClosed = 1;
    private const uint LockStateWrittenWhenOpen = 3;
    private const uint CriticalLockStateWrittenWhenClosed = 0;
    private const uint CriticalLockStateWrittenWhenOpen = 1;

    private static ReadOnlySpan<byte> Version3Banner => [0x41, 0x4E, 0x44, 0x5F, 0x53, 0x45, 0x43, 0x43, 0x46, 0x47, 0x5F, 0x76, 0x00, 0x00, 0x00, 0x00];

    private static ReadOnlySpan<byte> SoftwareCipherKey =>
        "25A1763A21BC854CD569DC23B4782B63"u8;

    private static ReadOnlySpan<byte> SoftwareCipherInitializationVector =>
        [0x57, 0x32, 0x5A, 0x5A, 0x12, 0x54, 0x97, 0x66, 0x12, 0x54, 0x97, 0x66, 0x57, 0x32, 0x5A, 0x5A];

    /// <summary>Selects the layout from the image header without decoding the body.
    /// <para>仅依据镜像头部选择布局，不解码主体。</para></summary>
    /// <param name="image">The exact partition bytes. 分区原始字节。</param>
    /// <returns>The detected layout, or <see langword="null"/> when neither documented header
    /// matches. <para>检测到的布局；两个已记载头部都不匹配时为 <see langword="null"/>。</para></returns>
    public static MtkSecCfgFormat? DetectFormat(ReadOnlySpan<byte> image)
    {
        if (image.Length >= 0xC && image[..0xC].SequenceEqual(Version3Banner[..0xC]))
        {
            return MtkSecCfgFormat.Version3;
        }

        return image.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(image) == Magic
            ? MtkSecCfgFormat.Version4
            : null;
    }

    /// <summary>Decodes a version 4 secure-boot configuration image.
    /// <para>解码版本 4 的安全启动配置镜像。</para></summary>
    /// <param name="image">The exact partition bytes read from the device.
    /// <para>从设备读取的分区原始字节。</para></param>
    /// <param name="report">Receives the decoded report when the call returns <see langword="true"/>.
    /// <para>调用返回 <see langword="true"/> 时接收解码结果。</para></param>
    /// <returns><see langword="true"/> when the magic, end marker, declared size and stored hash all
    /// satisfy the documented structure; <see langword="false"/> otherwise, including when only a
    /// hardware transform could explain the stored hash.
    /// <para>当魔数、结束标志、声明大小与存储摘要都满足已记载结构时返回 <see langword="true"/>；
    /// 否则返回 <see langword="false"/>，包括只有硬件变换才能解释存储摘要的情况。</para></returns>
    public static bool TryReadVersion4(ReadOnlySpan<byte> image, out MtkSecCfgV4Report? report)
    {
        report = null;
        if (image.Length < Version4HeaderLength)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(image);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(image[4..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(image[8..]);
        var lockState = BinaryPrimitives.ReadUInt32LittleEndian(image[12..]);
        var criticalLockState = BinaryPrimitives.ReadUInt32LittleEndian(image[16..]);
        var runtime = BinaryPrimitives.ReadUInt32LittleEndian(image[20..]);
        var endFlag = BinaryPrimitives.ReadUInt32LittleEndian(image[24..]);
        if (magic != Magic || endFlag != EndFlag || size < Version4AgreedSize || (ulong)size > (ulong)image.Length)
        {
            return false;
        }

        var storedHash = image.Slice((int)size - 0x20, 0x20);
        var header = image[..Version4HeaderLength].ToArray();
        var computed = SHA256.HashData(header);
        var hashKind = MtkSecCfgHashKind.Unresolved;
        var decrypted = SoftwareTransform(storedHash, encrypt: false);
        if (decrypted.SequenceEqual(computed))
        {
            hashKind = MtkSecCfgHashKind.Software;
        }

        report = new MtkSecCfgV4Report(
            Convert.ToHexString(header),
            magic,
            version,
            size,
            lockState,
            criticalLockState,
            runtime,
            endFlag,
            Convert.ToHexString(storedHash),
            Convert.ToHexString(computed),
            hashKind);
        return true;
    }

    /// <summary>Decodes the plaintext header of a version 3 secure-boot configuration image.
    /// <para>解码版本 3 安全启动配置镜像的明文头部。</para></summary>
    /// <param name="image">The exact partition bytes read from the device.
    /// <para>从设备读取的分区原始字节。</para></param>
    /// <param name="report">Receives the decoded header when the call returns <see langword="true"/>.
    /// <para>调用返回 <see langword="true"/> 时接收头部解码结果。</para></param>
    /// <remarks>
    /// The header fields sit at the offsets the documented reader consumes in order: the 0x10-byte
    /// banner at 0x00, the magic dword at 0x10, the version at 0x14, the declared structure size at
    /// 0x18, the encryption offset at 0x1C, the encryption length at 0x20, the two single-byte
    /// software lock counters at 0x24 and 0x25, the 16-bit page size at 0x26 and the page count at
    /// 0x28, with the body following at 0x2C and the end marker dword closing the declared size
    /// (seccfg.py:183-199).
    /// <para>头部字段按已记载读取器依次消费的偏移排列：0x10 字节的标头位于 0x00，魔数双字位于 0x10，
    /// 版本位于 0x14，声明的结构大小位于 0x18，加密偏移位于 0x1C，加密长度位于 0x20，两个单字节软件锁定
    /// 计数位于 0x24 与 0x25，16 位页大小位于 0x26，页数位于 0x28；主体从 0x2C 开始，结束标志双字位于
    /// 声明大小的末尾（seccfg.py:183-199）。</para>
    /// </remarks>
    /// <returns><see langword="true"/> when the banner, magic, declared size and end marker all
    /// satisfy the documented header layout. The opaque body is measured and digested, never
    /// interpreted, because its documented transforms require the target's SEJ engine
    /// (seccfg.py:202-215).
    /// <para>当标头、魔数、声明大小与结束标志都满足已记载的头部布局时返回 <see langword="true"/>。
    /// 不透明主体只做测量与摘要、不做解释，因为其记载的变换需要目标的 SEJ 引擎（seccfg.py:202-215）。</para></returns>
    public static bool TryReadVersion3(ReadOnlySpan<byte> image, out MtkSecCfgV3Report? report)
    {
        report = null;
        if (image.Length < Version3HeaderLength + 4 || !image[..Version3HeaderLength].StartsWith(Version3Banner))
        {
            return false;
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(image[0x18..]);
        if (declaredSize < Version3HeaderLength + 4 || (ulong)declaredSize > (ulong)image.Length)
        {
            return false;
        }

        var dataLength = checked((int)declaredSize - Version3HeaderLength - 4);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(image[0x10..]);
        var endFlag = BinaryPrimitives.ReadUInt32LittleEndian(image[(Version3HeaderLength + dataLength)..]);
        if (magic != Magic || endFlag != EndFlag)
        {
            return false;
        }

        var body = image.Slice(Version3HeaderLength, dataLength);
        report = new MtkSecCfgV3Report(
            Convert.ToHexString(image[..Version3BannerLength]),
            magic,
            BinaryPrimitives.ReadUInt32LittleEndian(image[0x14..]),
            declaredSize,
            BinaryPrimitives.ReadUInt32LittleEndian(image[0x1C..]),
            BinaryPrimitives.ReadUInt32LittleEndian(image[0x20..]),
            image[0x24],
            image[0x25],
            BinaryPrimitives.ReadUInt16LittleEndian(image[0x26..]),
            BinaryPrimitives.ReadUInt32LittleEndian(image[0x28..]),
            endFlag,
            dataLength,
            Convert.ToHexString(SHA256.HashData(body)));
        return true;
    }

    /// <summary>Builds the image bytes that close or open a version 4 configuration.
    /// <para>构造用于关闭或打开版本 4 配置的镜像字节。</para></summary>
    /// <remarks>
    /// The dword values, their order, the hash placement and the zero padding all follow
    /// seccfg.py:89-102 exactly. Only a report whose stored hash was shown to use the software
    /// transform can be rewritten, because seccfg.py:55-62 delegate the other variants to the
    /// target's SEJ engine.
    /// <para>双字取值及其顺序、摘要位置与补零完全遵循 seccfg.py:89-102。只有存储摘要已被证实使用软件
    /// 变换的报告才能被重写，因为 seccfg.py:55-62 把其他变体交给目标的 SEJ 引擎处理。</para>
    /// </remarks>
    /// <param name="report">A report decoded by <see cref="TryReadVersion4"/>.
    /// <para>由 <see cref="TryReadVersion4"/> 解码得到的报告。</para></param>
    /// <param name="intent">The requested state transition. 请求的状态转换。</param>
    /// <returns>The padded image, exactly a multiple of <see cref="ImageAlignment"/> bytes.
    /// <para>补白后的镜像，长度恰为 <see cref="ImageAlignment"/> 的整数倍。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.
    /// <para><paramref name="report"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intent"/> is undefined.
    /// <para><paramref name="intent"/> 未定义。</para></exception>
    /// <exception cref="InvalidOperationException">The configuration already carries the requested
    /// state, which the documented writer refuses (seccfg.py:79-82).
    /// <para>配置已处于请求状态，已记载的写入器会拒绝该请求（seccfg.py:79-82）。</para></exception>
    /// <exception cref="NotSupportedException">The stored hash is not protected by the reproducible
    /// software transform, or the declared size contradicts the documented reader and writer layout.
    /// <para>存储摘要并非由可复现的软件变换保护，或声明大小与已记载的读写布局矛盾。</para></exception>
    public static byte[] CreateVersion4(MtkSecCfgV4Report report, MtkSecCfgLockIntent intent)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateVersion4Rewrite(report, intent);

        if (report.HashKind != MtkSecCfgHashKind.Software)
        {
            throw new NotSupportedException(
                "The stored hash is not protected by the software transform, so rewriting it requires the target SEJ engine.");
        }

        var header = BuildVersion4Header(report, intent);
        var hash = SoftwareTransform(SHA256.HashData(header), encrypt: true);
        return BuildVersion4Image(header, hash);
    }

    /// <summary>Builds the image that closes or opens a version 4 configuration whose stored hash is
    /// protected by a target-driven hardware engine.
    /// <para>构造关闭或打开版本 4 配置的镜像，其存储摘要由目标驱动的硬件引擎保护。</para></summary>
    /// <remarks>
    /// The digest recomputation, the dword order and the zero padding follow seccfg.py:89-102; only the
    /// hash transform is delegated to <paramref name="engine"/>, using exactly the variant the read
    /// resolved for the stored hash.
    /// <para>摘要重算、双字顺序与补零遵循 seccfg.py:89-102；只有摘要变换交给 <paramref name="engine"/>，
    /// 且使用的正是读取阶段为存储摘要解析出的那一个变体。</para>
    /// </remarks>
    /// <param name="report">A report decoded by <see cref="TryReadVersion4Async"/> with a resolved hardware
    /// hash kind. <para>由 <see cref="TryReadVersion4Async"/> 解码且硬件摘要种类已解析的报告。</para></param>
    /// <param name="intent">The requested state transition. 请求的状态转换。</param>
    /// <param name="engine">The target-driven transform engine. 目标驱动的变换引擎。</param>
    /// <param name="cancellationToken">Cancellation for every target exchange.
    /// <para>每次目标交换的取消令牌。</para></param>
    /// <returns>The padded image, exactly a multiple of <see cref="ImageAlignment"/> bytes.
    /// <para>补白后的镜像，长度恰为 <see cref="ImageAlignment"/> 的整数倍。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> or <paramref name="engine"/> is null.
    /// <para><paramref name="report"/> 或 <paramref name="engine"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intent"/> is undefined.
    /// <para><paramref name="intent"/> 未定义。</para></exception>
    /// <exception cref="InvalidOperationException">The configuration already carries the requested state.
    /// <para>配置已处于请求状态。</para></exception>
    /// <exception cref="NotSupportedException">The declared size contradicts the documented layout, or the
    /// hash kind is <see cref="MtkSecCfgHashKind.Unresolved"/>.
    /// <para>声明大小与已记载布局矛盾，或摘要种类为 <see cref="MtkSecCfgHashKind.Unresolved"/>。</para></exception>
    public static async Task<byte[]> CreateVersion4Async(MtkSecCfgV4Report report, MtkSecCfgLockIntent intent,
        IMtkSecCfgHashEngine engine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(engine);
        ValidateVersion4Rewrite(report, intent);

        if (report.HashKind is MtkSecCfgHashKind.Software or MtkSecCfgHashKind.Unresolved)
        {
            throw new NotSupportedException(
                report.HashKind == MtkSecCfgHashKind.Software
                    ? "The software transform is applied by the synchronous writer."
                    : "The hash kind is unresolved, so no documented transform can be applied.");
        }

        var header = BuildVersion4Header(report, intent);
        var hash = await engine.TransformAsync(report.HashKind, SHA256.HashData(header), encrypt: true,
            cancellationToken).ConfigureAwait(false);
        return BuildVersion4Image(header, hash);
    }

    private static void ValidateVersion4Rewrite(MtkSecCfgV4Report report, MtkSecCfgLockIntent intent)
    {
        if (intent == MtkSecCfgLockIntent.Lock && report.LockStateRaw == LockStateWrittenWhenClosed)
        {
            throw new InvalidOperationException("The configuration is already closed.");
        }

        if (intent == MtkSecCfgLockIntent.Unlock && report.LockStateRaw == LockStateWrittenWhenOpen)
        {
            throw new InvalidOperationException("The configuration is already open.");
        }

        if (report.Size != Version4AgreedSize)
        {
            throw new NotSupportedException(
                $"The declared structure size 0x{report.Size:X} does not agree with the documented version 4 reader and writer layout.");
        }
    }

    private static byte[] BuildVersion4Header(MtkSecCfgV4Report report, MtkSecCfgLockIntent intent)
    {
        var (lockState, criticalLockState) = intent switch
        {
            MtkSecCfgLockIntent.Lock => (LockStateWrittenWhenClosed, CriticalLockStateWrittenWhenClosed),
            MtkSecCfgLockIntent.Unlock => (LockStateWrittenWhenOpen, CriticalLockStateWrittenWhenOpen),
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };

        var header = new byte[Version4HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, report.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), report.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), report.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), lockState);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), criticalLockState);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), report.RuntimeRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), EndFlag);
        return header;
    }

    private static byte[] BuildVersion4Image(byte[] header, byte[] hash)
    {
        var image = new byte[checked(Version4HeaderLength + hash.Length)];
        header.CopyTo(image, 0);
        hash.CopyTo(image.AsSpan(Version4HeaderLength));
        var padded = (image.Length + ImageAlignment - 1) / ImageAlignment * ImageAlignment;
        return padded == image.Length ? image : [.. image, .. new byte[padded - image.Length]];
    }

    /// <summary>Decodes a version 4 secure-boot configuration image, resolving hardware-protected hash
    /// variants through a target-driven engine when the software transform does not match.
    /// <para>解码版本 4 安全启动配置镜像；软件变换不匹配时，通过目标驱动的引擎解析硬件保护的摘要变体。</para></summary>
    /// <remarks>
    /// The probe order follows seccfg.py:100-115: software first, then the version 3 engine variant without
    /// the legacy bring-up, the same variant with it, and finally the first-generation engine variant.
    /// <para>试探顺序遵循 seccfg.py:100-115：先软件，再不带遗留启动序列的版本 3 引擎变体、带遗留启动序列的
    /// 同一变体，最后是第一代引擎变体。</para>
    /// </remarks>
    /// <param name="image">The exact partition bytes read from the device.
    /// <para>从设备读取的分区原始字节。</para></param>
    /// <param name="engine">The optional target-driven engine. 可选的目标驱动引擎。</param>
    /// <param name="cancellationToken">Cancellation for every target exchange.
    /// <para>每次目标交换的取消令牌。</para></param>
    /// <returns>The decoded report, or <see langword="null"/> when neither documented header matches or no
    /// transform explained the stored hash. <para>解码结果；头部都不匹配或没有任何变换能解释存储摘要时为
    /// <see langword="null"/>。</para></returns>
    public static async Task<MtkSecCfgV4Report?> TryReadVersion4Async(ReadOnlyMemory<byte> image,
        IMtkSecCfgHashEngine? engine, CancellationToken cancellationToken = default)
    {
        if (!TryReadVersion4(image.Span, out var report) || report is null)
        {
            return null;
        }

        if (report.HashKind != MtkSecCfgHashKind.Unresolved || engine is null)
        {
            return report;
        }

        var storedHash = image.Span.Slice((int)report.Size - 0x20, 0x20).ToArray();
        var computed = Convert.FromHexString(report.ComputedHashHex);
        foreach (var legacy in new[] { false, true })
        {
            var kind = legacy ? MtkSecCfgHashKind.HardwareV3Legacy : MtkSecCfgHashKind.HardwareV3;
            var decrypted = await engine.TransformAsync(kind, storedHash, encrypt: false, cancellationToken)
                .ConfigureAwait(false);
            if (decrypted.AsSpan().SequenceEqual(computed))
            {
                return report with { HashKind = kind };
            }
        }

        {
            var decrypted = await engine.TransformAsync(MtkSecCfgHashKind.Hardware, storedHash, encrypt: false,
                cancellationToken).ConfigureAwait(false);
            if (decrypted.AsSpan().SequenceEqual(computed))
            {
                return report with { HashKind = MtkSecCfgHashKind.Hardware };
            }
        }

        return report;
    }

    /// <summary>Validates that a caller-supplied image carries one of the documented headers and is
    /// aligned the way the documented writer produces.
    /// <para>校验调用方提供的镜像带有已记载的头部之一，且按已记载写入器的方式对齐。</para></summary>
    /// <param name="image">The exact bytes intended for the <c>seccfg</c> partition.
    /// <para>计划写入 <c>seccfg</c> 分区的精确字节。</para></param>
    /// <returns>The layout the header identifies. 头部标识的布局。</returns>
    /// <exception cref="ArgumentException"><paramref name="image"/> is empty, not a multiple of
    /// <see cref="ImageAlignment"/>, or has no documented header.
    /// <para><paramref name="image"/> 为空、不是 <see cref="ImageAlignment"/> 的整数倍，或不含已记载头部。</para></exception>
    public static MtkSecCfgFormat ValidateWriteCandidate(ReadOnlySpan<byte> image)
    {
        if (image.Length == 0 || image.Length % ImageAlignment != 0)
        {
            throw new ArgumentException(
                $"A secure-boot configuration image must be a non-empty multiple of 0x{ImageAlignment:X} bytes.", nameof(image));
        }

        return DetectFormat(image) ?? throw new ArgumentException(
            "The image carries neither documented secure-boot configuration header.", nameof(image));
    }

    /// <summary>Applies the software integrity transform documented for the version 4 hash and the
    /// version 3 software body (seccfg.py:51, :94, :202 and :270, whose parameters are given at
    /// Library/Hardware/hwcrypto_sej.py:671-685).
    /// <para>对版本 4 摘要与版本 3 软件主体应用已记载的软件完整性变换（seccfg.py:51、:94、:202 与 :270，
    /// 其参数见 Library/Hardware/hwcrypto_sej.py:671-685）。</para></summary>
    /// <param name="data">The block-aligned input bytes. 按块对齐的输入字节。</param>
    /// <param name="encrypt"><see langword="true"/> to apply the forward transform, <see langword="false"/>
    /// to apply the inverse used by the documented reader.
    /// <para><see langword="true"/> 表示正向变换，<see langword="false"/> 表示已记载读取器使用的逆变换。</para></param>
    /// <returns>The transformed bytes, the same length as <paramref name="data"/>.
    /// <para>变换后的字节，长度与 <paramref name="data"/> 相同。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not a multiple of the cipher
    /// block size. <para><paramref name="data"/> 不是密码块大小的整数倍。</para></exception>
    internal static byte[] SoftwareTransform(ReadOnlySpan<byte> data, bool encrypt)
    {
        if (data.Length % 16 != 0)
        {
            throw new ArgumentException("The software transform input must be block-aligned.", nameof(data));
        }

        using var cipher = Aes.Create();
        cipher.Key = SoftwareCipherKey.ToArray();
        cipher.IV = SoftwareCipherInitializationVector.ToArray();
        cipher.Mode = CipherMode.CBC;
        cipher.Padding = PaddingMode.None;
        using var transform = encrypt ? cipher.CreateEncryptor() : cipher.CreateDecryptor();
        return transform.TransformFinalBlock(data.ToArray(), 0, data.Length);
    }
}