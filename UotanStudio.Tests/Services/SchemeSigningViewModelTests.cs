using System.Security.Cryptography;
using UotanStudio.Projects;
using UotanStudio.Projects.Schemes;
using UotanStudio.Services.Keys;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Workflow.Execution;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Scheme editor and operator signature and asset behavior: signing a saved scheme, importing project assets,
/// rejecting tampered files, confirming unsigned runs, pinning unknown publishers, blocking rotated fingerprints
/// and materializing assets before a run.
/// <para>Scheme 编辑器与操作员的签名与资产行为：为已保存 Scheme 签名、导入工程资产、拒绝篡改文件、
/// 确认未签名运行、固定未知发布者、阻止已轮换指纹，以及运行前物化资产。</para>
/// </summary>
public sealed class SchemeSigningViewModelTests : IDisposable
{
    private const string PublisherId = "scheme.publisher";
    private const string PublisherName = "Scheme Publisher";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task EditorSignAsync_WithPublisherKey_PersistsSignatureAndDetachedCopy()
    {
        var token = TestContext.Current.CancellationToken;
        var stateDirectory = Path.Combine(_temp.Path, "project", ".uotan");
        Directory.CreateDirectory(stateDirectory);
        var publisherKey = NewManagedKey();
        var editor = CreateEditor(stateDirectory, publisherKey);
        editor.Name = "Editor signed";
        editor.Author = "author";
        var stage = editor.AddStage();
        stage.Id = "apply";
        stage.Embed(new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo" }]
        }, "workflow-a");
        var path = Path.Combine(_temp.Path, "editor-signed.uotanscheme");
        Assert.True(await editor.SaveAsync(path, token));
        Assert.False(editor.IsSigned);

        Assert.True(await editor.SignAsync(token));

