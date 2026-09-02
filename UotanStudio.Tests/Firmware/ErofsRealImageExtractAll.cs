using UotanStudio.Firmware.Erofs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsRealImageExtractAll
{
    [Fact]
    public void ExtractAll_FilesMatchSizes()
    {
        var img = @"C:\Users\28319\Desktop\新建文件夹\test\odm.img";
        if (!File.Exists(img)) return;
        var outDir = @"C:\Users\28319\Desktop\新建文件夹\test\extract_all";
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

        using var parser = new ErofsParser(img);
        var files = parser.EnumerateFiles();
        parser.ExtractToDirectory(outDir);

        var failures = new List<string>();
        foreach (var f in files)
        {
            if (f.IsSymlink) continue;
            var p = Path.Combine(outDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) { failures.Add($"missing {f.Path}"); continue; }
            var len = new FileInfo(p).Length;
            if (len != f.Size) failures.Add($"size {f.Path}: {len} != {f.Size}");
        }
        Assert.Empty(failures);
        Assert.Equal(files.Count(f => !f.IsSymlink), Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length);
    }
}
