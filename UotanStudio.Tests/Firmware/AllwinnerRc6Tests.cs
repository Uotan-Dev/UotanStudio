using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AllwinnerRc6Tests
{
    [Theory]
    [InlineData("00000000000000000000000000000000", "00000000000000000000000000000000", "8fc3a53656b1f778c129df4e9848a41e")]
    [InlineData("0123456789abcdef0112233445566778", "02132435465768798a9bacbdcedfe0f1", "524e192f4715c6231f51f6367ea43f18")]
    [InlineData("000000000000000000000000000000000000000000000000", "00000000000000000000000000000000", "6cd61bcb190b30384e8a3f168690ae82")]
    [InlineData("0123456789abcdef0112233445566778899aabbccddeeff0", "02132435465768798a9bacbdcedfe0f1", "688329d019e505041e52e92af95291d4")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "00000000000000000000000000000000", "8f5fbd0510d15fa893fa3fda6e857ec2")]
    [InlineData("0123456789abcdef0112233445566778899aabbccddeeff01032547698badcfe", "02132435465768798a9bacbdcedfe0f1", "c8241816f0d7e48920ad16a1674e5d48")]
    public void Cipher_OfficialRc6Vectors_ProduceReferenceCiphertext(string keyHex, string plainHex, string cipherHex)
    {
        var key = Convert.FromHexString(keyHex);
        var block = Convert.FromHexString(plainHex);
        var cipher = Rc6.Create(key);

        cipher.EncryptBlock(block);

        Assert.Equal(Convert.FromHexString(cipherHex), block);

        cipher.DecryptBlock(block);

        Assert.Equal(Convert.FromHexString(plainHex), block);
    }

    [Fact]
    public void Cipher_KeyOutsideSupportedRange_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Rc6.Create(new byte[8]));
        Assert.Throws<ArgumentException>(() => Rc6.Create(new byte[33]));
    }

    [Fact]
    public void Cipher_PartialBlock_IsRejected()
    {
        var cipher = Rc6.Create(new byte[32]);

        Assert.Throws<ArgumentException>(() => cipher.DecryptBlock(new byte[15]));
    }
}