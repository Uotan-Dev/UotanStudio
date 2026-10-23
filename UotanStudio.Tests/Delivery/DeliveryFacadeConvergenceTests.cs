using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using UotanStudio.Delivery;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryFacadeConvergenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-facade-" + Guid.NewGuid().ToString("N"));

    public DeliveryFacadeConvergenceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task BuildAsync_SignedPayloadPlanWithKeyProvider_PublishesVerifiableSignature()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "keys"));
        var key = library.Generate("facade-key", ManagedKeyKind.Payload, 2048);
        var plan = PayloadPlan(library, Path.Combine(_root, "signed-out"), key);

        var outputs = await new DeliveryService(new NoTools(), new TestSigningKeys())
            .BuildAsync(Serialize(plan), null, null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(outputs);
        using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "ota.zip"));
        var entry = archive.GetEntry("payload.bin") ?? throw new FileNotFoundException("payload.bin is missing.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, TestContext.Current.CancellationToken);
        var (metadata, _, metadataSignature, _) = DecodePayload(memory.ToArray());
        using var rsa = KeyLibraryService.LoadPrivateKey(library.PrivatePath(key));
        Assert.True(rsa.VerifyData(metadata, metadataSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
    }

    [Fact]
    public async Task BuildAsync_SignedPayloadPlanWithoutKeyProvider_FailsHonestly()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "no-provider-keys"));
        var key = library.Generate("unsigned-facade", ManagedKeyKind.Payload, 2048);
        var plan = PayloadPlan(library, Path.Combine(_root, "unsigned-out"), key);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new DeliveryService(new NoTools())
            .BuildAsync(Serialize(plan), null, null, TestContext.Current.CancellationToken));

        Assert.Contains("signing-key provider", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(plan.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
    }

    [Fact]
    public void BuildProgressMapper_RoundTripsEveryProgressField()
    {
        var metadata = new Dictionary<string, string> { ["EquivalenceKey"] = "value" };
        var internalProgress = new DeliveryProgress(42.5, "stage text", (1024, 4096), "Delivery_Stage", (3, 9),
            "detail", metadata);

        var mapped = internalProgress.ToBuildProgress();
        var restored = mapped.ToDeliveryProgress();

        Assert.Equal(internalProgress.Percent, mapped.Percent);
        Assert.Equal(internalProgress.Message, mapped.Message);
        Assert.Equal(internalProgress.Stage, mapped.Stage);
        Assert.Equal(internalProgress.Detail, mapped.Detail);
        Assert.Equal(1024, mapped.ProcessedBytes);
        Assert.Equal(4096, mapped.TotalBytes);
        Assert.Equal(3, mapped.ProcessedItems);
        Assert.Equal(9, mapped.TotalItems);
        Assert.Equal(metadata, mapped.Metadata);
        Assert.Equal(internalProgress, restored);
    }

    [Fact]
    public void BuildProgressMapper_EmptyMetersStayAbsent()
    {
        var mapped = new DeliveryProgress(0, "text").ToBuildProgress();

        Assert.Null(mapped.ProcessedBytes);
        Assert.Null(mapped.TotalBytes);
        Assert.Null(mapped.ProcessedItems);
        Assert.Null(mapped.TotalItems);
        Assert.Null(mapped.Stage);
        Assert.Null(mapped.Metadata);
        Assert.Equal(new DeliveryProgress(0, "text"), mapped.ToDeliveryProgress());
    }

    [Fact]
    public async Task BuildAsync_ProgressSink_ReceivesOrderedStagedFrames()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "progress-keys"));
        var key = library.Generate("progress", ManagedKeyKind.Payload, 2048);
        var plan = PayloadPlan(library, Path.Combine(_root, "progress-out"), key);
        var frames = new List<DeliveryBuildProgress>();

        await new DeliveryService(new NoTools(), new TestSigningKeys())
            .BuildAsync(Serialize(plan), null, new CollectingProgress(frames), TestContext.Current.CancellationToken);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.InRange(frame.Percent, -1d, 100d));
        Assert.Contains(frames, frame => frame.Stage is { Length: > 0 });
        Assert.Equal(100d, frames[^1].Percent);
    }

    [Fact]
    public async Task BuildAsync_PreCancelledToken_CancelsWithoutStagingResidue()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var library = new KeyLibraryService(Path.Combine(_root, "cancel-keys"));
        var key = library.Generate("cancel", ManagedKeyKind.Payload, 2048);
        var plan = PayloadPlan(library, Path.Combine(_root, "cancel-out"), key);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeliveryService(new NoTools(), new TestSigningKeys())
            .BuildAsync(Serialize(plan), null, null, cancellation.Token));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
    }

    [Fact]
    public void ListOutputs_FacadeMatchesInternalPlannedAndExistingPaths()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "listing-keys"));
        var key = library.Generate("listing", ManagedKeyKind.Payload, 2048);
        var plan = PayloadPlan(library, Path.Combine(_root, "listing-out"), key);
        Directory.CreateDirectory(plan.OutputDirectory);
        File.WriteAllText(Path.Combine(plan.OutputDirectory, "ota.zip"), "present");

        var listed = DeliveryService.ListOutputs(Serialize(plan));

        Assert.Equal(DeliveryOutputService.PlannedOutputPaths(plan), listed.Planned);
        Assert.Equal(DeliveryOutputService.FindExistingOutputs(plan), listed.Existing);
        Assert.Contains(Path.Combine(plan.OutputDirectory, "ota.zip"), listed.Existing, StringComparer.Ordinal);
    }

    [Fact]
    public void FacadeSerialization_RoundTripsEveryPlanField()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "roundtrip-keys"));
        var key = library.Generate("roundtrip", ManagedKeyKind.Payload, 2048);
        var plan = PayloadPlan(library, Path.Combine(_root, "roundtrip-out"), key);

        var restored = JsonSerializer.Deserialize(Serialize(plan), DeliveryJsonContext.Default.DeliveryPlan);

        Assert.NotNull(restored);
        Assert.Equal(plan.Version, restored.Version);
        Assert.Equal(plan.OutputDirectory, restored.OutputDirectory);
        Assert.Equal(plan.PackPayload, restored.PackPayload);
        Assert.Equal(plan.OrganizeDirectory, restored.OrganizeDirectory);
        Assert.Equal(plan.ContainerConfirmed, restored.ContainerConfirmed);
        Assert.Equal(plan.Txt, restored.Txt);
        Assert.Equal(plan.Payload.SigningKeyPath, restored.Payload.SigningKeyPath);
        Assert.Equal(plan.Payload.SigningKeyFingerprint, restored.Payload.SigningKeyFingerprint);
        Assert.Equal(plan.Payload.Version, restored.Payload.Version);
        Assert.Equal(plan.Payload.BlockSize, restored.Payload.BlockSize);
        Assert.Equal(plan.Super.Group, restored.Super.Group);
        Assert.Equal(plan.Super.BlockSize, restored.Super.BlockSize);
        Assert.Equal(plan.Container.Select(static file => file.Partition), restored.Container.Select(static file => file.Partition));
        Assert.Equal(plan.Container.Select(static file => file.SourcePath), restored.Container.Select(static file => file.SourcePath));
        Assert.Equal(plan.Container.Select(static file => file.FileName), restored.Container.Select(static file => file.FileName));
        Assert.Equal(plan.Container.Select(static file => file.SourceLength), restored.Container.Select(static file => file.SourceLength));
        Assert.Equal(plan.Files.Select(static file => file.Partition + file.FileName), restored.Files.Select(static file => file.Partition + file.FileName));
    }

    private static string Serialize(DeliveryPlan plan)
        => JsonSerializer.Serialize(plan, DeliveryJsonContext.Default.DeliveryPlan);

    private DeliveryPlan PayloadPlan(KeyLibraryService library, string outputDirectory, ManagedKey key)
    {
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".img");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(8192));
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Container = [new() { Partition = "system", FileName = "system.img", SourcePath = source, SourceLength = 8192 }],
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            Payload = new()
            {
                SigningKeyPath = library.PrivatePath(key),
                SigningKeyFingerprint = key.Fingerprint
            },
            OutputDirectory = outputDirectory,
            Txt = false
        };
        return plan;
    }

    private static (byte[] Metadata, byte[] Payload, byte[] MetadataSignature, byte[] PayloadSignature) DecodePayload(byte[] bytes)
    {
        Assert.Equal("CrAU"u8.ToArray(), bytes[..4]);
        var metadataSize = checked(24 + (int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(12)));
        var signatureSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(bytes[24..metadataSize]);
        var dataStart = metadataSize + signatureSize;
        var dataEnd = checked(dataStart + (int)manifest.SignaturesOffset);
        Assert.Equal(bytes.Length, checked(dataEnd + (int)manifest.SignaturesSize));
        return (bytes[..metadataSize], [.. bytes[..metadataSize], .. bytes[dataStart..dataEnd]],
            ReadSignature(bytes[metadataSize..dataStart]), ReadSignature(bytes[dataEnd..]));
    }

    private static byte[] ReadSignature(byte[] encoded)
    {
        using var outer = new CodedInputStream(encoded);
        Assert.Equal(10u, outer.ReadTag());
        using var nested = new CodedInputStream(outer.ReadBytes().ToByteArray());
        Assert.Equal(18u, nested.ReadTag());
        var signature = nested.ReadBytes().ToByteArray();
        Assert.Equal(29u, nested.ReadTag());
        Assert.Equal((uint)signature.Length, nested.ReadFixed32());
        Assert.True(nested.IsAtEnd);
        Assert.True(outer.IsAtEnd);
        return signature;
    }

    private sealed class CollectingProgress(List<DeliveryBuildProgress> frames) : IProgress<DeliveryBuildProgress>
    {
        public void Report(DeliveryBuildProgress value) => frames.Add(value);
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress,
            CancellationToken token) => Task.CompletedTask;
    }

    private sealed class TestSigningKeys : IDeliverySigningKeyProvider
    {
        public RSA LoadPrivateKey(string path) => KeyLibraryService.LoadPrivateKey(path);
    }
}