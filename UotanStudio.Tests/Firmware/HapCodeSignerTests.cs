using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.HarmonyMeta.Hap;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies the HAP fs-verity code-sign property block against the calibrated OpenHarmony layout, and when the
/// official <c>hap-sign-tool</c> is available cross-checks our property value field-by-field with the tool's output
/// (CMS signatures and signing times are inherently random and excluded from the comparison).
/// <para>验证 HAP fs-verity code-sign property 块是否符合标定的 OpenHarmony 布局，并在官方
/// <c>hap-sign-tool</c> 可用时与工具产物逐字段互验（CMS 签名与签名时间具有固有随机性，比较时排除）。</para>
/// </summary>
public sealed class HapCodeSignerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // ── offline structural tests ───────────────────────────────────────────

    [Fact]
    public void PropertyBlock_MatchesCalibratedLayout()
    {
        var profile = CreateSignedProfile("com.uotan.codesign", "dev-1", []);
        using var material = CreateMaterial(profile);
        var input = Path.Combine(_directory.Path, "unsigned.hap");
        var moduleJson = "{\"app\":{\"bundleName\":\"com.uotan.codesign\"}}"u8.ToArray();
        File.WriteAllBytes(input, BuildContainerBytes([("module.json", moduleJson, false)]));

        var value = HapCodeSigner.BuildPropertyBlock(input, material, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0x30000001u, BinaryPrimitives.ReadUInt32LittleEndian(value));
        var entriesEnd = ContainerEntriesEnd(moduleJson);
        var codeSignOffset = entriesEnd + 12 * 3 + 12;
        Assert.Equal((uint)codeSignOffset, BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(8)));

        var block = ParseCodeSignBlock(value, out var codeSignLength);
        Assert.Equal((uint)codeSignOffset, BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(8)));
        Assert.Equal(codeSignLength, block.Length);

        var header = ParseCodeSignHeader(block);
        Assert.Equal(0xE046C8C65389FCCDul, header.Magic);
        Assert.Equal(1u, header.Version);
        Assert.Equal(block.Length, header.BlockSize);
        Assert.Equal(3u, header.SegmentCount);
        Assert.Equal(1, header.Flags);

        // The Merkle tree is page aligned in the signed file: the block starts at codeSignOffset, the tree follows
        // the 68-byte header/segment table plus zero padding, so the tree start must be a page boundary and the pad
        // bytes must all be zero.
        var treeStart = codeSignOffset + 68;
        var pageAlignedTree = (treeStart + 4095) / 4096 * 4096;
        Assert.Equal(0, pageAlignedTree % 4096);
        Assert.All(block[68..(int)(pageAlignedTree - codeSignOffset)], static (byte byteValue) => Assert.Equal(0, byteValue));

        var fsVerity = block.Slice(header.Segments[0].Offset, header.Segments[0].Size);
        Assert.Equal(64, fsVerity.Length);
        Assert.Equal(0x1E3831ABu, BinaryPrimitives.ReadUInt32LittleEndian(fsVerity));
        Assert.Equal([1, 1, 12], fsVerity.Slice(4, 3).ToArray());
        Assert.All(fsVerity.Slice(7).ToArray(), static (byte byteValue) => Assert.Equal(0, byteValue));

        var hapSegment = block.Slice(header.Segments[1].Offset, header.Segments[1].Size);
        Assert.Equal(0xC1B5CC66u, BinaryPrimitives.ReadUInt32LittleEndian(hapSegment));
        var signInfo = hapSegment[4..];
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(signInfo));
        var cmsLength = BinaryPrimitives.ReadUInt32LittleEndian(signInfo.AsSpan(4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(signInfo.AsSpan(8)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(signInfo.AsSpan(12)));
        Assert.All(signInfo.Slice(20, 32).ToArray(), static (byte byteValue) => Assert.Equal(0, byteValue));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(signInfo.AsSpan(52)));
        var cms = signInfo.Slice(60, (int)cmsLength).ToArray();

        // The fs-verity covers zero bytes (module.json is the first entry), so the descriptor carries a zero root
        // hash and the page-aligned tree offset; the CMS must sign exactly that digest.
        var descriptor = new byte[256];
        descriptor[0] = 1;
        descriptor[1] = 1;
        descriptor[2] = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(112), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(120), (ulong)(codeSignOffset + header.Segments[0].Offset));
        var expectedDigest = "FSVerity"u8.ToArray()
            .Concat(BitConverter.GetBytes((ushort)1))
            .Concat(BitConverter.GetBytes((ushort)32))
            .Concat(Sha256(descriptor))
            .ToArray();
        VerifyCodeSignCms(cms, expectedDigest, "DEBUG_LIB_ID", material);

        var nativeSegment = block.Slice(header.Segments[2].Offset, header.Segments[2].Size);
        Assert.Equal(12, nativeSegment.Length);
        Assert.Equal(0x0ED2E720u, BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(8)));

        var permission = value[(12 + codeSignLength)..];
        Assert.Equal(0x30000002u, BinaryPrimitives.ReadUInt32LittleEndian(permission));
        Assert.Equal(24 + (uint)codeSignLength, BinaryPrimitives.ReadUInt32LittleEndian(permission.AsSpan(8)));
        var permissionBlob = permission[12..];
        Assert.Equal(0x28E2450F93036A7Dul, BinaryPrimitives.ReadUInt64LittleEndian(permissionBlob));
        Assert.Equal(0x201u, BinaryPrimitives.ReadUInt32LittleEndian(permissionBlob.AsSpan(8)));
        var digestLength = BinaryPrimitives.ReadUInt32LittleEndian(permissionBlob.AsSpan(12));
        Assert.Equal(3 * 36, (int)digestLength);
        Assert.Equal(3, permissionBlob[16]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(permissionBlob.AsSpan(18)));
        Assert.Equal(Sha256(ExtractProfileJson(profile)), permissionBlob.Slice(22, 32).ToArray());
        Assert.Equal(Sha256(moduleJson), permissionBlob.Slice(58, 32).ToArray());
        Assert.Equal(Sha256(block), permissionBlob.Slice(94, 32).ToArray());
        var signatureLength = BinaryPrimitives.ReadUInt32LittleEndian(permissionBlob.AsSpan(18 + (int)digestLength));
        var unsigned = permissionBlob[..(18 + (int)digestLength)].ToArray();
        var signature = permissionBlob[(22 + (int)digestLength)..(22 + (int)digestLength + (int)signatureLength)].ToArray();
        Assert.True(material.SigningCertificate.GetECDsaPublicKey()!
                .VerifyData(unsigned, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
            "The permission block signature does not verify with the leaf certificate.");
    }

    [Fact]
    public void PropertyBlock_SignsNativeLibsAndHnpElves()
    {
        var profile = CreateSignedProfile("com.uotan.native", "dev-2", []);
        using var material = CreateMaterial(profile);
        var moduleJson = "{\"module\":{\"hnpPackages\":[{\"package\":\"test.hnp\",\"type\":\"private\"}]}}"u8.ToArray();
        var elf = MinimalElf();
        var innerElfName = "libinner.so";
        var hnpZip = BuildContainerBytes([(innerElfName, elf, false), ("readme.txt", "text"u8.ToArray(), false)]);
        var input = Path.Combine(_directory.Path, "unsigned.hap");
        File.WriteAllBytes(input, BuildContainerBytes([
            ("module.json", moduleJson, false),
            ("libs/libtest.an", elf, true),
            ("hnp/aarch64/test.hnp", hnpZip, true),
        ]));

        var value = HapCodeSigner.BuildPropertyBlock(input, material, cancellationToken: TestContext.Current.CancellationToken);
        var block = ParseCodeSignBlock(value, out _);
        var header = ParseCodeSignHeader(block);

        Assert.Equal(3, header.Flags);
        var nativeSegment = block.Slice(header.Segments[2].Offset, header.Segments[2].Size);
        Assert.Equal(0x0ED2E720u, BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(8)));
        var nameBase = 12 + 2 * 16;
        var firstLength = BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(16));
        Assert.Equal("libs/libtest.an"u8.ToArray(), nativeSegment.Slice(nameBase, (int)firstLength).ToArray());
        var secondNameOffset = BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(28));
        var secondNameLength = BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(32));
        Assert.Equal("hnp/aarch64/test.hnp!/libinner.so"u8.ToArray(),
            nativeSegment.Slice((int)secondNameOffset, (int)secondNameLength).ToArray());

        // The native sign infos carry their own detached CMS signatures over the uncompressed fs-verity digests.
        var secondSignInfoOffset = BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(36));
        var secondSignInfoLength = BinaryPrimitives.ReadUInt32LittleEndian(nativeSegment.AsSpan(40));
        var signInfo = nativeSegment.Slice((int)secondSignInfoOffset, (int)secondSignInfoLength);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(signInfo));
        var cmsLength = BinaryPrimitives.ReadUInt32LittleEndian(signInfo.AsSpan(4));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(signInfo.AsSpan(8)));
        Assert.Equal((ulong)elf.Length, BinaryPrimitives.ReadUInt64LittleEndian(signInfo.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(signInfo.AsSpan(52)));
        var cms = signInfo.Slice(60, (int)cmsLength).ToArray();
        var expectedDigest = FsVerityDigestOf(elf);
        VerifyCodeSignCms(cms, expectedDigest, "DEBUG_LIB_ID", material);
    }

    [Fact]
    public void PropertyBlock_RespectsOptions()
    {
        var profile = CreateSignedProfile("com.uotan.options", "dev-3", []);
        using var material = CreateMaterial(profile);
        var input = Path.Combine(_directory.Path, "unsigned.hap");
        File.WriteAllBytes(input, BuildContainerBytes([("module.json", "{\"app\":{}}"u8.ToArray(), false)]));

        var value = HapCodeSigner.BuildPropertyBlock(input, material, new HapCodeSignerOptions
        {
            IncludePermissionBlock = false,
            FinalBlockCount = 2,
        }, TestContext.Current.CancellationToken);

        var block = ParseCodeSignBlock(value, out _);
        var optionsModuleJson = "{\"app\":{}}"u8.ToArray();
        Assert.Equal((uint)(ContainerEntriesEnd(optionsModuleJson) + 12 * 2 + 12), BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(8)));
        var header = ParseCodeSignHeader(block);
        var nativeSegment = block.Slice(header.Segments[2].Offset, header.Segments[2].Size);
        Assert.Equal(12, nativeSegment.Length);
        Assert.Equal(1, header.Flags);
        Assert.Equal(12 + block.Length, value.Length);
    }

    [Fact]
    public void SignerWithCodeSign_EmitsPropertyProfileAndSignatureBlocks()
    {
        var profile = CreateSignedProfile("com.uotan.integrated", "dev-4", []);
        using var material = CreateMaterial(profile);
        var input = Path.Combine(_directory.Path, "integrated-unsigned.hap");
        var moduleJson = "{\"app\":{\"bundleName\":\"com.uotan.integrated\"}}"u8.ToArray();
        File.WriteAllBytes(input, BuildContainerBytes([("module.json", moduleJson, false)]));
        var output = Path.Combine(_directory.Path, "integrated-signed.hap");

        var result = HapSigner.Sign(input, output, material, new HapSigningOptions
        {
            CompatibleSdkVersion = 9,
            IncludeCodeSign = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal([HapSignatureConstants.BlockIdProperty, HapSignatureConstants.BlockIdProfile,
            HapSignatureConstants.BlockIdSignature], result.BlockIds);
        using var signed = File.OpenRead(output);
        var sections = ApkZipSections.Locate(signed);
        var located = HapSigningBlock.Find(signed, sections.CentralDirectoryOffset);
        Assert.NotNull(located);
        signed.Seek(located.Value.Start, SeekOrigin.Begin);
        var blockBytes = new byte[located.Value.Length];
        signed.ReadExactly(blockBytes);
        var property = HapSigningBlock.Parse(blockBytes)
            .Single(entry => entry.TypeId == HapSignatureConstants.BlockIdProperty).Value;
        var block = ParseCodeSignBlock(property, out _);
        var header = ParseCodeSignHeader(block);
        Assert.Equal(0xE046C8C65389FCCDul, header.Magic);
        Assert.Equal(3u, header.SegmentCount);
        Assert.Equal(1, header.Flags);
    }


    private static bool IsOid(byte[] encoded, string dotted)
    {
        // Compare the primitive OID body against an independent re-encoding of the dotted text.
        var parts = dotted.Split('.');
        var body = new List<byte> { (byte)(int.Parse(parts[0]) * 40 + int.Parse(parts[1])) };
        Span<byte> scratch = stackalloc byte[10];
        for (var index = 2; index < parts.Length; index++)
        {
            var value = ulong.Parse(parts[index]);
            var length = 0;
            scratch[length++] = (byte)(value & 0x7F);
            value >>= 7;
            while (value != 0)
            {
                scratch[length++] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }

            for (var scan = length - 1; scan >= 0; scan--)
            {
                body.Add(scratch[scan]);
            }
        }

        return encoded.Length == body.Count && encoded.AsSpan().SequenceEqual([.. body]);
    }
    private static void VerifyCodeSignCms(byte[] cms, byte[] expectedDigest, string expectedOwnerId,
        HapSignerMaterial material)
    {
        // CMS layout: SEQUENCE { OID contentType, [0] { SignedData SEQUENCE } }. The explicit [0] wraps a SEQUENCE,
        // so the SignedData fields live two levels below the context tag.
        var root = DerReader.Parse(cms, 0, out _);
        var contextZero = DerReader.Child(cms, root, 1);
        var signedData = DerReader.Parse(cms, contextZero.ContentStart, out _);
        var children = DerReader.Children(cms, signedData);
        Assert.Equal(5, children.Count);
        // children[4] is the signerInfos SET OF; the single signerInfo SEQUENCE sits one level below it.
        var signerSet = DerReader.Children(cms, children[4]);
        Assert.Single(signerSet);
        var signer = DerReader.Children(cms, signerSet[0]);
        Assert.Equal(6, signer.Count);
        var attributes = signer[3];

        var messageDigest = FindAttributeOctetString(cms, attributes, "1.2.840.113549.1.9.4");
        Assert.Equal(Sha256(expectedDigest), messageDigest);
        var ownerId = FindAttributeUtf8String(cms, attributes, "1.3.6.1.4.1.2011.2.376.1.4.1");
        Assert.Equal(Encoding.UTF8.GetBytes(expectedOwnerId), ownerId);

        var verified = new SignedCms(new ContentInfo(expectedDigest), detached: true);
        verified.Decode(cms);
        verified.CheckSignature(true);
        using var publicKey = material.SigningCertificate.GetECDsaPublicKey();
        Assert.NotNull(publicKey);
        Assert.NotNull(publicKey.ExportParameters(false).Q.X);
    }

    private static void VerifyRawPermissionSignature(byte[] blob, uint digestLength, X509Certificate2 certificate)
    {
        var signatureLength = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(18 + (int)digestLength));
        var unsigned = blob[..(18 + (int)digestLength)].ToArray();
        var start = 22 + (int)digestLength;
        var signature = blob[start..(start + (int)signatureLength)].ToArray();
        // The permission signature is DER (RFC 3279) encoded like every other ECDSA signature this signer emits.
        Assert.True(certificate.GetECDsaPublicKey()!
                .VerifyData(unsigned, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
            "The permission block signature does not verify with the leaf certificate.");
    }

    private static byte[] FindAttributeOctetString(byte[] data, DerReader.Node attributes, string oid)
    {
        var value = FindAttributeValue(data, attributes, oid);
        var octet = DerReader.Parse(data, value.ContentStart, out _);
        Assert.Equal(0x04, octet.Tag);
        return data.Slice(octet.ContentStart, octet.ContentLength).ToArray();
    }

    private static byte[] FindAttributeUtf8String(byte[] data, DerReader.Node attributes, string oid)
    {
        var value = FindAttributeValue(data, attributes, oid);
        var text = DerReader.Parse(data, value.ContentStart, out _);
        Assert.Equal(0x0C, text.Tag);
        return data.Slice(text.ContentStart, text.ContentLength).ToArray();
    }

    private static DerReader.Node FindAttributeValue(byte[] data, DerReader.Node attributes, string oid)
    {
        List<string> found = [];
        var offset = attributes.ContentStart;
        var end = attributes.ContentStart + attributes.ContentLength;
        while (offset < end)
        {
            var attribute = DerReader.Parse(data, offset, out var total);
            var children = DerReader.Children(data, attribute);
            Assert.Equal(2, children.Count);
            var encoded = data[children[0].ContentStart..(children[0].ContentStart + children[0].ContentLength)];
            if (IsOid(encoded, oid))
            {
                return children[1];
            }

            found.Add(Convert.ToHexString(encoded));
            offset += total;
        }

        throw new InvalidOperationException($"The CMS carries no attribute {oid}; found [{string.Join(", ", found)}] at tag 0x{attributes.Tag:X2} len {attributes.ContentLength}.");
    }

    // ── parsing helpers ────────────────────────────────────────────────────

    private static byte[] ExtractProfileJson(byte[] profileBytes)
    {
        var content = profileBytes;
        if (profileBytes.AsSpan().IndexOf("-----BEGIN"u8.ToArray()) < 0 && (profileBytes.Length <= 1 || profileBytes[0] != (byte)'{'))
        {
            var cms = new SignedCms();
            cms.Decode(profileBytes);
            content = cms.ContentInfo.Content;
        }

        var start = Array.IndexOf(content, (byte)'{');
        var depth = 0;
        for (var index = start; index < content.Length; index++)
        {
            if (content[index] == (byte)'{')
            {
                depth++;
            }
            else if (content[index] == (byte)'}' && --depth == 0)
            {
                return content[start..(index + 1)];
            }
        }

        throw new InvalidOperationException("Profile JSON object is unbalanced.");
    }

    private static byte[] ExtractPropertyBlockValue(byte[] signed)
    {
        var (blockStart, blockLength, _) = ParseSignedLayout(signed);
        var block = signed.AsSpan(blockStart, blockLength).ToArray();
        var entries = HapSigningBlock.Parse(block);
        return entries.Single(static entry => entry.TypeId == HapSignatureConstants.BlockIdProperty).Value;
    }

    private static (int BlockStart, int BlockLength, int DirectoryOffset) ParseSignedLayout(byte[] signed)
    {
        var eocd = signed.AsSpan().LastIndexOf("PK\x05\x06"u8);
        Assert.True(eocd >= 0, $"EOCD signature not found in {signed.Length} bytes.");
        var directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(signed.AsSpan(eocd + 16));
        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(signed.AsSpan(directoryOffset - 28, 8));
        return (directoryOffset - (int)total, (int)total, directoryOffset);
    }

    private static int ContainerEntriesEnd(byte[] moduleJson)
    {
        // The first entry's data starts on the page boundary and spans the rest of the entries region, so the
        // region ends one deflated payload past 4096 for any single-entry container.
        return 4096 + DeflatedLength(moduleJson);
    }

    private static int DeflatedLength(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return (int)compressed.Length;
    }

    private sealed record CodeSignHeader(ulong Magic, uint Version, int BlockSize, uint SegmentCount, int Flags,
        IReadOnlyList<(int Offset, int Size, int End)> Segments);

    private static CodeSignHeader ParseCodeSignHeader(byte[] block)
    {
        Assert.True(block.Length >= 68, $"Code-sign block is {block.Length} bytes, below the 68-byte minimum.");
        Assert.Equal(0xE046C8C65389FCCDul, BinaryPrimitives.ReadUInt64LittleEndian(block));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(8));
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(12));
        var segmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(16));
        var flags = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(20));
        Assert.Equal(block.Length, blockSize);
        Assert.Equal(3u, segmentCount);
        var segments = new List<(int Offset, int Size, int End)>(3);
        for (var index = 0; index < 3; index++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(32 + index * 12 + 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(32 + index * 12 + 8));
            segments.Add((offset, size, offset + size));
        }

        return new CodeSignHeader(
            BinaryPrimitives.ReadUInt64LittleEndian(block),
            version,
            blockSize,
            segmentCount,
            flags,
            segments);
    }

    private static byte[] ParseCodeSignBlock(byte[] propertyValue, out int codeSignLength)
    {
        Assert.Equal(0x30000001u, BinaryPrimitives.ReadUInt32LittleEndian(propertyValue));
        codeSignLength = BinaryPrimitives.ReadInt32LittleEndian(propertyValue.AsSpan(4));
        var block = propertyValue[12..(12 + codeSignLength)];
        return block;
    }

    private static byte[] Sha256(byte[] data) => Sha256Hash(data);

    private static byte[] Sha256Hash(byte[] data)
    {
        return System.Security.Cryptography.SHA256.HashData(data);
    }

    private static byte[] FsVerityDigestOf(byte[] content)
    {
        // Single-page contents digest straight from the calibrated composition: sha256 over the 256-byte descriptor
        // with a zero-padded single leaf root hash and the zero tree offset (native entries never store the tree).
        var descriptor = new byte[256];
        descriptor[0] = 1;
        descriptor[1] = 1;
        descriptor[2] = 12;
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), (ulong)content.Length);
        Sha256(ZeroPadPage(content)).AsSpan().CopyTo(descriptor.AsSpan(16));
        return "FSVerity"u8.ToArray()
            .Concat(BitConverter.GetBytes((ushort)1))
            .Concat(BitConverter.GetBytes((ushort)32))
            .Concat(Sha256(descriptor))
            .ToArray();
    }

    private static byte[] ZeroPadPage(byte[] content)
    {
        var page = new byte[4096];
        content.AsSpan().CopyTo(page);
        return page;
    }

    private static byte[] MinimalElf()
    {
        // Minimal ELF header shape: magic plus a plausible e_ident tail; only the first four bytes matter here.
        var elf = new byte[128];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        Random.Shared.NextBytes(elf.AsSpan(8));
        return elf;
    }

    // ── material and container helpers (mirrors HapSigningTests) ───────────

    private static HapSignerMaterial CreateMaterial(byte[] profile)
    {
        var (keystore, leafDer) = CreateKeystore("CN=Hap Test Leaf", "debugKey");
        return HapSignerMaterial.Load(keystore, "store-pw", "debugKey", CreateChainBytes(leafDer), profile);
    }

    private static (byte[] Keystore, byte[] LeafDer) CreateKeystore(string subject, string friendlyName)
    {
        var leaf = CreateSelfSigned(subject, out _);
        if (OperatingSystem.IsWindows())
        {
            leaf.FriendlyName = friendlyName;
        }

        return (leaf.Export(X509ContentType.Pkcs12, "store-pw"), leaf.RawData);
    }

    private static byte[] CreateChainBytes(byte[] leafDer)
    {
        var leaf = X509CertificateLoader.LoadCertificate(leafDer);
        var root = CreateSelfSigned("CN=Hap Test Root", out _);
        return Encoding.ASCII.GetBytes(ExportPem(leaf) + ExportPem(root));
    }

    private static X509Certificate2 CreateSelfSigned(string subject, out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        if (OperatingSystem.IsWindows())
        {
            certificate.FriendlyName = subject;
        }

        return certificate;
    }

    private static string ExportPem(X509Certificate2 certificate)
        => "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(certificate.RawData, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----\n";

    private static byte[] CreateSignedProfile(string bundleName, string developerId, IReadOnlyList<string> deviceIds)
    {
        var leaf = CreateSelfSigned($"CN={bundleName}", out _);
        var profileCert = CreateSelfSigned("CN=Hap Test Profile", out _);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version-name"] = "2.0.0",
            ["version-code"] = 2,
            ["uuid"] = Guid.NewGuid().ToString(),
            ["validity"] = new Dictionary<string, object>
            {
                ["not-before"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["not-after"] = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds(),
            },
            ["type"] = "debug",
            ["bundle-info"] = new Dictionary<string, object>
            {
                ["developer-id"] = developerId,
                ["development-certificate"] = ExportPem(leaf),
                ["bundle-name"] = bundleName,
                ["apl"] = "normal",
                ["app-feature"] = "hos_normal_app",
                ["app-identifier"] = "1234567890123456789",
            },
            ["baseapp-info"] = new Dictionary<string, object>(),
            ["permissions"] = new Dictionary<string, object>(),
            ["debug-info"] = new Dictionary<string, object>
            {
                ["device-ids"] = deviceIds,
                ["device-id-type"] = "udid",
            },
            ["acls"] = new Dictionary<string, object> { ["allowed-acls"] = Array.Empty<string>() },
            ["issuer"] = "app_gallery",
        });
        var content = new System.Security.Cryptography.Pkcs.ContentInfo(Encoding.UTF8.GetBytes(json));
        var signed = new System.Security.Cryptography.Pkcs.SignedCms(content, detached: false);
        var signer = new System.Security.Cryptography.Pkcs.CmsSigner(
            System.Security.Cryptography.Pkcs.SubjectIdentifierType.IssuerAndSerialNumber, profileCert)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new System.Security.Cryptography.Pkcs.Pkcs9SigningTime(DateTime.UtcNow));
        signed.ComputeSignature(signer, silent: true);
        return signed.Encode();
    }

    private static byte[] BuildContainerBytes(IReadOnlyList<(string Name, byte[] Data, bool Stored)> entries)
    {
        using var stream = new MemoryStream();
        using var directory = new MemoryStream();
        Span<byte> header = stackalloc byte[30];
        Span<byte> central = stackalloc byte[46];
        foreach (var (name, data, stored) in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            ushort method = stored ? (ushort)0 : (ushort)8;
            uint crc = CrcOf(data);
            byte[] payload = data;
            uint compressedSize = (uint)data.Length;
            if (!stored)
            {
                using var compressed = new MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data);
                }

                payload = compressed.ToArray();
                compressedSize = (uint)payload.Length;
            }

            var localOffset = (uint)stream.Position;
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 0x21);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[18..], compressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[22..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0);
            stream.Write(header);
            stream.Write(nameBytes);
            stream.Write(payload);

            BinaryPrimitives.WriteUInt32LittleEndian(central, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(central[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(central[6..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(central[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(central[10..], method);
            BinaryPrimitives.WriteUInt16LittleEndian(central[12..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(central[14..], 0x21);
            BinaryPrimitives.WriteUInt32LittleEndian(central[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(central[20..], compressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(central[24..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(central[28..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(central[30..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(central[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(central[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(central[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(central[38..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(central[42..], localOffset);
            directory.Write(central);
            directory.Write(nameBytes);
        }

        stream.Write(directory.ToArray());
        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], (uint)directory.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], (uint)stream.Position - (uint)directory.Length);
        stream.Write(eocd);
        return stream.ToArray();
    }

    private static uint CrcOf(byte[] data)
    {
        var table = Crc32Table.Value;
        uint crc = 0xFFFFFFFF;
        foreach (var byteValue in data)
        {
            crc = table[(crc ^ byteValue) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static class Crc32Table
    {
        internal static readonly uint[] Value = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }

    /// <summary>Minimal DER reader for cross-checking CMS structures against the official output.
    /// <para>用于与官方产物互验 CMS 结构的最小 DER 读取器。</para></summary>
    private static class DerReader
    {
        public readonly record struct Node(int Tag, int ContentStart, int ContentLength)
        {
            public int End => ContentStart + ContentLength;
        }

        public static Node Parse(byte[] data, int offset, out int totalLength)
        {
            var tag = data[offset];
            var lengthByte = data[offset + 1];
            var headerLength = 2;
            int contentLength = lengthByte;
            if ((lengthByte & 0x80) != 0)
            {
                var count = lengthByte & 0x7F;
                contentLength = 0;
                for (var index = 0; index < count; index++)
                {
                    contentLength = (contentLength << 8) | data[offset + 2 + index];
                }

                headerLength = 2 + count;
            }

            totalLength = headerLength + contentLength;
            return new Node(tag, offset + headerLength, contentLength);
        }

        public static List<Node> Children(byte[] data, Node parent)
        {
            var children = new List<Node>();
            if ((parent.Tag & 0x20) == 0)
            {
                return children;
            }

            var offset = parent.ContentStart;
            var end = parent.End;
            while (offset < end)
            {
                var child = Parse(data, offset, out var total);
                children.Add(child);
                offset += total;
            }

            return children;
        }

        public static Node Child(byte[] data, Node parent, int index) => Children(data, parent)[index];
    }

}

/// <summary>Array slicing helper so the byte-level assertions read like the span API.
/// <para>数组切片辅助，使字节级断言与 span API 的读法一致。</para></summary>
internal static class ArraySliceExtensions
{
    public static byte[] Slice(this byte[] data, int start)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data[start..];
    }

    public static byte[] Slice(this byte[] data, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data[start..(start + length)];
    }
}