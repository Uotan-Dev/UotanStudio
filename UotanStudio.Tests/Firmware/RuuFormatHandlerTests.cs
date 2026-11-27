using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Htc.Ruu;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// HTC RUU ROM ZIP tests: detection by magic at offset 0/0x100, key derivation from the published
/// tables, a real AES-CBC round trip through <see cref="RuuFormat"/>, and unpacking to a bounded directory.
/// <para>HTC RUU ROM ZIP 测试：偏移 0/0x100 的魔数检测、公布表的密钥派生、经 <see cref="RuuFormat"/>
/// 的真实 AES-CBC 往返，以及解包到受限目录。</para>
/// </summary>
[Collection("Workspace state")]
public class RuuFormatHandlerTests
{
    private const string RuuFormatName = "HTC RUU ROM Package";
    private const string PayloadName = "android-info.txt";
    private const string PayloadText = "modelid: 0P6B10000\nmainver: 6.20.654.3\n";

    [Fact]
    public void Detect_PlainZipByContent_ReturnsRuuHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("firmware.ruu", BuildZipBytes());

        var handler = FormatDetector.Detect(path);

        Assert.NotNull(handler);
        Assert.Equal(RuuFormatName, handler!.FormatName);
    }

    [Fact]
    public void Detect_SignedZipAtSignaturePrefix_ReturnsRuuHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var plain = BuildZipBytes();
        var signed = new byte[RuuFormat.SignaturePrefixLength + plain.Length];
        plain.CopyTo(signed, RuuFormat.SignaturePrefixLength);
        var path = temp.WriteBytes("signed.bin", signed);

        var handler = FormatDetector.Detect(path);

        Assert.NotNull(handler);
        Assert.Equal(RuuFormatName, handler!.FormatName);
    }

    [Fact]
    public void Detect_EncryptedHeaderAndLargeZip_ReturnRuuHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var encrypted = new byte[RuuFormat.HeaderLength];
        "Htc@egi$"u8.CopyTo(encrypted);
        BinaryPrimitives.WriteUInt16BigEndian(encrypted.AsSpan(8, 2), 0xA4);
        encrypted[10] = 0;
        Encoding.ASCII.GetBytes("0.00.0000.0").CopyTo(encrypted, 11);
        var large = new byte[RuuFormat.HeaderLength];
        "LaR@eZip"u8.CopyTo(large);

        var encryptedPath = temp.WriteBytes("enc.bin", encrypted);
        var largePath = temp.WriteBytes("large.bin", large);

        Assert.Equal(RuuFormatName, FormatDetector.Detect(encryptedPath)?.FormatName);
        Assert.Equal(RuuFormatName, FormatDetector.Detect(largePath)?.FormatName);
    }

    [Fact]
    public void Detect_RandomData_DoesNotReturnRuuHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var random = new byte[RuuFormat.HeaderLength * 2];
        RandomNumberGenerator.Fill(random);
        var path = temp.WriteBytes("random.ruu", random);

        var handler = FormatDetector.Detect(path);

        Assert.NotEqual(RuuFormatName, handler?.FormatName);
    }

    [Fact]
    public async Task ReadMetadata_EncryptedHeader_ExposesKeyFields()
    {
        using var temp = new TempDirectory();
        var header = BuildHtcHeader(keymapIndex: 0xA4, chunks: 0, mainver: "1.23.456.7");
        var path = temp.WriteBytes("rom.ruu", header);

        var result = await new RuuFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("EncryptedZip", result.Metadata["ContainerKind"]);
        Assert.Equal("0xA4", result.Metadata["KeymapIndex"]);
        Assert.Equal("0x0", result.Metadata["Chunks"]);
        Assert.Equal("1048576", result.Metadata["ChunkSize"]);
        Assert.Equal("1.23.456.7", result.Metadata["Mainver"]);
    }

    [Fact]
    public async Task Unpack_PlainZip_WritesEntryWithExactContent()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("rom.ruu", BuildZipBytes());
        var output = Path.Combine(temp.Path, "out");

        var result = await new RuuFormatHandler().UnpackAsync(path, output, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var extracted = File.ReadAllText(Path.Combine(output, PayloadName));
        Assert.Equal(PayloadText, extracted);
    }

    [Fact]
    public async Task Unpack_EncryptedZip_RoundTripsThroughPublishedKeys()
    {
        using var temp = new TempDirectory();
        var keys = RuuFormat.DeriveKeys(keydataIndex: 7, keymapIndex: 12);
        var zip = BuildZipBytes();
        var alignedLength = (zip.Length + RuuFormat.KeyLength - 1) / RuuFormat.KeyLength * RuuFormat.KeyLength;
        Array.Resize(ref zip, alignedLength);
        var cipher = (byte[])zip.Clone();
        using (var aes = Aes.Create())
        {
            aes.Key = keys.Key;
            aes.IV = keys.Iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var transform = aes.CreateEncryptor();
            transform.TransformBlock(cipher, 0, cipher.Length, cipher, 0);
        }

        var header = BuildHtcHeader(keymapIndex: 12, chunks: 0, mainver: "9.99.999.9");
        var ruu = new byte[header.Length + cipher.Length];
        header.CopyTo(ruu, 0);
        cipher.CopyTo(ruu, header.Length);
        var path = temp.WriteBytes("encrypted.ruu", ruu);
        var output = Path.Combine(temp.Path, "out");

        var result = await new RuuFormatHandler().UnpackAsync(path, output, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(PayloadText, File.ReadAllText(Path.Combine(output, PayloadName)));
    }

    private static byte[] BuildHtcHeader(int keymapIndex, int chunks, string mainver)
    {
        var header = new byte[RuuFormat.HeaderLength];
        "Htc@egi$"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)keymapIndex);
        header[10] = (byte)chunks;
        Encoding.ASCII.GetBytes(mainver).CopyTo(header, 11);
        for (var i = 11 + Encoding.ASCII.GetByteCount(mainver); i < 31; i++)
        {
            header[i] = 0xFF;
        }

        return header;
    }

    private static byte[] BuildZipBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(PayloadName, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(PayloadText);
        }

        return stream.ToArray();
    }
}