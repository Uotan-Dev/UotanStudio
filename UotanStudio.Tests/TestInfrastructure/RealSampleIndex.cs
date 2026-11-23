using System.Buffers;
using System.Security.Cryptography;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Computes SHA-256 identities for sample files and pins expected digests so a substituted file cannot be
/// mistaken for the sample the assertions were derived from.
/// <para>计算样本文件的 SHA-256 身份并比对固定摘要，避免替换文件被误认为断言所依据的样本。</para>
/// </summary>
public static class RealSampleIndex
{
    /// <summary>Name used in messages for a SELinux binary policy sample.
    /// <para>SELinux 二进制策略样本在消息中的名称。</para></summary>
    public const string SelinuxPolicyVariable = "UOTAN_SELINUX_POLICY_SAMPLE";

    /// <summary>Name used in messages for a Qualcomm ELF Firehose sample.
    /// <para>Qualcomm ELF Firehose 样本在消息中的名称。</para></summary>
    public const string ElfFirehoseVariable = "UOTAN_ELF_FIREHOSE_SAMPLE";

    /// <summary>SHA-256 of the SELinux policy sample recorded in <c>docs/firmware-sample-index.md</c>.
    /// <para><c>docs/firmware-sample-index.md</c> 中记录的 SELinux 策略样本的 SHA-256。</para></summary>
    public const string SelinuxPolicySha256 =
        "B832719D9671A257DBEAE356B1E5429362DA0A710090FFDE92DBFF31B4F7ECA5";

    /// <summary>SHA-256 of the Qualcomm ELF Firehose sample. The pin is pending: no Firehose file is
    /// readable on this machine, so the digest recorded in <c>docs/qualcomm-metadata.md</c> cannot be
    /// reproduced and nothing is asserted.
    /// <para>Qualcomm ELF Firehose 样本的 SHA-256。该固定值尚未确定：本机没有可读取的 Firehose 文件，
    /// <c>docs/qualcomm-metadata.md</c> 中记录的摘要无法复现，因此不做任何断言。</para></summary>
    public const string? ElfFirehoseSha256 = null;

    private const long MaxSampleBytes = 256L * 1024 * 1024;
    private const int SampleBufferSize = 128 * 1024;

    /// <summary>Computes the SHA-256 of a sample and asserts it against the pinned digest when one exists.
    /// <para>计算样本的 SHA-256，并在已存在固定摘要时与其比对。</para></summary>
    /// <param name="path">Sample file path.<para>样本文件路径。</para></param>
    /// <param name="sampleName">Sample name used in diagnostic messages.<para>诊断消息中使用的样本名称。</para></param>
    /// <param name="expectedSha256Hex">Optional pinned uppercase or lowercase digest.<para>可选固定摘要，大小写不敏感。</para></param>
    /// <param name="cancellationToken">Cancels the hash computation.<para>取消哈希计算。</para></param>
    /// <returns>The computed uppercase digest.<para>计算得到的大写摘要。</para></returns>
    public static async Task<string> VerifyIdentityAsync(
        string path, string sampleName, string? expectedSha256Hex,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var length = new FileInfo(path).Length;
        if (length > MaxSampleBytes)
        {
            throw new InvalidOperationException(
                $"Sample {path} named by {sampleName} is {length} bytes, " +
                $"above the {MaxSampleBytes} byte verification limit.");
        }

        var hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (expectedSha256Hex is null)
            return hash;

        if (string.Equals(hash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            return hash;

        throw new InvalidOperationException(
            $"Sample identity mismatch for {sampleName} at {path}: " +
            $"expected SHA-256 {expectedSha256Hex} but computed {hash}. " +
            "Re-confirm sample provenance before trusting recorded assertions.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SampleBufferSize);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                SampleBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            int read;
            do
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, SampleBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read > 0)
                    sha256.TransformBlock(buffer, 0, read, null, 0);
            }
            while (read > 0);
            sha256.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha256.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}