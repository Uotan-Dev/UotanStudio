using System.Security.Cryptography;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.OnePlusOps;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class OnePlusOpsUnpackTests
{
    [Theory]
    [InlineData("aes")]
    [InlineData("reversed")]
    [InlineData("sbox")]
    public async Task Unpack_EncryptedEntry_WritesRawAndDecryptedByteExactly(string mode)
    {
        var plaintext = new byte[200 * 1024];
        "ANDROID!"u8.CopyTo(plaintext);
        for (var i = 8; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(i * 37 + i / 251);
        }

        var ciphertext = Encrypt(plaintext, mode);
        var root = Path.Combine(Path.GetTempPath(), "uotan-oneplus-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        var opsPath = Path.Combine(root, "firmware.ops");
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllBytes(Path.Combine(input, "boot.img"), ciphertext);
            File.WriteAllBytes(Path.Combine(input, "raw.bin"), [0x52, 0x49, 0x46, 0x46]);

            var handler = new OnePlusOpsFormatHandler();
            var packed = await handler.PackAsync(input, opsPath, TestContext.Current.CancellationToken);
            Assert.True(packed.Success, packed.Summary);

            var unpacked = await handler.UnpackAsync(opsPath, output, new UnpackOptions { Overwrite = true },
                TestContext.Current.CancellationToken);

            Assert.True(unpacked.Success, unpacked.Summary);
            Assert.Equal(ciphertext, File.ReadAllBytes(Path.Combine(output, "boot.img")));
            Assert.Equal([0x52, 0x49, 0x46, 0x46], File.ReadAllBytes(Path.Combine(output, "raw.bin")));
            Assert.Equal(plaintext, File.ReadAllBytes(Path.Combine(output, "boot.img.decrypted")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Unpack_PlainEntry_WritesNoDecryptedSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-oneplus-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        var opsPath = Path.Combine(root, "firmware.ops");
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllBytes(Path.Combine(input, "raw.bin"), [0x52, 0x49, 0x46, 0x46]);

            var handler = new OnePlusOpsFormatHandler();
            var packed = await handler.PackAsync(input, opsPath, TestContext.Current.CancellationToken);
            Assert.True(packed.Success, packed.Summary);

            var unpacked = await handler.UnpackAsync(opsPath, output, new UnpackOptions { Overwrite = true },
                TestContext.Current.CancellationToken);

            Assert.True(unpacked.Success, unpacked.Summary);
            Assert.Equal([0x52, 0x49, 0x46, 0x46], File.ReadAllBytes(Path.Combine(output, "raw.bin")));
            Assert.False(File.Exists(Path.Combine(output, "raw.bin.decrypted")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] Encrypt(byte[] plaintext, string mode)
    {
        switch (mode)
        {
            case "aes":
                return AesEcb(plaintext, OnePlusKeyTable.GetAesKey(0));
            case "reversed":
                var reversed = (byte[])plaintext.Clone();
                Array.Reverse(reversed);
                return AesEcb(reversed, OnePlusKeyTable.GetAesKey(0));
            default:
                var sbox = OnePlusOpsFormatHandler.SBoxTable();
                var encrypted = new byte[plaintext.Length];
                for (var i = 0; i < plaintext.Length; i++)
                {
                    encrypted[i] = (byte)(plaintext[i] ^ sbox[i % 256]);
                }

                return encrypted;
        }
    }

    private static byte[] AesEcb(byte[] plaintext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }
}