using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// OZIP Mode 1 regression tests for the 16-byte encrypted prefix plus 0x4000-byte plaintext layout.
/// <para>OZIP Mode 1 回归测试，覆盖 16 字节加密前缀与 0x4000 字节明文组成的真实布局。</para>
/// </summary>
public sealed class OzipExtractionTests
{
    private const int EncryptionUnitSize = 0x4010;
    private const int EncryptedPrefixSize = 16;
    private static readonly byte[] TestKey = Convert.FromHexString("D4D2CD61D4AFDCE13B5E01221BD14D20");

    [Fact]
    public void Decrypt_Mode1Units_RoundTripAndSeekExactly()
    {
        var plaintext = CreatePattern(EncryptionUnitSize * 3 + 257);
        Encoding.ASCII.GetBytes("PK\x03\x04").CopyTo(plaintext, 0);
        var encrypted = EncryptMode1(plaintext);

        using var decryptedOutput = new MemoryStream();
        OzipDecryptor.Decrypt(new MemoryStream(encrypted), decryptedOutput, TestKey, encrypted.Length);
        Assert.Equal(plaintext, decryptedOutput.ToArray());

        using var ozip = CreateOzipStream(encrypted);
        using var decryptedView = new OzipBlockDecryptStream(ozip, TestKey, encrypted.Length, leaveOpen: true);
        foreach (var position in new long[]
                 {
                     0,
                     EncryptedPrefixSize - 1,
                     EncryptedPrefixSize,
                     EncryptionUnitSize - 1,
                     EncryptionUnitSize,
                     EncryptionUnitSize + EncryptedPrefixSize - 1,
                     encrypted.Length - 41,
                 })
        {
            decryptedView.Position = position;
            var actual = new byte[(int)Math.Min(73, plaintext.Length - position)];
            decryptedView.ReadExactly(actual);
            Assert.Equal(plaintext.AsSpan((int)position, actual.Length).ToArray(), actual);
        }
    }

    [Fact]
    public void Extract_ImageAfterFirstEncryptionUnit_IsWrittenByteExactly()
    {
        var image = CreatePattern(96 * 1024);
        Encoding.ASCII.GetBytes("ANDROID!").CopyTo(image, 0);
        var zip = CreateInnerZip(image);
        var ozipBytes = CreateOzipBytes(EncryptMode1(zip));
        var root = Path.Combine(Path.GetTempPath(), "uotan-ozip-" + Guid.NewGuid().ToString("N"));
        var inputPath = Path.Combine(root, "firmware.ozip");
        var outputPath = Path.Combine(root, "out");

        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(inputPath, ozipBytes);

            var reader = new OppReader();
            var archive = reader.Parse(inputPath);
            Assert.Contains(archive.Entries, entry => entry.Name == "firmware-update/boot.img");

            var result = reader.Extract(
                inputPath,
                outputPath,
                new OppExtractOptions { Overwrite = true });

            Assert.True(result.ExtractedFileCount >= 3);
            Assert.Equal(image, File.ReadAllBytes(Path.Combine(outputPath, "firmware-update_boot.img")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateInnerZip(byte[] image)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "data_update_patch/conf/engineermode/persistname_common.txt", [0x31, 0x0A]);
            WriteEntry(zip, "padding.bin", CreatePattern(48 * 1024));
            WriteEntry(zip, "firmware-update/boot.img", image);
        }

        var bytes = output.ToArray();
        Assert.True(bytes.Length > EncryptionUnitSize);
        Assert.True(bytes.Length % EncryptionUnitSize is 0 or >= EncryptedPrefixSize);
        return bytes;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] CreatePattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 37 + i / 251);
        }

        return data;
    }

    private static byte[] EncryptMode1(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(TestKey, null);
        using var output = new MemoryStream(plaintext.Length);

        var position = 0;
        var encryptedPrefix = new byte[EncryptedPrefixSize];
        while (position < plaintext.Length)
        {
            if (plaintext.Length - position < EncryptedPrefixSize)
            {
                throw new InvalidOperationException("Test OZIP data ends with a partial encrypted prefix.");
            }

            Assert.Equal(
                EncryptedPrefixSize,
                encryptor.TransformBlock(plaintext, position, EncryptedPrefixSize, encryptedPrefix, 0));
            output.Write(encryptedPrefix);
            position += EncryptedPrefixSize;

            var plaintextLength = Math.Min(0x4000, plaintext.Length - position);
            output.Write(plaintext, position, plaintextLength);
            position += plaintextLength;
        }

        return output.ToArray();
    }

    private static MemoryStream CreateOzipStream(byte[] encryptedData)
        => new(CreateOzipBytes(encryptedData), writable: false);

    private static byte[] CreateOzipBytes(byte[] encryptedData)
    {
        var result = new byte[OzipHeader.DataOffset + encryptedData.Length];
        Encoding.ASCII.GetBytes("OPPOENCRYPT!").CopyTo(result, 0);
        Encoding.ASCII.GetBytes(encryptedData.Length.ToString()).CopyTo(result, 0x10);
        encryptedData.CopyTo(result, OzipHeader.DataOffset);
        return result;
    }
}