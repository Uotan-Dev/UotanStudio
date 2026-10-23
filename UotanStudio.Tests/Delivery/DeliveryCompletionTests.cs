using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using UotanStudio.Delivery;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed partial class DeliveryCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-completion-" + Guid.NewGuid().ToString("N"));
    public DeliveryCompletionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private DeliveryFile Image(string name)
    {
        var path = Path.Combine(_root, name + ".img");
        File.WriteAllBytes(path, Enumerable.Range(0, 8192).Select(i => (byte)(i % 31)).ToArray());
        return new() { SourcePath = path, FileName = name + ".img", Partition = name, SourceLength = 8192 };
    }
    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class FixedSigningKeys(RSA key) : IDeliverySigningKeyProvider
    {
        public RSA LoadPrivateKey(string path) => RSA.Create(key.ExportParameters(includePrivateParameters: true));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("BZip2")]
    [InlineData("Xz")]
    public async Task PostinstallAndMetadataSurviveCompressedDelivery(string compression)
    {
        var image = Image("system"); var config = Path.Combine(_root, "postinstall.txt");
        File.WriteAllText(config, "RUN_POSTINSTALL_system=true\nPOSTINSTALL_PATH_system=bin/otapreopt_script\nFILESYSTEM_TYPE_system=ext4\nPOSTINSTALL_OPTIONAL_system=false\n");
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            PackPayload = true,
            ContainerConfirmed = true,
            Container = [image],
            Txt = false,
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            OutputDirectory = Path.Combine(_root, "out"),
            Payload = new() { PostinstallFile = config, BlockSize = 8192, MaxTimestamp = 1800000000, SecurityPatchLevel = "2026-09-05", Compression = compression }
        };
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        var unpacked = Path.Combine(_root, "ota-content");
        ZipFile.ExtractToDirectory(Path.Combine(plan.OutputDirectory, "ota.zip"), unpacked);
        var payload = Path.Combine(unpacked, "payload.bin");
        var manifest = PayloadParser.GetPayloadMetadata(payload).Manifest;
        Assert.Equal(8192u, manifest.BlockSize); Assert.Equal(1800000000L, manifest.MaxTimestamp);
        Assert.Equal("2026-09-05", manifest.SecurityPatchLevel);
        var partition = Assert.Single(manifest.Partitions);
        Assert.True(partition.RunPostinstall); Assert.False(partition.PostinstallOptional);
        Assert.Equal("bin/otapreopt_script", partition.PostinstallPath); Assert.Equal("ext4", partition.FilesystemType);
        Assert.Equal(partition, partition.Clone()); Assert.Equal(partition, PartitionUpdate.Parser.ParseFrom(partition.ToByteArray()));
        var output = Path.Combine(_root, "extracted"); Directory.CreateDirectory(output);
        PayloadParser.Extract(payload, ["system"], output);
        Assert.Equal(File.ReadAllBytes(image.SourcePath), File.ReadAllBytes(Path.Combine(output, "system.img")));
    }

    [Theory]
    [InlineData("RUN_POSTINSTALL_unknown=true")]
    [InlineData("RUN_POSTINSTALL_system=yes")]
    [InlineData("RUN_POSTINSTALL_system=true\nPOSTINSTALL_PATH_system=../evil")]
    [InlineData("RUN_POSTINSTALL_system=false\nRUN_POSTINSTALL_system=true")]
    [InlineData("POSTINSTALL_PATH_system=bin/postinst")]
    public void InvalidPostinstallRejected(string text)
    {
        var path = Path.Combine(_root, "config"); File.WriteAllText(path, text);
        Assert.Throws<InvalidDataException>(() => DeliveryPostinstall.Read(path, ["system"]));
    }

    [Fact]
    public async Task SuperPreservesMultipleGroupCapacityAndSourceMembership()
    {
        var files = new[] { Image("system_a"), Image("vendor_a") };
        var groups = DeliveryGroups.Parse("main_a 16384 system_a\nother_a 8192 vendor_a");
        var options = new DeliverySuperOptions { DeviceSize = 4 * 1024 * 1024, Groups = groups };
        var path = Path.Combine(_root, "super.img");
        await DeliverySuperBuilder.BuildAsync(files, options, path, token: Token);
        var metadata = MetadataReader.ReadFromImageFile(path);
        Assert.Equal(16384UL, metadata.Groups.Single(g => g.NameString == "main_a").MaximumSize);
        var source = SourceContainerMetadataService.Capture(path, ContainerKind.Super);
        var restored = DeliveryGroups.FromSource(source);
        Assert.Equal("vendor_a", Assert.Single(restored.Single(g => g.Name == "other_a").Partitions));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverySuperBuilder.BuildAsync(files,
            options with { Groups = DeliveryGroups.Parse("main_a 4096 system_a vendor_a") }, Path.Combine(_root, "bad.img"), token: Token));
        Assert.False(File.Exists(Path.Combine(_root, "bad.img")));
    }

    [Fact]
    public async Task OversizedPartitionsReportRequiredBytesAndMinimumSize()
    {
        var files = new[] { Image("system_a"), Image("vendor_a") };
        var options = new DeliverySuperOptions { DeviceSize = 409_600 };
        var path = Path.Combine(_root, "too-small.img");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DeliverySuperBuilder.BuildAsync(files, options, path, token: Token));
        Assert.Contains("Super 空间不足", error.Message, StringComparison.Ordinal);
        Assert.Contains("421888", error.Message, StringComparison.Ordinal);
        Assert.Equal(421_888UL, DeliverySuperCapacity.MinimumDeviceSize([8192, 8192], options));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_root, "too-small.img.*"));
    }

    [Fact]
    public async Task PackageIsReadableAndAndroidWholeFileSignatureRejectsTampering()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Delivery test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cert = Path.Combine(_root, "cert.pem"); File.WriteAllText(cert, certificate.ExportCertificatePem());
        var privateKey = Path.Combine(_root, "key.pem"); File.WriteAllText(privateKey, key.ExportPkcs8PrivateKeyPem());
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            PackPayload = true,
            ContainerConfirmed = true,
            Container = [Image("boot")],
            Txt = false,
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            OutputDirectory = Path.Combine(_root, "out"),
            DeviceCodename = "test_device",
            Payload = new() { SigningKeyPath = privateKey, MaxTimestamp = 1800000000, SecurityPatchLevel = "2026-09-05" },
            Package = new()
            {
                Ota = true,
                SigningKeyPath = privateKey,
                CertificatePath = cert,
                BuildFingerprint = "brand/product/device:16/ABC/123:user/release-keys",
                Incremental = "123",
                SdkLevel = 36
            }
        };
        await new DeliveryOutputService(new NoTools(), new FixedSigningKeys(key)).ExportAsync(plan, token: Token);
        var path = Path.Combine(plan.OutputDirectory, "ota.zip");
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("payload.bin")); Assert.NotNull(zip.GetEntry("payload_properties.txt"));
            Assert.Null(zip.GetEntry(DeliveryRecoveryUpdater.EntryPath));
            using var text = new StreamReader(zip.GetEntry("META-INF/com/android/metadata")!.Open());
            Assert.Contains("ota-type=AB\npre-device=test_device", await text.ReadToEndAsync(Token));
        }
        var bytes = File.ReadAllBytes(path);
        var distance = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 6));
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2));
        Assert.Equal(255, bytes[^4]); Assert.Equal(255, bytes[^3]);
        var signedLength = bytes.Length - commentLength - 2;
        var signature = bytes.AsSpan(bytes.Length - distance, distance - 6).ToArray();
        var cms = new SignedCms(new ContentInfo(bytes[..signedLength]), true); cms.Decode(signature); cms.CheckSignature(true);
        Assert.Empty(cms.SignerInfos[0].SignedAttributes);
        bytes[40] ^= 1;
        cms = new SignedCms(new ContentInfo(bytes[..signedLength]), true); cms.Decode(signature);
        Assert.Throws<CryptographicException>(() => cms.CheckSignature(true));
        using var wrong = RSA.Create(2048); File.WriteAllText(privateKey, wrong.ExportPkcs8PrivateKeyPem());
        plan = plan with { OutputDirectory = Path.Combine(_root, "wrong-key") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools(), new FixedSigningKeys(wrong)).ExportAsync(plan, token: Token));
        Assert.False(Directory.Exists(plan.OutputDirectory));
    }

    [Fact]
    public async Task RecoveryNormalizesSparseAndUsesOnlyDefaultSelection()
    {
        var image = Image("boot");
        var sparse = Path.Combine(_root, "boot-sparse.img");
        UotanStudio.Firmware.BlockImage.Sparse.SparseImageConverter.ConvertRawToSparse(image.SourcePath, sparse);
        var alternate = Image("alternative") with { Partition = "boot", Optional = true };
        var plan = new DeliveryPlan
        {
            Recovery = true,
            Txt = false,
            DeviceCodename = "test_device",
            OutputDirectory = Path.Combine(_root, "recovery-out"),
            Files = [image with { SourcePath = sparse, SourceLength = new FileInfo(sparse).Length, Optional = true, Default = true }, alternate]
        };
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "recovery.zip"));
        Assert.Null(zip.GetEntry("images/alternative.img"));
        using var content = zip.GetEntry("images/boot.img")!.Open(); using var expected = new MemoryStream(); await content.CopyToAsync(expected, Token);
        Assert.Equal(File.ReadAllBytes(image.SourcePath), expected.ToArray());
        using var script = new StreamReader(zip.GetEntry("META-INF/com/google/android/update-binary")!.Open());
        var text = await script.ReadToEndAsync(Token);
        var verify = text.IndexOf("verify_member 'images/boot.img'", StringComparison.Ordinal);
        var write = text.IndexOf("stream_image 'images/boot.img' dd", StringComparison.Ordinal);
        Assert.True(verify >= 0 && write > verify);
        Assert.Throws<NotSupportedException>(() => DeliveryPackageBuilder.Validate(plan with { Files = [image with { Fastbootd = true }] }));
    }

    [Fact]
    public async Task RecoverySupportsDatBrotliImageSet()
    {
        var transfer = Path.Combine(_root, "system.transfer.list");
        var data = Path.Combine(_root, "system.new.dat.br");
        var patch = Path.Combine(_root, "system.patch.dat");
        File.WriteAllText(transfer, "4\n1\n0\n0\nnew 2,0,1\n");
        using (var output = File.Create(data))
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal)) compressor.Write(new byte[4096]);
        File.WriteAllBytes(patch, []);
        static DeliveryFile FileEntry(string path, string name) => new()
        {
            SourcePath = path,
            FileName = name,
            Partition = "system",
            SourceLength = new FileInfo(path).Length
        };
        var plan = new DeliveryPlan
        {
            Recovery = true,
            Txt = false,
            OutputDirectory = Path.Combine(_root, "dat-recovery-out"),
            Package = new() { RecoveryUpdaterPath = WriteUpdaterFixture() },
            Files = [FileEntry(transfer, "system.transfer.list"), FileEntry(data, "system.new.dat.br"), FileEntry(patch, "system.patch.dat")]
        };

        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);

        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "recovery.zip"));
        Assert.NotNull(zip.GetEntry("images/system.transfer.list"));
        Assert.NotNull(zip.GetEntry("images/system.new.dat.br"));
        Assert.NotNull(zip.GetEntry("images/system.patch.dat"));
        var fixture = File.ReadAllBytes(plan.Package.RecoveryUpdaterPath);
        using (var updater = zip.GetEntry(DeliveryRecoveryUpdater.EntryPath)!.Open())
        using (var actual = new MemoryStream())
        {
            await updater.CopyToAsync(actual, Token);
            Assert.Equal(fixture, actual.ToArray());
        }
        using var edify = new StreamReader(zip.GetEntry(DeliveryRecoveryUpdater.ScriptPath)!.Open());
        var edifyText = await edify.ReadToEndAsync(Token);
        Assert.Contains("block_image_update(\"/dev/block/bootdevice/by-name/system\" + getprop(\"ro.boot.slot_suffix\")", edifyText);
        Assert.Contains("package_extract_file(\"images/system.transfer.list\")", edifyText);
        Assert.Contains("\"images/system.new.dat.br\", \"images/system.patch.dat\"", edifyText);
    }

    [Fact]
    public void ScriptGeneratorKeepsDatInRecoveryAndOutOfFastbootScripts()
    {
        static DeliveryFile Entry(string name) => new()
        {
            FileName = name,
            Partition = "system",
            Optional = false,
            Default = true
        };
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            Recovery = true,
            Linux = true,
            Windows = true,
            Txt = true,
            Files = [Entry("system.transfer.list"), Entry("system.new.dat.br")]
        };

        var scripts = DeliveryScriptGenerator.Generate(plan);

        Assert.Contains("updater-script", scripts.Keys);
        Assert.DoesNotContain("update-binary", scripts.Keys);
        Assert.DoesNotContain("Recovery.sh", scripts.Keys);
        Assert.DoesNotContain("FlashLinux.sh", scripts.Keys);
        Assert.DoesNotContain("FlashWindows.bat", scripts.Keys);
        Assert.DoesNotContain("flash.txt", scripts.Keys);
        Assert.Contains("block_image_update", scripts["updater-script"]);
    }

    [Fact]
    public void RecoveryRejectsIncompleteDatImageSet()
    {
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            Recovery = true,
            Files =
            [
                new DeliveryFile { FileName = "system.new.dat.br", Partition = "system", SourcePath = "placeholder" }
            ]
        };

        Assert.Throws<InvalidDataException>(() => DeliveryPackageBuilder.Validate(plan));
    }

    [Fact]
    public async Task OrganizedSuperUsesRecoveryScriptLayoutAndRootImageEntry()
    {
        var image = Image("system") with { IncludeInSuper = true };
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            PackSuper = true,
            ContainerConfirmed = true,
            Container = [image],
            Files = [new DeliveryFile { Generated = true, Partition = "super", FileName = "super.zip" }],
            Super = new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024 },
            Txt = false,
            OutputDirectory = Path.Combine(_root, "super-zip-out")
        };

        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);

        var archivePath = Path.Combine(plan.OutputDirectory, "super.zip");
        Assert.True(File.Exists(archivePath));
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry("super.img"));
        Assert.Null(archive.GetEntry("images/super.img"));
        var text = await new StreamReader(archive.GetEntry("META-INF/com/google/android/update-binary")!.Open()).ReadToEndAsync(Token);
        Assert.Contains("OUTFD=/proc/self/fd/$2", text);
        Assert.Contains("ZIPFILE=\"$3\"", text);
        Assert.Contains("ui_print", text);
        Assert.Contains("package_extract_file", text);
        Assert.Contains("unzip -p", text);
        Assert.Contains("/dev/block/by-name", text);
        Assert.Contains("/dev/block/bootdevice/by-name", text);
        Assert.Contains("stream_image 'super.img' dd", text);
    }

    [Fact]
    public async Task OrganizedSuperZipUsesVendorLayoutAndFirmwareUpdateFolder()
    {
        var superMember = Image("system") with { IncludeInSuper = true };
        var firmware = Image("abl");
        var boot = Image("boot");
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            PackSuper = true,
            ContainerConfirmed = true,
            Container = [superMember],
            Files =
            [
                new DeliveryFile { Generated = true, Partition = "super", FileName = "super.zip" },
                firmware,
                boot
            ],
            Super = new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024 },
            Windows = false,
            Linux = false,
            MacOS = false,
            Txt = false,
            Recovery = true,
            OutputDirectory = Path.Combine(_root, "super-vendor-out")
        };

        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);

        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "super.zip"));
        Assert.NotNull(zip.GetEntry("super.img"));
        Assert.NotNull(zip.GetEntry("firmware-update/abl.img"));
        Assert.NotNull(zip.GetEntry("boot.img"));
        Assert.NotNull(zip.GetEntry("META-INF/com/google/android/update-binary"));
        Assert.NotNull(zip.GetEntry("META-INF/com/google/android/updater-script"));
        Assert.NotNull(zip.GetEntry("README.txt"));
        Assert.NotNull(zip.GetEntry("checksums.sha256"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("images/", StringComparison.Ordinal));

        var scripts = DeliveryScriptGenerator.Generate(plan with { Windows = true, Txt = false });
        Assert.Contains("super.img", scripts["FlashWindows.bat"]);
        Assert.DoesNotContain("super.zip", scripts["FlashWindows.bat"]);
        Assert.DoesNotContain("\\\\\"IMAGE", scripts["FlashWindows.bat"]);
    }

    [Theory]
    [InlineData(false, "super.img")]
    [InlineData(true, "super.zst")]
    public async Task UnorganizedSuperPublishesSingleRawContainer(bool zstd, string expectedName)
    {
        var member = Image("system") with { IncludeInSuper = true };
        var plan = new DeliveryPlan
        {
            PackSuper = true,
            ContainerConfirmed = true,
            Container = [member],
            Files = [new DeliveryFile { Generated = true, Partition = "super", FileName = expectedName }],
            Super = new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024, Zstd = zstd },
            OrganizeDirectory = false,
            OutputDirectory = Path.Combine(_root, "super-raw-" + zstd)
        };

        var paths = await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        Assert.Equal([Path.Combine(plan.OutputDirectory, expectedName)], paths);
        Assert.True(File.Exists(paths[0]));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(plan.OutputDirectory), p => !p.EndsWith(expectedName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdvancedSettingsPersistAndScriptPreflightPrecedesWrites()
    {
        var plan = new DeliveryPlan
        {
            Files = [Image("boot"), Image("system") with { Fastbootd = true }],
            Windows = true,
            Linux = true,
            MacOS = false,
            AskWipe = true,
            Reboot = true,
            Slot = "b",
            LocalZstd = new() { ["Linux"] = Path.Combine(_root, "zstd") },
            Super = new() { Zstd = true },
            Package = new() { SigningKeyPath = Path.Combine(_root, "key.pem"), CertificatePath = Path.Combine(_root, "cert.pem"), RecoveryUpdaterPath = Path.Combine(_root, "META-INF", "update-binary") }
        };
        var state = DeliveryPlanStore.CaptureElement(_root, plan);
        var restored = DeliveryPlanStore.LoadFromState(state, _root)!;
        Assert.True(restored.AskWipe); Assert.True(restored.Reboot); Assert.Equal("b", restored.Slot);
        Assert.Equal(plan.LocalZstd["Linux"], restored.LocalZstd["Linux"]); Assert.True(restored.Super.Zstd);
        Assert.Equal(plan.Package, restored.Package);
        Assert.Empty(restored.Files);
        Assert.Empty(restored.Container);
        var scripts = DeliveryScriptGenerator.Generate(restored with { Files = plan.Files });
        Assert.False(scripts.ContainsKey("FlashMacOS.sh"));
        Assert.Contains("sha256sum --strict -c", scripts["FlashLinux.sh"]);
        Assert.Contains("reboot fastboot", scripts["FlashLinux.sh"]); Assert.Contains("ensure_mode yes", scripts["FlashLinux.sh"]);
        Assert.Contains("Get-FileHash", scripts["FlashWindows.bat"]); Assert.Contains("call :ensure_mode yes", scripts["FlashWindows.bat"]);
    }

    [Fact]
    public void ContainerFileNameUsesArchiveNameWhenOrganizingZip()
    {
        var plan = new DeliveryPlan { OrganizeDirectory = true, PackPayload = true, ArchiveName = "  my_custom  " };
        Assert.Equal("my_custom.zip", DeliveryOutputService.ContainerFileName(plan));
        Assert.Equal("my_custom.zip", DeliveryOutputService.ZipFileName(plan, "ota.zip"));
        Assert.Equal("ota.zip", DeliveryOutputService.ContainerFileName(plan with { ArchiveName = "" }));
    }

    [Fact]
    public async Task RecoveryExportPublishesUnderCustomArchiveName()
    {
        var plan = new DeliveryPlan
        {
            Recovery = true,
            Txt = false,
            ArchiveName = "custom_flash",
            OutputDirectory = Path.Combine(_root, "archive-out"),
            Files = [Image("boot")]
        };
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        Assert.True(File.Exists(Path.Combine(plan.OutputDirectory, "custom_flash.zip")));
        Assert.False(File.Exists(Path.Combine(plan.OutputDirectory, "recovery.zip")));
    }

    [Fact]
    public void InvalidArchiveNameRejected()
    {
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            ArchiveName = "bad/name",
            Files = [new() { FileName = "boot.img", Partition = "boot" }]
        };
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan));
    }

    [Fact]
    public async Task PayloadExportWithCustomArchiveNamePublishesThePlannedContainer()
    {
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Txt = false,
            ArchiveName = "custom_ota",
            Container = [Image("boot")],
            Files = [new() { Generated = true, Partition = "payload", FileName = "custom_ota.zip" }],
            OutputDirectory = Path.Combine(_root, "custom-ota-out")
        };
        var planned = DeliveryOutputService.PlannedOutputPaths(plan).ToArray();
        Assert.Equal(planned.Length, planned.Distinct(StringComparer.Ordinal).Count());
        var published = await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        var container = Path.Combine(plan.OutputDirectory, "custom_ota.zip");
        Assert.Contains(container, published);
        Assert.True(File.Exists(container));
        Assert.False(File.Exists(Path.Combine(plan.OutputDirectory, "ota.zip")));
    }

    [Fact]
    public void SuperDeliveryWithoutExplicitDeviceSizeIsRejected()
    {
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = false,
            PackSuper = true,
            ContainerConfirmed = true,
            Container = [Image("system")],
            Files = [new() { Generated = true, Partition = "super", FileName = "super.img" }],
            OutputDirectory = Path.Combine(_root, "no-size-out")
        };
        var error = Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan));
        Assert.Contains("super-partition-size", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan with { Super = new() { Empty = true } }));
    }

    [Fact]
    public async Task SuperBuilderWithoutExplicitDeviceSizePublishesNothing()
    {
        var output = Path.Combine(_root, "unset-size.img");
        await Assert.ThrowsAsync<InvalidDataException>(() => DeliverySuperBuilder.BuildAsync(
            [Image("system")], new() { }, output, token: Token));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuperScriptsCompareDeviceSuperSizeBeforeFlashingSuper(bool packSuper)
    {
        var plan = new DeliveryPlan
        {
            PackSuper = packSuper,
            ContainerConfirmed = true,
            Windows = true,
            Linux = true,
            MacOS = true,
            Container = [Image("system")],
            Super = new() { DeviceSize = 1234567890 },
            Files = packSuper
                ? [new() { Generated = true, Partition = "super", FileName = "super.zip" }, Image("boot")]
                : [Image("boot")],
            OutputDirectory = Path.Combine(_root, "super-guard-out")
        };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        foreach (var name in new[] { "FlashLinux.sh", "FlashMacOS.sh", "FlashWindows.bat" })
        {
            if (packSuper)
            {
                Assert.Contains("getvar super-partition-size", scripts[name], StringComparison.Ordinal);
                Assert.Contains("1234567890", scripts[name], StringComparison.Ordinal);
            }
            else Assert.DoesNotContain("super-partition-size", scripts[name], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FastbootScriptsStateUnlockedBootloaderWithoutClaimingRecovery()
    {
        var plan = new DeliveryPlan { Windows = true, Linux = true, MacOS = true, Files = [Image("boot")] };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        foreach (var name in new[] { "FlashWindows.bat", "FlashLinux.sh", "FlashMacOS.sh" })
        {
            Assert.Contains("unlocked bootloader", scripts[name], StringComparison.Ordinal);
            Assert.DoesNotContain("custom recovery", scripts[name], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RecoveryInstallerAnnouncesUnlockedBootloaderAndCustomRecovery()
    {
        var plan = DatPlan();
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: Token);
        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "recovery.zip"));
        using var reader = new StreamReader(zip.GetEntry(DeliveryRecoveryUpdater.ScriptPath)!.Open());
        var text = await reader.ReadToEndAsync(Token);
        Assert.Contains("unlocked bootloader", text, StringComparison.Ordinal);
        Assert.Contains("custom recovery", text, StringComparison.Ordinal);
    }
}