        Assert.True(editor.IsSigned);
        Assert.True(File.Exists(SchemeFile.GetSignaturePath(path)));
        var loaded = Assert.IsType<SchemePackage>(await SchemeFile.LoadVerifiedAsync(path, token));
        Assert.True(loaded.IsSigned);
        Assert.Equal(publisherKey.Id, loaded.Publisher!.Id);
        Assert.Equal(PublisherName, loaded.Publisher.Name);
        Assert.Equal(PluginSignatureAlgorithms.RsaPssSha256, loaded.Signature!.Algorithm);
    }

    [Fact]
    public async Task EditorSaveAsync_ContentChangedAfterSigning_DropsTheStaleSignature()
    {
        var token = TestContext.Current.CancellationToken;
        var stateDirectory = Path.Combine(_temp.Path, "project", ".uotan");
        var editor = CreateEditor(stateDirectory, NewManagedKey());
        editor.Name = "Signed then edited";
        var stage = editor.AddStage();
        stage.Id = "apply";
        stage.Embed(new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo" }]
        }, "workflow-a");
        var path = Path.Combine(_temp.Path, "editor-resign.uotanscheme");
        Assert.True(await editor.SaveAsync(path, token));
        Assert.True(await editor.SignAsync(token));
        Assert.True(editor.IsSigned);

        editor.Name = "Edited after signing";

        Assert.True(await editor.SaveAsync(path, token));

        Assert.False(editor.IsSigned);
        var loaded = Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, token));
        Assert.False(loaded.IsSigned);
        Assert.Null(loaded.Signature);
        Assert.False(File.Exists(SchemeFile.GetSignaturePath(path)));
    }

    [Fact]
    public async Task EditorSignAsync_WithoutSavedFile_ReportsFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var editor = CreateEditor(Path.Combine(_temp.Path, ".uotan"), NewManagedKey());

        Assert.False(await editor.SignAsync(token));
        Assert.True(editor.HasError);
    }

    [Fact]
    public async Task EditorImportAssetAsync_CopiesIntoProjectStateAndStoresRelativePath()
    {
        var token = TestContext.Current.CancellationToken;
        var projectRoot = Path.Combine(_temp.Path, "project");
        var stateDirectory = Path.Combine(projectRoot, ".uotan");
        Directory.CreateDirectory(projectRoot);
        var editor = CreateEditor(stateDirectory, NewManagedKey());
        editor.Name = "Asset scheme";
        var path = Path.Combine(projectRoot, "asset-scheme.uotanscheme");
        Assert.True(await editor.SaveAsync(path, token));
        var source = Path.Combine(_temp.Path, "picked.bin");
        var content = "picked bytes"u8.ToArray();
        await File.WriteAllBytesAsync(source, content, token);

        Assert.True(await editor.ImportAssetAsync(source, token));

        var row = Assert.Single(editor.Assets);
        var asset = row.ToAsset();
        var expectedFile = Path.Combine(stateDirectory, "scheme-assets", editor.Id, row.Id, "picked.bin");
        Assert.True(File.Exists(expectedFile));
        Assert.Equal(".uotan/scheme-assets/" + editor.Id + "/" + row.Id + "/picked.bin", asset.RelativePath);
        Assert.Equal((long)content.Length, asset.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), asset.Sha256);
        Assert.False(editor.HasError);
    }

    [Fact]
    public async Task LoadAsync_TamperedSignedScheme_IsRejected()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var path = await WriteSignedSchemeAsync(key, "tampered.uotanscheme");
        var text = await File.ReadAllTextAsync(path, token);
        await File.WriteAllTextAsync(path, text.Replace("Signed scheme", "Tampered scheme", StringComparison.Ordinal),
            token);
        var viewModel = CreateOperator();

        Assert.False(await viewModel.LoadAsync(path, token));

        Assert.False(viewModel.HasScheme);
        Assert.True(viewModel.HasError);
        Assert.True(!string.IsNullOrEmpty(viewModel.ErrorMessage));
    }

    [Fact]
    public async Task LoadAsync_UnknownPublisher_ShowsFingerprintAndOffersTrust()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var path = await WriteSignedSchemeAsync(key, "unknown.uotanscheme");
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));
        var viewModel = CreateOperator(trustStore: store);

        Assert.True(await viewModel.LoadAsync(path, token));

        Assert.True(viewModel.IsSigned);
        Assert.Equal(PublisherTrustStatus.UnknownPublisher, viewModel.SignatureStatus);
        Assert.True(viewModel.HasSignatureWarning);
        Assert.True(viewModel.ShowTrustPublisher);
        Assert.Contains(PublisherId, viewModel.PublisherText, StringComparison.Ordinal);
        Assert.StartsWith("SHA256:", viewModel.FingerprintText, StringComparison.Ordinal);
        Assert.NotNull(viewModel.SignatureText);
    }

    [Fact]
    public async Task ConfirmAndTrustPublisherAsync_PinsTheUnknownPublisher()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var path = await WriteSignedSchemeAsync(key, "trust.uotanscheme");
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));
        var viewModel = CreateOperator(trustStore: store, confirm: (_, _, _) => Task.FromResult(true));
        Assert.True(await viewModel.LoadAsync(path, token));

        Assert.True(await viewModel.ConfirmAndTrustPublisherAsync());

        Assert.Equal(PublisherTrustStatus.Trusted, viewModel.SignatureStatus);
        var pin = Assert.IsType<PublisherTrustRecord>(store.Find(PublisherId));
        Assert.Equal(viewModel.FingerprintText, pin.Fingerprint);
    }

    [Fact]
    public async Task RunAsync_UnknownPublisherDeclined_DoesNotStartTheRunner()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var path = await WriteSignedSchemeAsync(key, "declined.uotanscheme");
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));
        var runs = 0;
        var viewModel = CreateOperator((_, _, _) =>
        {
            runs++;
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        }, store, (_, _, _) => Task.FromResult(false));
        Assert.True(await viewModel.LoadAsync(path, token));

        Assert.False(await viewModel.RunAsync(token));

        Assert.Equal(0, runs);
        Assert.True(viewModel.HasError);
        Assert.Equal(PublisherTrustStatus.UnknownPublisher, viewModel.SignatureStatus);
    }

    [Fact]
    public async Task RunAsync_UnknownPublisherConfirmed_PinsTrustAndStartsTheRunner()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var path = await WriteSignedSchemeAsync(key, "confirmed.uotanscheme");
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));
        var runs = 0;
        var viewModel = CreateOperator((_, _, _) =>
        {
            runs++;
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        }, store, (_, _, _) => Task.FromResult(true));
        Assert.True(await viewModel.LoadAsync(path, token));

        Assert.True(await viewModel.RunAsync(token));

        Assert.Equal(1, runs);
        Assert.Equal(PublisherTrustStatus.Trusted, viewModel.SignatureStatus);
        Assert.NotNull(store.Find(PublisherId));
    }

    [Fact]
    public async Task RunAsync_RotatedFingerprint_IsBlockedBeforeTheRunner()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));
        using var firstKey = RSA.Create(2048);
        var firstPath = await WriteSignedSchemeAsync(firstKey, "first.uotanscheme");
        var first = CreateOperator(trustStore: store, confirm: (_, _, _) => Task.FromResult(true));
        Assert.True(await first.LoadAsync(firstPath, token));
        Assert.True(await first.ConfirmAndTrustPublisherAsync());
        Assert.Equal(PublisherTrustStatus.Trusted, first.SignatureStatus);

        using var rotatedKey = RSA.Create(2048);
        var rotatedPath = await WriteSignedSchemeAsync(rotatedKey, "rotated.uotanscheme");
        var runs = 0;
        var prompted = false;
        var rotated = CreateOperator((_, _, _) =>
        {
            runs++;
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        }, store, (_, _, _) =>
        {
            prompted = true;
            return Task.FromResult(true);
        });
        Assert.True(await rotated.LoadAsync(rotatedPath, token));
        Assert.Equal(PublisherTrustStatus.FingerprintMismatch, rotated.SignatureStatus);

        Assert.False(await rotated.RunAsync(token));

        Assert.Equal(0, runs);
        Assert.False(prompted);
        Assert.True(rotated.HasError);
    }

    [Fact]
    public async Task RunAsync_UnsignedDeclined_DoesNotStartTheRunner()
    {
        var token = TestContext.Current.CancellationToken;
        var package = CreateUnsignedPackage();
        var path = Path.Combine(_temp.Path, "unsigned.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        var runs = 0;
        var viewModel = CreateOperator((_, _, _) =>
        {
            runs++;
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        }, confirm: (_, _, _) => Task.FromResult(false));
        Assert.True(await viewModel.LoadAsync(path, token));
        Assert.Equal(PublisherTrustStatus.Unsigned, viewModel.SignatureStatus);

        Assert.False(await viewModel.RunAsync(token));

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task RunAsync_UnsignedWithoutPrompt_StartsTheRunner()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_temp.Path, "unsigned-run.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, CreateUnsignedPackage(), token));
        var runs = 0;
        var viewModel = CreateOperator((_, _, _) =>
        {
            runs++;
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        });
        Assert.True(await viewModel.LoadAsync(path, token));

        Assert.True(await viewModel.RunAsync(token));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task LoadAsync_ValidAsset_ShowsStatusAndMaterializesBeforeTheRun()
    {
        var token = TestContext.Current.CancellationToken;
        var projectRoot = Path.Combine(_temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var content = "asset payload"u8.ToArray();
        var assetPath = Path.Combine(projectRoot, "payload.bin");
        await File.WriteAllBytesAsync(assetPath, content, token);
        var asset = new SchemeAsset
        {
            Id = "payload",
            Name = "Payload",
            RelativePath = "payload.bin",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Length = content.Length
        };
        var package = CreateUnsignedPackage();
        package.Assets = [asset];
        var path = Path.Combine(projectRoot, "asset-run.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        var runsParent = Path.Combine(_temp.Path, "runs");
        var materializedPath = string.Empty;
        var existedDuringRun = false;
        var viewModel = CreateOperator((_, options, _) =>
        {
            materializedPath = Assert.IsType<string>(options.AssetPaths["payload"]);
            existedDuringRun = File.Exists(materializedPath);
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        }, projectRoot: () => projectRoot, temporaryDirectory: () => runsParent);

        Assert.True(await viewModel.LoadAsync(path, token));

        var status = Assert.Single(viewModel.Assets);
        Assert.Equal(SchemeAssetState.Valid, status.State);
        Assert.True(status.IsValid);

        Assert.True(await viewModel.RunAsync(token));

        Assert.True(existedDuringRun);
        Assert.StartsWith(Path.GetFullPath(runsParent), materializedPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(materializedPath));
        Assert.True(!Directory.Exists(runsParent) || Directory.GetDirectories(runsParent).Length == 0);
    }

    [Fact]
    public async Task RunAsync_MissingAsset_FailsBeforeTheRunner()
    {
        var token = TestContext.Current.CancellationToken;
        var projectRoot = Path.Combine(_temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var package = CreateUnsignedPackage();
        package.Assets =
        [
            new SchemeAsset
            {
                Id = "payload",
                Name = "Payload",
                RelativePath = "absent.bin",
                Length = 8
            }
        ];
        var path = Path.Combine(projectRoot, "missing-asset.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        var runs = 0;
        var viewModel = CreateOperator((_, _, _) =>
        {
            runs++;
            return Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded });
        }, projectRoot: () => projectRoot,
            temporaryDirectory: () => Path.Combine(_temp.Path, "runs"));

        Assert.True(await viewModel.LoadAsync(path, token));

        Assert.Equal(SchemeAssetState.Missing, Assert.Single(viewModel.Assets).State);
        Assert.False(await viewModel.RunAsync(token));
        Assert.Equal(0, runs);
        Assert.True(viewModel.HasError);
    }

    private SchemeEditorViewModel CreateEditor(string stateDirectory, ManagedKey publisherKey) =>
        new(
            () => ["workflow-a"],
            (_, _) => Task.FromResult<WorkflowSnapshot?>(new WorkflowSnapshot
            {
                Nodes = [new NodeDto { InstanceId = "embedded", TypeKey = "Test.Scheme.Echo" }]
            }),
            _ => null,
            () => [publisherKey],
            _ => RSA.Create(2048),
            () => stateDirectory);

    private SchemeOperatorViewModel CreateOperator(
        Func<SchemePackage, SchemeRunOptions, CancellationToken, Task<SchemeRunResult>>? runner = null,
        PublisherTrustStore? trustStore = null,
        Func<string, string, string, Task<bool>>? confirm = null,
        Func<string?>? projectRoot = null,
        Func<string?>? temporaryDirectory = null) =>
        new(runner ?? ((_, _, _) => Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded })),
            (_, _) => Task.FromResult<WorkflowSnapshot?>(null),
            trustStore,
            confirm,
            projectRoot ?? new Func<string?>(() => null),
            temporaryDirectory ?? new Func<string?>(() => null));

    private async Task<string> WriteSignedSchemeAsync(RSA key, string fileName)
    {
        var package = CreateUnsignedPackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);
        var path = Path.Combine(_temp.Path, fileName);
        Assert.True(await SchemeFile.SaveAsync(path, package, TestContext.Current.CancellationToken));
        return path;
    }

    private static SchemePackage CreateUnsignedPackage() => new()
    {
        Id = "signed",
        Name = "Signed scheme",
        Stages =
        [
            new SchemeStage
            {
                Id = "only",
                Kind = SchemeStageKind.Generic,
                Graph = new WorkflowSnapshot
                {
                    Nodes = [new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo" }]
                }
            }
        ]
    };

    private static ManagedKey NewManagedKey() => new(Guid.NewGuid().ToString("N"), PublisherName,
        ManagedKeyKind.Publisher, 2048, "SHA256:" + new string('A', 64), DateTimeOffset.UtcNow);
}