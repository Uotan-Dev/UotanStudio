using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed partial class DeliveryCompletionTests
{
    // Header-only fixture exercises packaging/validation, never executable updater behavior.
    private string WriteUpdaterFixture(string? path = null)
    {
        path ??= Path.Combine(_root, "update-binary");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = new byte[64];
        "\u007fELF"u8.CopyTo(header);
        header[4] = 2; header[5] = 1; header[6] = 1; header[16] = 2; header[18] = 183;
        File.WriteAllBytes(path, [.. header, .. Encoding.ASCII.GetBytes("block_image_update block_image_verify package_extract_file file_getprop updater-script")]);
        return path;
    }

    private DeliveryPlan DatPlan(bool compressed = false)
    {
        var transfer = Path.Combine(_root, "system.transfer.list");
        File.WriteAllText(transfer, "4\n1\n0\n0\nnew 2,0,1\n");
        var data = Path.Combine(_root, compressed ? "system.new.dat.BR" : "system.new.dat");
        if (compressed)
        {
            using var file = File.Create(data);
            using var encoder = new BrotliStream(file, CompressionLevel.Optimal);
            encoder.Write(new byte[4096]);
        }
        else File.WriteAllBytes(data, new byte[4096]);
        return new()
        {
            Recovery = true,
            Txt = false,
            DeviceCodename = "test_device",
            Information = "Test author",
            OutputDirectory = Path.Combine(_root, "output"),
            Package = new() { RecoveryUpdaterPath = WriteUpdaterFixture() },
            Files = [DatFile(transfer), DatFile(data)]
        };
    }

    private static DeliveryFile DatFile(string path) => new()
    {
        FileName = Path.GetFileName(path),
        SourcePath = path,
        Partition = "system",
        SourceLength = new FileInfo(path).Length
    };

    [Fact]
    public void DeliverySourceEnumerationKeepsTransferListAndEmptyPatch()
    {
        var plan = DatPlan(true);
        File.WriteAllBytes(Path.Combine(_root, "system.patch.dat"), []);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Not an image");
        var names = UotanStudio.Views.Controls.PackImageSelectionView.EnumerateImages(_root, Token)
            .Select(info => Path.GetFileName(info.FilePath)).ToArray();
        Assert.Equal(new[] { "system.new.dat.BR", "system.patch.dat", "system.transfer.list" }, names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatPackageAddsEmptyPatchAndHashesActualMembers(bool compressed)
    {
        var plan = DatPlan(compressed);
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "recovery.zip"));
        Assert.Equal(0, zip.GetEntry("images/system.patch.dat")!.Length);
        Assert.NotNull(zip.GetEntry(compressed ? "images/system.new.dat.br" : "images/system.new.dat"));
        using var reader = new StreamReader(zip.GetEntry("checksums.sha256")!.Open());
        foreach (var line in (await reader.ReadToEndAsync(Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var entry = zip.GetEntry(line[66..])!.Open();
            Assert.Equal(line[..64], Convert.ToHexString(await SHA256.HashDataAsync(entry, Token)).ToLowerInvariant());
        }
        using var edify = new StreamReader(zip.GetEntry(DeliveryRecoveryUpdater.ScriptPath)!.Open());
        var text = await edify.ReadToEndAsync(Token);
        Assert.True(text.IndexOf("block_image_verify(", StringComparison.Ordinal) < text.IndexOf("block_image_update(", StringComparison.Ordinal));
        Assert.Contains("ui_print(\"Device: test_device\")", text);
        Assert.Contains("ui_print(\"Test author\")", text);
    }

    [Fact]
    public async Task DatPackageExpandsBrotliPatchAndKeepsEditedEdify()
    {
        var plan = DatPlan();
        var path = Path.Combine(_root, "system.patch.dat.br");
        var bytes = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
        using (var output = File.Create(path))
        using (var encoder = new BrotliStream(output, CompressionLevel.Optimal)) encoder.Write(bytes);
        plan = plan with { Files = [.. plan.Files, DatFile(path)] };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        scripts["updater-script"] += "ui_print(\"Custom author text\");\n";
        plan = plan with { Scripts = scripts, ScriptFingerprint = DeliveryScriptGenerator.Fingerprint(plan) };
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        Assert.False(File.Exists(Path.Combine(plan.OutputDirectory, "updater-script")));
        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "recovery.zip"));
        Assert.Null(zip.GetEntry("images/system.patch.dat.br"));
        using var content = zip.GetEntry("images/system.patch.dat")!.Open();
        using var actual = new MemoryStream();
        await content.CopyToAsync(actual, Token);
        Assert.Equal(bytes, actual.ToArray());
        using var edify = new StreamReader(zip.GetEntry(DeliveryRecoveryUpdater.ScriptPath)!.Open());
        Assert.Equal(scripts["updater-script"], await edify.ReadToEndAsync(Token));
    }

    [Fact]
    public void NativeUpdaterDiscoveryRejectsMissingAndShellInterpreters()
    {
        var plan = DatPlan() with { Package = new() };
        Assert.Throws<InvalidDataException>(() => DeliveryRecoveryUpdater.Resolve(plan));
        var path = WriteUpdaterFixture(Path.Combine(_root, "META-INF", "com", "google", "android", "update-binary"));
        Assert.Equal(path, DeliveryRecoveryUpdater.Resolve(plan));
        File.WriteAllText(path, "#!/sbin/sh\n" + new string('#', 64));
        Assert.Throws<InvalidDataException>(() => DeliveryRecoveryUpdater.Resolve(plan));
        // Preview remains available without a native updater; publishing requires it.
        Assert.Contains("block_image_update", DeliveryScriptGenerator.Generate(plan)["updater-script"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorruptBrotliPatchNeverPublishesOutput(bool truncated)
    {
        var plan = DatPlan();
        var path = Path.Combine(_root, "system.patch.dat.br");
        using (var output = File.Create(path))
        using (var encoder = new BrotliStream(output, CompressionLevel.Optimal)) encoder.Write(new byte[65536]);
        var data = File.ReadAllBytes(path);
        File.WriteAllBytes(path, truncated ? data[..^1] : [.. data, 1]);
        plan = plan with { Files = [.. plan.Files, DatFile(path)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token));
        Assert.False(Directory.Exists(plan.OutputDirectory));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".uotan-delivery-*"));
    }

    [Fact]
    public async Task MissingUpdaterDoesNotPublishAnything()
    {
        var plan = DatPlan() with { Package = new() };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token));
        Assert.False(Directory.Exists(plan.OutputDirectory));
    }

    [Theory]
    [InlineData("system.new.dat", "system")]
    [InlineData("vendor.patch.dat", "system")]
    [InlineData("system.patch.dat", "vendor")]
    public void DatRejectsDuplicateDataOrMismatchedCompanions(string name, string partition)
    {
        var plan = DatPlan(true);
        plan = plan with { Files = [.. plan.Files, new DeliveryFile { FileName = name, Partition = partition }] };
        Assert.Throws<InvalidDataException>(() => DeliveryScriptGenerator.Generate(plan));
    }

    [Fact]
    public void MixedImgDatPlanHasCompleteRecoveryAndOnlyImgFastboot()
    {
        var plan = DatPlan() with { Windows = true, Linux = true, MacOS = true, Txt = true };
        plan = plan with { Files = [.. plan.Files, Image("boot")] };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        foreach (var name in new[] { "FlashWindows.bat", "FlashLinux.sh", "FlashMacOS.sh", "flash.txt" })
        {
            Assert.Contains("boot.img", scripts[name]);
            Assert.DoesNotContain("system.new.dat", scripts[name]);
        }
        Assert.Contains("package_extract_file(\"images/boot.img\", \"/dev/block/bootdevice/by-name/boot\" + getprop(\"ro.boot.slot_suffix\"))", scripts["updater-script"]);
    }

    [Fact]
    public void DatRowsSharePartitionNamesWithoutBecomingAlternatives()
    {
        var plan = DatPlan();
        var model = new DeliveryViewModel();
        model.Reset(plan);
        foreach (var row in model.Output) { row.PartitionName = "vendor"; row.IsDefault = true; }
        Assert.All(model.Output, row => { Assert.Equal("vendor", row.PartitionName); Assert.True(row.IsDefault); });
        DeliveryValidation.Validate(plan with { Files = model.Output.Select(i => i.Snapshot()).ToList() }, false);
    }

    [Fact]
    public void EveryExecutableFormatIncludesLocalizedDeviceAndAuthor()
    {
        var plan = DatPlan() with { Windows = true, Linux = true, MacOS = true, Information = "作者\r\n100% & \"quotes\" ! ^ | < > ( )\nO'Brien $() `text` \\path" };
        plan = plan with { Files = [.. plan.Files, Image("boot")] };
        var scripts = DeliveryScriptGenerator.Generate(plan, key => key switch
        {
            "Device" => "机型代号：{0}",
            "AuthorInformation" => "作者信息：",
            _ => key
        });
        foreach (var name in new[] { "FlashWindows.bat", "FlashLinux.sh", "FlashMacOS.sh", "updater-script" })
        {
            Assert.Contains("机型代号：test_device", scripts[name]);
            Assert.Contains("作者信息：", scripts[name]);
            Assert.Contains("作者", scripts[name]);
        }
        Assert.Contains("100%% ^& ^\"quotes^\" ! ^^ ^| ^< ^> ^( ^)", scripts["FlashWindows.bat"]);
        Assert.Contains("O'Brien", scripts["updater-script"]);
        Assert.Contains("\\\"quotes\\\"", scripts["updater-script"]);
    }

    [Fact]
    public async Task BatchBannerPrintsLiteralSpecialCharactersWithoutExecutingThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new[] { "作者", "100% & \"quotes\" ! ^ | < > ( )", "\"& echo INJECTED > injected.txt &\"", "%PATH%", "", "O'Brien $() `text` \\path" };
        var script = Path.Combine(_root, "banner.bat");
        await File.WriteAllTextAsync(script, "@echo off\r\nsetlocal DisableDelayedExpansion\r\nchcp 65001 >nul\r\n"
            + string.Join("\r\n", lines.Select(DeliveryScriptGenerator.BatchEcho)) + "\r\n", new UTF8Encoding(false), Token);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", script },
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await error);
        Assert.Equal(string.Join("\n", lines) + "\n", (await output).ReplaceLineEndings("\n"));
        Assert.False(File.Exists(Path.Combine(_root, "injected.txt")));
    }

    [Fact]
    public async Task OrganizedSuperExpandsSparseCompanionsForRecovery()
    {
        var boot = Image("boot");
        var sparse = Path.Combine(_root, "sparse-boot.img");
        UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion.SparseImageConverter.ConvertRawToSparse(boot.SourcePath, sparse);
        var plan = new DeliveryPlan
        {
            PackSuper = true,
            ContainerConfirmed = true,
            Recovery = true,
            Txt = false,
            Container = [Image("system")],
            Super = new() { DeviceSize = 8 * 1024 * 1024, Sparse = true },
            Files = [new() { Generated = true, Partition = "super", FileName = "super.zip" },
                boot with { SourcePath = sparse, SourceLength = new FileInfo(sparse).Length }],
            OutputDirectory = Path.Combine(_root, "super-output")
        };
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "super.zip"));
        using var input = zip.GetEntry("boot.img")!.Open();
        using var bytes = new MemoryStream();
        await input.CopyToAsync(bytes, Token);
        Assert.Equal(File.ReadAllBytes(boot.SourcePath), bytes.ToArray());
        using var super = zip.GetEntry("super.img")!.Open();
        var header = new byte[4]; await super.ReadExactlyAsync(header, Token);
        Assert.NotEqual(new byte[] { 0x3a, 0xff, 0x26, 0xed }, header);
    }
}