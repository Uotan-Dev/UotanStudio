using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Registration gate for the real-device compatibility evidence matrix required by the Comm gap
/// review. Every matrix cell names a protocol, a mode, and a verification status; a cell may only
/// claim a real-hardware status when it carries an absolute verification date, a transcript path,
/// and 64-hex resource hashes. The validator itself is under test with deliberately broken rows so
/// the gate is proven to be able to fail.
/// <para>Comm 缺口评审所要求真机兼容性证据仓库的登记门。每个矩阵单元写明协议、模式与验证状态；
/// 只有携带绝对验证日期、transcript 路径与 64 位十六进制资源哈希的单元才能声称真机状态。
/// 校验器本身用故意构造的坏行做测试，证明这道门能够失败。</para>
/// </summary>
public sealed class CompatibilityMatrixRegistrationTests
{
    [Fact]
    public void Validate_SeedMatrix_OnlyClaimsScriptedOrUnverifiedStatuses()
    {
        var errors = CompatibilityMatrix.Validate(CompatibilityMatrix.Seed);

        Assert.Empty(errors);
        foreach (var cell in CompatibilityMatrix.Seed)
        {
            Assert.True(cell.Status is CompatibilityMatrixStatus.Unverified
                or CompatibilityMatrixStatus.Scripted,
                $"Seed cell '{cell.Protocol}/{cell.Mode}' must not claim hardware evidence yet.");
        }
    }

    [Fact]
    public void Validate_SeedMatrix_CoversEveryReviewedSubsystem()
    {
        var covered = CompatibilityMatrix.Seed
            .Select(cell => cell.Protocol)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var protocol in new[] { "sahara", "firehose", "mtk-brom", "mtk-da", "mtk-xflash",
            "mtk-xml-da", "usb", "serial" })
        {
            Assert.Contains(protocol, covered);
        }
    }

    [Fact]
    public void Validate_DeviceReadonlyCellWithoutTranscriptOrDate_ReportsErrors()
    {
        var broken = new CompatibilityMatrixCell[]
        {
            new("sahara", "hello-v1", CompatibilityMatrixStatus.DeviceReadonly, null, null, [])
        };

        var errors = CompatibilityMatrix.Validate(broken);

        Assert.Contains(errors, static error => error.Contains("transcript", StringComparison.Ordinal));
        Assert.Contains(errors, static error => error.Contains("verified-on date", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ResourceHashThatIsNotLowerHex64_ReportsError()
    {
        var broken = new CompatibilityMatrixCell[]
        {
            new("mtk-da", "v6-emmc", CompatibilityMatrixStatus.DeviceReadonly,
                "2026-09-01", "transcripts/mtk-da-v6.md", ["ZZZ"])
        };

        var errors = CompatibilityMatrix.Validate(broken);

        Assert.Contains(errors, static error => error.Contains("resource hash", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FullyEvidenceBackedCell_Passes()
    {
        var sound = new CompatibilityMatrixCell[]
        {
            new("mtk-da", "v6-emmc", CompatibilityMatrixStatus.DeviceReadonly,
                "2026-09-01", "transcripts/mtk-da-v6.md", [new string('a', 64)])
        };

        Assert.Empty(CompatibilityMatrix.Validate(sound));
    }
}

internal enum CompatibilityMatrixStatus
{
    Unverified,
    Scripted,
    RealTransport,
    DeviceReadonly,
    ControlledWrite
}

internal readonly record struct CompatibilityMatrixCell(
    string Protocol,
    string Mode,
    CompatibilityMatrixStatus Status,
    string? VerifiedOnDate,
    string? TranscriptPath,
    IReadOnlyList<string> ResourceSha256Hexes);

internal static class CompatibilityMatrix
{
    internal static readonly CompatibilityMatrixCell[] Seed =
    [
        new("sahara", "hello-mode-v1-v2", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("sahara", "read-memory", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("sahara", "programmer-upload", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("firehose", "configure-and-nop", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("firehose", "storage-geometry", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("firehose", "raw-read", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-brom", "reverse-handshake", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-brom", "sla-challenge", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-da", "v5-upload", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-da", "v6-upload", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-xflash", "emmc-geometry", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-xflash", "ufs-geometry", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-xflash", "nand-geometry", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-xflash", "nor-geometry", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-xml-da", "control-and-setup", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("mtk-xml-da", "storage-read", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("usb", "reenumeration-correlation", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("usb", "multi-device-same-vid-pid", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("serial", "short-read-recovery", CompatibilityMatrixStatus.Scripted, null, null, []),
        new("serial", "timeout-and-cancel", CompatibilityMatrixStatus.Scripted, null, null, [])
    ];

    internal static IReadOnlyList<string> Validate(IReadOnlyList<CompatibilityMatrixCell> cells)
    {
        List<string> errors = [];
        foreach (var cell in cells)
        {
            if (string.IsNullOrWhiteSpace(cell.Protocol) || string.IsNullOrWhiteSpace(cell.Mode))
            {
                errors.Add($"Cell '{cell.Protocol}/{cell.Mode}' is missing protocol or transcript mode name.");
            }

            var claimsHardware = cell.Status is CompatibilityMatrixStatus.RealTransport
                or CompatibilityMatrixStatus.DeviceReadonly or CompatibilityMatrixStatus.ControlledWrite;
            if (!claimsHardware)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(cell.TranscriptPath))
            {
                errors.Add($"Cell '{cell.Protocol}/{cell.Mode}' claims '{cell.Status}' without a transcript path.");
            }

            if (cell.VerifiedOnDate is not { Length: 10 } ||
                !DateOnly.TryParseExact(cell.VerifiedOnDate, "yyyy-MM-dd", out _))
            {
                errors.Add($"Cell '{cell.Protocol}/{cell.Mode}' claims '{cell.Status}' without an absolute " +
                    "verified-on date (yyyy-MM-dd).");
            }

            if (cell.ResourceSha256Hexes.Count == 0)
            {
                errors.Add($"Cell '{cell.Protocol}/{cell.Mode}' claims '{cell.Status}' without any resource hash.");
            }

            foreach (var hash in cell.ResourceSha256Hexes)
            {
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                {
                    errors.Add($"Cell '{cell.Protocol}/{cell.Mode}' carries an invalid 64-hex resource hash.");
                }
            }
        }

        return errors;
    }
}
