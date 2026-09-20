using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Writer;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// WSL2 Alpine cross-validation of the managed DAT unpack wiring against an independent Python
/// reference decoder (<c>TestInfrastructure/dat2img.py</c>). A pattern image is written to an
/// uncompressed full-OTA DAT pair with the managed <see cref="DatWriter"/>, then reconstructed two
/// ways: through <see cref="DatFormatHandler.UnpackAsync"/> on Windows and through the pinned
/// <c>python3 dat2img.py</c> in Alpine. Byte identity across all three is a semantic (G2) check of
/// the transfer-list grammar and new.dat block layout against a separate implementation, not merely
/// a writer/reader round trip.
/// <para>WSL2 Alpine 交叉验证：以托管 <see cref="DatWriter"/> 写出未压缩全量 OTA DAT 镜像对，然后分别用
/// Windows 上的 <see cref="DatFormatHandler.UnpackAsync"/> 与 Alpine 中固定的 <c>python3 dat2img.py</c>
/// 重建镜像。三者逐字节一致构成对 transfer list 语法与 new.dat 块布局的语义级（G2）跨实现校验，而不仅是
/// 写入/读取往返。</para>
/// </summary>
/// <remarks>
/// The classic decoder handles only the full-OTA command subset, so Brotli
/// (<c>.new.dat.br</c>) cross-validation is out of scope here (no brotli module in the guest);
/// that path is covered by the managed round-trip tests.
/// <para>该解码器只处理全量命令子集，因此不在此交叉验证 Brotli（<c>.new.dat.br</c>）路径（guest 无
/// brotli 模块）；该路径由托管往返测试覆盖。</para>
/// </remarks>
public class DatWslCrossValidationTests
{
    [Fact]
    public async Task DatFormatHandlerUnpack_MatchesIndependentPythonDat2Img()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("python3")) { Assert.Skip("python3 is unavailable in the WSL distribution"); }
        Assert.True(File.Exists(WslTestHelpers.Dat2ImgPath), $"Missing bundled reference decoder: {WslTestHelpers.Dat2ImgPath}");

        using var temp = new TempDirectory();
        var image = BuildMixedBlockImage(totalBlocks: 48, dataFactor: 11);
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "system.new.dat");
        var listPath = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var managedDir = Path.Combine(temp.Path, "managed");
        var managedResult = await new DatFormatHandler()
            .UnpackAsync(listPath, managedDir, new UnpackOptions(), TestContext.Current.CancellationToken);
        Assert.True(managedResult.Success, managedResult.Summary);
        var managedImage = File.ReadAllBytes(Path.Combine(managedDir, "system.img"));
        Assert.Equal(image, managedImage);

        var referencePath = Path.Combine(temp.Path, "reference.img");
        var (output, exitCode) = WslTestHelpers.RunDat2Img(datPath, listPath, referencePath);
        Assert.True(exitCode == 0, $"dat2img.py failed (exit={exitCode}):\n{output}");
        Assert.True(File.Exists(referencePath), "dat2img.py produced no output image.");

        var referenceImage = File.ReadAllBytes(referencePath);
        Assert.Equal(image, referenceImage);
        Assert.Equal(managedImage, referenceImage);
    }

    /// <summary>
    /// Cross-validates the managed <see cref="DatFormatHandler.PackAsync"/> output: repack a pattern
    /// image through the handler (raw source, explicit <c>.transfer.list</c> path), then decode the
    /// produced pair with the pinned Python <c>dat2img.py</c> in Alpine. Byte identity with the source
    /// proves the transfer-list grammar and block layout are accepted by an independent decoder, not
    /// just by the paired managed reader.
    /// <para>交叉验证托管 <see cref="DatFormatHandler.PackAsync"/>：用显式 <c>.transfer.list</c> 路径把
    /// 样例镜像重打包，再用 Alpine 固定的 <c>dat2img.py</c> 解码该镜像对。逐字节一致证明 transfer list
    /// 语法与块布局能被独立解码器接受，而不仅仅是配对的托管读取器。</para>
    /// </summary>
    [Fact]
    public async Task PackAsync_TransferListOutput_DecodesWithIndependentPythonDat2Img()
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("python3")) { Assert.Skip("python3 is unavailable in the WSL distribution"); }
        Assert.True(File.Exists(WslTestHelpers.Dat2ImgPath), $"Missing bundled reference decoder: {WslTestHelpers.Dat2ImgPath}");

        using var temp = new TempDirectory();
        var image = BuildMixedBlockImage(totalBlocks: 40, dataFactor: 13);
        var imagePath = temp.WriteBytes("input.img", image);
        var outputDir = Path.Combine(temp.Path, "pair");
        Directory.CreateDirectory(outputDir);

        var packResult = await new DatFormatHandler()
            .PackAsync(imagePath, outputDir, TestContext.Current.CancellationToken);
        Assert.True(packResult.Success, packResult.Summary);
        var listPath = Path.Combine(outputDir, "input.transfer.list");
        var datPath = Path.Combine(outputDir, "input.new.dat");
        Assert.True(File.Exists(listPath));
        Assert.True(File.Exists(datPath));

        var referencePath = Path.Combine(temp.Path, "reference.img");
        var (output, exitCode) = WslTestHelpers.RunDat2Img(datPath, listPath, referencePath);
        Assert.True(exitCode == 0, $"dat2img.py failed on managed PackAsync output (exit={exitCode}):\n{output}");
        Assert.Equal(image, File.ReadAllBytes(referencePath));
    }

    /// <summary>Builds a block-aligned image whose third blocks are zero, ending on a data block.
    /// <para>构造每第三块为零、以数据块结尾的块对齐镜像。</para></summary>
    private static byte[] BuildMixedBlockImage(int totalBlocks, int dataFactor)
    {
        var data = new byte[totalBlocks * DatWriter.BlockSize];
        for (var block = 0; block < totalBlocks; block++)
        {
            if (block % 3 == 0)
            {
                continue; // 全零块，交由新.dat 之外的空隙保持为零。 Zero block, left as untouched space.
            }

            var offset = block * DatWriter.BlockSize;
            for (var i = 0; i < DatWriter.BlockSize; i++)
            {
                data[offset + i] = (byte)((i + block * 7 + dataFactor) & 0xFF);
            }
        }

        return data;
    }
}