using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Firmware.Executable.Qualcomm;
using UotanStudio.Hash.Digests;

namespace UotanStudio.ViewModels.Editors;

internal sealed partial class QualcommWorkspaceAnalyzer : IQualcommWorkspaceAnalyzer
{
    private const int MaximumRootBytes = 1024 * 1024;

    public async Task<QualcommWorkspaceReport> AnalyzeAsync(
        string imagePath, string? trustedRootPath, CancellationToken cancellationToken)
    {
        var options = new QualcommAnalysisOptions();
        if (!string.IsNullOrWhiteSpace(trustedRootPath))
        {
            await using var input = new FileStream(trustedRootPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            if (input.Length is <= 0 or > MaximumRootBytes)
            {
                throw new InvalidDataException("The selected root certificate exceeds the supported size.");
            }

            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            using var certificate = LoadRootCertificate(bytes);
            options = options with
            {
                TrustedRootCertificateSha256 = [Convert.ToHexString(Sha256.ComputeHash(certificate.RawData))]
            };
        }

        var result = await QualcommImageAnalyzer.AnalyzeAsync(imagePath, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var overview = new List<QualcommReportField>
        {
            Field("Recognized", result.Format?.ToString() ?? "Unrecognized"),
            Field("FileSize", result.FileSize.ToString("N0", CultureInfo.CurrentCulture))
        };
        if (result.Mbn is { } mbn)
        {
            overview.Add(Field("MbnVersion", mbn.Version.ToString(CultureInfo.InvariantCulture)));
            if (mbn.ImageId is { } imageId)
            {
                overview.Add(Field("ImageId", $"0x{imageId:X8}"));
            }
            if (mbn.SoftwareId is { } softwareId)
            {
                overview.Add(Field("SoftwareId", $"0x{softwareId:X8}"));
            }
            overview.Add(Field("HashAlgorithm", string.IsNullOrEmpty(mbn.HashAlgorithm) ? "Unknown" : mbn.HashAlgorithm,
                string.IsNullOrEmpty(mbn.HashAlgorithm)));
            overview.Add(Field("HashLength", mbn.HashLength.ToString(CultureInfo.InvariantCulture)));
        }

        List<QualcommReportField> segments = [];
        if (result.Elf is { } elf)
        {
            overview.Add(Field("Architecture", elf.Header.Machine switch
            {
                40 => "ARM (0x0028)",
                183 => "AArch64 (0x00B7)",
                _ => $"0x{elf.Header.Machine:X4}"
            }));
            overview.Add(Field("EntryPoint", $"0x{elf.Header.EntryPoint:X}"));
            overview.Add(Field("ElfClass", elf.Header.Is64Bit ? "ELF64" : "ELF32"));
            overview.Add(Field("ProgramHeaderCount", elf.Segments.Count.ToString(CultureInfo.InvariantCulture)));
            foreach (var segment in elf.Segments.Take(512))
            {
                var group = $"#{segment.Index}";
                segments.Add(Field("FileOffset", $"0x{segment.Offset:X}", group: group));
                segments.Add(Field("SegmentType", $"0x{segment.Type:X}", group: group));
                segments.Add(Field("Flags", $"0x{segment.Flags:X8}", group: group));
                segments.Add(Field("VirtualAddress", $"0x{segment.VirtualAddress:X}", group: group));
                segments.Add(Field("PhysicalAddress", $"0x{segment.PhysicalAddress:X}", group: group));
                segments.Add(Field("FileSize", $"0x{segment.FileSize:X}", group: group));
                segments.Add(Field("MemorySize", $"0x{segment.MemorySize:X}", group: group));
                segments.Add(Field("Alignment", $"0x{segment.Alignment:X}", group: group));
                segments.Add(Field("PbtType", $"0x{segment.Flags >> 24 & 7:X}", group: group));
                segments.Add(Field("PbtPaged", (segment.Flags & 0x100000) != 0 ? "Yes" : "No", true, group));
            }
            if (elf.Segments.Count > 512)
            {
                segments.Add(Field("MoreSegments", (elf.Segments.Count - 512).ToString(CultureInfo.InvariantCulture)));
            }
        }
        foreach (var digest in result.SegmentDigests.Take(512))
        {
            segments.Add(new($"#{digest.SegmentIndex}" + (digest.PageIndex is { } page ? $"/{page}" : ""),
                digest.Status.ToString(), LocalizedValue: true));
        }
        if (result.SegmentDigests.Count > 512)
        {
            segments.Add(Field("MoreDigests", (result.SegmentDigests.Count - 512).ToString(CultureInfo.InvariantCulture)));
        }

        var verification = new List<QualcommReportField>
        {
            Status("Digest", result.HashStatus),
            Status("Signature", result.SignatureStatus),
            Status("QtiSignature", result.QualcommSignature.SignatureStatus),
            Status("OemSignature", result.OemSignature.SignatureStatus),
            Status("QtiChain", result.QualcommSignature.CertificateChainStatus),
            Status("OemChain", result.OemSignature.CertificateChainStatus),
            Status("MetadataRootHash", result.MetadataRootHashStatus),
            Status("Trust", result.TrustedRootStatus),
            Status("Device", result.DeviceUsabilityStatus)
        };
        foreach (var certificate in result.Certificates.Take(32))
        {
            var group = $"{certificate.Authority} #{certificate.Index}";
            verification.Add(Field("Subject", certificate.Subject, group: group));
            verification.Add(Field("Issuer", certificate.Issuer, group: group));
            verification.Add(Field("CertificateSerial", certificate.SerialNumber, group: group));
            verification.Add(Field("NotBefore", certificate.NotBefore.ToString("u", CultureInfo.InvariantCulture), group: group));
            verification.Add(Field("NotAfter", certificate.NotAfter.ToString("u", CultureInfo.InvariantCulture), group: group));
            verification.Add(Field("CertificateAlgorithm", certificate.SignatureAlgorithmOid, group: group));
            verification.Add(Field("PublicKeyAlgorithm", certificate.PublicKeyAlgorithmOid, group: group));
            verification.Add(Field("CertificateSha256", certificate.Sha256, group: group));
            verification.Add(Field("CertificateSha384", certificate.Sha384, group: group));
        }
        AddAuthority("QTI", result.QualcommSignature);
        AddAuthority("OEM", result.OemSignature);
        foreach (var diagnostic in result.Diagnostics.Take(32))
        {
            verification.Add(new("Diagnostic", diagnostic, LocalizedName: true));
        }

        List<QualcommReportField> programmer = [];
        foreach (var command in result.SupportedCommands.Take(256))
        {
            programmer.Add(Field("EvidenceSource", command.Source, true, command.Name));
            programmer.Add(Field("FileOffset", $"0x{command.FileOffset:X}", group: command.Name));
        }
        foreach (var version in result.VersionStrings)
        {
            programmer.Add(Field(version.Name, version.Value));
            programmer.Add(Field("EvidenceSource", "StringLiteral", true));
            programmer.Add(Field("FileOffset", $"0x{version.FileOffset:X}"));
        }
        return new QualcommWorkspaceReport(
            result.Format is null ? "UnrecognizedStatus" : "Unverified",
            overview, segments, verification, programmer)
        {
            Metadata = MetadataFields(result),
            Analysis = result
        };

        void AddAuthority(string name, QualcommAuthorityResult authority)
        {
            if (authority.SignatureAlgorithm is { } algorithm)
            {
                verification.Add(Field("VerifiedAlgorithm", algorithm, group: name));
            }
            if (authority.RootCertificateSha256 is { } root)
            {
                verification.Add(Field("SelectedRootSha256", root, group: name));
            }
        }
    }

    private static X509Certificate2 LoadRootCertificate(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith("-----BEGIN CERTIFICATE-----"u8))
        {
            var pem = System.Text.Encoding.ASCII.GetString(bytes);
            if (pem.IndexOf("-----BEGIN CERTIFICATE-----", 1, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException("Select a single root certificate.");
            }

            return X509Certificate2.CreateFromPem(pem);
        }

        return X509CertificateLoader.LoadCertificate(bytes);
    }

    private static QualcommReportField Field(string name, string value, bool localizedValue = false, string? group = null)
        => new(name, value, LocalizedName: true, LocalizedValue: localizedValue, Group: group);

    private static QualcommReportField Status(string name, QualcommVerificationStatus status)
        => Field(name, status.ToString(), localizedValue: true);
}