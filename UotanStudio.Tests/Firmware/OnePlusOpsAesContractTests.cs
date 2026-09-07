using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Contract tests for the OPS AES index-reload API: AES keys and MBox data are
/// clearly distinguished; MBox entries are rejected with NotSupportedException
/// before any AES allocation; illegal indices remain argument-range errors.
/// <para>OPS AES 索引重载 API 契约测试：明确区分 AES 密钥与 MBox 数据；
/// MBox 条目以 NotSupportedException 在 AES 调用前拒绝；非法索引仍为参数越界错误。</para>
/// </summary>
public class OnePlusOpsAesContractTests
{
    // ---- Known-answer test for the AES key (index 0) ----

    [Fact]
    public void AesKey_HasCorrectLength()
    {
        var key = OnePlusKeyTable.GetAesKey(0);
        Assert.Equal(16, key.Length);
    }

    [Fact]
    public void AesKey_KnownAnswer_DecryptsCorrectly()
    {
        // Use the real AES key (index 0) and verify a known-answer round-trip.
        var key = OnePlusKeyTable.GetAesKey(0);
        var plaintext = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
                                     0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        // Decrypt via OpsDecryptor to confirm the API path works.
        using var ms = new MemoryStream(ciphertext);
        var decryptor = new OpsDecryptor(ms);
        var decrypted = decryptor.DecryptAesEcb(ciphertext, key);

        Assert.Equal(plaintext, decrypted);
    }

    // ---- Index 0 (AES key) works through the index overload ----

    [Fact]
    public void DecryptAesEcb_Index0_AesKey_Succeeds()
    {
        var key = OnePlusKeyTable.GetAesKey(0);
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF,
                                     0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF };

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var enc = aes.CreateEncryptor();
        var ct = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);

        using var ms = new MemoryStream(ct);
        var dec = new OpsDecryptor(ms);

        // index 0 = AES key → should succeed
        var result = dec.DecryptAesEcb(ct, 0);
        Assert.Equal(plaintext, result);
    }

    // ---- Indices 1/2/3 (MBox data) are rejected with NotSupportedException ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecryptAesEcb_MBoxIndex_ThrowsNotSupported(int mboxIndex)
    {
        // Prepare some dummy ciphertext (16 bytes).
        var ct = new byte[16];

        using var ms = new MemoryStream(ct);
        var dec = new OpsDecryptor(ms);

        var ex = Assert.Throws<NotSupportedException>(() => dec.DecryptAesEcb(ct, mboxIndex));
        // Error message must mention MBox data and that it cannot be used with AES.
        Assert.Contains("MBox data", ex.Message);
        Assert.Contains("AES", ex.Message);
    }

    // ---- Illegal indices remain argument-range errors ----

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(100)]
    public void DecryptAesEcb_IllegalIndex_ThrowsArgumentOutOfRange(int badIndex)
    {
        var ct = new byte[16];
        using var ms = new MemoryStream(ct);
        var dec = new OpsDecryptor(ms);

        Assert.Throws<ArgumentOutOfRangeException>(() => dec.DecryptAesEcb(ct, badIndex));
    }

    // ---- Direct GetAesKey / GetMBoxData API contracts ----

    [Fact]
    public void GetAesKey_OnlyAcceptsAesKeyIndex()
    {
        // Index 0 is the sole AES key.
        var key = OnePlusKeyTable.GetAesKey(0);
        Assert.Equal(16, key.Length);

        // Indices 1–3 are MBox data, not AES keys.
        for (var i = 1; i <= 3; i++)
        {
            Assert.Throws<NotSupportedException>(() => OnePlusKeyTable.GetAesKey(i));
        }
    }

    [Fact]
    public void GetMBoxData_Returns62ByteBlobs()
    {
        for (var i = 0; i < OnePlusKeyTable.MBoxDataCount; i++)
        {
            var mbox = OnePlusKeyTable.GetMBoxData(i);
            Assert.Equal(62, mbox.Length);
        }
    }

    [Fact]
    public void AesKeyCount_IsOne()
    {
        Assert.Equal(1, OnePlusKeyTable.AesKeyCount);
    }

    [Fact]
    public void MBoxDataCount_IsThree()
    {
        Assert.Equal(3, OnePlusKeyTable.MBoxDataCount);
    }

    // ---- Legacy GetKey still returns all entries unchanged ----

    [Fact]
    public void LegacyGetKey_ReturnsAllEntries()
    {
        // The legacy accessor must continue to return all 4 entries for the
        // SBox stream-cipher path, which iterates over every artefact.
        Assert.Equal(4, OnePlusKeyTable.KeyCount);
        for (var i = 0; i < OnePlusKeyTable.KeyCount; i++)
        {
            var entry = OnePlusKeyTable.GetKey(i);
            Assert.NotEmpty(entry);
        }
    }

    // ---- Table entries are not truncated or rewritten ----

    [Fact]
    public void AesKey_MatchesLegacyGetKeyEntry0()
    {
        var viaAes = OnePlusKeyTable.GetAesKey(0);
        var viaLegacy = OnePlusKeyTable.GetKey(0);
        Assert.Equal(viaLegacy, viaAes);
    }

    [Fact]
    public void MBoxData_MatchesLegacyGetKeyEntries1to3()
    {
        for (var i = 0; i < OnePlusKeyTable.MBoxDataCount; i++)
        {
            var viaMbox = OnePlusKeyTable.GetMBoxData(i);
            var viaLegacy = OnePlusKeyTable.GetKey(OnePlusKeyTable.MBoxDataStartIndex + i);
            Assert.Equal(viaLegacy, viaMbox);
        }
    }
}
