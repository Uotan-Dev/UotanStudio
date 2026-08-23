using System.Security.Cryptography;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Verifies the SHA256 hashes attached to install operations.
/// <para>校验附加在安装操作上的 SHA256 哈希。</para>
/// </summary>
internal static class HashVerificationHelper
{
    /// <summary>
    /// Verifies the data blob hash of an install operation.
    /// <para>校验安装操作的数据块哈希。</para>
    /// </summary>
    /// <param name="data">The operation data blob read from the payload stream.
    /// <para>从载荷流读取的操作数据块。</para>
    /// </param>
    /// <param name="dataLength">The number of valid bytes in <paramref name="data"/>.
    /// <para><paramref name="data"/> 中的有效字节数。</para>
    /// </param>
    /// <param name="op">The install operation carrying the expected hash.
    /// <para>携带预期哈希的安装操作。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when the computed hash does not match the expected hash.
    /// <para>当计算出的哈希与预期哈希不匹配时抛出。</para>
    /// </exception>
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
    /// <param name="sourceData">The source data read from the output file.
    /// <para>从输出文件读取的源数据。</para>
    /// </param>
    /// <param name="dataLength">The number of valid bytes in <paramref name="sourceData"/>.
    /// <para><paramref name="sourceData"/> 中的有效字节数。</para>
    /// </param>
    /// <param name="op">The install operation carrying the expected hash.
    /// <para>携带预期哈希的安装操作。</para>
    /// </param>
    /// <exception cref="PartitionExtractionException">Thrown when the computed hash does not match the expected hash.
    /// <para>当计算出的哈希与预期哈希不匹配时抛出。</para>
    /// </exception>
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