using System.Buffers.Binary;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

/// <summary>
/// A VBMeta header flag bit that tells the bootloader to skip one verification stage.
/// <para>通知引导程序跳过一个校验阶段的 VBMeta 头标志位。</para>
/// </summary>
[Flags]
public enum AvbVbmetaFlag : uint
{
    /// <summary>No flag selected. <para>未选择任何标志位。</para></summary>
    None = 0,

    /// <summary>
    /// Bit 0 of the big-endian <c>flags</c> field: dm-verity hashtree verification is skipped.
    /// <para>大端 <c>flags</c> 字段的第 0 位：跳过 dm-verity 哈希树校验。</para>
    /// </summary>
    HashtreeDisabled = AvbSigner.VbmetaFlagHashtreeDisabled,

    /// <summary>
    /// Bit 1 of the big-endian <c>flags</c> field: AVB signature verification is skipped, so the
    /// bootloader accepts the vbmeta struct without checking its stored hash or signature.
    /// <para>大端 <c>flags</c> 字段的第 1 位：跳过 AVB 签名校验，引导程序不再检查 vbmeta 结构存储的哈希与签名。</para>
    /// </summary>
    VerificationDisabled = AvbSigner.VbmetaFlagVerificationDisabled
}

public static partial class AvbSigner
{
    /// <summary>
    /// Sets or clears one VBMeta verification flag in place, mirroring fastboot's
    /// <c>--disable-verity</c> and <c>--disable-verification</c>: only the big-endian flags field of the
    /// VBMeta struct is rewritten, and the stored hash, signature and descriptors are left untouched.
    /// <para>原地设置或清除一个 VBMeta 校验标志位，复现 fastboot 的 <c>--disable-verity</c> 与
    /// <c>--disable-verification</c>：只改写 VBMeta 结构的大端标志字段，已存储的哈希、签名与描述符保持不变。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Standalone VBMeta image starting with <c>AVB0</c>, or a partition image whose trailing
    /// <c>AVBf</c> footer locates an in-image VBMeta struct; the image is opened read-write and must be
    /// at least 256 bytes long.
    /// <para>以 <c>AVB0</c> 开头的独立 VBMeta 镜像，或尾部 <c>AVBf</c> 指示镜像内 VBMeta 结构的分区镜像；
    /// 镜像以读写方式打开，长度至少 256 字节。</para>
    /// </param>
    /// <param name="flag">
    /// The single flag bit to write; <see cref="AvbVbmetaFlag.None"/> and combined masks are rejected.
    /// <para>要写入的单个标志位；<see cref="AvbVbmetaFlag.None"/> 与组合掩码会被拒绝。</para>
    /// </param>
    /// <param name="disabled">
    /// <see langword="true"/> sets the bit, <see langword="false"/> clears it; all other flag bits are preserved.
    /// <para>为 <see langword="true"/> 时置位，为 <see langword="false"/> 时清位；其余标志位保持不变。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before opening the file and observed by the bounded reads and the flags write.
    /// <para>打开文件前检查，并由有界读取与标志写入观察。</para>
    /// </param>
    /// <returns>
    /// Success result reporting the image path, its unchanged size and the <c>flags-patched</c> algorithm
    /// label, or a failure result explaining rejected input or an I/O fault.
    /// <para>成功结果报告镜像路径、未改变的大小与 <c>flags-patched</c> 算法标签；被拒绝的输入或 I/O 故障返回说明原因的失败结果。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static async Task<AvbSignResult> SetVbmetaFlagAsync(
        string imagePath,
        AvbVbmetaFlag flag,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Fail("imagePath must be provided. 必须提供 imagePath。");
        }

        if (flag is not (AvbVbmetaFlag.HashtreeDisabled or AvbVbmetaFlag.VerificationDisabled))
        {
            return Fail($"VBMeta flag '{flag}' is not a single supported bit. VBMeta 标志 '{flag}' 不是受支持的单个标志位。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(imagePath))
            {
                return Fail($"Image '{imagePath}' does not exist. 镜像 '{imagePath}' 不存在。");
            }

            await using var stream = new FileStream(
                imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BlockSize, FileOptions.RandomAccess);
            var length = stream.Length;
            if (length < AvbVBMetaImageHeader.Size)
            {
                return Fail($"'{imagePath}' is shorter than a VBMeta header. '{imagePath}' 短于 VBMeta 头。");
            }

            var vbmetaOffset = await LocateVbmetaAsync(stream, cancellationToken).ConfigureAwait(false);
            if (vbmetaOffset < 0)
            {
                return Fail($"'{imagePath}' carries no AVB0 VBMeta struct. '{imagePath}' 不含 AVB0 VBMeta 结构。");
            }

            var headerBytes = new byte[AvbVBMetaImageHeader.Size];
            stream.Position = vbmetaOffset;
            await stream.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            var header = AvbVBMetaImageHeader.FromBytes(headerBytes);
            if (!header.IsValid())
            {
                return Fail($"'{imagePath}' carries no AVB0 VBMeta header at offset {vbmetaOffset}. " +
                    $"'{imagePath}' 在偏移 {vbmetaOffset} 处没有 AVB0 VBMeta 头。");
            }

            var mask = (uint)flag;
            var updated = disabled ? header.Flags | mask : header.Flags & ~mask;
            if (updated != header.Flags)
            {
                var flagsBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(flagsBytes, updated);
                stream.Position = vbmetaOffset + 120;
                await stream.WriteAsync(flagsBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);

                var written = new byte[4];
                stream.Position = vbmetaOffset + 120;
                await stream.ReadExactlyAsync(written, cancellationToken).ConfigureAwait(false);
                if (!written.AsSpan().SequenceEqual(flagsBytes))
                {
                    return Fail($"Patched flags read back differently in '{imagePath}'. '{imagePath}' 中已修补的标志位回读不一致。");
                }
            }

            return new AvbSignResult
            {
                Success = true,
                OutputPath = imagePath,
                AlgorithmName = "flags-patched",
                OutputSize = length
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Locates the VBMeta struct inside an image: offset 0 for a standalone <c>AVB0</c> image, or the
    /// footer-declared offset for a partition image, validated to contain a full 256-byte header.
    /// <para>在镜像内定位 VBMeta 结构：独立 <c>AVB0</c> 镜像为偏移 0，分区镜像取尾部声明的偏移，并校验其能容纳完整 256 字节头。</para>
    /// </summary>
    private static async Task<long> LocateVbmetaAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        stream.Position = 0;
        await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (magic.AsSpan().SequenceEqual(MagicBytes))
        {
            return 0;
        }

        var length = stream.Length;
        if (length < AvbFooter.Size)
        {
            return -1;
        }

        var footerBytes = new byte[AvbFooter.Size];
        stream.Position = length - AvbFooter.Size;
        await stream.ReadExactlyAsync(footerBytes, cancellationToken).ConfigureAwait(false);
        if (AvbFooter.TryLoad(footerBytes) is not { } footer
            || footer.VbmetaOffset > (ulong)length - AvbVBMetaImageHeader.Size)
        {
            return -1;
        }

        return (long)footer.VbmetaOffset;
    }
}