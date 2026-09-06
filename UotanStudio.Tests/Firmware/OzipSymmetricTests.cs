using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Oppo;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Symmetric OZip encryption/decryption tests: a test-side AES-128-ECB encryptor builds a
/// valid OZip container (header + 16-byte encrypted prefix per 0x4000-byte plaintext unit,
/// stride 0x4010) using keys from the shipped <see cref="OzipKeyDatabase"/>, then the
/// decryptor must recover the original bytes — including key auto-detection and the
/// seekable on-demand decrypt stream.
/// <para>OZip 对称加解密测试：测试侧以 AES-128-ECB 加密器、使用内置
/// <see cref="OzipKeyDatabase"/> 密钥构造合法 OZip 容器（头部 + 每 0x4000 字节明文单元
/// 带 16 字节加密前缀、步长 0x4010），解密端须还原原始字节——覆盖密钥自动检测与
/// 可定位的按需解密流。</para>
/// </summary>
public class OzipSymmetricTests
{
    /// <summary>Magic prefix that makes the plaintext recognizable to key auto-detection.</summary>
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Builds a valid OZip container exactly per the Mode 1 format: 0x1050-byte header
    /// ("OPPOENCRYPT!" magic + 16-byte ASCII decimal size), then 16-byte AES-ECB encrypted
    /// prefixes interleaved with 0x4000-byte plaintext runs.
    /// <para>严格按 Mode 1 格式构造合法 OZip 容器：0x1050 字节头部（"OPPOENCRYPT!" 魔数 +
    /// 16 字节 ASCII 十进制大小），随后为 16 字节 AES-ECB 加密前缀与 0x4000 字节明文交替。</para>
    /// </summary>
    private static byte[] BuildOzip(byte[] plaintext, byte[] key)
    {
        Assert.Equal(16, key.Length);

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("OPPOENCRYPT!"));
        ms.SetLength(OzipHeader.DataOffset);
        ms.Position = OzipHeader.SizeOffset;
        var sizeField = new byte[OzipHeader.SizeFieldLength];
        var sizeBytes = Encoding.ASCII.GetBytes(plaintext.Length.ToString());
        Assert.True(sizeBytes.Length <= sizeField.Length, "plaintext too large for the ASCII size field");
        Array.Copy(sizeBytes, sizeField, sizeBytes.Length);
        ms.Write(sizeField);
        ms.Position = OzipHeader.DataOffset;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key, null);

        var offset = 0;
        while (offset < plaintext.Length)
        {
            // 每个加密单元：16 字节 AES 前缀 + 0x4000 字节明文（尾部允许部分明文，
            // 且尾部至少 16 字节——与解密端的单元语义一致）。
            // Each unit: a 16-byte AES prefix followed by 0x4000 plaintext bytes (partial
            // tail allowed, at least 16 bytes — matching the decryptor's unit semantics).
            var prefix = plaintext[offset..Math.Min(offset + 16, plaintext.Length)];
            var padded = new byte[16];
            Array.Copy(prefix, padded, prefix.Length);
            Assert.Equal(16, encryptor.TransformBlock(padded, 0, 16, padded, 0));
            ms.Write(padded, 0, 16);
            offset += 16;

            var plainLen = Math.Min(OzipDecryptor.PlaintextBlockSize, plaintext.Length - offset);
            if (plainLen > 0)
            {
                ms.Write(plaintext, offset, plainLen);
                offset += plainLen;
            }
        }

        return ms.ToArray();
    }

    private static byte[] BuildPlaintext(int length)
    {
        var data = new byte[length];
        ZipMagic.CopyTo(data, 0);
        for (var i = 4; i < length; i++)
        {
            data[i] = (byte)(i * 31 + 7);
        }
        return data;
    }

    private static byte[] KeyFromDatabase()
    {
        Assert.True(OzipKeyDatabase.Count > 0, "OzipKeyDatabase must contain at least one key");
        return OzipKeyDatabase.GetAllKeys()[0].KeyBytes;
    }

    [Fact]
    public void DecryptFile_AppliedKey_RecoversPlaintext()
    {
        OppoInitializer.EnsureInitialized();
        var key = KeyFromDatabase();
        // 两个完整单元 + 部分明文尾部，覆盖步长与边界路径。
        // Two full units plus a partial plaintext tail to cover stride and edge paths.
        var plaintext = BuildPlaintext(2 * OzipDecryptor.EncryptionUnitSize + 100);
        var ozip = BuildOzip(plaintext, key);

        using var input = new MemoryStream(ozip, writable: false);
        using var output = new MemoryStream();
        OzipDecryptor.DecryptFile(input, output, key);

        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public void DetectKey_FindsDatabaseKey()
    {
        OppoInitializer.EnsureInitialized();
        var key = KeyFromDatabase();
        var plaintext = BuildPlaintext(64);
        var ozip = BuildOzip(plaintext, key);

        var firstEncrypted = ozip[OzipHeader.DataOffset..(OzipHeader.DataOffset + 16)];
        var detected = OzipDecryptor.DetectKey(firstEncrypted);

        Assert.NotNull(detected);
        Assert.Equal(key, detected!.KeyBytes);
    }

    [Fact]
    public void DecryptFileWithAutoDetection_RecoversPlaintext()
    {
        OppoInitializer.EnsureInitialized();
        var key = KeyFromDatabase();
        var plaintext = BuildPlaintext(OzipDecryptor.EncryptionUnitSize + 100);
        var ozip = BuildOzip(plaintext, key);

        using var input = new MemoryStream(ozip, writable: false);
        using var output = new MemoryStream();
        var detected = OzipDecryptor.DecryptFileWithAutoDetection(input, output);

        Assert.Equal(key, detected.KeyBytes);
        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public void BlockDecryptStream_SeekableOnDemandView_IsConsistent()
    {
        OppoInitializer.EnsureInitialized();
        var key = KeyFromDatabase();
        var plaintext = BuildPlaintext(2 * OzipDecryptor.EncryptionUnitSize);
        var ozip = BuildOzip(plaintext, key);

        using var input = new MemoryStream(ozip, writable: false);
        using var decrypted = OzipDecryptor.OpenDecryptedStream(input, out var detected, leaveOpen: false);
        Assert.Equal(key, detected.KeyBytes);

        // 顺序读取与 seek 后读取须产生一致结果。
        // Sequential and post-seek reads must yield identical bytes.
        var sequential = new byte[plaintext.Length];
        Assert.Equal(plaintext.Length, decrypted.Read(sequential, 0, plaintext.Length));
        Assert.Equal(plaintext, sequential);

        decrypted.Position = OzipDecryptor.PlaintextBlockSize + 5;
        var window = new byte[32];
        Assert.Equal(32, decrypted.Read(window, 0, 32));
        Assert.Equal(plaintext[(int)(OzipDecryptor.PlaintextBlockSize + 5)..((int)(OzipDecryptor.PlaintextBlockSize + 5) + 32)], window);
    }

    [Fact]
    public void Detect_OzipExtension_IsOppoHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        OppoInitializer.EnsureInitialized();
        var root = Path.Combine(Path.GetTempPath(), "uotan-ozip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "firmware.ozip");
        try
        {
            var key = KeyFromDatabase();
            File.WriteAllBytes(path, BuildOzip(BuildPlaintext(4096), key));

            var handler = FormatDetector.Detect(path);
            Assert.Equal("Oppo Firmware (OFP/OPS/OZIP)", handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
