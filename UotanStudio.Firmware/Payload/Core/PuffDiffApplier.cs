using System.IO.Compression;
using BsDiff;
using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Applies PUFFDIFF patches. A PUFFDIFF patch is a bsdiff-style patch whose payload has been
/// re-encoded with the deterministic "puff" deflate subset (RFC 1951), so it can be decoded by a
/// standard raw-deflate inflater before the bsdiff patch is applied against the source partition data.
/// <para>应用 PUFFDIFF 补丁。PUFFDIFF 补丁是 bsdiff 风格补丁，其数据段已用确定性的
/// "puff" deflate 子集（RFC 1951）重新编码，因此可先用标准原始 deflate 解压器解码，
/// 再对源分区数据应用 bsdiff 补丁。</para>
/// </summary>
public static class PuffDiffApplier
{
    public static byte[] DecompressPatch(byte[] puffDiffPatch)
    {
        ArgumentNullException.ThrowIfNull(puffDiffPatch);
        return DecompressPatch(puffDiffPatch.AsSpan());
    }

    public static byte[] DecompressPatch(ReadOnlySpan<byte> puffDiffPatch)
    {
        if (puffDiffPatch.IsEmpty)
        {
            throw new PayloadException("PUFFDIFF patch is empty. PUFFDIFF 补丁为空。");
        }

        try
        {
            using var input = new MemoryStream(puffDiffPatch.ToArray(), writable: false);
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                deflate.CopyTo(output);
            }

            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new PayloadException($"Failed to puff-decompress PUFFDIFF patch: {ex.Message} PUFFDIFF 补丁解压失败。", ex);
        }
    }

    public static byte[] Apply(byte[] puffDiffPatch, byte[] oldData)
    {
        ArgumentNullException.ThrowIfNull(puffDiffPatch);
        ArgumentNullException.ThrowIfNull(oldData);
        return Apply(puffDiffPatch.AsSpan(), oldData.AsSpan());
    }

    public static byte[] Apply(ReadOnlySpan<byte> puffDiffPatch, ReadOnlySpan<byte> oldData)
    {
        var bsdiffPatch = DecompressPatch(puffDiffPatch);
        try
        {
            using var oldStream = new MemoryStream(oldData.ToArray(), writable: false);
            using var newStream = new MemoryStream();
            BinaryPatch.Apply(oldStream, () => new MemoryStream(bsdiffPatch, writable: false), newStream);
            return newStream.ToArray();
        }
        catch (Exception ex)
        {
            throw new PayloadException($"Failed to apply bsdiff patch on top of the PUFFDIFF data: {ex.Message} 应用 PUFFDIFF 差分失败。", ex);
        }
    }

    /// <summary>
    /// Creates a PUFFDIFF patch from two buffers (bsdiff against <paramref name="oldData"/>, then
    /// deflate-compressed with the puff-compatible deflate encoder). Primarily used for tooling and
    /// tests; the <see cref="PayloadBuilder"/> keeps producing full (non-delta) payloads.
    /// <para>由两个缓冲区生成 PUFFDIFF 补丁（对 <paramref name="oldData"/> 做 bsdiff，再用
    /// 兼容 puff 的 deflate 编码器压缩）。主要用于工具与测试；<see cref="PayloadBuilder"/>
    /// 仍保持全量构建。</para>
    /// </summary>
    /// <param name="oldData">The source partition base data. <para>源分区基准数据。</para></param>
    /// <param name="newData">The target partition data. <para>目标分区数据。</para></param>
    /// <returns>The puff-compressed bsdiff patch. <para>puff 压缩的 bsdiff 补丁。</para></returns>
    public static byte[] CreatePatch(byte[] oldData, byte[] newData)
    {
        ArgumentNullException.ThrowIfNull(oldData);
        ArgumentNullException.ThrowIfNull(newData);

        using var patchStream = new MemoryStream();
        BinaryPatch.Create(oldData, newData, patchStream);
        var patchBytes = patchStream.ToArray();

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(patchBytes, 0, patchBytes.Length);
        }

        return output.ToArray();
    }
}
