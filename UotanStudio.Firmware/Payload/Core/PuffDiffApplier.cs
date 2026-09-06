using System.IO.Compression;
using BsDiff;
using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

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
    /// Creates a PUFFDIFF patch from two buffers (bsdiff against oldData, then deflate-compressed with the puff-compatible deflate encoder).
    /// <para>由两个缓冲区生成 PUFFDIFF 补丁（对 oldData 做 bsdiff，再用 兼容 puff 的 deflate 编码器压缩）。</para>
    /// </summary>
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
