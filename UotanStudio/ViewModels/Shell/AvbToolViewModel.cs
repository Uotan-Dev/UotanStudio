using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Keys;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// AVB tool state: AVB key selection, partition footer signing/verification and A/B OTA patching.
/// <para>AVB 工具状态：AVB 密钥选择、分区尾部签名/校验，以及 A/B OTA 修补。</para>
/// </summary>
public partial class AvbToolViewModel : ViewModelBase
{
    private const int MaximumPublicKeyBlobBytes = 8192;
    private const long AvbBlockSize = 4096;

    private readonly KeyLibraryService _library;
    private readonly AvbOtaService _ota;
    private CancellationTokenSource? _operation;
    private string _defaultFooterOutputPath = "";

    /// <summary>
    /// Creates the tool over the shared key library and a default OTA service.
    /// <para>基于共享密钥库与默认 OTA 服务创建工具。</para>
    /// </summary>
    public AvbToolViewModel() : this(new KeyLibraryService(), new AvbOtaService())
    {
    }

    /// <summary>
    /// Creates the tool over an explicit key library so callers can isolate the library root.
    /// <para>基于显式密钥库创建工具，便于调用方隔离密钥库根目录。</para>
    /// </summary>
    /// <param name="library">The shared key library instance. <para>共享密钥库实例。</para></param>
    public AvbToolViewModel(KeyLibraryService library) : this(library, new AvbOtaService())
    {
    }

    /// <summary>
    /// Creates the tool with both services injected for tests.
    /// <para>供测试注入两个服务以创建工具。</para>
    /// </summary>
    /// <param name="library">The key library instance. <para>密钥库实例。</para></param>
    /// <param name="ota">The OTA patch service. <para>OTA 修补服务。</para></param>
    internal AvbToolViewModel(KeyLibraryService library, AvbOtaService ota)
    {
        _library = library;
        _ota = ota;
    }

    /// <summary>AVB entries from the shared key library. <para>共享密钥库中的 AVB 条目。</para></summary>
    public ObservableCollection<ManagedKey> AvbKeys { get; } = [];

    /// <summary>Payload entries offered to the OTA signing section. <para>供 OTA 签名区选择的 Payload 条目。</para></summary>
    public ObservableCollection<ManagedKey> PayloadKeys { get; } = [];

    /// <summary>ZIP entries offered as the OTA certificate source. <para>作为 OTA 证书来源的 ZIP 条目。</para></summary>
    public ObservableCollection<ManagedKey> ZipKeys { get; } = [];

    /// <summary>Currently selected AVB key. <para>当前选中的 AVB 密钥。</para></summary>
    [ObservableProperty] private ManagedKey? _selectedKey;

    /// <summary>Images offered to the footer section. <para>供尾部区选择的镜像。</para></summary>
    public ObservableCollection<string> Images { get; } = [];

    /// <summary>Currently selected partition image. <para>当前选中的分区镜像。</para></summary>
    [ObservableProperty] private string? _selectedImage;

    /// <summary>Partition name embedded in the AVB descriptor. <para>写入 AVB 描述符的分区名。</para></summary>
    [ObservableProperty] private string _partitionName = "boot";

    /// <summary>Footer mode index: 0 selects hash, 1 selects hashtree. <para>尾部模式索引：0 为哈希，1 为哈希树。</para></summary>
    [ObservableProperty] private int _footerModeIndex;

    /// <summary>Whether newly signed VBMeta skips dm-verity hashtree checks.
    /// <para>新签名的 VBMeta 是否跳过 dm-verity 哈希树检查。</para></summary>
    [ObservableProperty] private bool _disableVerity;

    /// <summary>Whether newly signed VBMeta skips AVB signature verification.
    /// <para>新签名的 VBMeta 是否跳过 AVB 签名校验。</para></summary>
    [ObservableProperty] private bool _disableAvbVerification;

    /// <summary>Rollback index text; blank or non-numeric values are rejected before signing.
    /// <para>回滚索引文本；空白或非数字值在签名前被拒绝。</para></summary>
    [ObservableProperty] private string _rollbackIndex = "0";

