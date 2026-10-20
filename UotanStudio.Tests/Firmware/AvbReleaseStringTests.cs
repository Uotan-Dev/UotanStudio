using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Checks that the managed vbmeta header carries the pinned release identity so downstream tools
/// and cross-validation see the same tool version. <para>校验托管 vbmeta 头部固定身份，使下游工具
/// 与交叉验证看到相同的工具版本。</para>
/// </summary>
public sealed class AvbReleaseStringTests
{
    private const int ReleaseStringOffset = 128;
    private const int ReleaseStringSize = 48;

    [Fact]
    public async Task SignVbmeta_EmbedsPinnedReleaseString()
    {
        using var rsa = SigningTestKeys.Create(2048);
        var privateKey = rsa;
        using var temp = new TempDirectory();
        var seed = new byte[256];
        "AVB0"u8.CopyTo(seed);
        var seedPath = temp.WriteBytes("vbmeta.img", seed);
        var outputPath = Path.Combine(temp.Path, "signed-vbmeta.img");

        var result = await AvbSigner.SignVbmetaAsync<object>(
            seedPath, privateKey, outputPath,
            [new AvbKernelCmdlineDescriptor { Flags = 0, KernelCmdline = "androidboot.verifiedbootstate=green" }],
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage ?? "SignVbmeta failed");

        var vbmeta = await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken);
        var raw = Encoding.ASCII.GetString(vbmeta, ReleaseStringOffset, ReleaseStringSize).TrimEnd('\0');
        Assert.Equal("avbtool 1.3.0", raw);
    }

    [Fact]
    public void WriteTo_ReleaseStringAtCapacity_IsPreservedWithTrailingNul()
    {
        var header = new AvbVBMetaImageHeader { Magic = "AVB0", ReleaseString = new string('a', 47) };
        var buffer = new byte[AvbVBMetaImageHeader.Size];
        header.WriteTo(buffer);
        Assert.Equal(new string('a', 47), Encoding.ASCII.GetString(buffer, ReleaseStringOffset, ReleaseStringSize).TrimEnd('\0'));
        Assert.Equal(0, buffer[ReleaseStringOffset + 47]);
    }

    [Fact]
    public void WriteTo_ReleaseStringBeyondCapacity_IsRejectedInsteadOfTruncated()
    {
        var header = new AvbVBMetaImageHeader { Magic = "AVB0", ReleaseString = new string('a', 48) };
        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[AvbVBMetaImageHeader.Size]));
    }
}