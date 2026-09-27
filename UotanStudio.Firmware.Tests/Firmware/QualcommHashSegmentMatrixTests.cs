using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class QualcommHashSegmentMatrixTests
{
    private static byte[] BuildMbn(uint version, Chain qti, Chain oem, bool applyMasks,
        int qtiMetadataLength = 0, int oemMetadataLength = 0)
    {
        const int hashLength = 32;
        const int signatureLength = 256;
        var headerSize = version == 6 ? 48 : 40;
        var common = version == 7 ? 24 : 0;
        var qtiSignatureLength = version == 3 ? 0 : signatureLength;
        var qtiCertificateLength = version == 3 ? 0 : qti.Certificates.Length;
        var metadataStart = headerSize + common;
        var hashOffset = metadataStart + qtiMetadataLength + oemMetadataLength;
        var image = new byte[hashOffset + hashLength + qtiSignatureLength + qtiCertificateLength
            + signatureLength + oem.Certificates.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(image, version == 7 ? 0u : 5u);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), hashLength);
        if (version == 7)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)qtiMetadataLength);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)oemMetadataLength);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), (uint)qtiSignatureLength);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), (uint)qtiCertificateLength);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), signatureLength);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), (uint)oem.Certificates.Length);
            image.AsSpan(40, common).Fill(0x33);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)(image.Length - headerSize));
            if (version == 6)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(40), (uint)qtiMetadataLength);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), (uint)oemMetadataLength);
            }

            if (version == 3)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 0xDEADBEEF);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 0xFEEDFACE);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)qtiSignatureLength);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)qtiCertificateLength);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), signatureLength);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), (uint)oem.Certificates.Length);
        }

        image.AsSpan(metadataStart, qtiMetadataLength).Fill(0xC3);
        if (qtiMetadataLength >= 16)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(metadataStart + 12), 0);
        }

        image.AsSpan(metadataStart + qtiMetadataLength, oemMetadataLength).Fill(0xD4);
        image.AsSpan(hashOffset, hashLength).Fill(0x77);

        var signedLength = hashOffset + hashLength;
        var full = image[..signedLength].ToArray();
        var qtiDigestSource = version == 3 || !applyMasks
            ? full
            : Masked(full, version, authorityIsQti: true, metadataStart, qtiMetadataLength, oemMetadataLength);
        var oemDigestSource = version == 3 || !applyMasks
            ? (byte[])full.Clone()
            : Masked(full, version, authorityIsQti: false, metadataStart, qtiMetadataLength, oemMetadataLength);
        if (version == 3 && !applyMasks)
        {
            Array.Clear(oemDigestSource, 8, 4);
            Array.Clear(oemDigestSource, 12, 4);
        }

        var cursor = hashOffset + hashLength;
        if (version != 3)
        {
            qti.Sign(qtiDigestSource).CopyTo(image, cursor);
            qti.Certificates.CopyTo(image, cursor + qtiSignatureLength);
            cursor += qtiSignatureLength + qtiCertificateLength;
        }

        oem.Sign(oemDigestSource).CopyTo(image, cursor);
        oem.Certificates.CopyTo(image, cursor + signatureLength);
        return image;
    }

    private static byte[] Masked(byte[] data, uint version, bool authorityIsQti, int metadataStart,
        int qtiMetadataLength, int oemMetadataLength)
    {
        var copy = (byte[])data.Clone();
        if (authorityIsQti)
        {
            Array.Clear(copy, version == 7 ? 32 : 28, 4);
            Array.Clear(copy, 36, 4);
            Array.Clear(copy, metadataStart + qtiMetadataLength, oemMetadataLength);
        }
        else
        {
            Array.Clear(copy, version == 7 ? 24 : 8, 4);
            Array.Clear(copy, version == 7 ? 28 : 12, 4);
            Array.Clear(copy, metadataStart, qtiMetadataLength);
        }

        return copy;
    }

    private static QualcommAnalysisResult Analyze(byte[] image)
    {
        using var stream = new MemoryStream(image);
        return QualcommImageAnalyzer.Analyze(stream, new QualcommAnalysisOptions(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void Analyze_ReferenceMatrixSignedSegments_AcceptsBothAuthorities(uint version)
    {
        using var qtiChain = Chain.Create("CN=Qti Leaf");
        using var oemChain = Chain.Create("CN=Oem Leaf");
        var image = BuildMbn(version, qtiChain, oemChain, applyMasks: true,
            qtiMetadataLength: version == 7 ? 32 : 0, oemMetadataLength: version == 7 ? 32 : 0);

        var result = Analyze(image);

        Assert.Equal(version, result.Mbn!.Version);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.CertificateChainStatus);
        if (version == 3)
        {
            Assert.Equal(QualcommVerificationStatus.NotPresent, result.QualcommSignature.SignatureStatus);
        }
        else
        {
            Assert.Equal(QualcommVerificationStatus.Valid, result.QualcommSignature.SignatureStatus);
            Assert.Equal(QualcommVerificationStatus.Valid, result.QualcommSignature.CertificateChainStatus);
        }
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void Analyze_SignaturesIgnoringMaskFields_RejectSignedAuthorities(uint version)
    {
        using var qtiChain = Chain.Create("CN=Qti Leaf");
        using var oemChain = Chain.Create("CN=Oem Leaf");
        var image = BuildMbn(version, qtiChain, oemChain, applyMasks: false,
            qtiMetadataLength: version == 7 ? 32 : 0, oemMetadataLength: version == 7 ? 32 : 0);

        var result = Analyze(image);

        Assert.NotEqual(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        if (version != 3)
        {
            Assert.NotEqual(QualcommVerificationStatus.Valid, result.QualcommSignature.SignatureStatus);
        }
    }

    [Fact]
    public void Analyze_QtiMetadataRegionPoke_LeavesOemSignatureValidAndBreaksQti()
    {
        using var qtiChain = Chain.Create("CN=Qti Leaf");
        using var oemChain = Chain.Create("CN=Oem Leaf");
        var image = BuildMbn(7, qtiChain, oemChain, applyMasks: true, qtiMetadataLength: 32, oemMetadataLength: 32);
        var tampered = (byte[])image.Clone();
        tampered[64] ^= 0x02;

        var result = Analyze(tampered);

        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.NotEqual(QualcommVerificationStatus.Valid, result.QualcommSignature.SignatureStatus);
    }

    [Fact]
    public void Analyze_UnmaskedFieldPoke_BreaksBothAuthorities()
    {
        using var qtiChain = Chain.Create("CN=Qti Leaf");
        using var oemChain = Chain.Create("CN=Oem Leaf");
        var image = BuildMbn(7, qtiChain, oemChain, applyMasks: true, qtiMetadataLength: 32, oemMetadataLength: 32);
        var tampered = (byte[])image.Clone();
        tampered[20] ^= 0x01;

        var result = Analyze(tampered);

        Assert.NotEqual(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.NotEqual(QualcommVerificationStatus.Valid, result.QualcommSignature.SignatureStatus);
    }

    private sealed class Chain : IDisposable
    {
        private readonly RSA _leafKey;
        private readonly X509Certificate2 _root;
        private readonly X509Certificate2 _leaf;

        private Chain(RSA leafKey, X509Certificate2 root, X509Certificate2 leaf)
        {
            _leafKey = leafKey;
            _root = root;
            _leaf = leaf;
            Certificates = [.. leaf.RawData, .. root.RawData];
        }

        public byte[] Certificates { get; }

        public byte[] Sign(byte[] data) => _leafKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public static Chain Create(string subject)
        {
            using var rootKey = RSA.Create(2048);
            var leafKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Matrix Root", rootKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            var now = DateTimeOffset.UtcNow;
            var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));
            var leafRequest = new CertificateRequest(subject, leafKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var leaf = leafRequest.Create(root, now.AddHours(-12), now.AddDays(6), RandomNumberGenerator.GetBytes(16));
            return new Chain(leafKey, root, leaf);
        }

        public void Dispose()
        {
            _leafKey.Dispose();
            _root.Dispose();
            _leaf.Dispose();
        }
    }
}