    /// <summary>Rollback index location text. <para>回滚索引槽位文本。</para></summary>
    [ObservableProperty] private string _rollbackIndexLocation = "0";

    /// <summary>Exact output capacity in bytes; blank emits the smallest block-aligned image.
    /// <para>精确输出容量（字节）；留空时输出最小的块对齐镜像。</para></summary>
    [ObservableProperty] private string _partitionSize = "";

    /// <summary>Explicit signed output path; blank derives <c>&lt;image&gt;.avb.img</c>.
    /// <para>显式签名输出路径；留空时按 <c>&lt;image&gt;.avb.img</c> 推导。</para></summary>
    [ObservableProperty] private string _footerOutputPath = "";

    /// <summary>Optional libavb public-key blob used to pin the verification signer.
    /// <para>可选的 libavb 公钥数据块，用于固定校验时的签名者。</para></summary>
    [ObservableProperty] private string _trustedPublicKeyPath = "";

    /// <summary>Input OTA package path. <para>输入的 OTA 包路径。</para></summary>
    [ObservableProperty] private string _otaInputPath = "";

    /// <summary>Output path of the patched OTA package. <para>修补后的 OTA 包输出路径。</para></summary>
    [ObservableProperty] private string _otaOutputPath = "";

    /// <summary>Partitions parsed from the current OTA package. <para>从当前 OTA 包解析出的分区。</para></summary>
    public ObservableCollection<AvbOtaPartitionOption> OtaPartitions { get; } = [];

    /// <summary>OTA footer mode index: 0 selects hash, 1 selects hashtree. <para>OTA 尾部模式索引：0 为哈希，1 为哈希树。</para></summary>
    [ObservableProperty] private int _otaFooterModeIndex;

    /// <summary>Whether the patched package disables AVB verification. <para>修补后的包是否禁用 AVB 校验。</para></summary>
    [ObservableProperty] private bool _disableAvb;

    /// <summary>Payload key used for payload signing. <para>用于 Payload 签名的密钥。</para></summary>
    [ObservableProperty] private ManagedKey? _selectedPayloadKey;

    /// <summary>ZIP key whose certificate signs the OTA package. <para>其证书用于签名 OTA 包的 ZIP 密钥。</para></summary>
    [ObservableProperty] private ManagedKey? _selectedZipKey;

    /// <summary>OTA certificate path; auto-filled from the selected ZIP key. <para>OTA 证书路径；由所选 ZIP 密钥自动填入。</para></summary>
    [ObservableProperty] private string _otaCertificatePath = "";

    /// <summary>AVB private key path; auto-filled from the selected AVB key. <para>AVB 私钥路径；由所选 AVB 密钥自动填入。</para></summary>
    [ObservableProperty] private string _otaPrivateKeyPath = "";

    /// <summary>Multi-line OTA result report. <para>多行 OTA 结果报告。</para></summary>
    [ObservableProperty] private string _otaSummary = "";

    /// <summary>Whether an operation is running. <para>是否正在执行操作。</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Last operation status; failures are formatted through <see cref="UserError"/>.
    /// <para>最近一次操作状态；失败信息经 <see cref="UserError"/> 格式化。</para></summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Whether no operation is running. <para>当前没有操作在运行。</para></summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Whether the OTA package lists at least one partition. <para>OTA 包是否已列出至少一个分区。</para></summary>
    public bool HasOtaPartitions => OtaPartitions.Count > 0;

    /// <summary>The path the footer section writes the signed image to. <para>尾部区写入已签名镜像的路径。</para></summary>
    public string SignedOutputPath => !string.IsNullOrWhiteSpace(FooterOutputPath)
        ? FooterOutputPath
        : SelectedImage is { Length: > 0 } image ? image + ".avb.img" : "";

    /// <summary>The path the footer section writes the unsigned image to. <para>尾部区写入未签名镜像的路径。</para></summary>
    public string UnsignedOutputPath => SelectedImage is { Length: > 0 } image ? image + ".unsigned.img" : "";

