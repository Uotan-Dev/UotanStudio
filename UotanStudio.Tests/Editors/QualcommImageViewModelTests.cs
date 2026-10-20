using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Editors;

[Collection("Workspace state")]
public sealed class QualcommImageViewModelTests
{
    [Theory]
    [InlineData("renamed.txt")]
    [InlineData("program.bin")]
    [InlineData("program")]
    public void ResolveMode_ValidatedQualcommElf_RoutesToElfPanel(string name)
    {
        using var temp = new TempDirectory();
        var data = QualcommMetadataFixture.CreateElf(new byte[16], QualcommMetadataFixture.CreateMbn(6, 0, 120));
        var path = temp.WriteBytes(name, data);

        Assert.Equal(EditorOpenMode.Elf, FileOpenMode.ResolveMode(path, data[..64]));
        Assert.Equal(EditorOpenMode.Elf, FileOpenMode.ResolveMode("missing.elf", data));
    }

    [Fact]
    public void ResolveMode_HashFlagWithoutValidMbn_DoesNotClaimQualcomm()
    {
        using var temp = new TempDirectory();
        var data = QualcommMetadataFixture.CreateElf(new byte[16], new byte[64]);
        var path = temp.WriteBytes("fake.elf", data);

        Assert.Equal(EditorOpenMode.Elf, FileOpenMode.ResolveMode(path, data));
    }

    [Theory]
    [InlineData("libskel.so")]
    [InlineData("skel")]
    public void ResolveMode_HashSegmentInSharedObject_RoutesToElfPanel(string name)
    {
        using var temp = new TempDirectory();
        var data = QualcommMetadataFixture.CreateElf(new byte[16],
            QualcommMetadataFixture.CreateMbn(6, 0, 120), withDynamicSegment: true);
        var path = temp.WriteBytes(name, data);

        Assert.Equal(EditorOpenMode.Elf, FileOpenMode.ResolveMode(path, data[..64]));
    }

