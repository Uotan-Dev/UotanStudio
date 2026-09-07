using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

internal static class HashVerificationHelper
{
    /// <summary>
    /// Verifies the data blob hash of an install operation.
    /// <para>校验安装操作的数据块哈希。</para>
    /// </summary>
    internal static void VerifyDataHash(byte[] data, int dataLength, InstallOperation op)
    {
        if (op.DataSha256Hash is null || op.DataSha256Hash.Length == 0)
        {
            return;
        }

        var computed = SHA256.HashData(data.AsSpan(0, dataLength));
        var expectedHash = op.DataSha256Hash.ToByteArray();
        if (!ConstantTimeEquals(computed, expectedHash))
        {
            throw new PartitionExtractionException(
                $"Data SHA256 hash verification failed for operation at offset {op.DataOffset}. " +
                $"Expected: {Convert.ToHexString(expectedHash)}, " +
                $"Actual: {Convert.ToHexString(computed)}",
                string.Empty);
        }
    }

    /// <summary>
    /// Verifies the source data hash of an install operation.
    /// <para>校验安装操作的源数据哈希。</para>
    /// </summary>
    internal static void VerifySourceHash(byte[] sourceData, int dataLength, InstallOperation op)
    {
        if (op.SrcSha256Hash is null || op.SrcSha256Hash.Length == 0)
        {
            return;
        }

        var computed = SHA256.HashData(sourceData.AsSpan(0, dataLength));
        var expectedHash = op.SrcSha256Hash.ToByteArray();
        if (!ConstantTimeEquals(computed, expectedHash))
        {
            throw new PartitionExtractionException(
                $"Source SHA256 hash verification failed for operation at offset {op.DataOffset}. " +
                $"Expected: {Convert.ToHexString(expectedHash)}, " +
                $"Actual: {Convert.ToHexString(computed)}",
                string.Empty);
        }
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var result = 0;
        for (var i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }

        return result == 0;
    }
}
