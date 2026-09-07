using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Models;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.ViewModels;

/// <summary>
/// <para>分区表编辑器测试：构造 LP / Rockchip 样例 → 解析 → 编辑 → 回写 → 重新解析断言 round-trip。</para>
/// Partition-table editor tests: build LP / Rockchip samples, parse, edit, write back, then re-parse
/// and assert the round-trip.
/// </summary>
public sealed class PartitionEditorTests
{
    [Fact]
    public async Task LpSample_ParseEditSave_RoundTrips()
    {
        // 构造 64 MiB 设备的 LP super 镜像：system 32 MiB + vendor 8 MiB（均为线性单 extent）。
        // Build a 64 MiB LP super image: system 32 MiB + vendor 8 MiB (single linear extents each).
        var deviceSize = 64UL * 1024 * 1024;
        const ulong firstLogicalSector = (12288 + 4 * 65536) / 512;
        var builder = MetadataBuilder.New(deviceSize, 64 * 1024, 2);
        builder.AddGroup("default", 0);
        builder.AddGroup("vendor", 0);
        builder.AddPartition("system", "default", 32UL * 1024 * 1024);
        builder.AddPartition("vendor", "vendor", 8UL * 1024 * 1024);

        var path = Path.Combine(Path.GetTempPath(), "uotan-lp-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            MetadataWriter.WriteToImageFile(path, builder.Build());

            // 解析。
            // Parse.
            var vm = new PartitionEditorViewModel();
            await vm.LoadAsync(path);
            try
            {
                Assert.Equal(2, vm.PartitionCount);
                Assert.Equal(2, vm.Partitions.Count);

                Assert.Equal("system", vm.Partitions[0].Name);
                Assert.Equal("default", vm.Partitions[0].Group);
                Assert.Equal("linear", vm.Partitions[0].Type);
                Assert.Equal(32UL * 1024 * 1024 / 512, vm.Partitions[0].SizeSectors);
                // LP linear extents use absolute device sectors after both metadata copies.
                Assert.Equal(firstLogicalSector, vm.Partitions[0].StartSector);

                Assert.Equal("vendor", vm.Partitions[1].Name);
                Assert.Equal("vendor", vm.Partitions[1].Group);
                Assert.Equal(firstLogicalSector + 32UL * 1024 * 1024 / 512, vm.Partitions[1].StartSector);

                // 编辑：system 改名/缩到 16 MiB/类型改 zero；vendor 起始后移。
                // Edit: rename system, shrink it to 16 MiB, switch its type to zero, and move vendor's start.
                vm.Partitions[0].Name = "system_new";
                vm.Partitions[0].Size = PartitionTableFormat.FormatSectors(16UL * 1024 * 1024 / 512);
                vm.Partitions[0].Type = "zero";
                vm.Partitions[1].Start = PartitionTableFormat.FormatSectors(80_000);
                await vm.SaveAsync();
                Assert.Empty(vm.CompileError);
            }
            finally
            {
                vm.Cleanup();
            }

            // 回读并断言 round-trip。
            // Re-read and assert the round-trip.
            var vm2 = new PartitionEditorViewModel();
            await vm2.LoadAsync(path);
            try
            {
                Assert.Empty(vm2.CompileError);
                Assert.Equal(2, vm2.Partitions.Count);

                Assert.Equal("system_new", vm2.Partitions[0].Name);
                Assert.Equal("zero", vm2.Partitions[0].Type);
                Assert.Equal(16UL * 1024 * 1024 / 512, vm2.Partitions[0].SizeSectors);
                Assert.Equal(firstLogicalSector, vm2.Partitions[0].StartSector);

                Assert.Equal("vendor", vm2.Partitions[1].Name);
                Assert.Equal(80_000UL, vm2.Partitions[1].StartSector);
                Assert.Equal(8UL * 1024 * 1024 / 512, vm2.Partitions[1].SizeSectors);
            }
            finally
            {
                vm2.Cleanup();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RockchipSample_ParseEditSave_RoundTrips()
    {
        // 构造带 CMDLINE: mtdparts= 的 parameter 文本（boot 0x2000@0x4000、system 0x100000@0x6000）。
        // Build a parameter text with CMDLINE: mtdparts= (boot 0x2000@0x4000, system 0x100000@0x6000).
        var parameterText =
            "FIRMWARE_VER: 8.1\n" +
            "MACHINE_MODEL: R1\n" +
            "MANUFACTURER: Rockchip\n" +
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(boot),0x00100000@0x00006000(system):gpt\n";
        var path = Path.Combine(Path.GetTempPath(), "uotan-param-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, parameterText);
        try
        {
            var vm = new PartitionEditorViewModel();
            await vm.LoadAsync(path);
            try
            {
                Assert.Equal(2, vm.PartitionCount);
                Assert.Equal("boot", vm.Partitions[0].Name);
                Assert.Equal(0x2000UL, vm.Partitions[0].SizeSectors);
                Assert.Equal(0x4000UL, vm.Partitions[0].StartSector);
                Assert.Equal("mtd", vm.Partitions[0].Type);

                Assert.Equal("system", vm.Partitions[1].Name);
                Assert.Equal(0x100000UL, vm.Partitions[1].SizeSectors);
                Assert.Equal(0x6000UL, vm.Partitions[1].StartSector);

                // 编辑：system 改名/改大小/改起始，boot 不动。
                // Edit: rename system, change its size and start; boot is untouched.
                vm.Partitions[1].Name = "system_x";
                vm.Partitions[1].Size = PartitionTableFormat.FormatSectors(0x80000);
                vm.Partitions[1].Start = PartitionTableFormat.FormatSectors(0x10000);
                await vm.SaveAsync();
                Assert.Empty(vm.CompileError);
            }
            finally
            {
                vm.Cleanup();
            }

            // 回读并断言 round-trip；非 CMDLINE 行须原样保留。
            // Re-read and assert the round-trip; non-CMDLINE lines must be preserved.
            var vm2 = new PartitionEditorViewModel();
            await vm2.LoadAsync(path);
            try
            {
                Assert.Empty(vm2.CompileError);
                Assert.Equal(2, vm2.Partitions.Count);

                Assert.Equal("boot", vm2.Partitions[0].Name);
                Assert.Equal(0x2000UL, vm2.Partitions[0].SizeSectors);
                Assert.Equal(0x4000UL, vm2.Partitions[0].StartSector);

                Assert.Equal("system_x", vm2.Partitions[1].Name);
                Assert.Equal(0x80000UL, vm2.Partitions[1].SizeSectors);
                Assert.Equal(0x10000UL, vm2.Partitions[1].StartSector);
            }
            finally
            {
                vm2.Cleanup();
            }

            var saved = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("FIRMWARE_VER: 8.1", saved);
            Assert.Contains("MANUFACTURER: Rockchip", saved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenMode_ResolvesPartitionByContent()
    {
        // LP super 镜像按内容路由到分区表编辑器（.img 同时是固件扩展名，LP 魔数优先）。
        // An LP super image routes to the partition editor by content (the LP magic wins even though
        // .img is also a firmware extension).
        var lpPath = Path.Combine(Path.GetTempPath(), "uotan-lp-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var builder = MetadataBuilder.New(64UL * 1024 * 1024, 64 * 1024, 2);
            builder.AddGroup("default", 0);
            builder.AddPartition("system", "default", 16UL * 1024 * 1024);
            MetadataWriter.WriteToImageFile(lpPath, builder.Build());

            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(lpPath));
            var header = await File.ReadAllBytesAsync(lpPath, TestContext.Current.CancellationToken);
            Assert.True(FileOpenMode.IsLpMetadataMagic(header));
            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(lpPath, header));
        }
        finally
        {
            File.Delete(lpPath);
        }

        // Rockchip parameter（无扩展名）按文本特征路由到分区表编辑器。
        // An extension-less Rockchip parameter routes to the partition editor by its text markers.
        var paramPath = Path.Combine(Path.GetTempPath(), "uotan-param-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(paramPath, "CMDLINE: mtdparts=rk29xxnand:0x2000@0x4000(boot):gpt\n");
            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(paramPath));
            var header = await File.ReadAllBytesAsync(paramPath, TestContext.Current.CancellationToken);
            Assert.True(FileOpenMode.IsRockchipParameterText(header));
        }
        finally
        {
            File.Delete(paramPath);
        }
    }

    [Fact]
    public void SectorValueParser_RoundTripsHex()
    {
        // 0x 十六进制与十进制/后缀解析，与 Rockchip parameter 语法一致。
        // 0x-hex, decimal and suffix parsing, matching the Rockchip parameter syntax.
        Assert.Equal(0x1000UL, PartitionTableFormat.TryParseSectors("0x1000", out var v) ? v : 0);
        Assert.Equal(0x1000UL, PartitionTableFormat.TryParseSectors("4096", out var d) ? d : 0);
        Assert.Equal(8192UL, PartitionTableFormat.TryParseSectors("4M", out var m) ? m : 0); // 4 MiB = 8192 扇区
        Assert.False(PartitionTableFormat.TryParseSectors("not-a-number", out _));
        Assert.Equal("0x1000", PartitionTableFormat.FormatSectors(0x1000));
    }

    [Fact]
    public async Task GptSample_ParseEditSave_RoundTrips()
    {
        // 构造 64 MiB 磁盘的 GPT 镜像：system 32 MiB + vendor 8 MiB（起始 2048 扇区起）。
        // Build a 64 MiB GPT image: system 32 MiB + vendor 8 MiB (starting at sector 2048).
        var diskSize = 64UL * 1024 * 1024;
        var entries = new List<PartitionEntry>
        {
            new() { Name = "system", StartSector = 2048, SizeSectors = 32UL * 1024 * 1024 / 512, Type = "0FC63DAF-8483-4772-8E79-3D69D8477DE4" },
            new() { Name = "vendor", StartSector = 2048 + 32UL * 1024 * 1024 / 512, SizeSectors = 8UL * 1024 * 1024 / 512, Type = "0FC63DAF-8483-4772-8E79-3D69D8477DE4" }
        };
        var image = GptTable.Build(diskSize, Guid.NewGuid(), entries);
        var path = Path.Combine(Path.GetTempPath(), "uotan-gpt-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(path, image);

            var vm = new PartitionEditorViewModel();
            await vm.LoadAsync(path);
            try
            {
                Assert.Empty(vm.CompileError);
                Assert.Equal(2, vm.PartitionCount);
                Assert.Equal("system", vm.Partitions[0].Name);
                Assert.Equal(2048UL, vm.Partitions[0].StartSector);
                Assert.Equal(32UL * 1024 * 1024 / 512, vm.Partitions[0].SizeSectors);
                Assert.Equal(new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"), Guid.Parse(vm.Partitions[0].Type));

                // 编辑：system 改名并缩到 16 MiB。
                // Edit: rename system and shrink it to 16 MiB.
                vm.Partitions[0].Name = "system_new";
                vm.Partitions[0].Size = PartitionTableFormat.FormatSectors(16UL * 1024 * 1024 / 512);
                await vm.SaveAsync();
                Assert.Empty(vm.CompileError);
            }
            finally
            {
                vm.Cleanup();
            }

            // 回读并断言 round-trip（GPT CRC 已重算，解析须通过）。
            // Re-read and assert the round-trip (the GPT CRCs are recomputed, parsing must pass).
            var vm2 = new PartitionEditorViewModel();
            await vm2.LoadAsync(path);
            try
            {
                Assert.Empty(vm2.CompileError);
                Assert.Equal(2, vm2.Partitions.Count);
                Assert.Equal("system_new", vm2.Partitions[0].Name);
                Assert.Equal(16UL * 1024 * 1024 / 512, vm2.Partitions[0].SizeSectors);
                Assert.Equal("vendor", vm2.Partitions[1].Name);
                Assert.Equal(8UL * 1024 * 1024 / 512, vm2.Partitions[1].SizeSectors);
            }
            finally
            {
                vm2.Cleanup();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MbrSample_ParseEditSave_RoundTrips()
    {
        // 构造 3 主分区 MBR（类型 0x83/0x0C），分区间留间隙。
        // Build an MBR with 3 primary partitions (types 0x83/0x0C) with gaps between them.
        var entries = new List<PartitionEntry>
        {
            new() { Name = "p1", StartSector = 2048, SizeSectors = 32UL * 1024 * 1024 / 512, Type = "0x83" },
            new() { Name = "p2", StartSector = 2048 + 32UL * 1024 * 1024 / 512 + 2048, SizeSectors = 8UL * 1024 * 1024 / 512, Type = "0x0C" },
            new() { Name = "p3", StartSector = 2048 + 32UL * 1024 * 1024 / 512 + 2048 + 8UL * 1024 * 1024 / 512 + 2048, SizeSectors = 16UL * 1024 * 1024 / 512, Type = "0x83" }
        };
        var image = MbrTable.Build(0x12345678, entries);
        var path = Path.Combine(Path.GetTempPath(), "uotan-mbr-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(path, image);

            var vm = new PartitionEditorViewModel();
            await vm.LoadAsync(path);
            try
            {
                Assert.Empty(vm.CompileError);
                Assert.Equal(3, vm.PartitionCount);
                Assert.Equal("p1", vm.Partitions[0].Name);
                Assert.Equal(2048UL, vm.Partitions[0].StartSector);
                Assert.Equal("0x83", vm.Partitions[0].Type);
                Assert.Equal("0x0C", vm.Partitions[1].Type);

                // 编辑：p1 类型改 0x07，p2 大小调整。
                // Edit: change p1's type to 0x07 and resize p2.
                vm.Partitions[0].Type = "0x07";
                vm.Partitions[1].Size = PartitionTableFormat.FormatSectors(4UL * 1024 * 1024 / 512);
                await vm.SaveAsync();
                Assert.Empty(vm.CompileError);
            }
            finally
            {
                vm.Cleanup();
            }

            var vm2 = new PartitionEditorViewModel();
            await vm2.LoadAsync(path);
            try
            {
                Assert.Empty(vm2.CompileError);
                Assert.Equal(3, vm2.Partitions.Count);
                Assert.Equal("0x07", vm2.Partitions[0].Type);
                Assert.Equal(4UL * 1024 * 1024 / 512, vm2.Partitions[1].SizeSectors);
                Assert.Equal(16UL * 1024 * 1024 / 512, vm2.Partitions[2].SizeSectors);
            }
            finally
            {
                vm2.Cleanup();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MbrTable_ExtendedChain_RoundTrips()
    {
        // 3 主分区 + 2 逻辑分区（经 EBR 链）：逻辑分区之间留 EBR 间隙。
        // 3 primary + 2 logical partitions (via the EBR chain): logicals leave EBR gaps between them.
        var entries = new List<PartitionEntry>
        {
            new() { Name = "p1", StartSector = 2048, SizeSectors = 4096, Type = "0x83" },
            new() { Name = "p2", StartSector = 8192, SizeSectors = 4096, Type = "0x83" },
            new() { Name = "p3", StartSector = 14336, SizeSectors = 4096, Type = "0x83" },
            new() { Name = "p4", StartSector = 20480, SizeSectors = 4096, Type = "0x83" },
            new() { Name = "p5", StartSector = 26624, SizeSectors = 4096, Type = "0x83" }
        };
        var image = MbrTable.Build(0x12345678, entries);
        var info = MbrTable.Parse(image);
        Assert.Equal(5, info.Partitions.Count);
        Assert.Equal(2048UL, info.Partitions[0].StartLba);
        Assert.Equal(20480UL, info.Partitions[3].StartLba);
        Assert.Equal(26624UL, info.Partitions[4].StartLba);
        Assert.Equal(0x12345678u, info.DiskSignature);
    }

    [Fact]
    public async Task AmlogicSample_ParseEditSave_RoundTrips()
    {
        // 构造 Amlogic 镜像：3 分区（名称存名称表），起始 64 扇区起。
        // Build an Amlogic image: 3 partitions (names in the name table), starting at sector 64.
        var entries = new List<PartitionEntry>
        {
            new() { Name = "bootloader", StartSector = 64, SizeSectors = 4096, Type = "0x83" },
            new() { Name = "boot", StartSector = 4608, SizeSectors = 16384, Type = "0x83" },
            new() { Name = "system", StartSector = 22528, SizeSectors = 32UL * 1024 * 1024 / 512, Type = "0x83" }
        };
        var image = AmlogicTable.Build(0xDEADBEEF, entries);
        var path = Path.Combine(Path.GetTempPath(), "uotan-aml-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, image);

            var vm = new PartitionEditorViewModel();
            await vm.LoadAsync(path);
            try
            {
                Assert.Empty(vm.CompileError);
                Assert.Equal(3, vm.PartitionCount);
                Assert.Equal("bootloader", vm.Partitions[0].Name);
                Assert.Equal(64UL, vm.Partitions[0].StartSector);
                Assert.Equal("0x83", vm.Partitions[0].Type);
                Assert.Equal("system", vm.Partitions[2].Name);

                // 编辑：bootloader 改名并调整大小。
                // Edit: rename bootloader and resize it.
                vm.Partitions[0].Name = "spl";
                vm.Partitions[0].Size = PartitionTableFormat.FormatSectors(2048);
                await vm.SaveAsync();
                Assert.Empty(vm.CompileError);
            }
            finally
            {
                vm.Cleanup();
            }

            var vm2 = new PartitionEditorViewModel();
            await vm2.LoadAsync(path);
            try
            {
                Assert.Empty(vm2.CompileError);
                Assert.Equal(3, vm2.Partitions.Count);
                Assert.Equal("spl", vm2.Partitions[0].Name);
                Assert.Equal(2048UL, vm2.Partitions[0].SizeSectors);
                Assert.Equal("boot", vm2.Partitions[1].Name);
            }
            finally
            {
                vm2.Cleanup();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenMode_ResolvesGptAndMbrByContent()
    {
        // GPT：含保护性 MBR，GPT 魔数须优先命中（IsGptMagic 与 IsMbrMagic 同时为真）。
        // GPT: carries a protective MBR, so the GPT magic must win (both IsGptMagic and IsMbrMagic hold).
        var gptEntries = new List<PartitionEntry>
        {
            new() { Name = "system", StartSector = 2048, SizeSectors = 4096, Type = "0FC63DAF-8483-4772-8E79-3D69D8477DE4" }
        };
        var gptPath = Path.Combine(Path.GetTempPath(), "uotan-gpt-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(gptPath, GptTable.Build(64UL * 1024 * 1024, Guid.NewGuid(), gptEntries));
            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(gptPath));
            var gptHeader = await File.ReadAllBytesAsync(gptPath, TestContext.Current.CancellationToken);
            Assert.True(FileOpenMode.IsGptMagic(gptHeader));
            Assert.True(FileOpenMode.IsMbrMagic(gptHeader));
            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(gptPath, gptHeader));
        }
        finally
        {
            File.Delete(gptPath);
        }

        // 纯 MBR：只有 0x55AA，无 GPT 签名。
        // Plain MBR: only 0x55AA, no GPT signature.
        var mbrEntries = new List<PartitionEntry>
        {
            new() { Name = "p1", StartSector = 2048, SizeSectors = 4096, Type = "0x83" }
        };
        var mbrPath = Path.Combine(Path.GetTempPath(), "uotan-mbr-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(mbrPath, MbrTable.Build(0x12345678, mbrEntries));
            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(mbrPath));
            var mbrHeader = await File.ReadAllBytesAsync(mbrPath, TestContext.Current.CancellationToken);
            Assert.True(FileOpenMode.IsMbrMagic(mbrHeader));
            Assert.False(FileOpenMode.IsGptMagic(mbrHeader));
            Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode(mbrPath, mbrHeader));
        }
        finally
        {
            File.Delete(mbrPath);
        }
    }
}