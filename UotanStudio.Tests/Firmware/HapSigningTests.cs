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
/// Verifies the HAP signing block assembly, content digest, profile parsing and end-to-end signing against the
/// calibrated OpenHarmony scheme, plus an environment-gated differential against the official <c>hap-sign-tool</c>.
/// <para>验证 HAP 签名块组装、内容摘要、Profile 解析与端到端签名是否符合标定的 OpenHarmony 方案，
/// 并在与官方 <c>hap-sign-tool</c> 可用时做环境变量门控的差分互验。</para>
/// </summary>
public sealed class HapSigningTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // ── signing block assembly ─────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_Roundtrip_PreservesEntriesInOrder()
    {
        var blocks = new[]
        {
            new HapBlockEntry(HapSignatureConstants.BlockIdProperty, [1, 2, 3, 4]),
            new HapBlockEntry(HapSignatureConstants.BlockIdProfile, new byte[2972]),
            new HapBlockEntry(HapSignatureConstants.BlockIdSignature, [0x30, 0x82]),
        };

        var block = HapSigningBlock.Build(blocks, compatibleSdkVersion: 9);

        Assert.Equal(HapSignatureConstants.MagicV3, block[^20..^4].ToArray());
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(block[^4..]));
        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(block[^28..^20]);
        Assert.Equal(block.Length, total);

        var parsed = HapSigningBlock.Parse(block);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(HapSignatureConstants.BlockIdProperty, parsed[0].TypeId);
        Assert.Equal([1, 2, 3, 4], parsed[0].Value);
        Assert.Equal(HapSignatureConstants.BlockIdProfile, parsed[1].TypeId);
        Assert.Equal(2972, parsed[1].Value.Length);
        Assert.Equal(HapSignatureConstants.BlockIdSignature, parsed[2].TypeId);
        Assert.Equal([0x30, 0x82], parsed[2].Value);

        // Value offsets are measured from the block start, matching the official signer.
        Assert.Equal(HapSigningBlock.HeaderSize * 3u, BinaryPrimitives.ReadUInt32LittleEndian(block[8..12]));
    }

    [Fact]
    public void Build_SelectsSchemeByCompatibleVersion()
    {
        var blocks = new[] { new HapBlockEntry(HapSignatureConstants.BlockIdSignature, [1]) };

        var v2 = HapSigningBlock.Build(blocks, compatibleSdkVersion: 7);
        Assert.Equal(HapSignatureConstants.MagicV2, v2[^20..^4].ToArray());
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(v2[^4..]));

        var v3 = HapSigningBlock.Build(blocks, compatibleSdkVersion: 8);
        Assert.Equal(HapSignatureConstants.MagicV3, v3[^20..^4].ToArray());
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(v3[^4..]));
    }

    [Fact]
    public void Find_LocatesBlockImmediatelyBeforeCentralDirectory()
    {
        var entriesBytes = BuildContainerBytes([("module.json", "12345678"u8.ToArray(), true)]);
        var block = HapSigningBlock.Build([new HapBlockEntry(HapSignatureConstants.BlockIdProfile, [9, 9])], 9);
        var directory = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(directory, 0x02014b50);
        var container = new MemoryStream();
        container.Write(entriesBytes);
        container.Write(block);
        container.Write(directory);
        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(12), (uint)directory.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(16), (uint)(entriesBytes.Length + block.Length));
        container.Write(eocd);

        using var stream = new MemoryStream(container.ToArray());
        var sections = ApkZipSections.Locate(stream);
        var found = HapSigningBlock.Find(stream, sections.CentralDirectoryOffset);
        Assert.NotNull(found);
        Assert.Equal(entriesBytes.Length, found.Value.Start);
        Assert.Equal(block.Length, found.Value.Length);

        var parsed = HapSigningBlock.Parse(ReadBlock(stream, found.Value));
        Assert.Equal(HapSignatureConstants.BlockIdProfile, parsed[0].TypeId);
        Assert.Equal([9, 9], parsed[0].Value);
    }

    // ── digest message ─────────────────────────────────────────────────────

    [Fact]
    public void BuildDigestMessage_MatchesCalibratedLayout()
    {
        Span<byte> digest = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
        var message = HapContentDigestComputer.BuildDigestMessage(HapDigestAlgorithm.EcdsaSha256, digest);

        Assert.Equal(28, message.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(message));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8)));
        Assert.Equal(0x201u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(12)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(16)));
        Assert.Equal(digest.ToArray(), message[20..]);
    }

    [Fact]
    public void DigestComputer_MatchesReferenceComposition()
    {
        // The calibrated composition: sha256(0x5a | u32_LE(count) | chunk digests | optional values) where each
        // chunk digest is sha256(0xa5 | u32_LE(len) | chunk) and each section finalizes its trailing chunk.
        var first = new byte[3000];
        var second = new byte[500];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);

        var computer = HapContentDigestComputer.Create([first.Length, 0, second.Length, 22], HapDigestAlgorithm.EcdsaSha256);
        computer.UpdateSection(first);
        computer.UpdateSection([]);
        computer.UpdateSection(second);
        computer.UpdateSection(new byte[22]);
        Span<byte> property = [0xAA];
        Span<byte> profile = [0xBB, 0xCC];
        computer.UpdateOptionalBlock(property);
        computer.UpdateOptionalBlock(profile);
        var actual = computer.Finalize();

        var expectedInput = new List<byte> { 0x5a };
        expectedInput.AddRange(BitConverter.GetBytes(3u));
        expectedInput.AddRange(Sha256Of(0xa5, first));
        expectedInput.AddRange(Sha256Of(0xa5, second));
        expectedInput.AddRange(Sha256Of(0xa5, new byte[22]));
        expectedInput.AddRange(property.ToArray());
        expectedInput.AddRange(profile.ToArray());
        var expected = SHA256.HashData([.. expectedInput]);
        Assert.True(expected.AsSpan().SequenceEqual(actual));
    }

    private static byte[] Sha256Of(byte prefix, byte[] chunk)
    {
        var input = new byte[5 + chunk.Length];
        input[0] = prefix;
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(1), (uint)chunk.Length);
        chunk.CopyTo(input, 5);
        return SHA256.HashData(input);
    }

    // ── end-to-end signing ─────────────────────────────────────────────────

    [Fact]
    public void Sign_MinimalHap_RepacksAlignsAndSigns()
    {
        var profile = CreateSignedProfile("com.uotan.test", "dev-1", ["udid-A"]);
        using var material = CreateMaterial(profile);

        var input = Path.Combine(_directory.Path, "unsigned.hap");
        File.WriteAllBytes(input, BuildContainerBytes([
            ("module.json", "{\"app\":{\"bundleName\":\"com.uotan.test\"}}"u8.ToArray(), false),
        ]));
        var output = Path.Combine(_directory.Path, "signed.hap");

        var result = HapSigner.Sign(input, output, material, new HapSigningOptions
        {
            CompatibleSdkVersion = 9,
            Algorithm = HapDigestAlgorithm.EcdsaSha256,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3u, result.SchemeVersion);
        Assert.Equal([HapSignatureConstants.BlockIdProfile, HapSignatureConstants.BlockIdSignature], result.BlockIds);

        var signed = File.ReadAllBytes(output);
        var (blockStart, blockLength, directoryOffset) = ParseSignedLayout(signed);
        Assert.Equal(HapSignatureConstants.BlockIdProfile, BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(blockStart)));

        // The first entry's data is page aligned and its header is aligned with it, matching the official policy.
        using (var stream = new MemoryStream(signed))
        {
            var sections = ApkZipSections.Locate(stream);
            var entries = HapZipArchive.Read(stream, sections.CentralDirectoryOffset, sections.CentralDirectorySize);
            var entry = entries[0];
            var dataStart = entry.LocalOffset + HapZipEntry.LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length;
            Assert.Equal(0, (int)(dataStart % HapSignatureConstants.AlignmentPage));
        }

        // Recomputing the calibrated digest over the signed layout and comparing it with the CMS attached content
        // proves the end-to-end round trip.
        VerifyDigestAgainstCms(signed, blockStart, blockLength, directoryOffset);

        // Re-signing replaces the block and reproduces the same digest message.
        var reSigned = Path.Combine(_directory.Path, "resigned.hap");
        HapSigner.Sign(output, reSigned, material, new HapSigningOptions
        {
            CompatibleSdkVersion = 9,
            Algorithm = HapDigestAlgorithm.EcdsaSha256,
        }, TestContext.Current.CancellationToken);
        var firstMessage = ExtractDigestMessage(File.ReadAllBytes(output));
        var secondMessage = ExtractDigestMessage(File.ReadAllBytes(reSigned));
        Assert.Equal(firstMessage, secondMessage);
    }

    [Fact]
    public void Profile_RoundtripsThroughSignedCms()
    {
        var profileBytes = CreateSignedProfile("com.uotan.roundtrip", "team-9", ["UDID-1", "UDID-2"]);
        var profile = HapProfile.Parse(profileBytes);

        Assert.Equal("debug", profile.Type);
        Assert.Equal("com.uotan.roundtrip", profile.BundleName);
        Assert.Equal("team-9", profile.DeveloperId);
        Assert.True(profile.AuthorizesDevice("udid-2"));
        Assert.False(profile.AuthorizesDevice("udid-3"));
        Assert.Contains("BEGIN CERTIFICATE", profile.DevelopmentCertificatePem, StringComparison.Ordinal);
        Assert.True(profile.Validity.NotAfter > profile.Validity.NotBefore);
    }

    [Fact]
    public void Material_RejectsMismatchedLeaf()
    {
        var profile = CreateSignedProfile("com.uotan.mismatch", "dev", []);
        var (keystore, _) = CreateKeystore("CN=Hap Test Leaf", "debugKey");
        var unrelated = CreateSelfSigned("CN=Unrelated", out var _);
        var unrelatedRoot = CreateSelfSigned("CN=Unrelated Root", out var _);

        var mismatchedChain = Encoding.ASCII.GetBytes(ExportPem(unrelated) + ExportPem(unrelatedRoot));

        Assert.Throws<InvalidDataException>(() => HapSignerMaterial.Load(
            keystore, "store-pw", "debugKey", mismatchedChain, profile));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static byte[] ReadBlock(Stream stream, (long Start, int Length) block)
    {
        var original = stream.Position;
        stream.Seek(block.Start, SeekOrigin.Begin);
        var buffer = new byte[block.Length];
        stream.ReadExactly(buffer);
        stream.Seek(original, SeekOrigin.Begin);
        return buffer;
    }

    private static (int BlockStart, int BlockLength, int DirectoryOffset) ParseSignedLayout(byte[] signed)
    {
        var eocd = signed.AsSpan().LastIndexOf("PK\x05\x06"u8);
        Assert.True(eocd >= 0, $"EOCD signature not found in {signed.Length} bytes.");
        var directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(signed.AsSpan(eocd + 16));
        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(signed.AsSpan(directoryOffset - 28, 8));
        Assert.True(total >= HapSigningBlock.HeaderSize + HapSigningBlock.TailSize,
            $"Block total {total} is below the minimum (cd@{directoryOffset}, file {signed.Length}).");
        Assert.True(total <= directoryOffset,
            $"Block total {total} escapes the container (cd@{directoryOffset}).");
        return (directoryOffset - (int)total, (int)total, directoryOffset);
    }

    private static void VerifyDigestAgainstCms(byte[] signed, int blockStart, int blockLength, int directoryOffset)
    {
        var block = signed[blockStart..(blockStart + blockLength)];
        var entries = HapSigningBlock.Parse(block);
        var profileValue = entries.Single(static entry => entry.TypeId == HapSignatureConstants.BlockIdProfile).Value;
        var signatureValue = entries.Single(static entry => entry.TypeId == HapSignatureConstants.BlockIdSignature).Value;
        var propertyEntry = entries.FirstOrDefault(static entry => entry.TypeId == HapSignatureConstants.BlockIdProperty);
        var propertyValue = propertyEntry.TypeId == HapSignatureConstants.BlockIdProperty ? propertyEntry.Value : [];

        var eocd = signed.AsSpan().LastIndexOf("PK\x05\x06"u8);
        Span<byte> digestEocd = stackalloc byte[22];
        signed.AsSpan(eocd, 22).CopyTo(digestEocd);
        BinaryPrimitives.WriteUInt32LittleEndian(digestEocd.Slice(16), (uint)blockStart);

        var computer = HapContentDigestComputer.Create(
            [blockStart, 0, eocd - directoryOffset, 22],
            HapDigestAlgorithm.EcdsaSha256);
        computer.UpdateSection(signed.AsSpan(0, blockStart));
        computer.UpdateSection([]);
        computer.UpdateSection(signed.AsSpan(directoryOffset, eocd - directoryOffset));
        computer.UpdateSection(digestEocd);
        if (propertyValue.Length > 0)
        {
            computer.UpdateOptionalBlock(propertyValue);
        }

        computer.UpdateOptionalBlock(profileValue);
        var digest = computer.Finalize();

        // Independent composition straight from the calibrated formula, to separate a digest-computer defect from
        // a signing-feed defect.
        var manual = ManualDigest(signed, blockStart, blockLength, directoryOffset, eocd, propertyValue, profileValue);
        Assert.True(manual.AsSpan().SequenceEqual(digest),
            $"digest computer {Convert.ToHexStringLower(digest)} != manual {Convert.ToHexStringLower(manual)}");

        var message = HapContentDigestComputer.BuildDigestMessage(HapDigestAlgorithm.EcdsaSha256, digest);
        HapCmsSigner.Verify(signatureValue, message);
    }

    private static byte[] ManualDigest(byte[] signed, int blockStart, int blockLength, int directoryOffset, int eocd,
        byte[] propertyValue, byte[] profileValue)
    {
        var chunk = (byte[] section) =>
        {
            var prefix = new byte[5];
            prefix[0] = 0xa5;
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1), (uint)section.Length);
            var hashInput = new byte[prefix.Length + section.Length];
            prefix.CopyTo(hashInput, 0);
            section.CopyTo(hashInput, prefix.Length);
            return SHA256.HashData(hashInput);
        };
        var input = new List<byte> { 0x5a };
        input.AddRange(BitConverter.GetBytes(3u));
        input.AddRange(chunk([.. signed[..blockStart]]));
        input.AddRange(chunk([.. signed[directoryOffset..eocd]]));
        input.AddRange(chunk([.. digestEocdFor(signed, blockStart)]));
        input.AddRange(propertyValue);
        input.AddRange(profileValue);
        return SHA256.HashData([.. input]);

        static byte[] digestEocdFor(byte[] signed, int blockStart)
        {
            var at = signed.AsSpan().LastIndexOf("PK\x05\x06"u8);
            var bytes = new byte[22];
            signed.AsSpan(at, 22).CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)blockStart);
            return bytes;
        }
    }

    private static byte[] ExtractDigestMessage(byte[] signed)
    {
        var (_, _, directoryOffset) = ParseSignedLayout(signed);
        var (_, blockLength, _) = ParseSignedLayout(signed);
        var block = signed.AsSpan(directoryOffset - blockLength, blockLength).ToArray();
        var entries = HapSigningBlock.Parse(block);
        var signature = entries.Single(static entry => entry.TypeId == HapSignatureConstants.BlockIdSignature).Value;
        var cms = new SignedCms();
        cms.Decode(signature);
        return cms.ContentInfo.Content;
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
        // Mirror the ZIP CRC-32 the entries declare; the signing path never recomputes it.
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
        // The chain repeats the leaf certificate plus a root so the material loader sees the usual two-certificate
        // chain; the leaf must stay first because it is the signing certificate.
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
        var content = new ContentInfo(Encoding.UTF8.GetBytes(json));
        var signed = new SignedCms(content, detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, profileCert)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        signed.ComputeSignature(signer, silent: true);
        return signed.Encode();
    }
}