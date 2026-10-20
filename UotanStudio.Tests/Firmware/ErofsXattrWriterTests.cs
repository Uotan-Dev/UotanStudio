using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>xattr 写端回环测试：FileXattrs 注入打包 → Parser 读回名称/值一致。
/// Xattr-writer round-trip: inject FileXattrs, pack, read back and compare names/values.</summary>
public class ErofsXattrWriterTests
{
    [Fact]
    public void FileXattrs_Roundtrip_ReadBackByNameAndValue()
    {
        using var temp = new TempDirectory();
        temp.Write("target.txt", "payload");
        temp.Write("plain.txt", "no xattr here");

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = true,
            FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>>
            {
                ["target.txt"] =
                [
                    new ErofsXattrEntry { Name = "security.selinux", Value = "u:object_r:system_file:s0"u8.ToArray() },
                    new ErofsXattrEntry { Name = "user.comment", Value = "hello"u8.ToArray() },
                ],
            },
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var entries = parser.EnumerateFiles();
        var target = Assert.Single(entries, e => e.Path == "target.txt");
        Assert.NotNull(target.Xattrs);
        Assert.Equal(2, target.Xattrs!.Count);
        var selinux = Assert.Single(target.Xattrs, x => x.Name == "security.selinux");
        Assert.Equal("u:object_r:system_file:s0", System.Text.Encoding.UTF8.GetString(selinux.Value));
        var comment = Assert.Single(target.Xattrs, x => x.Name == "user.comment");
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(comment.Value));

        var expectedPayload = File.ReadAllBytes(Path.Combine(temp.Path, "target.txt"));
        Assert.Equal(expectedPayload, parser.ReadFileBytes("target.txt"));
        var plain = Assert.Single(entries, e => e.Path == "plain.txt");
        Assert.Null(plain.Xattrs);
    }
}