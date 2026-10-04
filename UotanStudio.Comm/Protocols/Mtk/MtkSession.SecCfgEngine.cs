using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Protocols.Mtk.HwCrypto;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Optional register seam a caller allows for the SEJ-protected seccfg variants.
    /// <para>调用方为 SEJ 保护的 seccfg 变体允许使用的寄存器接缝。</para></summary>
    private sealed class SejRegisterAccess(
        Func<uint, CancellationToken, Task<uint>> read,
        Func<uint, uint, CancellationToken, Task> write) : IMtkSejRegisterAccess
    {
        public Task<uint> ReadWordAsync(uint address, CancellationToken cancellationToken) => read(address, cancellationToken);

        public Task WriteWordAsync(uint address, uint value, CancellationToken cancellationToken) =>
            write(address, value, cancellationToken);
    }

    /// <summary>
    /// Builds the SEJ hash engine for this session's chip, or returns null when the reference chip table
    /// records no SEJ base for it.
    /// <para>为本会话芯片构建 SEJ 摘要引擎；参考芯片表未记录该芯片的 SEJ 基址时返回 null。</para></summary>
    private static MtkSejCryptoEngine? CreateSejEngine(MtkDeviceInfo? device, IMtkSejRegisterAccess access)
    {
        if (device is null || MtkBromChipTable.Find(device.HardwareCode) is not { } profile
            || profile.SejBaseAddress is not { } sejBase)
        {
            return null;
        }

        return new MtkSejCryptoEngine(device.HardwareCode, sejBase, access);
    }

    /// <summary>
    /// Builds the replacement image for the apply paths. The software transforms stay off-device; every
    /// hardware variant is resolved through the running target exactly the way the reference client does,
    /// and stays refused when no register seam or SEJ base is available.
    /// <para>为 Apply 路径构造替换镜像。软件变换保持在设备外；所有硬件变体都按参考客户端的方式经运行中的
    /// 目标解析，在没有寄存器接缝或 SEJ 基址时仍被拒绝。</para></summary>
    internal async Task<byte[]> CreateSecCfgReplacementAsync(MtkSecCfgView view, byte[] image,
        MtkSecCfgLockIntent intent, IMtkSejRegisterAccess? registerAccess, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(image);
        if (view.Version4 is null)
        {
            if (view.Version3 is null)
            {
                throw new NotSupportedException(
                    "The recorded secure-boot view carries neither a version 4 nor a version 3 report, so no documented layout can be rewritten.");
            }

            return await RewriteSecCfgVersion3Async(view.Version3, image, intent,
                CreateVersion3SejEngine(registerAccess), cancellationToken).ConfigureAwait(false);
        }

        if (view.Version4.HashKind == MtkSecCfgHashKind.Software)
        {
            return MtkSecCfg.CreateVersion4(view.Version4, intent);
        }

        if (registerAccess is null || CreateSejEngine(DeviceInfo, registerAccess) is not { } engine)
        {
            throw new NotSupportedException(
                "The stored hash is not protected by the software transform, so rewriting it requires the target SEJ engine.");
        }

        var resolved = await MtkSecCfg.TryReadVersion4Async(image, engine, cancellationToken).ConfigureAwait(false);
        if (resolved is null || resolved.HashKind == MtkSecCfgHashKind.Unresolved)
        {
            throw new NotSupportedException(
                "The target SEJ engine did not reproduce the stored hash, so the layout cannot be rewritten.");
        }

        return await MtkSecCfg.CreateVersion4Async(resolved, intent, engine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the target-driven engine a version 3 body rewrite may need. The apply callers hand a seam
    /// only for version 4 layouts, so a version 3 rewrite falls back to the register seam of the session
    /// stage that already served the partition read; the software body transform never needs the engine
    /// (<c>mtkclient/Library/Hardware/seccfg.py</c>, lines 293-310, probes it before any hardware variant).
    /// <para>解析版本 3 主体重写可能需要的目标驱动引擎。Apply 调用方只为版本 4 布局交出接缝，因此版本 3
    /// 重写回退到已经服务过本次分区读取的会话阶段所拥有的寄存器接缝；软件主体变换从不需要引擎
    /// （<c>mtkclient/Library/Hardware/seccfg.py</c> 第 293-310 行在任何硬件变体之前先试探软件变换）。</para>
    /// </summary>
    /// <param name="registerAccess">The caller-supplied seam, when one exists.
    /// <para>调用方提供的接缝（如有）。</para></param>
    /// <returns>The engine, or <see langword="null"/> when no stage seam or SEJ base is available.
    /// <para>引擎；没有可用的阶段接缝或 SEJ 基址时为 <see langword="null"/>。</para></returns>
    private IMtkSecCfgHashEngine? CreateVersion3SejEngine(IMtkSejRegisterAccess? registerAccess)
    {
        var access = registerAccess ?? TryCreateStageSejRegisterAccess();
        return access is null ? null : CreateSejEngine(DeviceInfo, access);
    }

    /// <summary>
    /// Builds the register seam of the storage family that answered the seccfg partition read: the Legacy
    /// register commands on the Legacy stage, the stage-two custom register codes on the XFlash stage, and
    /// nothing on any other stage. Creating the seam performs no device exchange.
    /// <para>为响应 seccfg 分区读取的存储家族构建寄存器接缝：Legacy 阶段用 Legacy 寄存器命令，XFlash 阶段用
    /// 二阶段自定义寄存器码，其它阶段没有接缝。创建接缝本身不进行任何设备交换。</para>
    /// </summary>
    /// <returns>The seam, or <see langword="null"/> for a stage without register access.
    /// <para>接缝；没有寄存器访问能力的阶段返回 <see langword="null"/>。</para></returns>
    private IMtkSejRegisterAccess? TryCreateStageSejRegisterAccess()
    {
        if (_state is MtkSessionState.XFlashStageTwoStarted or MtkSessionState.XFlashStorageReady)
        {
            var channel = _xflash;
            if (channel is null)
            {
                return null;
            }

            return new SejRegisterAccess(
                async (address, token) => BinaryPrimitives.ReadUInt32LittleEndian(
                    await ReadXFlashSecurityRegisterAsync(channel, address, token).ConfigureAwait(false)),
                (address, value, token) => WriteXFlashSecurityRegisterAsync(channel, address, value, token));
        }

        return _state == MtkSessionState.LegacyStorageReady
            ? new SejRegisterAccess(ReadLegacySecurityRegisterAsync, WriteLegacySecurityRegisterAsync)
            : null;
    }

    /// <summary>
    /// The byte length the documented version 3 body needs before the decoded fields: the 20 image-info
    /// slots of 0x68 bytes, the SIU status, the configuration status and the attribute dwords
    /// (<c>mtkclient/Library/Hardware/seccfg.py</c>, lines 317-323).
    /// <para>已记载的版本 3 主体在解码字段之前的最小字节数：20 个 0x68 字节的镜像信息槽，外加 SIU 状态、
    /// 配置状态与属性双字（<c>mtkclient/Library/Hardware/seccfg.py</c> 第 317-323 行）。</para>
    /// </summary>
    private const int Version3FieldsEnd = 0x850 + 3 * 4;

    private const uint Version3AttributeLock = 0x6000;
    private const uint Version3AttributeVerified = 0x6001;
    private const uint Version3AttributeCustom = 0x6002;
    private const uint Version3AttributeMpDefault = 0x6003;
    private const uint Version3AttributeDefault = 0x33333333;
    private const uint Version3AttributeUnlock = 0x44444444;
    private const uint Version3StatusComplete = 0x43434343;
    private const uint Version3StatusIncomplete = 0x49494949;

    /// <summary>
    /// Rewrites a version 3 secure-boot configuration image, computing the body integrity transform the
    /// same way the reference client does: the software body transform is reproduced off-device, every
    /// hardware variant is resolved through the running target's SEJ engine, the documented attribute
    /// transition is applied to the decoded body and the body is re-protected with the variant that
    /// explained it (<c>mtkclient/Library/Hardware/seccfg.py</c>, lines 274-328 and 330-386, driven by
    /// <c>Library/DA/xflash/extension/xflash.py</c>, lines 802-825).
    /// <para>重写版本 3 的安全启动配置镜像，主体完整性变换的计算方式与参考客户端一致：软件主体变换在设备外
    /// 复现，每个硬件变体都经运行中目标的 SEJ 引擎解析，对解码后的主体应用已记载的属性转换，再用解释它的
    /// 那个变体重新保护主体（<c>mtkclient/Library/Hardware/seccfg.py</c> 第 274-328 与 330-386 行，由
    /// <c>Library/DA/xflash/extension/xflash.py</c> 第 802-825 行驱动）。</para>
    /// </summary>
    /// <param name="report">The version 3 header report decoded by <see cref="MtkSecCfg.TryReadVersion3"/>.
    /// <para>由 <see cref="MtkSecCfg.TryReadVersion3"/> 解码的版本 3 头部报告。</para></param>
    /// <param name="image">The exact partition bytes read from the device.
    /// <para>从设备读取的分区原始字节。</para></param>
    /// <param name="intent">The requested state transition. 请求的状态转换。</param>
    /// <param name="engine">The target-driven engine for the hardware variants, or null when the layout
    /// can only be rewritten through the software transform.
    /// <para>硬件变体的目标驱动引擎；布局只能经软件变换重写时为 null。</para></param>
    /// <param name="cancellationToken">Cancellation for every target exchange.
    /// <para>每次目标交换的取消令牌。</para></param>
    /// <returns>The rewritten image, padded to a multiple of
    /// <see cref="MtkSecCfg.ImageAlignment"/>.
    /// <para>重写后的镜像，补齐到 <see cref="MtkSecCfg.ImageAlignment"/> 的整数倍。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.
    /// <para><paramref name="report"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The version 3 body is shorter than the documented field
    /// window or not block aligned, its protection cannot be explained by the software transform without
    /// a target engine, no variant reproduces the documented markers, the reference refuses the secondary
    /// marker offset, or the decoded status or attribute is outside the documented values.
    /// <para>版本 3 主体短于已记载字段窗口或未对齐块大小、没有目标引擎时其保护无法由软件变换解释、没有任何
    /// 变体能复现已记载标志、参考实现对次级标志偏移说不支持，或解码出的状态或属性不在已记载取值之内。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">The decoded attribute already carries the requested
    /// state, which the documented writer refuses (seccfg.py:338-341).
    /// <para>解码出的属性已处于请求状态，已记载的写入器会拒绝该请求（seccfg.py:338-341）。</para></exception>
    internal static async Task<byte[]> RewriteSecCfgVersion3Async(MtkSecCfgV3Report report,
        ReadOnlyMemory<byte> image, MtkSecCfgLockIntent intent, IMtkSecCfgHashEngine? engine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var bodyLength = report.BodyLength;
        if (image.Length < checked(MtkSecCfg.Version3HeaderLength + bodyLength)
            || bodyLength < Version3FieldsEnd
            || bodyLength % MtkSejCryptoEngine.BlockBytes != 0)
        {
            throw new NotSupportedException(
                "The version 3 body is shorter than the documented field window or is not block aligned, so no documented transform can rewrite it.");
        }

        var body = image.Span.Slice(MtkSecCfg.Version3HeaderLength, bodyLength).ToArray();
        var (plain, kind) = await ResolveVersion3BodyAsync(body, engine, cancellationToken).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(0x854));
        if (status is not (Version3StatusComplete or Version3StatusIncomplete))
        {
            throw new NotSupportedException(
                $"The decoded version 3 configuration status 0x{status:X8} is outside the documented values, so the layout is not rewritten.");
        }

        var attribute = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(0x858));
        var (newEncLen, newAttribute) = TranslateVersion3LockIntent(intent, attribute);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(0x858), newAttribute);
        var protectedBody = kind == MtkSecCfgHashKind.Software
            ? MtkSecCfg.SoftwareTransform(plain, encrypt: true)
            : await engine!.TransformAsync(kind, plain, encrypt: true, cancellationToken).ConfigureAwait(false);
        return BuildVersion3Image(report, newEncLen, protectedBody);
    }

    /// <summary>
    /// Probes the stored version 3 body against the documented transforms in the reference order: the
    /// software body transform first, then the version 3 engine variant, the first-generation engine
    /// variant and the version 3 variant with the legacy bring-up
    /// (<c>mtkclient/Library/Hardware/seccfg.py</c>, lines 293-310).
    /// <para>按参考顺序对存储的版本 3 主体试探已记载变换：先是软件主体变换，然后是版本 3 引擎变体、第一代
    /// 引擎变体，最后是带遗留启动序列的版本 3 变体（<c>mtkclient/Library/Hardware/seccfg.py</c>
    /// 第 293-310 行）。</para>
    /// </summary>
    /// <param name="body">The stored opaque body bytes. <para>存储的不透明主体字节。</para></param>
    /// <param name="engine">The target-driven engine, or null when only the software transform can be tried.
    /// <para>目标驱动引擎；只能尝试软件变换时为 null。</para></param>
    /// <param name="cancellationToken">Cancellation for every target exchange.
    /// <para>每次目标交换的取消令牌。</para></param>
    /// <returns>The plaintext body and the variant that explained it.
    /// <para>明文主体与解释它的那个变体。</para></returns>
    private static async Task<(byte[] Plain, MtkSecCfgHashKind Kind)> ResolveVersion3BodyAsync(byte[] body,
        IMtkSecCfgHashEngine? engine, CancellationToken cancellationToken)
    {
        var candidate = MtkSecCfg.SoftwareTransform(body, encrypt: false);
        var marker = Version3MarkerOffset(candidate);
        if (marker == 0x850)
        {
            throw new NotSupportedException(
                "The version 3 body marker sits at 0x850, an offset the reference client reports as unsupported instead of rewriting; no hash is fabricated.");
        }

        if (marker == 0)
        {
            return (candidate, MtkSecCfgHashKind.Software);
        }

        if (engine is null)
        {
            throw new NotSupportedException(
                "The version 3 body is not protected by the software transform, so rewriting it requires the target SEJ engine.");
        }

        foreach (var kind in new[]
        {
            MtkSecCfgHashKind.HardwareV3, MtkSecCfgHashKind.Hardware, MtkSecCfgHashKind.HardwareV3Legacy
        })
        {
            candidate = await engine.TransformAsync(kind, body, encrypt: false, cancellationToken)
                .ConfigureAwait(false);
            marker = Version3MarkerOffset(candidate);
            if (marker == 0)
            {
                return (candidate, kind);
            }

            if (marker == 0x850)
            {
                throw new NotSupportedException(
                    "The version 3 body marker sits at 0x850, an offset the reference client reports as unsupported instead of rewriting; no hash is fabricated.");
            }
        }

        throw new NotSupportedException(
            "The target SEJ engine did not reproduce the stored version 3 body transform, so the layout cannot be rewritten.");
    }

    /// <summary>
    /// Applies the documented version 3 attribute transition (seccfg.py:331-349): closing is allowed only
    /// from the unlock attribute, opening only from the documented non-unlock attributes, and the other
    /// documented values stay refused rather than guessed.
    /// <para>应用已记载的版本 3 属性转换（seccfg.py:331-349）：关闭只允许从解锁属性发起，打开只允许从已记载的
    /// 非解锁属性发起，其余已记载取值之外的一律拒绝而不是猜测。</para>
    /// </summary>
    /// <param name="intent">The requested state transition. 请求的状态转换。</param>
    /// <param name="attribute">The decoded attribute dword. <para>解码出的属性双字。</para></param>
    /// <returns>The encryption-length marker and the attribute the documented writer stores.
    /// <para>已记载写入器存储的加密长度标志与属性。</para></returns>
    private static (uint EncryptionLength, uint Attribute) TranslateVersion3LockIntent(
        MtkSecCfgLockIntent intent, uint attribute)
    {
        if (intent == MtkSecCfgLockIntent.Lock)
        {
            if (attribute is Version3AttributeLock or Version3AttributeDefault)
            {
                throw new InvalidOperationException("The configuration is already closed.");
            }

            if (attribute != Version3AttributeUnlock)
            {
                throw new NotSupportedException(
                    $"The version 3 attribute 0x{attribute:X8} carries no documented lock state, so it is not rewritten.");
            }

            return (MtkSecCfg.Version3ClosedEncryptionLength, Version3AttributeDefault);
        }

        if (intent == MtkSecCfgLockIntent.Unlock)
        {
            if (attribute == Version3AttributeUnlock)
            {
                throw new InvalidOperationException("The configuration is already open.");
            }

            if (attribute is not (Version3AttributeDefault or Version3AttributeMpDefault or Version3AttributeCustom
                or Version3AttributeVerified or Version3AttributeLock))
            {
                throw new NotSupportedException(
                    $"The version 3 attribute 0x{attribute:X8} carries no documented unlock state, so it is not rewritten.");
            }

            return (MtkSecCfg.Version3OpenEncryptionLength, Version3AttributeUnlock);
        }

        throw new ArgumentOutOfRangeException(nameof(intent));
    }

    /// <summary>
    /// Assembles the header, the protected body and the end marker exactly the way the documented writer
    /// does (seccfg.py:351-382) and pads the image to the writer's 0x200-byte alignment.
    /// <para>完全按已记载写入器的方式组装头部、受保护主体与结束标志（seccfg.py:351-382），并把镜像补齐到写入器
    /// 的 0x200 字节对齐。</para>
    /// </summary>
    /// <param name="report">The decoded header the fields are inherited from.
    /// <para>继承其字段的已解码头部。</para></param>
    /// <param name="encryptionLength">The state-marking encryption-length dword.
    /// <para>标识状态的加密长度双字。</para></param>
    /// <param name="protectedBody">The re-protected body bytes. <para>重新保护后的主体字节。</para></param>
    /// <returns>The padded image. <para>补白后的镜像。</para></returns>
    private static byte[] BuildVersion3Image(MtkSecCfgV3Report report, uint encryptionLength,
        byte[] protectedBody)
    {
        var banner = Convert.FromHexString(report.BannerHex);
        if (banner.Length != MtkSecCfg.Version3BannerLength)
        {
            throw new NotSupportedException(
                "The recorded version 3 banner is not the documented 16 bytes, so the layout is not rewritten.");
        }

        var declaredSize = checked(MtkSecCfg.Version3HeaderLength + protectedBody.Length + 4);
        var padded = checked((declaredSize + MtkSecCfg.ImageAlignment - 1) / MtkSecCfg.ImageAlignment
            * MtkSecCfg.ImageAlignment);
        var image = new byte[padded];
        banner.CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10), report.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), report.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), (uint)declaredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x1C), report.EncryptionOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), encryptionLength);
        image[0x24] = report.SoftwareLockTry;
        image[0x25] = report.SoftwareLockDone;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x26), report.PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x28), report.PageCount);
        protectedBody.CopyTo(image, MtkSecCfg.Version3HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(MtkSecCfg.Version3HeaderLength
            + protectedBody.Length), MtkSecCfg.EndFlag);
        return image;
    }

    /// <summary>
    /// Locates the version 3 body marker the reference verifier accepts: the IIII, CCCC or all-zero dword
    /// at offset 0 or at 0x850, the two offsets the documented verification step reads
    /// (<c>mtkclient/Library/Hardware/seccfg.py</c>, lines 265-272).
    /// <para>定位参考校验器接受的版本 3 主体标志：偏移 0 或 0x850 处的 IIII、CCCC 或全零双字，即已记载校验
    /// 步骤读取的两个偏移（<c>mtkclient/Library/Hardware/seccfg.py</c> 第 265-272 行）。</para>
    /// </summary>
    /// <param name="plain">The decrypted body bytes. <para>解密后的主体字节。</para></param>
    /// <returns>The marker offset, or <c>-1</c> when neither documented offset carries a marker.
    /// <para>标志所在偏移；两个已记载偏移都没有标志时为 <c>-1</c>。</para></returns>
    private static int Version3MarkerOffset(byte[] plain)
    {
        var primary = plain.AsSpan(0, 4);
        if (IsVersion3Marker(primary))
        {
            return 0;
        }

        return IsVersion3Marker(plain.AsSpan(0x850, 4)) ? 0x850 : -1;
    }

    private static bool IsVersion3Marker(ReadOnlySpan<byte> dword) =>
        dword is [0x49, 0x49, 0x49, 0x49] or [0x43, 0x43, 0x43, 0x43] or [0x00, 0x00, 0x00, 0x00];
}