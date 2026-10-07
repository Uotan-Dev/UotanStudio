using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodeOperationFailureTests
{
    [Theory]
    [InlineData("Build.Signature.Merge")]
    [InlineData("Build.Slots.Merge")]
    public async Task ExecuteAsync_UnsupportedLegacyBuild_FailsWithoutPublishingBytes(string typeKey)
    {
        var descriptor = NodeFactoryRegistrar.Descriptors.Single(candidate => candidate.TypeKey == typeKey);
        Assert.False(descriptor.IsToolboxVisible);
        var node = descriptor.Factory();
        var precedingEffect = new EffectNode();
        foreach (var input in node.Input)
        {
            input.InitialValue = new byte[] { 1, 2, 3 };
        }

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([precedingEffect, node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.True(node.IsUnavailable);
        Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.False(precedingEffect.Executed);
        Assert.All(node.Output, port => Assert.Null(port.Data));
    }

    [Fact]
    public async Task ExecuteAsync_UntakenFlowBranch_SkipsFirmwareSideEffect()
    {
        var branch = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Flow.Condition.Branch").Factory();
        branch.Input.Single(port => port.Title == "Condition").InitialValue = false;
        var firmware = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Firmware.Container.Unpack").Factory();
        firmware.Input.Single(port => port.Title == "Firmware Path").InitialValue = "missing.img";

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([branch, firmware],
            [new NodeConnection { SourceInstanceId = branch.InstanceId, SourceConnector = "True",
                TargetInstanceId = firmware.InstanceId, TargetConnector = "Flow" }],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(NodeExecutionStatus.Skipped, firmware.ExecutionStatus);
        Assert.Null(firmware.Output.Single(port => port.Title == "Output Dir").Data);
    }

    [Fact]
    public async Task VerifySignature_NonAvbInput_FailsWithoutReportingValid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"node-signature-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllTextAsync(path, "not a signed image", TestContext.Current.CancellationToken);
            var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Validation.Signature.Verify").Factory();
            node.Input.Single(port => port.Title == "Firmware").InitialValue = path;

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
                TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
            Assert.Null(node.Output.Single(port => port.Title == "Valid").Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyAvbAsync_UnsignedVbmeta_ReturnsInvalid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"node-unsigned-{Guid.NewGuid():N}.img");
        try
        {
            var bytes = new byte[256];
            "AVB0"u8.CopyTo(bytes);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

            var result = await FirmwareService.Instance.VerifyAvbAsync(path,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("Unsigned vbmeta", result.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifySignature_UnsignedVbmeta_FailsAndSkipsDependent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"node-unsigned-{Guid.NewGuid():N}.img");
        try
        {
            var bytes = new byte[256];
            "AVB0"u8.CopyTo(bytes);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var node = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "Validation.Signature.Verify").Factory();
            node.Input.Single(port => port.Title == "Firmware").InitialValue = path;
            var dependent = new EffectNode();

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node, dependent],
                [new NodeConnection
                {
                    SourceInstanceId = node.InstanceId, SourceConnector = "Valid",
                    TargetInstanceId = dependent.InstanceId, TargetConnector = "Input"
                }], TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
            Assert.Equal(false, node.Output.Single(port => port.Title == "Valid").Data);
            Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
            Assert.False(dependent.Executed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyAvbAsync_ValidAndTamperedVbmeta_ReportsSignatureValidity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"node-avb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            var privateKey = rsa;
            var templatePath = Path.Combine(directory, "template.img");
            var signedPath = Path.Combine(directory, "signed.img");
            var template = new byte[256];
            "AVB0"u8.CopyTo(template);
            await File.WriteAllBytesAsync(templatePath, template, TestContext.Current.CancellationToken);

            var sign = await AvbSigner.SignVbmetaAsync(templatePath, privateKey, signedPath,
                Array.Empty<AvbHashDescriptor>(), TestContext.Current.CancellationToken);
            Assert.True(sign.Success, sign.ErrorMessage);

            var valid = await FirmwareService.Instance.VerifyAvbAsync(signedPath,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(valid.Success, valid.Summary);

            var bytes = await File.ReadAllBytesAsync(signedPath, TestContext.Current.CancellationToken);
            bytes[140] ^= 1;
            await File.WriteAllBytesAsync(signedPath, bytes, TestContext.Current.CancellationToken);

            var tampered = await FirmwareService.Instance.VerifyAvbAsync(signedPath,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(tampered.Success);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractPayloadAsync_RequestedPartition_ExposesExistingImagePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"node-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "boot.img");
            var expected = new byte[4096];
            expected[0] = 42;
            await File.WriteAllBytesAsync(source, expected, TestContext.Current.CancellationToken);
            var payload = Path.Combine(directory, "payload.bin");
            using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
            {
                await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
            }
            var node = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "Firmware.Container.Extract").Factory();
            node.Input.Single(port => port.Title == "Firmware").InitialValue = payload;
            node.Input.Single(port => port.Title == "Partition Name").InitialValue = "boot";

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
                TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            var imagePath = Assert.IsType<string>(node.Output.Single(port => port.Title == "Image File").Data);
            var artifact = Assert.IsType<UotanStudio.Services.Projects.FileArtifact>(
                node.Output.Single(port => port.Title == "Image").Data);
            Assert.Equal(imagePath, artifact.Path);
            Assert.Equal(Path.GetDirectoryName(imagePath), node.Output.Single(port => port.Title == "Output Dir").Data);
            Assert.EndsWith("boot.img", imagePath);
            Assert.Equal(expected, await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractPayloadAsync_LegacyImageData_PublishesImageBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"node-payload-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "boot.img");
            var expected = new byte[4096];
            expected[0] = 42;
            expected[2] = 255;
            expected[3] = 17;
            await File.WriteAllBytesAsync(source, expected, TestContext.Current.CancellationToken);
            var payload = Path.Combine(directory, "payload.bin");
            using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
            {
                await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
            }
            var extract = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "Firmware.Container.Extract").Factory();
            extract.Input.Single(port => port.Title == "Firmware").InitialValue = payload;
            extract.Input.Single(port => port.Title == "Partition Name").InitialValue = "boot";
            var publish = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "File.Artifact.Publish").Factory();
            var destination = Path.Combine(directory, "published.img");
            publish.Input.Single(port => port.Title == "Path").InitialValue = destination;

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([extract, publish],
                [new NodeConnection
                {
                    SourceInstanceId = extract.InstanceId, SourceConnector = "Image",
                    TargetInstanceId = publish.InstanceId, TargetConnector = "Data"
                }], TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            Assert.Equal(expected, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateDeltaAsync_PublishedData_PreservesGeneratedPatchBytes()
    {
        var directory = Directory.CreateTempSubdirectory("node-delta-").FullName;
        var token = TestContext.Current.CancellationToken;
        try
        {
            var source = Path.Combine(directory, "source.img");
            var target = Path.Combine(directory, "target.img");
            var published = Path.Combine(directory, "published.bsdiff");
            await File.WriteAllBytesAsync(source, Enumerable.Range(0, 16 * 1024)
                .Select(index => (byte)(index % 251)).ToArray(), token);
            await File.WriteAllBytesAsync(target, Enumerable.Range(0, 16 * 1024)
                .Select(index => (byte)((index + 17) % 251)).ToArray(), token);
            var delta = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "Build.Ota.CreateDelta").Factory();
            delta.Input.Single(port => port.Title == "Source").InitialValue = source;
            delta.Input.Single(port => port.Title == "Target").InitialValue = target;
            var publish = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "File.Artifact.Publish").Factory();
            publish.Input.Single(port => port.Title == "Path").InitialValue = published;

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([delta, publish],
                [new NodeConnection
                {
                    SourceInstanceId = delta.InstanceId, SourceConnector = "Delta",
                    TargetInstanceId = publish.InstanceId, TargetConnector = "Data"
                }], token);

            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            var artifact = Assert.IsType<UotanStudio.Services.Projects.FileArtifact>(
                delta.Output.Single(port => port.Title == "Delta").Data);
            var patch = await File.ReadAllBytesAsync(artifact.Path, token);
            Assert.True(patch.AsSpan().StartsWith("BSDIFF40"u8));
            Assert.Equal(patch, await File.ReadAllBytesAsync(published, token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractPayloadAsync_CancelledBeforeExtraction_DoesNotCreateOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), $"node-cancelled-{Guid.NewGuid():N}");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FirmwareService.Instance.ExtractPayloadAsync("unused.payload", output, "boot", cancellation.Token));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task UnpackCpioAsync_CancelledBeforeExtraction_DoesNotCreateOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), $"node-cpio-cancelled-{Guid.NewGuid():N}");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FirmwareService.Instance.UnpackCpioAsync("unused.cpio", output,
                cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task PackFirmware_OutputPath_TakesPrecedenceOverLegacyConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"node-pack-{Guid.NewGuid():N}");
        var source = Path.Combine(directory, "source");
        var input = Path.Combine(directory, "extracted");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "sample.txt"), "content",
                TestContext.Current.CancellationToken);
            var archive = Path.Combine(directory, "original.cpio");
            var handler = new CpioFormatHandler();
            var created = await handler.PackAsync(source, archive, TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Summary);
            var extracted = await handler.UnpackAsync(archive, input,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(extracted.Success, extracted.Summary);
            Assert.True(RepackMetadata.Read(input)!.CanPackDirectory);
            var output = Path.Combine(directory, "selected.cpio");
            var legacy = Path.Combine(directory, "legacy.cpio");
            var node = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "Firmware.Container.Pack").Factory();
            node.Input.Single(port => port.Title == "Input Dir").InitialValue = input;
            node.Input.Single(port => port.Title == "Config").InitialValue = legacy;
            node.Input.Single(port => port.Title == "Output Path").InitialValue = output;

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
                TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            Assert.True(File.Exists(output));
            Assert.False(File.Exists(legacy));
            Assert.Equal(output, node.Output.Single(port => port.Title == "Firmware").Data);
            Assert.Equal(["sample.txt"], (await handler.ReadMetadataAsync(output,
                TestContext.Current.CancellationToken)).Entries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackFirmware_WithoutMetadata_RejectsBeforeReplacingOutput()
    {
        var directory = Directory.CreateTempSubdirectory("node-pack-").FullName;
        var input = Path.Combine(directory, "input");
        Directory.CreateDirectory(input);
        var output = Path.Combine(directory, "existing.img");
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(input, "sample.txt"), "content", token);
            await File.WriteAllTextAsync(output, "previous", token);
            var node = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
                descriptor.TypeKey == "Firmware.Container.Pack").Factory();
            node.Input.Single(port => port.Title == "Input Dir").InitialValue = input;
            node.Input.Single(port => port.Title == "Output Path").InitialValue = output;

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [], token);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Contains(LanguageService.Instance.GetString("Node_Pack_RequiresMetadata"), node.ExecutionError);
            Assert.Equal("previous", await File.ReadAllTextAsync(output, token));
            Assert.Null(node.Output.Single(port => port.Title == "Firmware").Data);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("File.Binary.Read", "Path", "nonexistent-file")]
    [InlineData("Data.Value.Convert", "Format", "unknown-format")]
    [InlineData("Data.Value.Convert", "Format", "bytes")]
    [InlineData("Data.Compression.Decompress", "Data", "invalid-gzip")]
    [InlineData("Data.Bytes.Slice", "Data", "")]
    [InlineData("Data.Bytes.Swap", "Data", "")]
    public async Task ExecuteAsync_InvalidNodeInput_FailsInsteadOfRunningDependent(string typeKey, string portTitle, string value)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
        if (typeKey == "Data.Value.Convert")
        {
            node.Input.Single(port => port.Title == "Input").InitialValue = "content";
        }
        node.Input.Single(port => port.Title == portTitle).InitialValue = value;
        var dependent = new EffectNode();
        var source = node.Output.First();

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node, dependent],
            [new NodeConnection { SourceInstanceId = node.InstanceId, SourceConnector = source.Title,
                TargetInstanceId = dependent.InstanceId, TargetConnector = "Input" }],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
        Assert.False(dependent.Executed);
    }

    [Fact]
    public async Task ExecuteAsync_SuperPackWithInvalidPartition_DoesNotPublishPartialImage()
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Image.Super.Pack").Factory();
        var output = Path.Combine(Path.GetTempPath(), $"invalid-super-{Guid.NewGuid():N}.img");
        node.Input.Single(port => port.Title == "Output Path").InitialValue = output;
        node.Input.Single(port => port.Title == "Device Size").InitialValue = 1024 * 1024d;
        node.Input.Single(port => port.Title == "Partitions").InitialValue = "system=missing.img;invalid";

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("File.Binary.Read", "Path")]
    [InlineData("Data.Value.Convert", "Input")]
    public async Task ExecuteAsync_OversizedFileData_FailsBeforeMaterializingInput(string typeKey, string portTitle)
    {
        var path = Path.Combine(Path.GetTempPath(), $"node-large-{Guid.NewGuid():N}.bin");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew))
            {
                file.SetLength(64L * 1024 * 1024 + 1);
            }
            var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
            node.Input.Single(port => port.Title == portTitle).InitialValue = path;

            var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
                TestContext.Current.CancellationToken);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Contains("in-memory node data limit", node.ExecutionError);
            Assert.All(node.Output, port => Assert.Null(port.Data));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DecompressionBomb_FailsAtBoundedOutput()
    {
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var chunk = new byte[64 * 1024];
            for (var i = 0; i <= 1024; i++)
            {
                await gzip.WriteAsync(chunk, TestContext.Current.CancellationToken);
            }
        }
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor =>
            descriptor.TypeKey == "Data.Compression.Decompress").Factory();
        node.Input.Single(port => port.Title == "Data").InitialValue = compressed.ToArray();

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("in-memory node limit", node.ExecutionError);
        Assert.Null(node.Output.Single().Data);
    }

    [Theory]
    [InlineData("Data.Cipher.Encrypt", "unknown-cipher")]
    [InlineData("Data.Cipher.Decrypt", "unknown-cipher")]
    [InlineData("Data.Compression.Compress", "unknown-compression")]
    [InlineData("Data.Compression.Decompress", "unknown-compression")]
    public async Task ExecuteAsync_UnsupportedAlgorithm_FailsWithoutOutput(string typeKey, string algorithm)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
        node.Input.Single(port => port.Title == "Data").InitialValue = new byte[] { 1, 2, 3 };
        node.Input.Single(port => port.Title == "Algorithm").InitialValue = algorithm;
        if (typeKey.StartsWith("Data.Cipher.", StringComparison.Ordinal))
        {
            node.Input.Single(port => port.Title == "Key").InitialValue = new byte[] { 4 };
        }

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.Null(node.Output.Single().Data);
    }

    [Theory]
    [InlineData("Data.Cipher.Encrypt")]
    [InlineData("Data.Cipher.Decrypt")]
    public async Task ExecuteAsync_MissingCipherKey_FailsWithoutOutput(string typeKey)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
        node.Input.Single(port => port.Title == "Data").InitialValue = new byte[] { 1, 2, 3 };

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Null(node.Output.Single().Data);
    }

    [Fact]
    public async Task ExecuteAsync_EncryptThenDecryptWithExplicitKey_RestoresInput()
    {
        var encrypt = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Data.Cipher.Encrypt").Factory();
        var decrypt = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Data.Cipher.Decrypt").Factory();
        var plain = new byte[] { 1, 2, 3, 4, 5 };
        encrypt.Input.Single(port => port.Title == "Data").InitialValue = plain;
        encrypt.Input.Single(port => port.Title == "Key").InitialValue = new byte[] { 6, 7, 8 };
        decrypt.Input.Single(port => port.Title == "Key").InitialValue = new byte[] { 6, 7, 8 };

        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([encrypt, decrypt],
            [new NodeConnection { SourceInstanceId = encrypt.InstanceId, SourceConnector = "Cipher",
                TargetInstanceId = decrypt.InstanceId, TargetConnector = "Data" }],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(plain, Assert.IsType<byte[]>(decrypt.Output.Single().Data));
    }

    [Theory]
    [InlineData("Device.Partition.Flash")]
    [InlineData("Device.Partition.Erase")]
    [InlineData("Device.Power.Reboot")]
    [InlineData("Device.Storage.EraseAll")]
    [InlineData("Device.Ota.Sideload")]
    [InlineData("Firmware.Container.Unpack")]
    [InlineData("Firmware.Container.Pack")]
    [InlineData("Firmware.Container.Extract")]
    [InlineData("Firmware.Cpio.Unpack")]
    [InlineData("Firmware.Cpio.Pack")]
    [InlineData("Image.Super.Unpack")]
    [InlineData("Image.Super.Pack")]
    [InlineData("Image.Sparse.Convert")]
    [InlineData("Build.Dat.Convert")]
    [InlineData("Build.Ota.CreateDelta")]
    public async Task ExecuteAsync_MissingOperationInputs_FailsAndBlocksDependent(string typeKey)
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();
        var dependent = new EffectNode();
        var source = node.Output.First();
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node, dependent],
            [new NodeConnection { SourceInstanceId = node.InstanceId, SourceConnector = source.Title,
                TargetInstanceId = dependent.InstanceId, TargetConnector = "Input" }], TestContext.Current.CancellationToken,
            new NodeExecutionOptions { Interactions = ConsentingInteractionHost.Instance });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.Equal(NodeExecutionStatus.Skipped, dependent.ExecutionStatus);
        Assert.False(dependent.Executed);
    }

    [Fact]
    public async Task ExecuteAsync_DestructiveNodeWithoutInteractiveHost_RejectedBeforeAnyEffect()
    {
        var node = NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == "Device.Partition.Flash").Factory();
        var result = await new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.NotEqual(NodeExecutionStatus.Failed, node.ExecutionStatus);
        Assert.Contains("destructive", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Destructive device graphs require explicit consent at preflight; this host grants it without UI.</summary>
    private sealed class ConsentingInteractionHost : IWorkflowInteractionService
    {
        internal static readonly ConsentingInteractionHost Instance = new();
        public bool IsSupported => true;
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InteractionResult { Outcome = InteractionOutcome.Submitted });
    }

    private sealed class EffectNode : FlowNodeViewModel
    {
        internal bool Executed { get; private set; }

        internal EffectNode() : base("Dependent effect", NodeCategory.Output)
        {
            Input.Add(new NodeConnectorViewModel { Title = "Input" });
        }

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            Executed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}