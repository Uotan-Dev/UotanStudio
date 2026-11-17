using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Acceptance of the flash page's Allwinner card-image path: the same "choose file" entry that loads a vendor
/// package must load a card image's GPT partitions into the main grid, and the rows must stay tickable because a
/// partial flash writes only the ticked ones.
/// <para>烧录页全志卡镜像路径的验收：加载厂商包的同一「选择文件」入口必须把卡镜像的 GPT 分区装进主网格，且行
/// 保持可勾选，因为部分刷入只写被勾选的那些。</para>
/// </summary>
public sealed class FlashSunxiCardImageTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uotan-sunxi-card-page-" + Guid.NewGuid().ToString("N"));

    public FlashSunxiCardImageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task LoadPackageAsync_CardImage_LoadsOneTickableRowPerGptPartition()
    {
        var path = WriteCardImage();
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new SilentUnifiedLog());

        await model.LoadPackageAsync(path);
        model.FlushPendingLog();

        Assert.Equal(["boot-resource", "rootfs"],
            model.Partitions.Select(row => row.Name).ToArray());
        Assert.All(model.Partitions, row =>
        {
            Assert.False(row.IsScriptStep);
            Assert.False(row.AcceptsFile);
        });
        Assert.Matches("2 ", model.LogText);
    }

    [Fact]
    public async Task LoadPackageAsync_CardImage_DoesNotFallBackToASingleWholeImageRow()
    {
        var path = WriteCardImage();
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new SilentUnifiedLog());

        await model.LoadPackageAsync(path);
        model.FlushPendingLog();

        Assert.DoesNotContain(model.Partitions, row => row.Name == Path.GetFileNameWithoutExtension(path));
        Assert.DoesNotContain(model.Partitions, row => row.Command == "flash");
    }

    private sealed class SilentUnifiedLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message)
        {
        }

        public void Write(LogLevel level, string source, string format, params object[] args)
        {
        }

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }

    private string WriteCardImage()
    {
        var fat = new byte[512];
        fat[54] = (byte)'F';
        fat[55] = (byte)'A';
        fat[56] = (byte)'T';
        fat[57] = (byte)'1';
        fat[58] = (byte)'6';
        fat[59] = (byte)' ';
        fat[60] = (byte)' ';
        fat[61] = (byte)' ';
        var rootfs = new byte[512];
        "hsqs"u8.CopyTo(rootfs);

        var image = SunxiCardImageBuilder.Build(
            new("boot-resource", 8, fat),
            new("rootfs", 8, rootfs));
        var path = Path.Combine(_root, "card.img");
        File.WriteAllBytes(path, image);
        return path;
    }
}