    partial void OnSelectedImageChanged(string? value)
    {
        var nextDefaultPath = value is { Length: > 0 } image ? image + ".avb.img" : "";
        if (string.IsNullOrWhiteSpace(FooterOutputPath)
            || string.Equals(FooterOutputPath, _defaultFooterOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            FooterOutputPath = nextDefaultPath;
        }

        _defaultFooterOutputPath = nextDefaultPath;
        OnPropertyChanged(nameof(SignedOutputPath));
        OnPropertyChanged(nameof(UnsignedOutputPath));
    }

    partial void OnFooterOutputPathChanged(string value) => OnPropertyChanged(nameof(SignedOutputPath));

    partial void OnSelectedKeyChanged(ManagedKey? value)
    {
        OtaPrivateKeyPath = value is { Kind: ManagedKeyKind.Avb } key ? _library.PrivatePath(key) : "";
    }

    /// <summary>Derives a patched output path next to the chosen OTA package when none is set.
    /// <para>未设置输出路径时，在所选 OTA 包旁边推导修补后的输出路径。</para>
    /// </summary>
    partial void OnOtaInputPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(OtaOutputPath) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(value);
            if (Path.GetDirectoryName(full) is not { } directory)
            {
                return;
            }

            OtaOutputPath = Path.Combine(directory,
                Path.GetFileNameWithoutExtension(full) + ".patched" + Path.GetExtension(full));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            OtaOutputPath = "";
        }
    }

    /// <summary>Fills the OTA certificate path whenever a ZIP key is selected.
    /// <para>选择 ZIP 密钥时填入 OTA 证书路径。</para>
    /// </summary>
    partial void OnSelectedZipKeyChanged(ManagedKey? value)
    {
        if (value is not { Kind: ManagedKeyKind.Zip } key)
        {
            return;
        }

        try
        {
            OtaCertificatePath = _library.EnsureCertificate(key);
        }
        catch (Exception ex)
        {
            Status = UserError.Describe(ex);
        }
    }

    /// <summary>
    /// Re-reads the shared key library and restores the current selections by id.
    /// <para>重新读取共享密钥库，并按 id 恢复当前选择。</para>
    /// </summary>
    /// <returns>A task completed once the collections reflect the library. <para>集合反映密钥库后完成的任务。</para></returns>
    public Task RefreshKeysAsync() => RunAsync(ReloadKeysAsync);

    /// <summary>
    /// Exports the selected AVB key's public-key metadata blob without replacing an existing file.
    /// <para>导出所选 AVB 密钥的公钥元数据块，不替换已有文件。</para>
    /// </summary>
    /// <param name="destination">Target file path, or a directory receiving <c>avb_pkmd.bin</c>.
    /// <para>目标文件路径，或接收 <c>avb_pkmd.bin</c> 的目录。</para></param>
    /// <returns>A task completed once the export finishes. <para>导出完成后结束的任务。</para></returns>
    public Task ExportAvbPublicKeyAsync(string destination) => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        if (SelectedKey is not { Kind: ManagedKeyKind.Avb } key)
        {
            Status = T("Avb_KeyEmpty");
            return;
        }

        var target = await Task.Run(() =>
        {
            var blob = _library.EnsureAvbPublicKey(key);
            var path = Path.HasExtension(destination)
                ? Path.GetFullPath(destination)
                : Path.Combine(Path.GetFullPath(destination), Path.GetFileName(blob));
            File.Copy(blob, path, false);
            return path;
        });
        Status = T("Avb_KeyExported", target);
    });

    /// <summary>
    /// Adds the selected image to the footer list, keeping the selection on the first entry.
    /// <para>把镜像加入尾部列表，并把选择停留在第一个条目。</para>
    /// </summary>
    /// <param name="paths">Image paths selected by the caller. <para>调用方选择的镜像路径。</para></param>
    public void AddImages(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (!Images.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                Images.Add(full);
            }
        }

        if (SelectedImage is not { } current || !Images.Contains(current))
        {
            SelectedImage = Images.FirstOrDefault();
        }
    }

    /// <summary>
    /// Removes the selected image and selects its neighbour.
    /// <para>移除所选镜像并选中相邻条目。</para>
    /// </summary>
    public void RemoveSelectedImage()
    {
        if (SelectedImage is not { } image)
        {
            return;
        }

        var index = Images.IndexOf(image);
        Images.Remove(image);
        SelectedImage = Images.Count == 0
            ? null
            : Images[Math.Clamp(index, 0, Images.Count - 1)];
    }

    /// <summary>
    /// Signs the selected image with the selected AVB key using the chosen footer mode.
    /// <para>使用所选尾部模式与 AVB 密钥对所选镜像签名。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task AddFooterAsync() => RunAsync(async cancellationToken =>
    {
        if (SelectedImage is not { Length: > 0 } input)
        {
            Status = T("Avb_FooterNoImage");
            return;
        }

        if (string.IsNullOrWhiteSpace(PartitionName))
        {
            Status = T("Avb_FooterPartitionRequired");
            return;
        }

        if (!TryParseIndices(out var rollbackIndex, out var rollbackLocation)
            || !TryParsePartitionSize(out var partitionSize))
        {
            return;
        }

        if (SelectedKey is not { Kind: ManagedKeyKind.Avb } key)
        {
            Status = T("Avb_KeyEmpty");
            return;
        }

        var keyPath = _library.PrivatePath(key);
        var options = new AvbResignOptions
        {
            PartitionName = PartitionName.Trim(),
            Mode = FooterModeIndex == 1 ? AvbResignMode.Hashtree : AvbResignMode.Hash,
            RollbackIndex = rollbackIndex,
            RollbackIndexLocation = rollbackLocation,
            Flags = (DisableVerity ? (uint)AvbVbmetaFlag.HashtreeDisabled : 0)
                | (DisableAvbVerification ? (uint)AvbVbmetaFlag.VerificationDisabled : 0),
            PartitionSize = partitionSize,
        };
        var output = SignedOutputPath;
        using var rsa = await Task.Run(() => AvbSigner.LoadRsaPrivateKey(keyPath), cancellationToken);
        if (rsa is null)
        {
            Status = T("Avb_KeyUnreadable");
            return;
        }

        var result = await AvbResigner.ResignImageAsync(input, output, rsa, options, cancellationToken);
        Status = result.Success
            ? T("Avb_FooterSigned", result.OutputPath ?? output)
            : UserError.WithDetails(result.ErrorMessage, "Avb_FooterSignFailed");
    });

    /// <summary>
    /// Verifies the selected image, optionally pinning the signer to <see cref="TrustedPublicKeyPath"/>.
    /// <para>校验所选镜像，可选地把签名者固定为 <see cref="TrustedPublicKeyPath"/>。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task VerifyImageAsync() => RunAsync(async cancellationToken =>
    {
        if (SelectedImage is not { Length: > 0 } input)
        {
            Status = T("Avb_FooterNoImage");
            return;
        }

        using var trusted = await LoadTrustedPublicKeyAsync(cancellationToken);
        var result = await AvbImageVerifier.VerifyAsync(input, trusted, cancellationToken);
        Status = DescribeImage(result);
    });

    /// <summary>
    /// Removes the AVB footer from the selected image into <see cref="UnsignedOutputPath"/>.
    /// <para>从所选镜像移除 AVB 尾部，输出到 <see cref="UnsignedOutputPath"/>。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task RemoveFooterAsync() => RunAsync(async cancellationToken =>
    {
        if (SelectedImage is not { Length: > 0 } input)
        {
            Status = T("Avb_FooterNoImage");
            return;
        }

        var output = UnsignedOutputPath;
        var result = await AvbSigner.RemoveFooterAsync(input, output, true, cancellationToken);
        Status = result.Success
            ? T("Avb_FooterUnsigned", result.OutputPath ?? output)
            : UserError.WithDetails(result.ErrorMessage, "Avb_FooterUnsignedFailed");
    });

    /// <summary>
    /// Describes the selected image footer, VBMeta signature and content checks without pinning a signer.
    /// <para>在不固定签名者的前提下描述所选镜像的尾部、VBMeta 签名与内容检查。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task InspectImageAsync() => RunAsync(async cancellationToken =>
    {
        if (SelectedImage is not { Length: > 0 } input)
        {
            Status = T("Avb_FooterNoImage");
            return;
        }

        var result = await AvbImageVerifier.VerifyAsync(input, null, cancellationToken);
        Status = DescribeImage(result);
    });

    /// <summary>
    /// Lists the partitions carried by the selected OTA package and selects them all.
    /// <para>列出所选 OTA 包携带的分区并全选。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task LoadOtaPartitionsAsync() => RunAsync(async cancellationToken =>
    {
        if (string.IsNullOrWhiteSpace(OtaInputPath))
        {
            OtaSummary = T("Avb_OtaNoInput");
            return;
        }

        var names = await _ota.ListPartitionsAsync(OtaInputPath, cancellationToken);
        OtaPartitions.Clear();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            OtaPartitions.Add(new AvbOtaPartitionOption(name) { IsSelected = true });
        }

        NotifyOtaPartitionsChanged();
        OtaSummary = T("Avb_OtaPartitionsLoaded", OtaPartitions.Count);
    });

    /// <summary>
    /// Patches the OTA package with the selected partitions, keys and footer mode.
    /// <para>按所选分区、密钥与尾部模式修补 OTA 包。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task PatchOtaAsync() => RunAsync(async cancellationToken =>
    {
        OtaSummary = "";
        if (string.IsNullOrWhiteSpace(OtaInputPath))
        {
            OtaSummary = T("Avb_OtaNoInput");
            return;
        }

        if (string.IsNullOrWhiteSpace(OtaOutputPath))
        {
            OtaSummary = T("Avb_OtaOutputRequired");
            return;
        }

        var partitions = OtaPartitions.Where(option => option.IsSelected).Select(option => option.Name).ToArray();
        if (partitions.Length == 0)
        {
            OtaSummary = T("Avb_OtaPartitionRequired");
            return;
        }

        if (string.IsNullOrWhiteSpace(OtaPrivateKeyPath))
        {
            OtaSummary = T("Avb_KeyEmpty");
            return;
        }

        if (!TryParseIndices(out var rollbackIndex, out var rollbackLocation))
        {
            OtaSummary = Status;
            return;
        }

        var request = new AvbOtaPatchRequest
        {
            InputPath = OtaInputPath,
            OutputPath = OtaOutputPath,
            AvbPrivateKeyPath = OtaPrivateKeyPath,
            PartitionNames = partitions,
            FooterMode = OtaFooterModeIndex == 1 ? AvbOtaFooterMode.Hashtree : AvbOtaFooterMode.Hash,
            DisableAvbVerification = DisableAvb,
            PayloadPrivateKeyPath = SelectedPayloadKey is { } payloadKey ? _library.PrivatePath(payloadKey) : "",
            OtaCertificatePath = OtaCertificatePath,
            OtaSigningKeyPath = SelectedZipKey is { } zipKey ? _library.PrivatePath(zipKey) : "",
            RollbackIndex = rollbackIndex,
            RollbackIndexLocation = rollbackLocation,
            PartitionSize = null,
            VerifyOutput = true,
        };
        var result = await _ota.PatchAsync(request, cancellationToken);
        OtaSummary = DescribePatch(result);
        Status = result.Success
            ? T("Avb_OtaPatched", result.PatchedPartitions.Count)
            : T("Avb_OtaPatchFailed");
    });

    /// <summary>
    /// Verifies the selected OTA package, its AVB chain and its payload signatures.
    /// <para>校验所选 OTA 包、其 AVB 链与 Payload 签名。</para>
    /// </summary>
    /// <returns>A task completed once the operation finishes. <para>操作结束后完成的任务。</para></returns>
    public Task VerifyOtaAsync() => RunAsync(async cancellationToken =>
    {
        OtaSummary = "";
        if (string.IsNullOrWhiteSpace(OtaInputPath))
        {
            OtaSummary = T("Avb_OtaNoInput");
            return;
        }

        var certificate = OtaCertificatePath;
        if (SelectedPayloadKey is { Kind: ManagedKeyKind.Payload } payloadKey)
        {
            certificate = _library.EnsureCertificate(payloadKey);
        }

        var request = new AvbOtaVerifyRequest
        {
            InputPath = OtaInputPath,
            TrustedRootPublicKeyPath = TrustedPublicKeyPath,
            CertificatePath = certificate,
            VerifyPayloadSignatures = true,
        };
        var result = await _ota.VerifyAsync(request, cancellationToken);
        OtaSummary = DescribeVerify(result);
        Status = result.Success ? T("Avb_OtaVerified") : T("Avb_OtaVerificationFailed");
    });

    /// <summary>
    /// Runs one operation while the busy flag is set; failures are captured into <see cref="Status"/>.
    /// <para>在忙碌标志置位期间执行一个操作；失败信息捕获到 <see cref="Status"/>。</para>
    /// </summary>
    /// <param name="action">The operation body. <para>操作主体。</para></param>
    /// <returns>A task completed once the operation ends. <para>操作结束后完成的任务。</para></returns>
    public Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(_ => action());
    }

    /// <summary>
    /// Runs one cancellable operation while the busy flag is set; exceptions never leave the UI action.
    /// <para>在忙碌标志置位期间执行一个可取消操作；异常不会从界面动作中逃出。</para>
    /// </summary>
    /// <param name="action">The operation body receiving the per-run cancellation token.
    /// <para>接收本次运行取消令牌的操作主体。</para></param>
    /// <returns>A task completed once the operation ends. <para>操作结束后完成的任务。</para></returns>
    public async Task RunAsync(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "";
        var source = new CancellationTokenSource();
        _operation = source;
        try
        {
            await action(source.Token);
        }
        catch (Exception ex)
        {
            Status = UserError.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_operation, source))
            {
                _operation = null;
            }

            source.Dispose();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Requests cancellation of the running operation; a no-op when the tool is idle.
    /// <para>请求取消正在运行的操作；空闲时不执行任何动作。</para>
    /// </summary>
    public void CancelPendingWork()
    {
        var source = _operation;
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReloadKeysAsync()
    {
        var all = await Task.Run(_library.List);
        var avbId = SelectedKey?.Id;
        var payloadId = SelectedPayloadKey?.Id;
        var zipId = SelectedZipKey?.Id;

        AvbKeys.Clear();
        foreach (var key in all.Where(key => key.Kind == ManagedKeyKind.Avb))
        {
            AvbKeys.Add(key);
        }

        PayloadKeys.Clear();
        foreach (var key in all.Where(key => key.Kind == ManagedKeyKind.Payload))
        {
            PayloadKeys.Add(key);
        }

        ZipKeys.Clear();
        foreach (var key in all.Where(key => key.Kind == ManagedKeyKind.Zip))
        {
            ZipKeys.Add(key);
        }

        SelectedKey = AvbKeys.FirstOrDefault(key => key.Id == avbId) ?? AvbKeys.FirstOrDefault();
        SelectedPayloadKey = PayloadKeys.FirstOrDefault(key => key.Id == payloadId) ?? PayloadKeys.FirstOrDefault();
        SelectedZipKey = ZipKeys.FirstOrDefault(key => key.Id == zipId) ?? ZipKeys.FirstOrDefault();
    }

    private void NotifyOtaPartitionsChanged() => OnPropertyChanged(nameof(HasOtaPartitions));

    private bool TryParseIndices(out ulong rollbackIndex, out uint rollbackLocation)
    {
        rollbackIndex = 0;
        rollbackLocation = 0;
        if (!ulong.TryParse(RollbackIndex, NumberStyles.None, CultureInfo.InvariantCulture, out rollbackIndex))
        {
            Status = T("Avb_FooterNumberInvalid", T("Avb_FooterRollbackIndex"));
            return false;
        }

        if (!uint.TryParse(RollbackIndexLocation, NumberStyles.None, CultureInfo.InvariantCulture, out rollbackLocation))
        {
            Status = T("Avb_FooterNumberInvalid", T("Avb_FooterRollbackLocation"));
            return false;
        }

        return true;
    }

    private bool TryParsePartitionSize(out long? partitionSize)
    {
        partitionSize = null;
        if (string.IsNullOrWhiteSpace(PartitionSize))
        {
            return true;
        }

        if (!long.TryParse(PartitionSize, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0
            || parsed % AvbBlockSize != 0)
        {
            Status = T("Avb_FooterSizeInvalid");
            return false;
        }

        partitionSize = parsed;
        return true;
    }

    private async Task<RSA?> LoadTrustedPublicKeyAsync(CancellationToken cancellationToken)
    {
        var path = TrustedPublicKeyPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var blob = await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumPublicKeyBlobBytes)
            {
                throw new InvalidDataException("The AVB public-key blob has an unsupported size.");
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }, cancellationToken);
        return AvbRsaPublicKeyCodec.Import(blob);
    }

    private static string DescribeImage(AvbImageVerifyResult result)
    {
        var lines = new List<string>
        {
            result.IsValid ? T("Avb_FooterVerified") : T("Avb_FooterVerifyFailed"),
            Label("Avb_DetailSigned", YesNo(result.IsSigned)),
            Label("Avb_DetailSignature", YesNo(result.SignatureIsValid)),
            Label("Avb_DetailContent", YesNo(result.ContentIsValid)),
            Label("Avb_DetailFec", result.FecIsValid is { } fec ? YesNo(fec) : T("Avb_NotChecked")),
            Label("Avb_DetailDescriptors", result.Descriptors.Count.ToString(CultureInfo.CurrentCulture)),
        };
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            lines.Add(result.ErrorMessage);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribePatch(AvbOtaPatchResult result)
    {
        var lines = new List<string> { result.Summary };
        if (result.OutputPath is { Length: > 0 } output)
        {
            lines.Add(output);
        }

        AppendSection(lines, "Avb_OtaWarnings", result.Warnings);
        AppendSection(lines, "Avb_OtaErrors", result.Errors);
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeVerify(AvbOtaVerifyResult result)
    {
        var lines = new List<string>
        {
            result.Summary,
            Label("Avb_OtaRootTrusted", YesNo(result.RootTrusted)),
            Label("Avb_OtaChain", YesNo(result.ChainSuccess)),
            Label("Avb_OtaPayloadSignature", YesNo(result.PayloadSignatureIsValid)),
        };
        AppendSection(lines, "Avb_OtaWarnings", result.Warnings);
        AppendSection(lines, "Avb_OtaErrors", result.Errors);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendSection(List<string> lines, string headingKey, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        lines.Add(T(headingKey) + ":");
        foreach (var value in values)
        {
            lines.Add("• " + value);
        }
    }

    private static string Label(string key, string value) => T(key) + ": " + value;

    private static string YesNo(bool value) => value ? T("Avb_Yes") : T("Avb_No");

    private static string YesNo(bool? value) => value is { } flag ? YesNo(flag) : T("Avb_NotChecked");

    private static string T(string key, params object[] arguments) => LanguageService.Instance.GetString(key, arguments);
}

/// <summary>
/// One selectable OTA partition row.
/// <para>一个可选择的 OTA 分区条目。</para>
/// </summary>
public partial class AvbOtaPartitionOption : ObservableObject
{
    /// <summary>Creates a selectable row for one partition name. <para>为一个分区名创建可选条目。</para></summary>
    /// <param name="name">The partition name reported by the OTA package. <para>OTA 包报告的分区名。</para></param>
    public AvbOtaPartitionOption(string name)
    {
        Name = name;
    }

    /// <summary>Partition name. <para>分区名。</para></summary>
    public string Name { get; }

    /// <summary>Whether the partition is included in the patch request. <para>该分区是否加入修补请求。</para></summary>
    [ObservableProperty] private bool _isSelected;
}