    [Theory]
    [InlineData(EditorOpenMode.Text)]
    [InlineData(EditorOpenMode.Hex)]
    public async Task OpenDocumentAsync_QualcommElfExplicitMode_PreservesUserChoice(EditorOpenMode mode)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sample.elf",
            QualcommMetadataFixture.CreateElf(new byte[16], QualcommMetadataFixture.CreateMbn(6, 0, 120)));
        using var workspace = new WorkspaceViewModel(new TestFactory());

        await workspace.OpenDocumentAsync(path, mode);

        var active = workspace.FindDocumentsPanePublic()!.ActiveDockable;
        if (mode == EditorOpenMode.Text)
        {
            Assert.Equal(EditorOpenMode.Text, Assert.IsType<FileViewModel>(active).OpenMode);
        }
        else
        {
            Assert.IsType<HexEditorViewModel>(active);
        }
    }

    [Fact]
    public async Task ExportAsync_MetadataReport_ContainsTypedValuesAndProvenance()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("metadata.mbn", QualcommMetadataFixture.CreateMbn(6, 120, 120));
        var document = new QualcommImageViewModel();
        try
        {
            await document.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.True(document.HasResults);
            Assert.NotEmpty(document.MetadataRows);
            var target = Path.Combine(temp.Path, "report.json");
            await document.ExportAsync(target, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

            var identities = json.RootElement.GetProperty("Analysis").GetProperty("SigningMetadata");
            Assert.Equal(2, identities.GetArrayLength());
            Assert.Equal("QTI", identities[0].GetProperty("Authority").GetString());
            Assert.Equal("OEM", identities[1].GetProperty("Authority").GetString());
            Assert.Equal(0u, identities[1].GetProperty("OemId").GetUInt32());
            Assert.Equal(3u, identities[1].GetProperty("AntiRollbackVersion").GetUInt32());
            Assert.False(identities[1].GetProperty("FromCertificate").GetBoolean());
            Assert.Contains(json.RootElement.GetProperty("Metadata").EnumerateArray(),
                value => value.GetProperty("Name").GetString() == "OemId");

            document.TrustedRootPath = "changed-root.der";
            Assert.Empty(document.MetadataRows);
            Assert.False(document.HasResults);
        }
        finally
        {
            document.Cleanup();
        }
    }

    [Fact]
    public async Task LoadAsync_TrustChange_RevokesEarlierResultAndReanalyzes()
    {
        var analyzer = new RecordingAnalyzer();
        var document = new QualcommImageViewModel(analyzer);
        try
        {
            await document.LoadAsync("sample.mbn", TestContext.Current.CancellationToken);
            Assert.True(document.HasResults);
            Assert.Single(document.OverviewRows);
            Assert.Null(analyzer.LastTrustedRoot);

            document.TrustedRootPath = "trusted-root.pem";
            Assert.False(document.HasResults);
            Assert.Empty(document.VerificationRows);

            await document.LoadAsync("sample.mbn", TestContext.Current.CancellationToken);
            Assert.True(document.HasResults);
            Assert.Equal("trusted-root.pem", analyzer.LastTrustedRoot);
            Assert.Equal(2, analyzer.CallCount);
        }
        finally
        {
            document.Cleanup();
        }
    }

    [Fact]
    public async Task LoadAsync_CanceledOrClosed_DoesNotPublishStaleResults()
    {
        var analyzer = new PendingAnalyzer();
        var document = new QualcommImageViewModel(analyzer);
        var loading = document.LoadAsync("first.mbn", TestContext.Current.CancellationToken);
        await analyzer.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        document.TrustedRootPath = "other-root.pem";
        await loading;
        Assert.False(document.HasResults);
        Assert.False(document.IsBusy);

        var second = document.LoadAsync("second.mbn", TestContext.Current.CancellationToken);
        await analyzer.SecondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        document.Cleanup();
        await second;
        Assert.False(document.HasResults);
    }

    [Fact]
    public async Task ExportAsync_CanceledBeforePublish_PreservesPreviousFile()
    {
        using var temp = new TempDirectory();
        var target = temp.WriteBytes("report.json", [4, 3, 2, 1]);
        var document = new QualcommImageViewModel(new RecordingAnalyzer());
        try
        {
            await document.LoadAsync("sample.elf", TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.ExportAsync(target, cancellation.Token));

            Assert.Equal(new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
            await document.ExportAsync(target, TestContext.Current.CancellationToken);
            Assert.Contains("Overview", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            document.Cleanup();
        }
    }

    [Fact]
    public async Task OpenDocumentAsync_ExplicitQualcommMode_PersistsAndRestoresOneDocument()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("unrecognized.bin", [0, 1, 2, 3]);
        using var workspace = new WorkspaceViewModel(new TestFactory());
        QualcommImageViewModel? document = null;
        try
        {
            Assert.NotEqual(EditorOpenMode.Qualcomm, FileOpenMode.ResolveMode(path, new byte[] { 0, 1, 2, 3 }));

            await workspace.OpenDocumentAsync(path, EditorOpenMode.Qualcomm);
            var dock = workspace.FindDocumentsPanePublic()!;
            document = Assert.IsType<QualcommImageViewModel>(dock.ActiveDockable);
            Assert.NotNull(ViewModelViewRegistry.Resolve(document.GetType()));
            Assert.True(WorkspaceSessionCapture.IsDocument(document));
            var snapshot = Assert.Single(WorkspaceSessionCapture.Documents(workspace.Layout, temp.Path));
            Assert.Equal("Qualcomm", snapshot.Kind);
            Assert.Equal("unrecognized.bin", snapshot.RelativePath);

            await workspace.RestoreOpenDocumentAsync(snapshot, temp.Path);
            Assert.Same(document, dock.ActiveDockable);
            Assert.Single(dock.VisibleDockables!);
        }
        finally
        {
            document?.Cleanup();
        }
    }

    [Fact]
    public async Task AnalyzeAsync_UnrecognizedImage_DoesNotClaimDeviceAcceptanceOrTrust()
    {
        using var temp = new TempDirectory();
        var image = temp.WriteBytes("unknown.bin", [1, 2, 3, 4]);
        var analyzer = new QualcommWorkspaceAnalyzer();

        var report = await analyzer.AnalyzeAsync(image, null, TestContext.Current.CancellationToken);

        Assert.Equal("UnrecognizedStatus", report.StatusKey);
        Assert.Contains(report.Overview, field => field.Name == "Recognized" && field.Value == "Unrecognized");
        Assert.Contains(report.Verification, field => field.Name == "Device" && field.Value == "NotChecked");
        Assert.Contains(report.Verification, field => field.Name == "Trust" && field.Value == "NotChecked");
    }

    [Fact]
    public async Task AnalyzeAsync_ExplicitPemRoot_DoesNotTurnUnrecognizedInputIntoTrustedImage()
    {
        using var temp = new TempDirectory();
        var image = temp.WriteBytes("unknown.bin", [1, 2, 3, 4]);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Root", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var root = Path.Combine(temp.Path, "root.pem");
        await File.WriteAllTextAsync(root, certificate.ExportCertificatePem(), TestContext.Current.CancellationToken);

        var report = await new QualcommWorkspaceAnalyzer().AnalyzeAsync(
            image, root, TestContext.Current.CancellationToken);

        Assert.Equal("UnrecognizedStatus", report.StatusKey);
        Assert.Contains(report.Verification, field => field.Name == "Trust" && field.Value == "NotChecked");
    }

    [Fact]
    public async Task AnalyzeAsync_OversizedRoot_RejectsBeforeAnalyzing()
    {
        using var temp = new TempDirectory();
        var image = temp.WriteBytes("unknown.bin", [1, 2, 3, 4]);
        var root = temp.WriteBytes("root.der", new byte[1024 * 1024 + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new QualcommWorkspaceAnalyzer().AnalyzeAsync(
            image, root, TestContext.Current.CancellationToken));
    }

    private sealed class RecordingAnalyzer : IQualcommWorkspaceAnalyzer
    {
        public int CallCount { get; private set; }
        public string? LastTrustedRoot { get; private set; }

        public Task<QualcommWorkspaceReport> AnalyzeAsync(string imagePath, string? trustedRootPath, CancellationToken cancellationToken)
        {
            CallCount++;
            LastTrustedRoot = trustedRootPath;
            return Task.FromResult(new QualcommWorkspaceReport("Unverified",
                [new QualcommReportField("Recognized", "MBN", LocalizedName: true)],
                [],
                [new QualcommReportField("Trust", trustedRootPath is null ? "NotChecked" : "Unknown", true, true)],
                []));
        }
    }

    private sealed class PendingAnalyzer : IQualcommWorkspaceAnalyzer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public async Task<QualcommWorkspaceReport> AnalyzeAsync(string imagePath, string? trustedRootPath, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                Started.TrySetResult();
            }
            else
            {
                SecondStarted.TrySetResult();
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class TestFactory : Dock.Model.Mvvm.Factory
    {
        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock { Id = "DocumentsPane", VisibleDockables = CreateList<IDockable>() };
            return new RootDock { VisibleDockables = CreateList<IDockable>(documents), ActiveDockable = documents };
        }
    }
}