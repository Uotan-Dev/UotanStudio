using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.PatchPackages;
using UotanStudio.Services.Ui;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Flash;

public sealed partial class FlashViewModel
{
    /// <summary>
    /// Maximum partition-table lines written to the log; anything beyond it is summarised so a
    /// several-hundred-partition table never floods the log.
    /// <para>写入日志的分区表最大行数；超出部分只给出余量提示，避免一张几百分区的表淹没日志。</para>
    /// </summary>
    private const int MaxPartitionTableLines = 64;

    private bool _busy;
    private bool _confirming;
    private string _loadedPath = string.Empty;
    private string? _requiredCodeName;
    private bool _requiresFastbootd;
    private string? _payloadPath;
    private string? _remotePayloadUrl;
    private static readonly HttpClient RemoteHttpClient = new();
    private string? _superPath;
    private string? _vendorPath;
    private readonly Dictionary<string, string> _vendorEntries = new(StringComparer.OrdinalIgnoreCase);
    private string? _oppoPath;
    private readonly Dictionary<string, (string Entry, string OutputName)> _oppoEntries = new(StringComparer.OrdinalIgnoreCase);
    private string? _rootPackagePath;
    private readonly Dictionary<FlashPartitionEntry, RawProgramEntry> _edlEntries = [];
    private string? _edlProgrammer;
    private IReadOnlyList<string> _edlProgrammerCandidates = [];
    private string? _edlProgrammerOverride;
    private bool _isCustomProgrammer;

    private string? EffectiveEdlProgrammer => _isCustomProgrammer ? _edlProgrammerOverride : _edlProgrammer;

    private static readonly string[] RkStorageTypes =
    [
        "1. FLASH",
        "2. EMMC",
        "3. SD",
        "4. SD1",
        "5. SPINOR",
        "6. SPINAND",
        "7. RAM",
        "8. USB",
        "9. SATA",
        "10. PCIE"
    ];
    private string? _edlProgrammerDirectory;
    private EdlProgrammerDirectoryResult? _edlProgrammerDirectoryResult;
    private UfsProvisionProfile? _edlUfsProfile;
    private string? _edlUfsProfilePath;
    private string? _edlNandTablePath;
    private bool _flatBinPackage;
    private bool _edlBundleReady;
    private CancellationTokenSource? _flashCancellation;
    private IReadOnlyList<FlashPartitionEntry> _overallRows = [];
    private MtkScatterPackageResolution? _mtkResolution;
    private readonly Dictionary<FlashPartitionEntry, int> _mtkRows = [];
    /// <summary>
    /// Maps a scatter row to its manifest entry, covering both flashable rows and erase-only rows;
    /// the erase button builds GPT-named steps straight from it.
    /// <para>scatter 行到其清单条目的映射，既含可刷写行也含仅可擦除行；擦除按钮据此直接构造 GPT 命名步骤。</para>
    /// </summary>
    private readonly Dictionary<FlashPartitionEntry, MtkScatterPartition> _mtkPartitions = [];
    private string? _mtkLoaderPath;
    private IReadOnlyList<MtkLoaderEntryInfo> _mtkLoaderEntries = [];
    private MtkEmiParameters? _mtkEmi;
    private string? _mtkKeyDirectory;
    private string? _mtkAuthFile;

    /// <summary>
    /// MTK port refresh sequence; only the latest request's result is written back to the combo box.
    /// <para>MTK 串口刷新请求序号；只有最新一次请求的结果才写回下拉框。</para>
    /// </summary>
    private int _mtkPortRequest;
    /// <summary>
    /// EDL port refresh sequence; only the latest request's result is written back to the combo box.
    /// <para>EDL 串口刷新请求序号；只有最新一次请求的结果才写回下拉框。</para>
    /// </summary>
    private int _edlSerialPortRequest;
    private DeviceViewModel? _deviceViewModel;

    private readonly LocalizedOptionSet _edlSectorSizeOptions = LocalizedOptionSet.FromOptions(
        LocalizedOption.FromResource("Flash_Auto"),
        LocalizedOption.FromText("512"),
        LocalizedOption.FromText("2048"),
        LocalizedOption.FromText("4096"));
    private readonly LocalizedOptionSet _edlSlotOptions = LocalizedOptionSet.FromOptions(
        LocalizedOption.FromResource("Flash_Auto"),
        LocalizedOption.FromText("1"),
        LocalizedOption.FromText("2"));
    private readonly LocalizedOptionSet _mtkProtocolOptions = new(
        "Flash_MtkProtocolXFlash", "Flash_MtkProtocolLegacy", "Flash_MtkProtocolXml");
    private readonly LocalizedOptionSet _mtkStageOptions = new(
        "Flash_MtkStageAuto", "Flash_MtkStageBootRom", "Flash_MtkStagePreloader", "Flash_MtkStageDa");
    private readonly LocalizedOptionSet _mtkRpmbModeOptions = new(
        "Flash_MtkRpmbModeFrames", "Flash_MtkRpmbModeByteRange", "Flash_MtkRpmbModeFullArea");

    /// <summary><para>区域转储的输出目录；未选择时读写动作在本地被拒绝，绝不回落到工作目录。</para>
    /// Output directory for region dumps; while it is unset the read actions refuse locally instead of
    /// falling back to a working directory.</summary>

    private DetectedDevice? SelectedDevice => _deviceViewModel?.SelectedDevice?.Device;

    /// <summary>
    /// The EDL physical region (LUN) selected on the page; the primary region 0 when none is picked.
    /// <para>取界面选择的 EDL 物理区域（LUN）；未选择时使用主区域 0。</para>
    /// </summary>
    private int SelectedEdlLun => Field<ComboBox>("EdlLunBox").SelectedItem is int lun ? lun : 0;

    internal void OnClearLogClick(object? sender, RoutedEventArgs e) => ClearLog();

    internal async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                FilePickerFileTypes.All,
                new FilePickerFileType(_localizer.GetString("Flash_RkConfigFileType"))
                {
                    Patterns = ["*.cfg"]
                }
            ]
        });
        if (!_busy && !_confirming && files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            PackagePath = path;
            await LoadPackageAsync(path);
        }
    }

    internal async void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString("Flash_ChooseFolder"),
            AllowMultiple = false
        });
        if (!_busy && !_confirming && folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
        {
            PackagePath = path;
            await LoadPackageAsync(path);
        }
    }

    internal async void OnGetPackageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming)
        {
            return;
        }

        var text = PackagePath?.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            await LoadPackageAsync(text);
        }
        else
        {
            ExternalLinkService.TryOpen("https://xiaomirom.com/series/");
        }
    }

    private async Task LoadPackageAsync(string path)
    {
        if (_busy || _confirming || string.Equals(path, _loadedPath, StringComparison.Ordinal))
        {
            return;
        }

        if (File.Exists(path) && RockchipConfigFile.LooksLike(path))
        {
            await ImportRkConfigAsync(path).ConfigureAwait(true);
            return;
        }

        SetBusy(true);
        _loadedPath = path;
        _requiredCodeName = null;
        _requiresFastbootd = false;
        _edlEntries.Clear();
        ResetEdlProgrammer();
        _edlProgrammerOverride = null;
        _flatBinPackage = false;
        _edlBundleReady = false;
        _mtkResolution = null;
        _mtkRows.Clear();
        _mtkPartitions.Clear();
        UpdateModeVisibility();
        _payloadPath = null;
        _remotePayloadUrl = null;
        _superPath = null;
        _vendorPath = null;
        _vendorEntries.Clear();
        _oppoPath = null;
        _oppoEntries.Clear();
        Field<Button>("ExtractSelectedButton").IsEnabled = false;
        Field<Button>("BuildWiredPackageButton").IsEnabled = false;
        Partitions.Clear();
        var remotePayload = Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https";
        try
        {
            if (!remotePayload && !Directory.Exists(path) && !File.Exists(path))
            {
                AppendLog(_localizer.GetString("Flash_FolderOnly"));
                _loadedPath = string.Empty;
                return;
            }
            if (remotePayload)
            {
                var partitions = await new FlashRemotePayloadService(RemoteHttpClient)
                    .ListAsync(path, CancellationToken.None);
                if (path != _loadedPath) return;
                _remotePayloadUrl = path;
                var remoteRows = new List<FlashPartitionEntry>(partitions.Count);
                foreach (var (name, size) in partitions)
                {
                    remoteRows.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                Partitions.Apply(0, remoteRows);
                Field<Button>("ExtractSelectedButton").IsEnabled = partitions.Count > 0;
                AppendLog($"{partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && (path.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
                                      path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase)))
            {
                if (Path.GetFileName(path).Equals("super_empty.img", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals("super_empty.img.zst", StringComparison.OrdinalIgnoreCase))
                {
                    if (!path.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            _localizer.GetString("Flash_CompressedSuperEmptyUnsupported"));
                    _ = FlashSuperEmptyService.Prepare(path);
                    Partitions.Add(new FlashPartitionEntry
                    {
                        Step = new FlashStep("wipe_super", "super_empty", path, new FileInfo(path).Length),
                        Name = "super_empty",
                        Command = "wipe_super",
                        FilePath = path,
                        FileName = Path.GetFileName(path)
                    });
                    return;
                }
                if (path.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                    await Task.Run(() =>
                    {
                        Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
                        return Firmware.Core.FormatDetector.Detect(path) is Firmware.BlockImage.Lp.LpFormatHandler;
                    }))
                {
                    var partitions = await FlashSuperService.ListAsync(path, CancellationToken.None);
                    _superPath = path;
                    var superRows = new List<FlashPartitionEntry>(partitions.Count);
                    foreach (var (partitionName, size) in partitions)
                    {
                        superRows.Add(new FlashPartitionEntry
                        {
                            Name = partitionName,
                            Size = $"{size / 1024d / 1024d:F1} MiB",
                            CanSelect = true
                        });
                    }
                    Partitions.Apply(0, superRows);
                    Field<Button>("ExtractSelectedButton").IsEnabled = partitions.Count > 0;
                    AppendLog($"{partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                    return;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                if (path.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
                {
                    name = Path.GetFileNameWithoutExtension(name);
                }
                var length = new FileInfo(path).Length;
                if (length == 0 || name.Length is < 1 or > 128 ||
                    !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    throw new InvalidDataException(
                        _localizer.GetString("Flash_ImageNameInvalid"));
                }

                Partitions.Add(new FlashPartitionEntry
                {
                    Name = name,
                    Command = "flash",
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Size = $"{length / 1024d / 1024d:F1} MiB"
                });
                return;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (File.Exists(path) && extension is ".ofp" or ".ops" or ".ozip")
            {
                var entries = await FlashOppoContainerService.ListAsync(path, CancellationToken.None);
                _oppoPath = path;
                var oppoRows = new List<FlashPartitionEntry>(entries.Count);
                foreach (var (name, archiveEntry, outputName, size) in entries)
                {
                    _oppoEntries.Add(name, (archiveEntry, outputName));
                    oppoRows.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                Partitions.Apply(0, oppoRows);
                Field<Button>("ExtractSelectedButton").IsEnabled = entries.Count > 0;
                AppendLog($"{entries.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && extension is ".nb0" or ".ntpi")
            {
                var entries = await FlashVendorContainerService.ListAsync(path, CancellationToken.None);
                _vendorPath = path;
                var vendorRows = new List<FlashPartitionEntry>(entries.Count);
                foreach (var (name, archiveEntry, size) in entries)
                {
                    _vendorEntries.Add(name, archiveEntry);
                    vendorRows.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                Partitions.Apply(0, vendorRows);
                Field<Button>("ExtractSelectedButton").IsEnabled = entries.Count > 0;
                AppendLog($"{entries.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && extension == ".xml" && LooksLikeScatter(path))
            {
                var resolution = MtkScatterPackage.Resolve(path, MtkScatterPackage.Read(path));
                _mtkResolution = resolution;
                var knownNames = new HashSet<string>(StringComparer.Ordinal);
                var scatterRows = new List<FlashPartitionEntry>(resolution.Rows.Count);
                for (var index = 0; index < resolution.Rows.Count; index++)
                {
                    var (partition, imagePath, length) = resolution.Rows[index];
                    var row = new FlashPartitionEntry
                    {
                        Name = partition.Name,
                        Command = "flash",
                        FilePath = imagePath,
                        FileName = Path.GetFileName(imagePath),
                        Size = $"{length / 1024d / 1024d:F1} MiB"
                    };
                    scatterRows.Add(row);
                    _mtkRows[row] = index;
                    _mtkPartitions[row] = partition;
                    knownNames.Add(partition.Name);
                }

                var eraseOnlyRows = 0;
                foreach (var partition in MtkScatterPackage.ListEraseTargets(resolution.Storage))
                {
                    if (!knownNames.Add(partition.Name))
                    {
                        continue;
                    }

                    var row = new FlashPartitionEntry
                    {
                        Name = partition.Name,
                        Command = "erase",
                        CanSelect = true,
                        Size = $"{partition.Size / 1024d / 1024d:F1} MiB"
                    };
                    scatterRows.Add(row);
                    eraseOnlyRows++;
                    _mtkPartitions[row] = partition;
                }

                Partitions.Apply(0, scatterRows);
                UpdateEdlButton();
                AppendLog($"{resolution.Rows.Count + eraseOnlyRows} {_localizer.GetString("Flash_ImagesLoaded")} · {resolution.StorageLabel}");
                return;
            }

            if (File.Exists(path) && extension is ".bat" or ".sh")
            {
                var lines = await File.ReadAllLinesAsync(path);
                var scriptRows = ParseScript(path, lines);
                Partitions.Apply(0, scriptRows);
                AppendLog($"{Partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && extension is ".txt")
            {
                var lines = await File.ReadAllLinesAsync(path);
                var txtRows = ParseManifestText(path, lines, out _requiredCodeName, out _requiresFastbootd);
                Partitions.Apply(0, txtRows);
                AppendLog($"{Partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path) && FlashPayloadService.LooksLikePayload(path))
            {
                var partitions = await FlashPayloadService.ListAsync(path, CancellationToken.None);
                if (path != _loadedPath) return;
                _payloadPath = path;
                var payloadRows = new List<FlashPartitionEntry>(partitions.Count);
                foreach (var (name, size) in partitions)
                {
                    payloadRows.Add(new FlashPartitionEntry
                    {
                        Name = name,
                        Size = $"{size / 1024d / 1024d:F1} MiB",
                        CanSelect = true
                    });
                }
                Partitions.Apply(0, payloadRows);
                Field<Button>("ExtractSelectedButton").IsEnabled = partitions.Count > 0;
                Field<Button>("BuildWiredPackageButton").IsEnabled = partitions.Count > 0;
                AppendLog($"{partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (Directory.Exists(path))
            {
                if (FlashAfterSalesPackage.Read(path) is { } afterSales)
                {
                    ApplyAfterSalesPackage(afterSales);
                    return;
                }

                var directoryImages = Directory.Exists(Path.Combine(path, "images")) ? Path.Combine(path, "images") : path;
                if (Directory.EnumerateFiles(directoryImages, "rawprogram*.xml", SearchOption.TopDirectoryOnly).Any())
                {
                    var bin = FlashBinPackage.Read(path);
                    if (bin.Images.Count > 0)
                    {
                        _flatBinPackage = true;
                        _edlProgrammerCandidates = bin.Programmers;
                        _edlProgrammer = bin.Programmer;
                        _isCustomProgrammer = false;
                        UpdateEdlProgrammerPickOptions();
                        var edlPrograms = bin.Programs.Where(entry =>
                            !string.IsNullOrEmpty(entry.Filename) && File.Exists(entry.SourceFile) &&
                            !entry.Filename.StartsWith("gpt_", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        var edlRows = new List<FlashPartitionEntry>();
                        foreach (var (name, image, length) in bin.Images)
                        {
                            var matches = edlPrograms.Where(entry =>
                                Path.GetFullPath(entry.SourceFile).Equals(Path.GetFullPath(image),
                                    StringComparison.OrdinalIgnoreCase)).ToArray();
                            foreach (var entry in matches.Length == 0 ? [null] : matches.Cast<RawProgramEntry?>())
                            {
                                var row = new FlashPartitionEntry
                                {
                                    Name = entry?.Label is { Length: > 0 } label ? label : name,
                                    Command = "flash",
                                    FilePath = image,
                                    FileName = Path.GetFileName(image),
                                    Size = FormatByteSize(length),
                                    CanSelect = true,
                                    IsSelected = false
                                };
                                edlRows.Add(row);
                                if (entry is not null) _edlEntries.Add(row, entry);
                            }
                        }
                        Partitions.Apply(0, edlRows);
                        _edlBundleReady = bin.Programs.Count > 0 && bin.Programmers.Count > 0;
                        if (bin.Programmers.Count > 1)
                        {
                            AppendLog($"{_localizer.GetString("Flash_EdlProgrammerChoice")}: {bin.Programmers.Count}");
                        }
                        UpdateEdlButton();
                        AppendLog($"{Partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                        return;
                    }
                }

                var dirRows = ParseDirectory(path);
                Partitions.Apply(0, dirRows);
                AppendLog($"{Partitions.Count} {_localizer.GetString("Flash_ImagesLoaded")}");
                return;
            }

            if (File.Exists(path))
            {
                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    _localizer.GetString("Flash_UnsupportedPackage"), Path.GetFileName(path)));
                _loadedPath = string.Empty;
            }
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            _loadedPath = string.Empty;
            _flatBinPackage = false;
            _edlBundleReady = false;
            _edlEntries.Clear();
            ResetEdlProgrammer();
            _edlProgrammerOverride = null;
            _mtkResolution = null;
            _mtkRows.Clear();
            _mtkPartitions.Clear();
            Partitions.Clear();
            UpdateEdlButton();
        }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Applies an after-sales package folder: marks the page as an EDL bundle, fills the programmer picker and lists
    /// every partition image of the IMAGES tree with the RADIO images merged in.
    /// <para>应用售后包目录：把页面标为 EDL 完整包，填充 programmer 选择，并按 IMAGES 树列出全部分区镜像，
    /// 同时合入 RADIO 镜像。</para>
    /// </summary>
    /// <param name="package">The package contents read from the folder.
    /// <para>从目录读出的包内容。</para></param>
    private void ApplyAfterSalesPackage(AfterSalesPackageContents package)
    {
        _flatBinPackage = true;
        _edlProgrammerCandidates = package.Programmers;
        _edlProgrammer = package.Programmer;
        _isCustomProgrammer = false;
        UpdateEdlProgrammerPickOptions();
        _edlBundleReady = package.Programs.Count > 0 && package.Programmers.Count > 0;
        var rows = new List<FlashPartitionEntry>(package.Images.Count);
        foreach (var image in package.Images)
        {
            var row = new FlashPartitionEntry
            {
                Name = image.Partition,
                Command = "flash",
                FilePath = image.FilePath,
                FileName = Path.GetFileName(image.FilePath),
                Size = FormatByteSize(image.Length),
                CanSelect = true
            };
            rows.Add(row);
            if (image.Program is { } program)
            {
                _edlEntries.Add(row, program);
            }
        }

        Partitions.Apply(0, rows);
        if (package.Programmers.Count > 1)
        {
            AppendLog($"{_localizer.GetString("Flash_EdlProgrammerChoice")}: {package.Programmers.Count}");
        }

        var merged = package.MergedRadioImages > 0
            ? $" · {_localizer.GetString("Flash_AfterSalesMerged")} {package.MergedRadioImages}"
            : string.Empty;
        AppendLog($"{package.Images.Count} {_localizer.GetString("Flash_ImagesLoaded")} · " +
            $"{_localizer.GetString("Flash_AfterSalesPackage")}{merged}");
        UpdateEdlButton();
    }

    internal async void OnPackagePathLostFocus(object? sender, RoutedEventArgs e)
        => await LoadPackageAsync(PackagePath);

    internal async void OnExtractSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _payloadPath is null && _remotePayloadUrl is null &&
            _superPath is null && _vendorPath is null && _oppoPath is null ||
            TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var selected = Partitions.Where(entry => entry.IsSelected).ToArray();
        if (selected.Length == 0) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString("Flash_ExtractSelected"),
            AllowMultiple = false
        });
        if (_busy || _confirming || !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal) ||
            selected.Any(entry => !entry.IsSelected || !Partitions.Contains(entry)) ||
            folders.Count != 1 || folders[0].TryGetLocalPath() is not { } parent) return;
        SetBusy(true);
        Field<Button>("ExtractSelectedButton").IsEnabled = false;
        try
        {
            var names = selected.Select(entry => entry.Name).ToArray();
            var output = _payloadPath is { } payload
                ? await FlashPayloadService.ExtractAsync(payload, names, parent, CancellationToken.None)
                : _remotePayloadUrl is { } remote
                    ? await new FlashRemotePayloadService(RemoteHttpClient)
                        .ExtractAsync(remote, names, parent, CancellationToken.None)
                : _superPath is { } super
                    ? await FlashSuperService.ExtractAsync(super, names, parent, CancellationToken.None)
                    : _vendorPath is { } vendor
                        ? await FlashVendorContainerService.ExtractAsync(vendor,
                            [.. names.Select(name => _vendorEntries[name])], parent, CancellationToken.None)
                        : await FlashOppoContainerService.ExtractAsync(_oppoPath!,
                            [.. names.Select(name => _oppoEntries[name].Entry)], parent, CancellationToken.None);
            foreach (var entry in selected)
            {
                var filename = _vendorPath is not null ? _vendorEntries[entry.Name]
                    : _oppoPath is not null ? _oppoEntries[entry.Name].OutputName
                    : entry.Name + ".img";
                var path = Path.Combine(output, filename);
                entry.FilePath = path;
                entry.FileName = Path.GetFileName(path);
                entry.Size = $"{new FileInfo(path).Length / 1024d / 1024d:F1} MiB";
            }
            AppendLog(output);
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            Field<Button>("ExtractSelectedButton").IsEnabled = true;
        }
    }

    internal async void OnBuildWiredPackageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _payloadPath is not { } payload || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString("Flash_BuildWiredPackage"),
            AllowMultiple = false
        });
        if (_busy || _confirming || !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal) ||
            _payloadPath != payload || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } parent) return;
        SetBusy(true);
        Field<Button>("BuildWiredPackageButton").IsEnabled = false;
        try
        {
            var output = await FlashPayloadService.BuildPackageAsync(payload,
                [.. Partitions.Select(entry => entry.Name)], parent, CancellationToken.None);
            AppendLog(output);
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            Field<Button>("BuildWiredPackageButton").IsEnabled = _payloadPath is not null;
        }
    }


    internal void UpdateRootControlsState()
    {
        var addRoot = Field<CheckBox>("AddRootBox").IsChecked == true;
        var sourceBox = Field<ComboBox>("RootFileSourceBox");
        var pathBox = Field<TextBox>("RootPackagePathBox");
        var chooseBtn = Field<Button>("ChooseRootPackageButton");

        var rootPanel = Field<StackPanel>("RootOptionsPanel");
        rootPanel.IsVisible = addRoot;
        rootPanel.IsEnabled = !_busy;
        var customFile = sourceBox.SelectedIndex == 5;
        pathBox.IsVisible = customFile;
        chooseBtn.IsVisible = customFile;
        var versionBox = Field<ComboBox>("RootVersionBox");
        versionBox.IsVisible = !customFile;
        if (!addRoot)
        {
            CancelRootOperations();
            _rootLoadedSource = null;
            return;
        }

        if (!customFile) EnsureRootReleasesLoaded();
    }

    internal void OnRootFileSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        var custom = Field<ComboBox>("RootFileSourceBox").SelectedIndex == 5;
        if (!custom)
        {
            Field<TextBox>("RootPackagePathBox").Text = string.Empty;
        }

        ResetRootReleases();
        UpdateRootControlsState();
    }

    internal void OnReadbackSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice?.Protocol == DeviceProtocolKind.Mtk || _mtkResolution is not null)
        {
            OnMtkReadbackClick(sender, e);
        }
        else if (SelectedDevice?.Protocol == DeviceProtocolKind.Edl || _edlEntries.Count > 0)
        {
            OnEdlReadbackClick(sender, e);
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
        }
    }

    internal async void OnRebootSystemClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Fastboot } fastbootDevice)
        {
            SetBusy(true);
            try
            {
                await FastbootStepService.RebootAsync(fastbootDevice, null, CancellationToken.None);
                AppendLog(string.Format(_localizer.GetString("Flash_OkResult"), "reboot"));
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally { SetBusy(false); }
        }
        else if (SelectedDevice is { Protocol: DeviceProtocolKind.Edl } || _edlEntries.Count > 0)
        {
            OnEdlResetClick(sender, e);
        }
        else if (SelectedDevice is { Protocol: DeviceProtocolKind.Mtk } || _mtkResolution is not null)
        {
            OnMtkRebootClick(sender, e);
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotSelected"));
        }
    }

    internal async void OnRebootBootloaderClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Fastboot } fastbootDevice)
        {
            SetBusy(true);
            try
            {
                await FastbootStepService.RebootAsync(fastbootDevice, "bootloader", CancellationToken.None);
                AppendLog(string.Format(_localizer.GetString("Flash_OkResult"), "reboot-bootloader"));
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally { SetBusy(false); }
        }
        else if (SelectedDevice is { Protocol: DeviceProtocolKind.Mtk } || _mtkResolution is not null)
        {
            OnMtkRebootFastbootClick(sender, e);
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
        }
    }

    internal async void OnRebootRecoveryClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Fastboot } fastbootDevice)
        {
            SetBusy(true);
            try
            {
                await FastbootStepService.RebootAsync(fastbootDevice, "recovery", CancellationToken.None);
                AppendLog(string.Format(_localizer.GetString("Flash_OkResult"), "reboot-recovery"));
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally { SetBusy(false); }
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
        }
    }

    internal async void OnRebootFastbootdClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Fastboot } fastbootDevice)
        {
            SetBusy(true);
            try
            {
                await FastbootStepService.RebootAsync(fastbootDevice, "fastboot", CancellationToken.None);
                AppendLog(string.Format(_localizer.GetString("Flash_OkResult"), "reboot-fastboot"));
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally { SetBusy(false); }
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
        }
    }

    internal async void OnRebootEdlClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Fastboot } fastbootDevice)
        {
            SetBusy(true);
            try
            {
                await FastbootStepService.RebootAsync(fastbootDevice, "edl", CancellationToken.None);
                AppendLog(string.Format(_localizer.GetString("Flash_OkResult"), "reboot-edl"));
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally { SetBusy(false); }
        }
        else if (SelectedDevice is { Protocol: DeviceProtocolKind.Edl } || _edlEntries.Count > 0)
        {
            OnEdlResetToEdlClick(sender, e);
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
        }
    }

    internal void OnPowerOffClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Edl } || _edlEntries.Count > 0)
        {
            OnEdlPowerOffClick(sender, e);
        }
        else if (SelectedDevice is { Protocol: DeviceProtocolKind.Mtk } || _mtkResolution is not null)
        {
            OnMtkPowerOffClick(sender, e);
        }
        else
        {
            AppendLog(_localizer.GetString("Flash_ProtocolMismatch"));
        }
    }

    internal async void OnChooseRootPackageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_ChooseRootPackage"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        SetBusy(true);
        _rootPackagePath = null;
        try
        {
            var validation = await RootPatchPackageService.Shared.ValidateAsync(path);
            if (!validation.IsSupported || validation.Detection is null ||
                !FlashRootImage.IsSupportedPackage(validation.Detection))
            {
                throw new InvalidDataException(
                    _localizer.GetString("Flash_RootPackageUnsupported"));
            }
            _rootPackagePath = validation.Path;
            Field<TextBox>("RootPackagePathBox").Text = path;
            Field<ComboBox>("RootFileSourceBox").SelectedIndex = 5;
            Field<CheckBox>("AddRootBox").IsEnabled = true;
            Field<CheckBox>("AddRootBox").IsChecked = true;
            ToolTip.SetTip(Field<Button>("ChooseRootPackageButton"), path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { SetBusy(false); UpdateRootControlsState(); }
    }

    internal async void OnChoosePartitionImageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || sender is not Button { DataContext: FlashPartitionEntry entry } ||
            entry.Step?.Command is not (null or "flash" or "wipe_super") ||
            TopLevel.GetTopLevel(Surface) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || !Partitions.Contains(entry) ||
            files.Count != 1 || files[0].TryGetLocalPath() is not { } path || !File.Exists(path))
        {
            return;
        }

        entry.FilePath = path;
        entry.FileName = Path.GetFileName(path);
        entry.Size = $"{new FileInfo(path).Length / 1024d / 1024d:F1} MiB";
        entry.CanSelect = true;
    }

    private DeviceInfo? _observedDeviceInfo;

    private void UpdateObservedDeviceInfo()
    {
        var current = _deviceViewModel?.SelectedDevice;
        if (ReferenceEquals(_observedDeviceInfo, current))
        {
            return;
        }

        if (_observedDeviceInfo is not null)
        {
            _observedDeviceInfo.PropertyChanged -= OnDeviceInfoPropertyChanged;
        }

        _observedDeviceInfo = current;
        if (_observedDeviceInfo is not null)
        {
            _observedDeviceInfo.PropertyChanged += OnDeviceInfoPropertyChanged;
        }
    }

    private void OnDeviceInfoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceInfo.IsUserspace) or nameof(DeviceInfo.State) or nameof(DeviceInfo.IsFastbootd))
        {
            UpdateEdlButton();
        }
    }

    private DetectedDevice? _lastObservedDevice;

    internal void ApplyDeviceProtocolExpansion(DeviceProtocolKind? protocol)
    {
        if (Surface is null) return;
        if (protocol is DeviceProtocolKind.Edl)
        {
            Field<Expander>("EdlExpander").IsExpanded = true;
            Field<Expander>("MtkExpander").IsExpanded = false;
            Field<Expander>("RkExpander").IsExpanded = false;
        }
        else if (protocol is DeviceProtocolKind.Mtk)
        {
            Field<Expander>("MtkExpander").IsExpanded = true;
            Field<Expander>("EdlExpander").IsExpanded = false;
            Field<Expander>("RkExpander").IsExpanded = false;
        }
        else if (protocol is DeviceProtocolKind.Rkdl)
        {
            Field<Expander>("RkExpander").IsExpanded = true;
            Field<Expander>("EdlExpander").IsExpanded = false;
            Field<Expander>("MtkExpander").IsExpanded = false;
        }
        else
        {
            Field<Expander>("EdlExpander").IsExpanded = false;
            Field<Expander>("MtkExpander").IsExpanded = false;
            Field<Expander>("RkExpander").IsExpanded = false;
        }
    }

    private void OnGlobalDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.SelectedDevice))
        {
            var currentDevice = SelectedDevice;
            if (!ReferenceEquals(_lastObservedDevice, currentDevice) ||
                _lastObservedDevice?.Protocol != currentDevice?.Protocol)
            {
                _lastObservedDevice = currentDevice;
                ApplyDeviceProtocolExpansion(currentDevice?.Protocol);
            }

            UpdateObservedDeviceInfo();
            UpdateEdlButton();
        }
    }

    private void UpdateEdlButton()
    {
        var busy = _busy;
        var protocol = SelectedDevice?.Protocol;
        UpdateModeVisibility();
        UpdateRockchipActionState(protocol is DeviceProtocolKind.Rkdl);

        SelectedDeviceText = SelectedDevice is { } device
            ? device.Protocol is DeviceProtocolKind.Mtk && device.State == "logging"
                ? $"{device.ProtocolLabel} · {device.DisplayName} — {_localizer.GetString("Flash_MtkLoggingOnlyHint")}"
                : $"{device.ProtocolLabel} · {device.DisplayName}"
            : _localizer.GetString("Flash_NoDevice");
    }

    /// <summary>
    /// Clears the package programmer candidates and picker; an ambiguous state keeps no implicit default.
    /// <para>清空包内 programmer 候选与选择控件；歧义状态不保留任何隐式默认值。</para>
    /// </summary>
    private void ResetEdlProgrammer()
    {
        _edlProgrammer = null;
        _edlProgrammerCandidates = [];
        _isCustomProgrammer = false;
        _edlProgrammerOverride = null;
        UpdateEdlProgrammerPickOptions();
    }

    /// <summary>
    /// Builds the EDL startup requirements from the page; outputs null when no compatibility switch is
    /// requested and every timeout keeps its default.
    /// <para>按界面选择构造 EDL 启动要求：未请求任何兼容开关且超时保持默认时输出 null。</para>
    /// </summary>
    private bool TryBuildEdlInitialization(out EdlInitializationOptions? initialization)
    {
        initialization = null;
        var qudRecovery = Field<CheckBox>("EdlQudRecoveryBox").IsChecked == true;
        var pendingCompletion = Field<CheckBox>("EdlPendingCompletionBox").IsChecked == true;
        var streamDload = Field<CheckBox>("EdlAllowStreamDloadBox").IsChecked == true;
        var skipResponse = Field<CheckBox>("EdlSkipResponseBox").IsChecked == true;
        initialization = qudRecovery || pendingCompletion || streamDload || skipResponse
            ? new EdlInitializationOptions
            {
                QudHelloRecoveryEnabled = qudRecovery,
                AllowSingleFirehosePendingCompletion = pendingCompletion,
                AllowStreamDload = streamDload,
                SkipResponseOnReadWrite = skipResponse
            }
            : null;
        return true;
    }


    /// <summary>
    /// Advances the session to the configured Firehose state with the selected programmer and startup
    /// requirements, reused by the single-operation erase and read-back paths; an already configured session
    /// without startup requirements is reused as-is.
    /// <para>用当前所选 programmer 与启动要求把会话推进到 Firehose 配置态，供擦除、回读等单项操作复用；
    /// 已配置且无启动要求时直接复用现有配置。</para>
    /// </summary>
    private async Task<FirehoseProtocol> ConfigureEdlAsync(EdlSession session,
        EdlInitializationOptions? initialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (initialization is null && session.IsConfigured && session.Firehose is { } existing)
        {
            return existing;
        }

        var programmerPath = EffectiveEdlProgrammer;
        using var programmer = programmerPath is { } loader && File.Exists(loader)
            ? File.OpenRead(loader)
            : null;
        await session.EnsureInitializedAsync(programmer, initialization, cancellationToken).ConfigureAwait(true);
        return session.Firehose ?? throw new InvalidOperationException(
            "The EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
    }

    internal void UpdateEdlProgrammerPickOptions()
    {
        if (Surface is null) return;
        var pickBox = Field<ComboBox>("EdlProgrammerPickBox");
        var customText = _localizer.GetString("Flash_EdlCustomFile");
        var items = _edlProgrammerCandidates.Select(Path.GetFileName).Concat([customText]).ToArray();
        var previousCustom = _isCustomProgrammer;
        var previousEdl = _edlProgrammer;
        pickBox.ItemsSource = items;

        if (previousCustom)
        {
            pickBox.SelectedIndex = items.Length - 1;
        }
        else if (previousEdl is not null)
        {
            var idx = -1;
            for (var i = 0; i < _edlProgrammerCandidates.Count; i++)
            {
                if (string.Equals(_edlProgrammerCandidates[i], previousEdl, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            pickBox.SelectedIndex = idx;
        }
        else
        {
            pickBox.SelectedIndex = -1;
        }

        var protocol = SelectedDevice?.Protocol;
        UpdateEdlProgrammerUiState();
        var isRk = protocol is DeviceProtocolKind.Rkdl;
        Field<Button>("RkEnterMaskromButton").IsEnabled = !_busy && isRk;
        Field<ComboBox>("RkStorageTypeBox").IsEnabled = !_busy;
        Field<Button>("RkExportConfigButton").IsEnabled = !_busy;
    }

    internal void UpdateEdlProgrammerUiState()
    {
        if (Surface is null) return;
        var pathBox = Field<TextBox>("EdlProgrammerPathBox");
        var chooseButton = Field<Button>("EdlChooseProgrammerButton");
        if (_isCustomProgrammer)
        {
            pathBox.Text = _edlProgrammerOverride ?? string.Empty;
            ToolTip.SetTip(pathBox, _edlProgrammerOverride ?? string.Empty);
            pathBox.IsEnabled = !_busy;
            chooseButton.IsEnabled = !_busy;
        }
        else
        {
            pathBox.Text = _edlProgrammer ?? string.Empty;
            ToolTip.SetTip(pathBox, _edlProgrammer ?? string.Empty);
            pathBox.IsEnabled = false;
            chooseButton.IsEnabled = false;
        }
    }

    internal void OnEdlProgrammerPickChanged(object? source, SelectionChangedEventArgs e)
    {
        var index = Field<ComboBox>("EdlProgrammerPickBox").SelectedIndex;
        if (index >= 0 && index < _edlProgrammerCandidates.Count)
        {
            _isCustomProgrammer = false;
            _edlProgrammer = _edlProgrammerCandidates[index];
        }
        else if (index >= 0 && index == _edlProgrammerCandidates.Count)
        {
            _isCustomProgrammer = true;
            _edlProgrammer = null;
        }
        else
        {
            _isCustomProgrammer = false;
            _edlProgrammer = null;
        }
        UpdateEdlProgrammerUiState();
        UpdateEdlButton();
    }

    internal async void OnRkEnterMaskromClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming) return;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            AppendLog(_localizer.GetString("Flash_DeviceNotConnected"));
            return;
        }

        SetBusy(true);
        try
        {
            AppendLog(_localizer.GetString("Flash_RkEnterMaskrom"));
            await App.DeviceMonitor.RunRkdlAsync(device, session =>
            {
                session.RebootMaskRom();
                return Task.FromResult(true);
            }, CancellationToken.None).ConfigureAwait(true);
            AppendLog(_localizer.GetString("Flash_OperationSucceeded"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    internal async void OnRkExportConfigClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localizer.GetString("Flash_RkExportConfig"),
            DefaultExtension = "cfg",
            SuggestedFileName = "config.cfg",
            FileTypeChoices = [
                new FilePickerFileType("Rockchip Config (*.cfg)") { Patterns = ["*.cfg", "*.txt"] },
                FilePickerFileTypes.All
            ]
        });
        if (file is null || file.TryGetLocalPath() is not { } savePath) return;

        try
        {
            SaveRkConfig(savePath);
            AppendLog($"{_localizer.GetString("Flash_RkExportConfig")} -> {Path.GetFileName(savePath)}");
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
    }

    private const string MtkStageBootRom = "brom";
    private const string MtkStagePreloader = "preloader";
    private const string MtkStageDa = "da";
    private const string MtkStageLogging = "logging";

    /// <summary>
    /// Reads the EDL protocol stage discovery actually observed; this page never probes a device itself, so an
    /// unobserved endpoint stays <see cref="EdlProtocolState.Unknown"/> and no action that needs protocol readiness is
    /// mistaken for ready.
    /// <para>取发现层实际观测到的 EDL 协议阶段；本页不自行探测设备，未观测时按 <see cref="EdlProtocolState.Unknown"/>
    /// 处理，因此任何需要协议就绪的动作都不会被误判为已就绪。</para>
    /// </summary>
    /// <value>
    /// The observed stage. <para>已观测到的阶段。</para>
    /// </value>
    private EdlProtocolState EdlStage => SelectedDevice is { Protocol: DeviceProtocolKind.Edl } device
        ? device.EdlProtocol
        : EdlProtocolState.Unknown;

    /// <summary>
    /// Reads the Sahara HELLO mode the latest EDL probe collected; this page only consumes that cache and never
    /// contacts a device to obtain a mode. The setter exists so UI tests can drive the same decision without
    /// hardware.
    /// <para>取发现层最近一次 EDL 探测附带采集到的 Sahara HELLO 模式；本页面只读该缓存，从不为获取模式而联系
    /// 设备。可替换是为了让界面测试能在没有真机的情况下驱动同一判断。</para>
    /// </summary>
    /// <value>
    /// The observed mode, or <see langword="null"/> when no probe result exists for the endpoint.
    /// <para>已观测到的模式；该端点没有探测结果时为 <see langword="null"/>。</para>
    /// </value>
    internal Func<DetectedDevice?, SaharaMode?> ObservedSaharaMode { get; set; } = device =>
        device is null ? null : App.DeviceMonitor.GetEdlDetails(device)?.Sahara?.Mode;

    /// <summary>
    /// The Firehose programmer candidates a package carries; UI tests use it to drive the candidate
    /// picker's visibility without reading a real package. Writing it does not recompute visibility,
    /// so the caller triggers the update itself.
    /// <para>包内多个 Firehose programmer 候选；界面测试用它驱动候选选择框的显隐，而不必真的读一个包。
    /// 写入后不会自动重算可见性，调用方需自行触发一次界面更新。</para>
    /// </summary>
    /// <value>
    /// The candidate file paths, empty when the package carries none.
    /// <para>候选文件路径；包内没有候选时为空。</para>
    /// </value>
    internal IReadOnlyList<string> ProgrammerCandidates
    {
        get => _edlProgrammerCandidates;
        set => _edlProgrammerCandidates = value ?? [];
    }

    /// <summary>
    /// Resolves the current MediaTek boot stage: an explicit declaration in the stage combo outranks the state string
    /// discovery reported, and while the declaration stays on "follow detection" the discovery value is used verbatim,
    /// including the empty string discovery could not decide.
    /// <para>取当前的联发科启动阶段：阶段选择框里的显式声明优先于发现层给出的状态字符串；声明停留在“按检测”档时
    /// 完全跟随发现层，包括发现层无法判定的空串。</para>
    /// </summary>
    /// <value>
    /// A stage token, or an empty string when neither discovery nor the user decided one.
    /// <para>阶段标记；发现层与用户都未给出时为空串。</para>
    /// </value>
    private string MtkStage
    {
        get
        {
            var declared = Field<ComboBox>("MtkStageBox").SelectedIndex switch
            {
                1 => MtkStageBootRom,
                2 => MtkStagePreloader,
                3 => MtkStageDa,
                _ => string.Empty,
            };
            return declared.Length > 0 ? declared : SelectedDevice?.State ?? string.Empty;
        }
    }

    /// <summary>
    /// Switches the action-pane expansion by the selected device's protocol: expands the protocol-matching
    /// expander and collapses the others; with no device selected the preparation state (the EDL bundle
    /// row and an MTK pane backed by a loaded scatter) determines the expansion.
    /// <para>按所选设备的协议切换操作面板展开状态：展开与选中设备协议匹配的分类并收起其他分类；
    /// 未选中设备时根据准备态（EDL 完整包或已加载的 MTK scatter）决定默认展开项。</para>
    /// </summary>
    private void UpdateModeVisibility()
    {
        var protocol = SelectedDevice?.Protocol;
        var isEdl = protocol is DeviceProtocolKind.Edl;
        var isFastboot = protocol is DeviceProtocolKind.Fastboot;
        var isMtk = protocol is DeviceProtocolKind.Mtk;
        var isMtkUsb = isMtk && SelectedDevice is { Transport: DeviceTransportKind.Usb };

        var edlFirehose = isEdl && EdlStage == EdlProtocolState.Firehose;
        var edlSaharaCommandMode = isEdl && EdlStage is not (EdlProtocolState.Firehose or EdlProtocolState.Streaming);

        Field<Button>("EdlSetBootableButton").IsEnabled = !_busy && edlFirehose;
        Field<Button>("EdlPartitionDigestButton").IsEnabled = !_busy && edlFirehose;

        var mtkStage = MtkStage;

        var isFastbootd =
            SelectedDevice is { Protocol: DeviceProtocolKind.Fastboot } fbDevice &&
            ((_deviceViewModel?.SelectedDevice is { } devInfo && devInfo.IsFastbootd) ||
             string.Equals(fbDevice.State, "fastbootd", StringComparison.OrdinalIgnoreCase));

        Field<CheckBox>("ShowAllPartitionsBox").IsEnabled = !_busy && isFastbootd;
        Field<CheckBox>("DeleteCowBox").IsEnabled = !_busy && isFastbootd;
        if (!isFastbootd)
        {
            Field<CheckBox>("ShowAllPartitionsBox").IsChecked = false;
            Field<CheckBox>("DeleteCowBox").IsChecked = false;
        }

        Field<CheckBox>("AddRootBox").IsEnabled = !_busy;
        UpdateRootControlsState();
        Field<Button>("ReadbackSelectedButton").IsEnabled = !_busy && (
            (protocol == DeviceProtocolKind.Edl && edlFirehose && HasSelectedEdlEntries()) ||
            (protocol == DeviceProtocolKind.Mtk && _mtkResolution is not null && Partitions.Any(p => p.IsSelected)));
        Field<DropDownButton>("RebootMenuButton").IsEnabled = !_busy;
        var mtkProtocolReady = !_busy && protocol is DeviceProtocolKind.Mtk && mtkStage != MtkStageLogging;
        Field<Button>("MtkRebootMetaButton").IsEnabled = mtkProtocolReady;
        Field<Button>("MtkRebootAndroidTestButton").IsEnabled = mtkProtocolReady;
        Field<TextBox>("MtkXmlPropertyBox").IsEnabled = mtkProtocolReady;
        Field<Button>("MtkXmlPropertyQueryButton").IsEnabled = mtkProtocolReady;

        UpdateEdlProgrammerUiState();
    }

    /// <summary>
    /// Returns whether any readable EDL entry is selected.
    /// <para>判断是否选中了可回读的 EDL 条目。</para>
    /// </summary>
    private bool HasSelectedEdlEntries()
        => Partitions.Any(row => row.IsSelected && row.CanSelect);

    private void SetBusy(bool value)
    {
        _busy = value;
        IsBusy = value;
        Field<StackPanel>("EdlPane").IsEnabled = !value;
        Field<StackPanel>("MtkPane").IsEnabled = !value;
        if (!value)
        {
            FlushPendingLog();
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlashViewModel.SelectedCount))
        {
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Enumerates the MTK serial ports on a background thread and fills the combo box back on the UI thread;
    /// the sequence number drops stale enumeration results.
    /// <para>后台线程枚举 MTK 串口，回到 UI 线程填充下拉框；序号用于丢弃过期的枚举结果。</para>
    /// </summary>
    private void RefreshMtkPorts()
    {
        var request = ++_mtkPortRequest;
        _ = ApplySerialPortsAsync(Field<ComboBox>("MtkPortBox"), ReadSerialPortsAsync(), () => _mtkPortRequest == request);
    }

    /// <summary>
    /// Enumerates the EDL serial ports on a background thread and fills the combo box back on the UI thread;
    /// the sequence number drops stale enumeration results.
    /// <para>后台线程枚举 EDL 串口，回到 UI 线程填充下拉框；序号用于丢弃过期的枚举结果。</para>
    /// </summary>
    private void RefreshEdlSerialPorts()
    {
        var request = ++_edlSerialPortRequest;
        _ = ApplySerialPortsAsync(Field<ComboBox>("EdlSerialPortBox"), ReadSerialPortsAsync(), () => _edlSerialPortRequest == request);
    }

    /// <summary>
    /// Writes the background-enumerated ports back to the combo box, keeping the current selection when it
    /// is still present. The continuation resumes on the UI thread, so touching the control is safe.
    /// <para>把后台枚举到的串口写回下拉框，尽量保留当前选中的端口。续接点在 UI 线程，因此写控件安全。</para>
    /// </summary>
    /// <param name="box">The serial port combo box to fill. <para>要填充的串口下拉框。</para></param>
    /// <param name="ports">The running enumeration task. <para>进行中的枚举任务。</para></param>
    /// <param name="isCurrent">Whether the result still belongs to the latest request. <para>结果是否仍属于最新一次请求。</para></param>
    private static async Task ApplySerialPortsAsync(ComboBox box, Task<IReadOnlyList<string>> ports,
        Func<bool> isCurrent)
    {
        var previous = box.SelectedItem as string;
        var available = await ports;
        if (!isCurrent())
        {
            return;
        }

        box.ItemsSource = available;
        box.SelectedItem = previous is { } name && available.Contains(name)
            ? previous
            : available.Count > 0 ? available[0] : null;
    }

    /// <summary>
    /// Reads the system port names on a background thread. The registry enumeration costs tens of milliseconds,
    /// and a failure is reported as "no ports" so the async pipeline never raises an unobserved exception.
    /// <para>在后台线程读取系统串口名。注册表枚举可达数十毫秒，失败时按无串口处理，不让异步管道抛出未观察异常。</para>
    /// </summary>
    /// <returns>The available port names. <para>可用串口名列表。</para></returns>
    private static async Task<IReadOnlyList<string>> ReadSerialPortsAsync()
    {
        try
        {
            return await Task.Run(SerialTransport.GetAvailablePorts);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void SetRowsQueued(IReadOnlyList<FlashPartitionEntry> rows)
    {
        _overallRows = [.. rows];
        var language = _localizer;
        foreach (var row in rows)
        {
            row.State = FlashRowState.Queued;
            row.Percent = 0;
            row.StatusText = language.GetString("Flash_Status_Queued");
        }

        UpdateOverall();
    }

    private void SetRowRunning(FlashPartitionEntry row, string textKey)
    {
        row.State = FlashRowState.Running;
        row.Percent = 0;
        row.StatusText = _localizer.GetString(textKey);
    }

    private void SetRowFinished(FlashPartitionEntry row, bool success)
    {
        var language = _localizer;
        row.State = success ? FlashRowState.Succeeded : FlashRowState.Failed;
        row.StatusText = language.GetString(success ? "Flash_Status_Succeeded" : "Flash_Status_Failed");
        if (success)
        {
            row.Percent = 100;
        }

        UpdateOverall();
    }

    private void MarkUnfinishedSkipped()
    {
        var language = _localizer;
        foreach (var row in _overallRows.Where(row => row.State is FlashRowState.Queued or FlashRowState.Running))
        {
            row.State = FlashRowState.Skipped;
            row.StatusText = language.GetString("Flash_Status_Skipped");
        }
    }

    private void UpdateOverall()
    {
        var rows = _overallRows;
        if (rows.Count == 0)
        {
            OverallPercent = 0;
            OverallText = string.Empty;
            return;
        }

        var finished = 0;
        double share = 0;
        foreach (var row in rows)
        {
            switch (row.State)
            {
                case FlashRowState.Succeeded:
                case FlashRowState.Failed:
                case FlashRowState.Skipped:
                    finished++;
                    share += 1;
                    break;
                case FlashRowState.Running:
                    share += Math.Clamp((double)row.Percent / 100d, 0d, 1d);
                    break;
            }
        }

        OverallPercent = share / rows.Count * 100d;
        OverallText = string.Format(_localizer.GetString("Flash_OverallProgress"), finished, rows.Count);
    }


    /// <summary>
    /// Formats a GPT partition table into lines, keeping at most <paramref name="maxLines"/> rows and
    /// appending a remainder note beyond that. The caller supplies the formats from the resource
    /// dictionary, so the result follows the current interface language.
    /// <para>把 GPT 分区表格式化为若干行；超过 <paramref name="maxLines"/> 时只保留前若干行并追加一条
    /// 余量提示。格式由调用方从资源字典给出，因此结果随当前界面语言变化。</para>
    /// </summary>
    /// <param name="partitions">The validated GPT partition list. <para>已校验的 GPT 分区列表。</para></param>
    /// <param name="maxLines">Maximum line count; must be positive. <para>最大行数，必须为正。</para></param>
    /// <param name="rowFormat">Per-row format (name, first sector, sector count). <para>单行格式（名称、起始扇区、扇区数）。</para></param>
    /// <param name="moreFormat">Remainder-note format (unlisted count). <para>余量提示格式（未列出的分区数）。</para></param>
    /// <returns>The log lines in order. <para>按顺序排列的日志行。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="partitions"/> 为 null。
    /// <para><paramref name="partitions"/> is null.</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLines"/> 非正。
    /// <para><paramref name="maxLines"/> is not positive.</para></exception>
    internal static List<string> FormatPartitionTable(IReadOnlyList<GptPartition> partitions, int maxLines,
        string rowFormat, string moreFormat)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(moreFormat);
        var lines = new List<string>(Math.Min(partitions.Count, maxLines + 1));
        for (var index = 0; index < partitions.Count; index++)
        {
            if (index == maxLines)
            {
                lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, moreFormat,
                    partitions.Count - index));
                break;
            }

            var partition = partitions[index];
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, rowFormat,
                partition.Name, partition.FirstLba, partition.LastLba - partition.FirstLba + 1));
        }

        return lines;
    }

    private void AppendPartitionTable(IReadOnlyList<GptPartition> partitions)
    {
        var language = _localizer;
        foreach (var line in FormatPartitionTable(partitions, MaxPartitionTableLines,
                     language.GetString("Flash_PartitionTableRow"),
                     language.GetString("Flash_PartitionTableMore")))
        {
            AppendLog(line);
        }
    }

    internal async void OnReadInfoClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { Protocol: DeviceProtocolKind.Mtk, Transport: DeviceTransportKind.Usb })
        {
            await RunMtkDeviceInfoAsync();
            return;
        }

        if (SelectedDevice is { Protocol: DeviceProtocolKind.Rkdl } rkdl && !_busy && !_confirming)
        {
            SetBusy(true);
            try
            {
                await RunRkReadInfoAsync(rkdl);
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally
            {
                SetBusy(false);
                UpdateEdlButton();
            }

            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device || _busy || _confirming) return;
        SetBusy(true);
        try
        {
            var partitions = await FastbootPartitionReader.ReadAsync(device,
                Field<CheckBox>("ShowAllPartitionsBox").IsChecked == true, CancellationToken.None);
            Partitions.Clear();
            _loadedPath = string.Empty;
            _requiredCodeName = null;
            _requiresFastbootd = false;
            _payloadPath = null;
            _remotePayloadUrl = null;
            _superPath = null;
            _vendorPath = null;
            _vendorEntries.Clear();
            _oppoPath = null;
            _oppoEntries.Clear();
            _mtkResolution = null;
            _mtkRows.Clear();
            _mtkPartitions.Clear();
            UpdateModeVisibility();
            Field<Button>("ExtractSelectedButton").IsEnabled = false;
            Field<Button>("BuildWiredPackageButton").IsEnabled = false;
            PackagePath = string.Empty;
            var deviceRows = new List<FlashPartitionEntry>(partitions.Count);
            foreach (var (name, size) in partitions)
            {
                deviceRows.Add(new FlashPartitionEntry
                {
                    Name = name,
                    Size = $"{size / 1024d / 1024d:F1} MiB",
                    CanSelect = true
                });
            }
            Partitions.Apply(0, deviceRows);
            AppendLog($"{partitions.Count} {_localizer.GetString("Flash_PartitionsLoaded")}");
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task RunMtkDeviceInfoAsync()
    {
        var language = _localizer;
        await RunWithReadyMtkOperationAsync(async (session, token) =>
        {
            if (SelectedMtkProtocol != MtkDaProtocol.XFlash)
            {
                AppendLog(language.GetString("Flash_MtkSpeedFamilyUnsupported"));
                return;
            }

            var label = language.GetString("Flash_MtkDevCtrlGroup");
            if (session.DeviceInfo is { } identity)
            {
                if (identity.BlVersion is { } blVersion)
                {
                    AppendLog($"{label} · {language.GetString("Flash_MtkBlVersion")}: 0x{blVersion:X2}");
                }

                if (identity.BromVersion is { } bromVersion)
                {
                    AppendLog($"{label} · {language.GetString("Flash_MtkBromVersion")}: 0x{bromVersion:X2}");
                }

                if (identity.Meid is { Length: > 0 } meid)
                {
                    AppendLog($"{label} · {language.GetString("Flash_MtkMeid")}: {Convert.ToHexString(meid)}");
                }

                if (identity.Socid is { Length: > 0 } socid)
                {
                    AppendLog($"{label} · {language.GetString("Flash_MtkSocid")}: {Convert.ToHexString(socid)}");
                }
            }

            async Task QueryAsync(string key, Func<CancellationToken, Task<string>> format,
                CancellationToken queryToken)
            {
                try
                {
                    AppendLog($"{label} · {language.GetString(key)}: {await format(queryToken).ConfigureAwait(true)}");
                }
                catch (Exception failure)
                {
                    AppendLog(failure.Message);
                }
            }

            var ram = await session.GetXFlashRamInfoAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkRamInfo")}: {ram}");
            var randomId = await session.GetXFlashRandomIdAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkRandomId")}: {Convert.ToHexString(randomId)}");
            var life = await session.GetXFlashStorageLifeCycleCheckAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkStorageLife")}: 0x{life:X8}");
            var expire = await session.GetXFlashExpireDateAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkExpireDate")}: {Convert.ToHexString(expire)}");
            var category = await session.GetXFlashPartitionTableCategoryAsync(token).ConfigureAwait(true);
            AppendLog($"{label} · {language.GetString("Flash_MtkPartitionCategory")}: {category}");
            await QueryAsync("Flash_MtkDaVersion",
                async queryToken => await session.GetXFlashDownloadAgentVersionAsync(queryToken).ConfigureAwait(true),
                token).ConfigureAwait(true);
            await QueryAsync("Flash_MtkExpireData",
                async queryToken => Convert.ToHexString(
                    await session.GetXFlashExpireDataAsync(queryToken).ConfigureAwait(true)),
                token).ConfigureAwait(true);
            await QueryAsync("Flash_MtkOtpLockStatus",
                async queryToken => Convert.ToHexString(
                    await session.GetXFlashOtpLockStatusAsync(queryToken).ConfigureAwait(true)),
                token).ConfigureAwait(true);
            await QueryAsync("Flash_MtkBatteryVoltage",
                async queryToken => Convert.ToHexString(
                    await session.GetXFlashBatteryVoltageAsync(queryToken).ConfigureAwait(true)),
                token).ConfigureAwait(true);
            await QueryAsync("Flash_MtkDramType",
                async queryToken => Convert.ToHexString(
                    await session.GetXFlashDramTypeAsync(queryToken).ConfigureAwait(true)),
                token).ConfigureAwait(true);
            await QueryAsync("Flash_MtkErrorDetail",
                async queryToken => Convert.ToHexString(
                    await session.GetXFlashErrorDetailAsync(queryToken).ConfigureAwait(true)),
                token).ConfigureAwait(true);
        });
    }

    /// <summary>
    /// Dispatches the flash request to the protocol that owns it: MediaTek runs its own plan, an EDL target (or a
    /// package loaded while no device is picked) runs the EDL bundle, and every other transport flashes the selected
    /// rows so a fastboot device can take an after-sales package partition by partition.
    /// <para>把刷写请求分发给所属协议：联发科走自己的计划；EDL 目标（或未选设备时加载的包）走 EDL 完整包；
    /// 其他传输按勾选逐行刷写，使 fastboot 设备可以逐个分区刷入售后包。</para>
    /// </summary>
    internal async void OnFlashSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice?.Protocol == DeviceProtocolKind.Mtk || _mtkResolution is not null)
        {
            OnMtkFlashClick(sender, e);
        }
        else if (SelectedDevice?.Protocol == DeviceProtocolKind.Edl ||
                 SelectedDevice is null && (_flatBinPackage || _edlEntries.Count > 0))
        {
            OnFlashEdlBundleClick(sender, e);
        }
        else
        {
            await RunSelectedAsync(erase: false);
        }
    }

    internal async void OnFlashEdlBundleClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || EffectiveEdlProgrammer is null || !_flatBinPackage ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner ||
            !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        await RunEdlBundleAsync(useSerialPort: false, device, serialPort: null, owner);
    }

    /// <summary>
    /// <para>执行完整 EDL 包刷写：USB 路径复扫设备后再刷，串口路径复核端口仍在；两条路径共用
    /// 全部选项、调优参数、进度与取消。</para>
    /// Runs a full EDL bundle flash: the USB path re-scans the device first and the serial path
    /// re-checks that the port is still present; both paths share every option, tuning value,
    /// progress report and cancellation.
    /// </summary>
    /// <param name="useSerialPort">Whether the run targets an explicit serial port.
    /// <para>本次运行是否针对显式串口。</para></param>
    /// <param name="device">The confirmed USB EDL device, or null for a serial run.
    /// <para>已确认的 USB EDL 设备；串口运行为 null。</para></param>
    /// <param name="serialPort">The confirmed serial port, or null for a USB run.
    /// <para>已确认的串口；USB 运行为 null。</para></param>
    /// <param name="owner">The window owning the confirmation dialogs. <para>持有确认对话框的窗口。</para></param>
    private async Task RunEdlBundleAsync(bool useSerialPort, DetectedDevice? device, string? serialPort, Window owner)
    {
        var language = _localizer;
        var programmerPath = EffectiveEdlProgrammer;
        if (programmerPath is null || !_flatBinPackage) return;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_FlashEdlBundle"),
                useSerialPort ? serialPort! : device!.DisplayName);
        }
        finally { _confirming = false; }
        if (!confirmed || PackagePath != _loadedPath) return;
        if (useSerialPort)
        {
            if (Field<ComboBox>("EdlSerialPortBox").SelectedItem is not string currentPort || currentPort != serialPort) return;
        }
        else if (SelectedDevice is not DetectedDevice current || current.Key != device!.Key ||
                 (await Task.Run(App.DeviceMonitor.Scan)).All(found =>
                     found.Key != device.Key || found.Protocol != DeviceProtocolKind.Edl))
        {
            return;
        }

        if (!TryReadEdlBundleTuning(out var bundleTuning)) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("FlashSelectedButton").IsEnabled = false;
        Field<Button>("FlashEdlBundleButton").IsEnabled = false;
        Field<Button>("EdlSerialFlashButton").IsEnabled = false;
        Field<TextBox>("PackagePathBox").IsEnabled = false;
        FlashPartitionEntry? bundleActiveRow = null;
        try
        {
            var selectedEdlLabels = Partitions
                .Where(row => row.IsSelected && row.CanSelect && _edlEntries.ContainsKey(row))
                .Select(row => _edlEntries[row].Label)
                .Where(label => label.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bundleLabels = selectedEdlLabels.Length > 0 && selectedEdlLabels.Length < _edlEntries.Count
                ? selectedEdlLabels
                : null;
            var bundleRows = Partitions
                .Where(row => row.CanSelect && _edlEntries.TryGetValue(row, out var entry) &&
                    (bundleLabels is null || (entry.Label.Length > 0 &&
                        bundleLabels.Contains(entry.Label, StringComparer.OrdinalIgnoreCase))))
                .ToArray();
            if (bundleRows.Length > 0)
            {
                SetRowsQueued(bundleRows);
            }

            var rowsByName = new Dictionary<string, FlashPartitionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in bundleRows)
            {
                var entry = _edlEntries[row];
                var displayName = entry.Label.Length > 0 ? entry.Label : entry.Filename;
                if (displayName.Length > 0)
                {
                    rowsByName.TryAdd(displayName, row);
                }
            }

            var progress = new Progress<FlashBundleProgress>(update =>
            {
                if (update.Phase is FlashBundlePhase.Programming or FlashBundlePhase.Erasing or FlashBundlePhase.Patching)
                {
                    var matched = MatchBundleRow(rowsByName, update.Message);
                    if (matched is not null && !ReferenceEquals(matched, bundleActiveRow))
                    {
                        if (bundleActiveRow is { State: FlashRowState.Running })
                        {
                            SetRowFinished(bundleActiveRow, success: true);
                        }

                        bundleActiveRow = matched;
                        SetRowRunning(matched, "Flash_Status_Flashing");
                    }

                    if (bundleActiveRow is { State: FlashRowState.Running } running && update.Total > 0)
                    {
                        running.Percent = (int)Math.Clamp(update.Current * 100d / update.Total, 0d, 100d);
                        UpdateOverall();
                    }
                }

                if (update.Phase is not FlashBundlePhase.Parsing)
                {
                    AppendLog(update.Message);
                }
            });
            var warnings = useSerialPort
                ? await App.DeviceMonitor.FlashEdlSerialAsync(
                    new SerialConnectionOptions
                    {
                        PortName = serialPort!,
                        BaudRate = Field<ComboBox>("EdlSerialBaudBox").SelectedItem is int baud ? baud : 115200,
                        Handshake = Handshake.None
                    },
                    _loadedPath, programmerPath,
                    configuration: null, includeUfsProvision: true,
                    cancellationToken: cancellation.Token,
                    continueOnFailure: Field<CheckBox>("ContinueOnFailureBox").IsChecked == true,
                    skipAbsentSources: Field<CheckBox>("SkipAbsentSourcesBox").IsChecked == true,
                    labels: bundleLabels, progress: progress,
                    verify: Field<CheckBox>("VerifyAfterFlashBox").IsChecked == true, tuning: bundleTuning)
                : await App.DeviceMonitor.FlashEdlBundleAsync(device!, _loadedPath, programmerPath,
                    cancellation.Token, Field<CheckBox>("ContinueOnFailureBox").IsChecked == true,
                    Field<CheckBox>("SkipAbsentSourcesBox").IsChecked == true, bundleLabels, progress,
                    Field<CheckBox>("VerifyAfterFlashBox").IsChecked == true, skipUfsProvision: false, bundleTuning,
                    initialization);
            if (bundleActiveRow is { State: FlashRowState.Running })
            {
                SetRowFinished(bundleActiveRow, success: true);
            }

            foreach (var row in _overallRows.Where(row =>
                         row.State is FlashRowState.Queued or FlashRowState.Running))
            {
                SetRowFinished(row, success: true);
            }

            AppendLog(string.Format(_localizer.GetString("Flash_OkResult"),
                _localizer.GetString("Flash_FlashEdlBundle")));
            foreach (var warning in warnings)
            {
                AppendLog($"{_localizer.GetString("Flash_WarningPrefix")}: {warning}");
            }
        }
        catch (Exception ex)
        {
            if (bundleActiveRow is { State: FlashRowState.Running })
            {
                SetRowFinished(bundleActiveRow, success: false);
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            AppendLog(ex.Message);
        }
        finally
        {
            _flashCancellation = null;
            SetBusy(false);
            Field<Button>("CancelFlashButton").IsEnabled = false;
            Field<Button>("FlashSelectedButton").IsEnabled = true;
            UpdateEdlButton();
            Field<TextBox>("PackagePathBox").IsEnabled = true;
        }
    }

    /// <summary>
    /// Reads the three bundle tuning controls and reports the validation outcome; the leading-wipe
    /// input must be a non-negative integer and the failure reason is written to the log.
    /// <para>读取整包三项调优控件并给出校验结论；前导清零输入必须是非负整数，失败原因写入日志。</para>
    /// </summary>
    /// <param name="tuning"><para>校验通过时输出的调优参数，否则输出未校验的默认值。</para>
    /// The tuning values on success, or unvalidated defaults on failure.</param>
    /// <returns>True when the input is valid. <para>输入合法时为 true。</para></returns>
    internal bool TryReadEdlBundleTuning(out EdlBundleTuning tuning)
    {
        tuning = new EdlBundleTuning();
        if (!long.TryParse(Field<TextBox>("EdlWipeFirstBox").Text, out var wipeFirstSectors) || wipeFirstSectors < 0)
        {
            AppendLog(_localizer.GetString("Flash_EdlWipeFirstInvalid"));
            return false;
        }

        long? maxPayload = null;
        long? pagesPerBlock = null;
        if (!string.IsNullOrWhiteSpace(Field<TextBox>("EdlMaxPayloadBox").Text))
        {
            if (!long.TryParse(Field<TextBox>("EdlMaxPayloadBox").Text, out var parsedMaxPayload) ||
                parsedMaxPayload is < 512 or > 16777216 || parsedMaxPayload % 512 != 0)
            {
                AppendLog(_localizer.GetString("Flash_EdlMaxPayloadInvalid"));
                return false;
            }

            maxPayload = parsedMaxPayload;
        }

        if (!string.IsNullOrWhiteSpace(Field<TextBox>("EdlPagesPerBlockBox").Text))
        {
            if (!long.TryParse(Field<TextBox>("EdlPagesPerBlockBox").Text, out var parsedPagesPerBlock) ||
                parsedPagesPerBlock is < 0 or > 65535)
            {
                AppendLog(_localizer.GetString("Flash_EdlPagesPerBlockInvalid"));
                return false;
            }

            pagesPerBlock = parsedPagesPerBlock == 0 ? null : parsedPagesPerBlock;
        }

        tuning = new EdlBundleTuning
        {
            OverrideSectorSize = Field<ComboBox>("EdlSectorSizeBox").SelectedIndex switch
            {
                1 => 512L,
                2 => 2048L,
                3 => 4096L,
                _ => null
            },
            Slot = Field<ComboBox>("EdlSlotBox").SelectedIndex > 0 ? Field<ComboBox>("EdlSlotBox").SelectedIndex : null,
            WipeFirstSectors = wipeFirstSectors,
            MaxProgramChunkBytes = maxPayload,
            NandPagesPerBlock = pagesPerBlock,
            ConfirmManifestWipe = Field<CheckBox>("EdlWipeConfirmBox").IsChecked == true,
            AllowEntireStorageWipe = Field<CheckBox>("EdlWipeEntireBox").IsChecked == true
        };
        return true;
    }

    /// <summary>
    /// Reads the expert-surface partition-entry array dimension overrides: with the switch off no box is read and
    /// all three values are null, and with it on a blank or 0 also outputs null so the headers report their own
    /// dimensions. The LBA must be a whole number of at least 2, the entry count must be between 1 and 65536, and
    /// the entry size must be a power of two from 128 through 4096; any invalid field logs its own localized
    /// diagnostic and returns false.
    /// <para>读取 expert 面的分区条目数组维度覆盖项：开关关闭时不读取任何文本框并输出三个 null；开启后空白或 0
    /// 同样输出 null，也就是沿用头部自报值。LBA 必须是不小于 2 的整数，条目数量必须是 1 至 65536，条目大小必须是
    /// 128 至 4096 之间 2 的幂；任一项不合法就记录对应的本地化提示并返回 false。</para>
    /// </summary>
    /// <param name="partitionEntryLba"><para>覆盖后的条目数组起始 LBA；沿用头部自报值时为 null。</para>
    /// The overridden entry-array LBA, or null when the header value is kept.</param>
    /// <param name="partitionEntryCount"><para>覆盖后的分区条目数量；沿用头部自报值时为 null。</para>
    /// The overridden partition-entry count, or null when the header value is kept.</param>
    /// <param name="partitionEntrySize"><para>覆盖后的分区条目大小；沿用头部自报值时为 null。</para>
    /// The overridden partition-entry size, or null when the header value is kept.</param>
    /// <returns>True when every supplied dimension is valid. <para>输入合法时为 true。</para></returns>
    internal bool TryReadEdlGptEntryOverrides(out ulong? partitionEntryLba, out uint? partitionEntryCount,
        out uint? partitionEntrySize)
    {
        partitionEntryLba = null;
        partitionEntryCount = null;
        partitionEntrySize = null;

        if (!TryReadEdlExpertNumber(Field<TextBox>("EdlGptEntryLbaBox").Text, out var requestedLba) || requestedLba is < 0 or 1)
        {
            AppendLog(_localizer.GetString("Flash_EdlGptEntryLbaInvalid"));
            return false;
        }

        partitionEntryLba = requestedLba == 0 ? null : (ulong?)requestedLba;

        if (!TryReadEdlExpertNumber(Field<TextBox>("EdlGptEntryCountBox").Text, out var requestedCount)
            || requestedCount is < 0 or > 65536)
        {
            AppendLog(_localizer.GetString("Flash_EdlGptEntryCountInvalid"));
            return false;
        }

        partitionEntryCount = requestedCount == 0 ? null : (uint?)requestedCount;

        if (!TryReadEdlExpertNumber(Field<TextBox>("EdlGptEntrySizeBox").Text, out var requestedSize)
            || (requestedSize != 0 && (requestedSize is < 128 or > 4096
                || (requestedSize & (requestedSize - 1)) != 0)))
        {
            AppendLog(_localizer.GetString("Flash_EdlGptEntrySizeInvalid"));
            return false;
        }

        partitionEntrySize = requestedSize == 0 ? null : (uint?)requestedSize;
        return true;
    }

    /// <summary>
    /// Parses an expert override box: blank parses as 0, which requests no override, and a non-number returns false
    /// so the caller rejects it with the diagnostic for its own field.
    /// <para>把 expert 覆盖框的文本解析为整数：空白解析为 0，也就是不覆盖；非数字返回 false，由调用方按各自字段的
    /// 本地化提示拒绝。</para>
    /// </summary>
    /// <param name="text">The box text. <para>文本框内容。</para></param>
    /// <param name="value">The parsed value, or 0 when the box is blank. <para>解析结果；空白时为 0。</para></param>
    /// <returns>True when the text is blank or an integer. <para>空白或可解析为整数时为 true。</para></returns>
    private static bool TryReadEdlExpertNumber(string? text, out long value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return true;
        }

        return long.TryParse(text, out value);
    }

    private static FlashPartitionEntry? MatchBundleRow(
        IReadOnlyDictionary<string, FlashPartitionEntry> rowsByName, string message)
    {
        FlashPartitionEntry? matched = null;
        var matchedLength = 0;
        foreach (var (name, row) in rowsByName)
        {
            if (name.Length > matchedLength && message.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                matched = row;
                matchedLength = name.Length;
            }
        }

        return matched;
    }

    internal async void OnEraseSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice?.Protocol == DeviceProtocolKind.Mtk || _mtkResolution is not null)
        {
            OnMtkEraseClick(sender, e);
        }
        else if (SelectedDevice?.Protocol == DeviceProtocolKind.Edl || _edlEntries.Count > 0)
        {
            OnEdlEraseClick(sender, e);
        }
        else
        {
            await RunSelectedAsync(erase: true);
        }
    }

    internal void OnSlotResetCurrentClick(object? sender, RoutedEventArgs e)
        => RunSwitchSlotActionAsync(SlotAction.ResetCurrent);

    internal void OnSlotSetAClick(object? sender, RoutedEventArgs e)
        => RunSwitchSlotActionAsync(SlotAction.SetA);

    internal void OnSlotSetBClick(object? sender, RoutedEventArgs e)
        => RunSwitchSlotActionAsync(SlotAction.SetB);

    private enum SlotAction { ResetCurrent, SetA, SetB }

    private async void RunSwitchSlotActionAsync(SlotAction action)
    {
        if (_busy || _confirming || SelectedDevice is not { } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        _confirming = true;
        bool confirmed;
        try { confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_SwitchSlot"), device.DisplayName); }
        finally { _confirming = false; }
        if (!confirmed) return;

        if (device.Protocol == DeviceProtocolKind.Fastboot)
        {
            if (SelectedDevice is not DetectedDevice current || current.Key != device.Key ||
                (await Task.Run(App.DeviceMonitor.Scan)).All(found => found.Key != device.Key ||
                    found.Protocol != DeviceProtocolKind.Fastboot))
            {
                AppendLog(language.GetString("Flash_DeviceChanged"));
                return;
            }
            SetBusy(true);
            Field<Button>("SwitchSlotButton").IsEnabled = false;
            try
            {
                var slot = action switch
                {
                    SlotAction.ResetCurrent => await FastbootSlotService.ResetCurrentAsync(device, CancellationToken.None),
                    SlotAction.SetA => await FastbootSlotService.SetActiveSlotAsync(device, "a", CancellationToken.None),
                    SlotAction.SetB => await FastbootSlotService.SetActiveSlotAsync(device, "b", CancellationToken.None),
                    _ => await FastbootSlotService.SetOtherAsync(device, CancellationToken.None)
                };
                AppendLog($"{language.GetString("Flash_ActiveSlot")}: {slot}");
            }
            catch (Exception ex) { AppendLog(ex.Message); }
            finally
            {
                SetBusy(false);
                Field<Button>("SwitchSlotButton").IsEnabled = true;
            }
        }
        else if (device.Protocol == DeviceProtocolKind.Edl)
        {
            var targetSlot = action switch
            {
                SlotAction.SetA => "a",
                SlotAction.SetB => "b",
                _ => "a"
            };
            await RunEdlPowerRequestAsync("Flash_SwitchSlot", "Flash_EdlSetActiveSlotConfirm",
                "Flash_EdlSetActiveSlotDone",
                (firehose, cancellationToken) => firehose.SetActivePartitionAsync(targetSlot, cancellationToken));
        }
        else
        {
            AppendLog(language.GetString("Flash_ModeMismatch"));
        }
    }

    internal void OnCancelFlashClick(object? sender, RoutedEventArgs e) => _flashCancellation?.Cancel();

    private async Task RunSelectedAsync(bool erase)
    {
        if (_busy || _confirming || SelectedDevice is not DetectedDevice device) return;
        if (!string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            AppendLog(_localizer.GetString("Flash_PackageChanged"));
            return;
        }
        var selected = Partitions.Where(entry => entry.IsSelected && entry.CanSelect &&
            (!erase || entry.Name != "reboot" && entry.Step?.Command is not ("set_active" or "reboot" or "wipe_super"))).ToArray();
        if (selected.Length == 0) return;
        var rkImageDir = selected.FirstOrDefault(row => row.FilePath.Length > 0) is { } firstRow
            ? Path.GetDirectoryName(Path.GetFullPath(firstRow.FilePath)) ?? string.Empty
            : string.Empty;
        if (!FlashModeAllowed(device.Protocol, _flatBinPackage,
                EffectiveEdlProgrammer is not null,
                rkImageDir.Length > 0 && FlashService.HasRockchipParameter(rkImageDir)) ||
            device.Protocol == DeviceProtocolKind.Adb && !await FlashSuService.HasSuAsync(device, CancellationToken.None))
        {
            AppendLog(_localizer.GetString("Flash_ModeMismatch"));
            return;
        }
        var plainOptions = erase || Field<CheckBox>("DeleteCowBox").IsChecked == true || Field<CheckBox>("EraseDataBox").IsChecked == true ||
            Field<CheckBox>("RebootAfterBox").IsChecked == true || Field<CheckBox>("DisableVbmetaBox").IsChecked == true || Field<CheckBox>("AddRootBox").IsChecked == true;
        if (device.Protocol != DeviceProtocolKind.Fastboot && plainOptions)
        {
            AppendLog(_localizer.GetString("Flash_ModeMismatch"));
            return;
        }
        var canExtract = _payloadPath is not null || _remotePayloadUrl is not null ||
            _superPath is not null || _vendorPath is not null || _oppoPath is not null;
        if (!erase && selected.Any(entry => (entry.Step?.Command ?? "flash") is "flash" or "wipe_super" &&
            !File.Exists(entry.FilePath) && (!canExtract || entry.FilePath.Length != 0)))
        {
            AppendLog(_localizer.GetString("Flash_FileMissing"));
            return;
        }
        if (TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        var deleteCow = !erase && Field<CheckBox>("DeleteCowBox").IsChecked == true;
        var eraseData = !erase && Field<CheckBox>("EraseDataBox").IsChecked == true;
        var rebootAfter = !erase && Field<CheckBox>("RebootAfterBox").IsChecked == true;
        var disableVbmeta = !erase && Field<CheckBox>("DisableVbmetaBox").IsChecked == true;
        var addRoot = !erase && Field<CheckBox>("AddRootBox").IsChecked == true;
        var verifyRequested = !erase && Field<CheckBox>("VerifyAfterFlashBox").IsChecked == true;
        var verify = verifyRequested && device.Protocol is DeviceProtocolKind.Edl or DeviceProtocolKind.Rkdl;
        if (disableVbmeta && !selected.Any(entry => (entry.Step?.Command ?? "flash") == "flash" &&
            entry.Name.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog(language.GetString("Flash_VbmetaMissing"));
            return;
        }
        var rootEntry = addRoot
            ? FindRootPartitionEntry(selected, Field<ComboBox>("RootPartitionBox").SelectedItem as string)
            : null;
        if (addRoot && rootEntry is null)
        {
            AppendLog(language.GetString("Flash_RootPartitionNotSelected"));
            return;
        }

        if (selected.Any(entry => entry.Step?.Command == "reboot") && (eraseData || rebootAfter))
        {
            AppendLog(language.GetString("Flash_RebootConflict"));
            return;
        }
        if (!TryBuildEdlInitialization(out var initialization))
        {
            return;
        }

        if (!TryReadEdlGptEntryOverrides(out var gptEntryLba, out var gptEntryCount, out var gptEntrySize))
        {
            return;
        }

        var description = $"{device.DisplayName}: {string.Join(", ", selected.Select(entry => entry.Name))}";
        if (deleteCow) description += Environment.NewLine + language.GetString("Flash_DeleteCow");
        if (eraseData) description += Environment.NewLine + language.GetString("Flash_EraseData");
        if (rebootAfter) description += Environment.NewLine + language.GetString("Flash_RebootAfter");
        if (disableVbmeta) description += Environment.NewLine + language.GetString("Flash_DisableVbmeta");
        if (addRoot && rootEntry is { } rootTarget)
        {
            description += Environment.NewLine + language.GetString("Flash_AddRoot") + ": " + rootTarget.Name;
            if (_rootPackagePath is { } chosenPackage)
            {
                description += " <- " + Path.GetFileName(chosenPackage);
            }
        }
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString(erase ? "Flash_EraseSelected" : "Flash_FlashSelected"), description);
        }
        finally { _confirming = false; }
        if (!confirmed || selected.Any(entry => !entry.IsSelected || !Partitions.Contains(entry))) return;
        if (!string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal))
        {
            AppendLog(language.GetString("Flash_PackageChanged"));
            return;
        }
        if (SelectedDevice is not DetectedDevice current || current.Key != device.Key ||
            (await Task.Run(App.DeviceMonitor.Scan)).All(found => found.Key != device.Key || found.Protocol != device.Protocol))
        {
            AppendLog(language.GetString("Flash_DeviceChanged"));
            return;
        }

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("FlashSelectedButton").IsEnabled = false;
        Field<Button>("EraseSelectedButton").IsEnabled = false;
        Field<Button>("SwitchSlotButton").IsEnabled = false;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<TextBox>("PackagePathBox").IsEnabled = false;
        Field<Button>("ChooseFileButton").IsEnabled = false;
        Field<Button>("ChooseFolderButton").IsEnabled = false;
        Field<DataGrid>("PartitionGrid").IsEnabled = false;
        List<string> staged = [];
        string? extractionRoot = null;
        try
        {
            if (verifyRequested && !verify)
            {
                AppendLog(language.GetString("Flash_VerifyUnsupported"));
            }

            var rowsByName = new Dictionary<string, FlashPartitionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in selected)
            {
                rowsByName.TryAdd(row.Name, row);
            }

            var progress = new Progress<FlashProgress>(update =>
            {
                if (update.Partition is { } name && rowsByName.TryGetValue(name, out var row) &&
                    row.State == FlashRowState.Running)
                {
                    row.Percent = update.Percent;
                    UpdateOverall();
                }

                if (update.Percent is 0 or 100)
                {
                    AppendLog($"{update.Partition}: {update.Status}");
                }
            });
            SetRowsQueued(selected);
            if (device.Protocol is DeviceProtocolKind.Edl or DeviceProtocolKind.Adb)
            {
                var adbRootMode = device.Protocol == DeviceProtocolKind.Adb
                    ? await FlashSuService.ProbeRootModeAsync(device, cancellation.Token)
                    : AdbRootMode.None;
                if (device.Protocol == DeviceProtocolKind.Adb && adbRootMode == AdbRootMode.None)
                {
                    AppendLog(language.GetString("Flash_ModeMismatch"));
                    return;
                }
                if (device.Protocol == DeviceProtocolKind.Edl)
                {
                    await App.DeviceMonitor.RunEdlAsync(device, async edl =>
                    {
                        FirehoseGptSnapshot? deviceGpt = null;
                        foreach (var row in selected)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var manifestEntry = _edlEntries.TryGetValue(row, out var mapped) ? mapped : null;
                            if (manifestEntry is not null && (!File.Exists(row.FilePath) ||
                                !Path.GetFullPath(manifestEntry.SourceFile).Equals(
                                    Path.GetFullPath(row.FilePath), StringComparison.OrdinalIgnoreCase)))
                            {
                                throw new InvalidDataException(string.Format(
                                    language.GetString("Flash_EdlImageMismatch"), row.Name));
                            }

                            SetRowRunning(row, "Flash_Status_Flashing");
                            FlashResult result;
                            if (manifestEntry is { } entry)
                            {
                                result = await FlashService.Default.FlashEdlPartitionAsync(
                                    edl, entry, Path.GetDirectoryName(row.FilePath)!,
                                    EffectiveEdlProgrammer, progress, cancellation.Token, verify,
                                    initialization);
                            }
                            else
                            {
                                var firehose = await ConfigureEdlAsync(edl, initialization,
                                    cancellation.Token).ConfigureAwait(true);
                                var snapshot = deviceGpt ??= await firehose.ReadGptWithPartitionEntryOverridesAsync(
                                    SelectedEdlLun, gptEntryLba, gptEntryCount, gptEntrySize,
                                    cancellation.Token).ConfigureAwait(true);
                                var written = await firehose.ProgramRawPartitionFromFileAsync(snapshot, row.Name,
                                    row.FilePath, verify: verify, cancellationToken: cancellation.Token).ConfigureAwait(true);
                                result = new FlashResult
                                {
                                    Success = true,
                                    Summary = string.Format(language.GetString("Flash_EdlGptProgrammed"),
                                        row.Name, written.AcknowledgedBytes, written.VerifiedBytes)
                                };
                            }

                            AppendLog(result.Summary);
                            SetRowFinished(row, result.Success);
                            if (!result.Success) break;
                        }
                        return true;
                    }, cancellation.Token);
                }
                else
                {
                    foreach (var row in selected)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        SetRowRunning(row, "Flash_Status_Flashing");
                        var result = await FlashSuService.FlashAsync(device, row.Name, row.FilePath,
                            adbRootMode, cancellation.Token);
                        AppendLog(result);
                        SetRowFinished(row, success: true);
                    }
                }
                return;
            }
            var target = new DeviceInfo(device);
            if (_requiredCodeName is { } required)
            {
                var product = await FlashService.Default.GetDevicePropertyAsync(target, "product", cancellation.Token);
                if (!product.Success ||
                    !string.Equals(product.Summary, $"product={required}", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(language.GetString("Flash_ProductMismatch"));
                    return;
                }
            }
            if (_requiresFastbootd)
            {
                var userspace = await FlashService.Default.GetDevicePropertyAsync(target, "is-userspace", cancellation.Token);
                if (!userspace.Success || !userspace.Summary.Equals("is-userspace=yes", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog(language.GetString("Flash_RequiresFastbootd"));
                    return;
                }
            }
            Dictionary<FlashPartitionEntry, string> prepared = [];
            Dictionary<FlashPartitionEntry, PreparedSuperEmpty> preparedSuper = [];
            if (!erase)
            {
                foreach (var entry in selected.Where(entry => entry.Step?.Command == "wipe_super"))
                    preparedSuper[entry] = FlashSuperEmptyService.Prepare(entry.FilePath);
                var missingImages = selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
                    entry.FilePath.Length == 0).ToArray();
                if (missingImages.Length > 0)
                {
                    extractionRoot = Path.Combine(Path.GetTempPath(), "uotan-flash-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(extractionRoot);
                    var names = missingImages.Select(entry => entry.Name).ToArray();
                    var output = _payloadPath is { } payload
                        ? await FlashPayloadService.ExtractAsync(payload, names, extractionRoot, cancellation.Token)
                        : _remotePayloadUrl is { } remote
                            ? await new FlashRemotePayloadService(RemoteHttpClient)
                                .ExtractAsync(remote, names, extractionRoot, cancellation.Token)
                        : _superPath is { } super
                            ? await FlashSuperService.ExtractAsync(super, names, extractionRoot, cancellation.Token)
                        : _vendorPath is { } vendor
                            ? await FlashVendorContainerService.ExtractAsync(vendor,
                                [.. names.Select(name => _vendorEntries[name])], extractionRoot, cancellation.Token)
                        : await FlashOppoContainerService.ExtractAsync(_oppoPath!,
                            [.. names.Select(name => _oppoEntries[name].Entry)], extractionRoot, cancellation.Token);
                    foreach (var entry in missingImages)
                    {
                        var filename = _vendorPath is not null ? _vendorEntries[entry.Name]
                            : _oppoPath is not null ? _oppoEntries[entry.Name].OutputName
                            : entry.Name + ".img";
                        var image = Path.Combine(output, filename);
                        if (!File.Exists(image))
                        {
                            throw new InvalidDataException(string.Format(
                                language.GetString("Flash_MissingExtractedImage"), filename));
                        }
                        prepared[entry] = image;
                    }
                }
                foreach (var entry in selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
                    prepared.GetValueOrDefault(entry, entry.FilePath).EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    AppendLog($"{entry.FileName}: {language.GetString("Flash_Decompressing")}");
                    var path = await FlashCompressedImage.PrepareAsync(
                        prepared.GetValueOrDefault(entry, entry.FilePath), cancellation.Token);
                    staged.Add(path);
                    prepared[entry] = path;
                }
                if (disableVbmeta)
                {
                    foreach (var entry in selected.Where(entry => (entry.Step?.Command ?? "flash") == "flash" &&
                        entry.Name.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase)))
                    {
                        var source = prepared.GetValueOrDefault(entry, entry.FilePath);
                        var output = await FlashVbmetaImage.PrepareAsync(source, cancellation.Token);
                        staged.Add(output);
                        prepared[entry] = output;
                    }
                }
                if (addRoot && rootEntry is { } patchedEntry)
                {
                    var rootPackage = await ResolveRootPackageAsync(cancellation.Token);
                    var output = await FlashRootImage.PrepareAsync(rootPackage,
                        prepared.GetValueOrDefault(patchedEntry, patchedEntry.FilePath), cancellation.Token);
                    staged.Add(output);
                    prepared[patchedEntry] = output;
                }
            }
            if (deleteCow)
            {
                var count = await FastbootStepService.DeleteCowAsync(device, cancellation.Token);
                AppendLog($"{language.GetString("Flash_DeleteCow")}: {count}");
            }
            var succeeded = true;
            foreach (var entry in selected)
            {
                if (cancellation.IsCancellationRequested)
                {
                    AppendLog(language.GetString("Flash_Canceled"));
                    succeeded = false;
                    break;
                }
                var command = erase ? "erase" : entry.Step?.Command ?? "flash";
                if (command is "flash" or "wipe_super" &&
                    !File.Exists(prepared.GetValueOrDefault(entry, entry.FilePath)))
                {
                    AppendLog($"{entry.FileName}: {language.GetString("Flash_FileMissing")}");
                    entry.State = FlashRowState.Failed;
                    entry.StatusText = language.GetString("Flash_Status_Failed");
                    succeeded = false;
                    break;
                }

                AppendLog($"{command} {entry.Name}: {entry.FileName}");
                SetRowRunning(entry, erase ? "Flash_Status_Erasing" : "Flash_Status_Flashing");
                if (command == "wipe_super")
                {
                    await FlashSuperEmptyService.WipeAsync(device, preparedSuper[entry], cancellation.Token);
                    AppendLog(string.Format(language.GetString("Flash_OkResult"), command));
                    SetRowFinished(entry, success: true);
                }
                else if (command is "flash" or "erase")
                {
                    var result = command == "erase"
                        ? await FlashService.Default.ErasePartitionAsync(target, entry.Name, progress, cancellation.Token)
                        : device.Protocol == DeviceProtocolKind.Rkdl
                            ? await FlashService.FlashRkdlAsync(target, entry.Name,
                                prepared.GetValueOrDefault(entry, entry.FilePath), EffectiveRkLoaderPath,
                                progress, cancellation.Token, verify)
                            : await FlashService.Default.FlashImageAsync(target, entry.Name,
                                prepared.GetValueOrDefault(entry, entry.FilePath), progress, cancellation.Token);
                    AppendLog(result.Summary);
                    SetRowFinished(entry, result.Success);
                    if (!result.Success)
                    {
                        succeeded = false;
                        break;
                    }
                }
                else
                {
                    await FastbootStepService.ExecuteAsync(device, entry.Step!, cancellation.Token);
                    AppendLog(string.Format(language.GetString("Flash_OkResult"), command));
                    SetRowFinished(entry, success: true);
                }
            }
            if (succeeded && !cancellation.IsCancellationRequested && eraseData)
            {
                foreach (var partition in new[] { "metadata", "userdata" })
                {
                    var result = await FlashService.Default.ErasePartitionAsync(target, partition, progress, cancellation.Token);
                    AppendLog(result.Summary);
                    if (!result.Success)
                    {
                        succeeded = false;
                        break;
                    }
                }
            }
            if (succeeded && !cancellation.IsCancellationRequested && rebootAfter)
            {
                await FastbootStepService.ExecuteAsync(device, new FlashStep("reboot", ""), cancellation.Token);
                AppendLog(string.Format(language.GetString("Flash_OkResult"),
                    language.GetString("Flash_RebootAfter")));
            }
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            foreach (var path in staged)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
            if (extractionRoot is not null)
            {
                try { Directory.Delete(extractionRoot, recursive: true); } catch (IOException) { }
            }
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            Field<Button>("FlashSelectedButton").IsEnabled = true;
            Field<Button>("EraseSelectedButton").IsEnabled = true;
            Field<Button>("SwitchSlotButton").IsEnabled = true;
            Field<TextBox>("PackagePathBox").IsEnabled = true;
            Field<Button>("ChooseFileButton").IsEnabled = true;
            Field<Button>("ChooseFolderButton").IsEnabled = true;
            Field<DataGrid>("PartitionGrid").IsEnabled = true;
        }
    }

    private static bool LooksLikeScatter(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MtkScatterPackage.MaxScatterBytes)
        {
            return false;
        }

        var buffer = new byte[64 * 1024];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read).Contains("MTK_PLATFORM", StringComparison.OrdinalIgnoreCase);
    }

    internal void OnRefreshMtkPortsClick(object? sender, RoutedEventArgs e) => RefreshMtkPorts();

    internal async void OnChooseMtkDaClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_MtkDaFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        SetBusy(true);
        Field<ComboBox>("MtkEntryIndexBox").ItemsSource = null;
        Field<ComboBox>("MtkEntryIndexBox").SelectedIndex = -1;
        _mtkLoaderEntries = [];
        _mtkLoaderPath = null;
        try
        {
            var entries = await MtkScatterPackage.ListUsableLoaderEntriesAsync(path, CancellationToken.None);
            if (entries.Count == 0)
            {
                throw new InvalidDataException(
                    _localizer.GetString("Flash_MtkLoaderNoEntries"));
            }

            _mtkLoaderPath = path;
            _mtkLoaderEntries = entries;
            Field<ComboBox>("MtkEntryIndexBox").ItemsSource = entries.Select(entry =>
                string.Format(_localizer.GetString("Flash_MtkLoaderEntry"),
                    entry.Index, entry.HardwareCode.ToString("X4"), entry.HardwareSubCode.ToString("X4"),
                    $"{entry.HardwareVersion}{(entry.SoftwareVersion is { } software ? $".{software}" : string.Empty)}")).ToArray();
            Field<ComboBox>("MtkEntryIndexBox").SelectedIndex = 0;
            Field<TextBlock>("MtkDaPathText").Text = Path.GetFileName(path);
            ToolTip.SetTip(Field<TextBlock>("MtkDaPathText"), path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    internal async void OnChooseMtkEmiClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_MtkEmiFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        SetBusy(true);
        _mtkEmi = null;
        try
        {
            _mtkEmi = ReadEmi(path);
            Field<TextBlock>("MtkEmiPathText").Text = Path.GetFileName(path);
            ToolTip.SetTip(Field<TextBlock>("MtkEmiPathText"), path);
            AppendLog(Path.GetFileName(path));
        }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Picks the boot resource directory; the explicit authentication file wins over this directory, which is only consulted when no
    /// authentication file was chosen.
    /// <para>选择启动资源目录；显式认证文件优先，未选择认证文件时才回退到此目录。</para>
    /// </summary>
    internal async void OnChooseMtkKeyDirClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString("Flash_MtkKeyDir"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } path) return;
        _mtkKeyDirectory = Directory.Exists(path) ? path : null;
        Field<TextBlock>("MtkKeyDirText").Text = _mtkKeyDirectory is { } directory
            ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : string.Empty;
        ToolTip.SetTip(Field<TextBlock>("MtkKeyDirText"), _mtkKeyDirectory ?? string.Empty);
        AppendLog(_mtkKeyDirectory is null
            ? _localizer.GetString("Flash_MtkKeyDir")
            : $"{_localizer.GetString("Flash_MtkKeyDir")}: {_mtkKeyDirectory}");
    }

    internal async void OnChooseMtkAuthFileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_MtkAuthFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        _mtkAuthFile = File.Exists(path) ? path : null;
        Field<TextBlock>("MtkAuthFileText").Text = _mtkAuthFile is { } file ? Path.GetFileName(file) : string.Empty;
        ToolTip.SetTip(Field<TextBlock>("MtkAuthFileText"), _mtkAuthFile ?? string.Empty);
        AppendLog(_mtkAuthFile is null
            ? _localizer.GetString("Flash_MtkAuthFile")
            : $"{_localizer.GetString("Flash_MtkAuthFile")}: {_mtkAuthFile}");
    }

    private static MtkEmiParameters ReadEmi(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            try
            {
                return MtkEmiReader.ReadPreloader(stream, 0, MtkEmiPayloadLayout.BootloaderInfo);
            }
            catch (InvalidDataException)
            {
            }
        }

        using var retry = File.OpenRead(path);
        return MtkEmiReader.ReadPreloader(retry, 0, MtkEmiPayloadLayout.BinaryParameters);
    }



    /// <summary>
    /// Erases the selected EDL partitions over the sector range declared by the manifest;
    /// <c>NUM_DISK_SECTORS</c> relative addresses are forwarded as-is for the target to evaluate.
    /// Erasure is irreversible, so the user must confirm first, and every NAK is reported as a
    /// failure rather than treated as success.
    /// <para>按清单声明的扇区范围擦除所选 EDL 分区；<c>NUM_DISK_SECTORS</c> 相对地址原样下发、由目标端
    /// 求值。擦除不可恢复，执行前需用户确认，且每条 NAK 都按失败上报而不是当作成功。</para>
    /// </summary>
    internal async void OnEdlEraseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var selected = Partitions
            .Where(row => row.IsSelected && row.CanSelect)
            .ToArray();
        if (selected.Length == 0) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        if (!TryReadEdlGptEntryOverrides(out var gptEntryLba, out var gptEntryCount, out var gptEntrySize)) return;
        var language = _localizer;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_EdlErase"),
                $"{language.GetString("Flash_EdlEraseConfirm")}{Environment.NewLine}{device.DisplayName} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed || selected.Any(row => !row.IsSelected || !Partitions.Contains(row))) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("EdlEraseButton").IsEnabled = false;
        SetRowsQueued(selected);
        try
        {
            await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                FirehoseGptSnapshot? deviceGpt = null;
                foreach (var row in selected)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var entry = _edlEntries.TryGetValue(row, out var manifestEntry)
                        ? manifestEntry
                        : EdlGptPartitionService.ResolveEntry(
                            deviceGpt ??= await firehose.ReadGptWithPartitionEntryOverridesAsync(SelectedEdlLun,
                                gptEntryLba, gptEntryCount, gptEntrySize,
                                cancellationToken: cancellation.Token).ConfigureAwait(true),
                            row.Name);
                    var displayName = entry.Label.Length > 0 ? entry.Label : entry.Filename;
                    if (entry.NumPartitionSectors <= 0)
                    {
                        throw new InvalidDataException(string.Format(
                            language.GetString("Flash_EdlEraseRangeUnknown"), displayName));
                    }

                    SetRowRunning(row, "Flash_Status_Erasing");
                    var response = await firehose.EraseAsync(entry.StartSector, entry.NumPartitionSectors,
                        entry.PhysicalPartitionNumber, cancellation.Token).ConfigureAwait(true);
                    if (!response.IsAck)
                    {
                        throw new InvalidOperationException(string.Format(
                            language.GetString("Flash_EdlEraseFailed"), displayName,
                            EdlFailureText.Describe(response)));
                    }

                    AppendLog(displayName);
                    SetRowFinished(row, success: true);
                }

                return true;
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            UpdateEdlButton();
        }
    }


    /// <summary>
    /// Resets the selected EDL device over firehose after a best-effort bootable-storage setup; the
    /// user must confirm before the command is sent.
    /// <para>经 firehose 复位所选 EDL 设备；复位前会尽力设置可引导存储，执行前需用户确认。</para>
    /// </summary>
    internal async void OnEdlResetClick(object? sender, RoutedEventArgs e)
        => await RunEdlPowerRequestAsync("Flash_EdlReset", "Flash_EdlResetConfirm", "Flash_EdlResetDone",
            (firehose, cancellationToken) => firehose.ResetAsync(cancellationToken));

    /// <summary>
    /// Power-off, the return to EDL and the active-slot switch share the reset path: the same single
    /// confirmation, the same programmer and authentication configuration, and a response the target did
    /// not acknowledge reported as a failure rather than as success.
    /// <para>断电重启之外的三种电源类请求（关机、回到 EDL、切换活动分区）与复位共用同一条路径：同一次确认、
    /// 同一套 programmer 与鉴权配置，并把未被确认的响应作为失败上报。</para>
    /// </summary>
    internal void OnEdlPowerOffClick(object? sender, RoutedEventArgs e)
        => _ = RunEdlPowerRequestAsync("Flash_EdlPowerOff", "Flash_EdlPowerOffConfirm", "Flash_EdlPowerOffDone",
            (firehose, cancellationToken) => firehose.PowerOffAsync(cancellationToken));

    /// <summary>
    /// Returns the device from a firehose session to EDL download mode.
    /// <para>让设备从 firehose 会话回到 EDL 下载模式。</para>
    /// </summary>
    internal void OnEdlResetToEdlClick(object? sender, RoutedEventArgs e)
        => _ = RunEdlPowerRequestAsync("Flash_EdlResetToEdl", "Flash_EdlResetToEdlConfirm",
            "Flash_EdlResetToEdlDone", (firehose, cancellationToken) => firehose.ResetToEdlAsync(cancellationToken));

    /// <summary>
    /// Normalises the slot text to a lowercase identifier that may be sent: the reference lineages emit
    /// either <c>slot="a"</c> or <c>slot="1"</c>, so both a/b and 0/1 are accepted here and the value the
    /// user wrote is forwarded unchanged, with no conversion.
    /// <para>把槽位文本归一化为可发送的小写标识：参考实现两种谱系分别以 <c>slot="a"</c> 与
    /// <c>slot="1"</c> 的形式下发，因此这里同时接受 a/b 与 0/1，并按用户所写的值原样发送，不做任何换算。</para>
    /// </summary>
    /// <param name="text">The raw box text. <para>输入框原文。</para></param>
    /// <returns>The normalised slot, or <see langword="null"/>. <para>归一化后的槽位，或
    /// <see langword="null"/>。</para></returns>
    internal static string? NormalizeEdlSlot(string? text) =>
        text?.Trim().ToLowerInvariant() is { Length: 1 } slot && "ab01".Contains(slot, StringComparison.Ordinal)
            ? slot
            : null;


    /// <summary>
    /// Routes every region-level read or erase through one session template: build the initialization,
    /// mark the page busy, create a cancellation source, then run the body on the already-configured
    /// Firehose instance. These entries consume the session the discovery layer provides and never start
    /// an extra handshake of their own.
    /// <para>把区域级读写与擦除动作接到同一套会话样板上：解析初始化、置忙、建取消源，并在已配置好的
    /// Firehose 实例上执行调用体。这些入口只消费发现层已有的会话，不自行发起额外握手。</para>
    /// </summary>
    /// <param name="body">The operation to run, returning the summary line to log.
    /// <para>要执行的操作，返回写入日志的摘要行。</para></param>
    private async Task RunEdlRegionOperationAsync(
        Func<FirehoseProtocol, IProgress<FlashProgress>, CancellationToken, Task<string>> body)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            !TryBuildEdlInitialization(out var initialization))
        {
            return;
        }

        var language = _localizer;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var summary = await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                return await body(firehose, progress, cancellation.Token).ConfigureAwait(true);
            }, cancellation.Token).ConfigureAwait(true);
            AppendLog(summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Confirms a region action that submits a command to a device: the dialog body names the target too, so an
    /// intent formed for a previous device cannot land on the current lease.
    /// <para>确认一个会向设备提交命令的区域动作：对话框正文同时点名目标设备，避免把上一台设备的
    /// 意图落到当前租约上。</para>
    /// </summary>
    /// <param name="titleKey">The localized action title key. <para>本地化动作标题的键。</para></param>
    /// <returns><see langword="true"/> when the user confirmed.
    /// <para>用户确认时为 <see langword="true"/>。</para></returns>
    private async Task<bool> ConfirmEdlRegionWriteAsync(string titleKey)
    {
        if (_confirming || TopLevel.GetTopLevel(Surface) is not Window owner || SelectedDevice is not { } device)
        {
            return false;
        }

        var language = _localizer;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                $"{language.GetString("Flash_EdlRegionConfirmBody")}{Environment.NewLine}{device.DisplayName}");
        }
        finally { _confirming = false; }

        return confirmed;
    }

    /// <summary>
    /// The session template for service calls that open their own session: the EDL storage-control service loads
    /// the programmer and initializes the session itself, while the page only supplies validated input plus the
    /// busy/cancellation state.
    /// <para>走设备监视器会话的服务级调用样板：EDL 存储控制服务自己打开会话并加载 programmer，
    /// 页面只提供已校验的输入与忙碌/取消状态。</para>
    /// </summary>
    /// <param name="body">The service call returning the summary line. <para>返回摘要行的服务调用。</para></param>
    private async Task RunEdlStorageServiceCallAsync(Func<CancellationToken, Task<string>> body)
    {
        if (_busy || _confirming) return;
        var language = _localizer;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            AppendLog(await body(cancellation.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }


    /// <summary>
    /// Dumps the pane-selected LUN into <c>lun-N.bin</c> inside the chosen directory; the summary names the
    /// transferred byte count and the per-region SHA-256.
    /// <para>把界面选中的 LUN 整区转储到已选目录下的 <c>lun-N.bin</c>；摘要写明字节数与逐区 SHA-256。</para>
    /// </summary>
    /// <param name="sender">The read button. <para>读取按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlReadLunClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        _confirming = true;
        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = language.GetString("Flash_EdlRegionDirChoose"),
                AllowMultiple = false
            });
        }
        finally { _confirming = false; }
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } directory) return;
        if (!await ConfirmEdlRegionWriteAsync("Flash_EdlReadLun")) return;
        var lun = SelectedEdlLun;
        var destination = Path.Combine(directory, $"lun-{lun}.bin");
        await RunEdlRegionOperationAsync(async (firehose, _, token) =>
        {
            var result = await EdlGptRegionService.ReadLunRegionToFileAsync(firehose, lun, destination, token)
                .ConfigureAwait(true);
            return string.Format(System.Globalization.CultureInfo.CurrentCulture,
                language.GetString("Flash_EdlReadLunResult"),
                Path.GetFileName(destination),
                result.BytesTransferred.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.Sha256Hex);
        });
    }

    /// <summary>
    /// Points the boot medium at the stated physical partition number; the range is validated by the service
    /// and the dialog only names the target about to be submitted.
    /// <para>把启动介质指向给定的物理分区号；取值范围由服务层校验，对话框只描述将要提交的目标。</para>
    /// </summary>
    /// <param name="sender">The set button. <para>设置按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlSetBootableClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            !int.TryParse(Field<TextBox>("EdlBootableValueBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value is < 0 or > 255 ||
            !TryBuildEdlInitialization(out var initialization))
        {
            AppendLog(_localizer.GetString("Flash_EdlBootableValueInvalid"));
            return;
        }

        if (!await ConfirmEdlRegionWriteAsync("Flash_EdlSetBootable")) return;
        await RunEdlStorageServiceCallAsync(async token =>
        {
            var result = await EdlStorageControlService.SetBootableStorageDriveAsync(
                new DeviceInfo(device), value, token, EffectiveEdlProgrammer, initialization)
                .ConfigureAwait(true);
            return result.Summary;
        });
    }

    /// <summary>
    /// Reads one partition's SHA-256: a read-only command whose only input is the partition name, with the
    /// extent resolved from the device's own partition table.
    /// <para>读取指定分区的 SHA-256：只读命令，输入只有分区名，区间由设备自身分区表解析。</para>
    /// </summary>
    /// <param name="sender">The digest button. <para>摘要按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlPartitionDigestClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device) return;
        var partition = Field<TextBox>("EdlDigestPartitionBox").Text?.Trim() ?? string.Empty;
        if (partition.Length == 0 || !TryBuildEdlInitialization(out var initialization))
        {
            AppendLog(_localizer.GetString("Flash_EdlDigestPartitionMissing"));
            return;
        }

        await RunEdlStorageServiceCallAsync(async token =>
        {
            var digest = await EdlStorageControlService.GetPartitionSha256Async(
                new DeviceInfo(device), partition, SelectedEdlLun, token, EffectiveEdlProgrammer,
                initialization).ConfigureAwait(true);
            return $"{digest.Partition} [{digest.StartSector} x{digest.SectorCount}] lun {digest.Lun}: {digest.Sha256Hex}";
        });
    }


    /// <summary>
    /// Runs one power or slot request on the configured firehose session after confirmation; a response the
    /// target did not acknowledge is thrown as a failure with its classification text, and session
    /// retirement stays the protocol layer's concern.
    /// <para>在确认之后于已配置的 firehose 会话上执行一次电源或槽位请求；未被 ACK 的响应带分类文本作为失败抛出，
    /// 会话 retirement 由协议层负责。</para>
    /// </summary>
    private async Task RunEdlPowerRequestAsync(string titleKey, string confirmKey, string doneKey,
        Func<FirehoseProtocol, CancellationToken, Task<FirehoseResponse>> request)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } ||
            TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                language.GetString(confirmKey));
        }
        finally { _confirming = false; }
        if (!confirmed || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } current) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            await App.DeviceMonitor.RunEdlAsync(current, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token)
                    .ConfigureAwait(true);
                var response = await request(firehose, cancellation.Token).ConfigureAwait(true);
                if (!response.IsAck)
                {
                    throw new InvalidOperationException(string.Format(
                        language.GetString("Flash_EdlPowerNotAcknowledged"), EdlFailureText.Describe(response)));
                }

                return true;
            }, cancellation.Token);
            AppendLog(language.GetString(doneKey));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Picks and parses a UFS provisioning descriptor file; a parse failure clears the selection and
    /// reports the reason in the log.
    /// <para>选择并解析 UFS provisioning 描述符文件；解析失败会清空已选配置并把原因写进日志。</para>
    /// </summary>
    internal async void OnEdlChooseUfsProfileClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_EdlUfsProfileChoose"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            _edlUfsProfile = await UfsProvisionProfileLoader.LoadAsync(path, CancellationToken.None);
            _edlUfsProfilePath = path;
            Field<TextBlock>("EdlUfsProfileText").Text = Path.GetFileName(path);
            ToolTip.SetTip(Field<TextBlock>("EdlUfsProfileText"), path);
        }
        catch (Exception ex)
        {
            _edlUfsProfile = null;
            _edlUfsProfilePath = null;
            Field<TextBlock>("EdlUfsProfileText").Text = string.Empty;
            AppendLog(ex.Message);
        }

        UpdateEdlButton();
    }

    /// <summary>
    /// Runs one streaming NAND operation: the group is only visible while the session really is on Streaming
    /// DLOAD, so this adds just the busy state, cancellation and logging, while the device lease, the partition
    /// resolution and every wire-format check live in the service layer.
    /// <para>执行一次流式 NAND 操作：分组本身只在会话确实处于 Streaming DLOAD 时可见，因此这里只补忙碌态、
    /// 取消与日志；设备租约、分区解析与线格式校验都在服务层完成。</para>
    /// </summary>
    /// <param name="operation"><para>要执行的设备级操作，返回可直接显示的摘要。</para>
    /// The device-level operation, returning renderable summary text.</param>
    private async Task RunEdlStreamingNandAsync(Func<DeviceInfo, CancellationToken, Task<string>> operation)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            EdlStage != EdlProtocolState.Streaming)
        {
            return;
        }

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            AppendLog(await operation(new DeviceInfo(device), cancellation.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            AppendLog(_localizer.GetString("Flash_Canceled"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Parses the four layout boxes and the spare-area check into a controller layout; a non-integer or negative
    /// value is refused, so neither a device report nor a typed value addresses the arrays unchecked.
    /// <para>把四个布局框与备用区勾选解析成控制器布局；任一非整数或负值都拒绝，绝不用设备或输入值直接寻址。</para>
    /// </summary>
    /// <param name="layout">The parsed layout, or null. <para>解析得到的布局；失败时为 null。</para></param>
    /// <returns>True when every field is a non-negative uint. <para>三者都合法时为 true。</para></returns>
    private bool TryReadEdlNandLayout(out EdlStreamingNandLayout? layout)
    {
        layout = null;
        if (!TryReadEdlNandUInt(Field<TextBox>("EdlNandBaseBox").Text, out var nandBase) ||
            !TryReadEdlNandUInt(Field<TextBox>("EdlNandPagesBox").Text, out var pagesPerBlock) ||
            !TryReadEdlNandUInt(Field<TextBox>("EdlNandSectorsBox").Text, out var sectorsPerPage) ||
            !TryReadEdlNandUInt(Field<TextBox>("EdlNandBadLenBox").Text, out var badBlockByteNum))
        {
            AppendLog(_localizer.GetString("Flash_EdlNandInputInvalid"));
            return false;
        }

        layout = new EdlStreamingNandLayout(nandBase, pagesPerBlock, sectorsPerPage, badBlockByteNum,
            Field<CheckBox>("EdlNandBadInSpareBox").IsChecked == true ? 1u : 0u);
        return true;
    }

    private static bool TryReadEdlNandUInt(string? text, out uint value) =>
        uint.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Reads the block index and the scan count: the index must be zero or more and the count at least one.
    /// <para>读取块序号与扫描块数：序号不小于 0，扫描数至少为 1。</para>
    /// </summary>
    /// <param name="block">The parsed block index. <para>解析出的块序号。</para></param>
    /// <param name="blockCount">The parsed scan count. <para>解析出的扫描块数。</para></param>
    /// <returns>True when both fields are usable. <para>两者都合法时为 true。</para></returns>
    private bool TryReadEdlNandBlocks(out uint block, out int blockCount)
    {
        block = 0;
        blockCount = 0;
        if (!TryReadEdlNandUInt(Field<TextBox>("EdlNandBlockBox").Text, out block) ||
            !int.TryParse(Field<TextBox>("EdlNandBlockCountBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out blockCount) || blockCount < 1)
        {
            block = 0;
            blockCount = 0;
            AppendLog(_localizer.GetString("Flash_EdlNandInputInvalid"));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the partition name, or null with a localized hint when the box is blank, so the service never
    /// receives a request it would have to guess at.
    /// <para>取分区名；空白时给出本地化提示并返回 null，服务层因此收不到任何模糊匹配请求。</para>
    /// </summary>
    private string? EdlNandPartition() => Field<TextBox>("EdlNandPartitionBox").Text?.Trim() is { Length: > 0 } name
        ? name
        : null;

    internal async void OnChooseEdlNandTableClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_EdlNandTableChoose"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        _edlNandTablePath = File.Exists(path) ? path : null;
        Field<TextBlock>("EdlNandTableText").Text = _edlNandTablePath is null ? string.Empty : Path.GetFileName(path);
        UpdateEdlButton();
    }

    /// <summary>
    /// Reads the target's own bad-marker state only, programming and erasing no array location.
    /// <para>只读取设备自身坏块标记状态，不编程也不擦除任何阵列位置。</para>
    /// </summary>
    /// <param name="sender">The read button. <para>读取按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandReadMapClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEdlNandBlocks(out _, out var blockCount) || !TryReadEdlNandLayout(out var layout) ||
            EdlNandPartition() is not { } partition)
        {
            return;
        }

        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.ReadBadBlockMapAsync(device, partition, layout!, blockCount, token)
                .ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Programs a bad marker on the given block; the operation really programs, and without the confirmation
    /// check the service layer refuses before any byte goes out.
    /// <para>给指定块写入坏块标记；该操作会真的编程，未勾选确认时由服务层在发出任何字节前拒绝。</para>
    /// </summary>
    /// <param name="sender">The mark button. <para>标记按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandMarkBadClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEdlNandBlocks(out var block, out _) || !TryReadEdlNandLayout(out var layout) ||
            EdlNandPartition() is not { } partition)
        {
            return;
        }

        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.MarkBlockBadAsync(device, partition, layout!, block,
                Field<CheckBox>("EdlNandConfirmBox").IsChecked == true, token).ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Clears the bad marker on the given block; also an irreversible program step, so it shares the write
    /// confirmation check.
    /// <para>清除指定块的坏块标记；同样是不可逆编程，因此共用写入确认勾选。</para>
    /// </summary>
    /// <param name="sender">The clear button. <para>清除按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandUnmarkBadClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEdlNandBlocks(out var block, out _) || !TryReadEdlNandLayout(out var layout) ||
            EdlNandPartition() is not { } partition)
        {
            return;
        }

        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.UnmarkBlockBadAsync(device, partition, layout!, block,
                Field<CheckBox>("EdlNandConfirmBox").IsChecked == true, token).ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Reads back only the controller configuration words and their derived values so the operator can check the
    /// layout; nothing is written back to any register.
    /// <para>只回读控制器配置字与派生值，供操作者核对布局；绝不写回任何寄存器。</para>
    /// </summary>
    /// <param name="sender">The register button. <para>寄存器按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandRegistersClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEdlNandLayout(out var layout))
        {
            return;
        }

        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.ReadRegisterReportAsync(device, layout!, token)
                .ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Reads the codeword and ECC status of one block to judge whether it is bad; in the reference this is a
    /// page-read sequence that asks for confirmation.
    /// <para>读取一个块的码字与 ECC 状态以判断它是否已坏；按参考实现这属于要确认的页读序列。</para>
    /// </summary>
    /// <param name="sender">The codeword button. <para>码字按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandCodewordClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadEdlNandBlocks(out var block, out _) || !TryReadEdlNandLayout(out var layout) ||
            EdlNandPartition() is not { } partition)
        {
            return;
        }

        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.EvaluateCodewordStatusAsync(device, partition, layout!, block,
                Field<CheckBox>("EdlNandConfirmBox").IsChecked == true, token).ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Puts the target into flash mode, a precondition of the write sequence, so it needs the write confirmation
    /// too.
    /// <para>让目标进入 flash 模式；这是写序列的前置状态，因此也要求写入确认。</para>
    /// </summary>
    /// <param name="sender">The flash-mode button. <para>flash 模式按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandFlashModeClick(object? sender, RoutedEventArgs e)
    {
        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.EnterFlashModeAsync(device, Field<CheckBox>("EdlNandConfirmBox").IsChecked == true, token)
                .ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Requests secure mode from the target, trusted or not by the check box; either way it changes session state
    /// and writes no content.
    /// <para>请求目标进入安全模式；受信与否由勾选决定，两者都只改变会话状态而不写内容。</para>
    /// </summary>
    /// <param name="sender">The secure-mode button. <para>安全模式按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandSecureModeClick(object? sender, RoutedEventArgs e)
    {
        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.EnterSecureModeAsync(device,
                Field<CheckBox>("EdlNandTrustedBox").IsChecked == true, token).ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Sends the chosen partition-table file to the target; replacing an existing table needs its own allowance
    /// check, and an unconfirmed request is refused by the service layer first.
    /// <para>把选定的分区表文件送给目标；替换既有表还要单独的允许勾选，未确认时服务层先拒绝。</para>
    /// </summary>
    /// <param name="sender">The send button. <para>发送按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandSendTableClick(object? sender, RoutedEventArgs e)
    {
        if (_edlNandTablePath is not { } tablePath)
        {
            AppendLog(_localizer.GetString("Flash_EdlNandTableMissing"));
            return;
        }

        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.SendPartitionTableAsync(device, tablePath,
                Field<CheckBox>("EdlNandOverrideBox").IsChecked == true, Field<CheckBox>("EdlNandConfirmBox").IsChecked == true, token)
                .ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Closes the streaming session properly; leaving it open after a write keeps the target in flash mode.
    /// <para>正常收尾流式会话；写操作之后不关闭就会把目标留在 flash 模式里。</para>
    /// </summary>
    /// <param name="sender">The close button. <para>关闭按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnEdlNandCloseClick(object? sender, RoutedEventArgs e)
    {
        await RunEdlStreamingNandAsync(async (device, token) =>
            (await EdlStreamingNandService.CloseStreamAsync(device, token).ConfigureAwait(false)).Summary);
    }

    /// <summary>
    /// Runs one UFS provisioning step: a write is confirmed by the user first, non-UFS storage is refused by the
    /// service layer, and session initialization with the pane's programmer selection is delegated to the
    /// device-level entry point while this method owns only the confirmation, busy state and log.
    /// <para>执行一次 UFS provisioning 步骤：写入前先经用户确认，非 UFS 存储由服务层拒绝，会话初始化与
    /// programmer 选择交给设备级入口自己完成，本方法只负责确认、忙碌态与日志。</para>
    /// </summary>
    private async Task RunEdlUfsOperationAsync(string titleKey, string? confirmKey,
        Func<DeviceInfo, EdlInitializationOptions?, string?, CancellationToken, Task<FlashResult>> operation)
    {
        if (_busy || _confirming || SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        if (confirmKey is not null)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                    $"{language.GetString(confirmKey)}{Environment.NewLine}{device.DisplayName}");
            }
            finally { _confirming = false; }
            if (!confirmed) return;
        }

        if (!TryBuildEdlInitialization(out var initialization)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("EdlUfsPreviewButton").IsEnabled = false;
        Field<Button>("EdlUfsProvisionButton").IsEnabled = false;
        Field<Button>("EdlUfsResetButton").IsEnabled = false;
        try
        {
            var result = await operation(new DeviceInfo(device), initialization,
                EffectiveEdlProgrammer, cancellation.Token).ConfigureAwait(true);
            AppendLog(result.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Previews the provisioning plan the descriptor file would produce for this device; no byte is
    /// written.
    /// <para>预览描述符文件对该设备会产生的 provisioning 计划；不写出任何字节。</para>
    /// </summary>
    internal async void OnEdlUfsPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_edlUfsProfile is null || _edlUfsProfilePath is not { } profilePath)
        {
            AppendLog(_localizer.GetString("Flash_EdlUfsProfileMissing"));
            return;
        }

        await RunEdlUfsOperationAsync("Flash_EdlUfsPreview", null, (device, initialization, programmerPath, token) =>
            EdlUfsProvisionService.PreviewAsync(device, profilePath,
                new Progress<FlashProgress>(update => AppendLog(update.Status)), token, programmerPath,
                initialization));
    }

    /// <summary>
    /// Submits the provisioning plan: the logical units the descriptor declares are really created, so the
    /// user must confirm first.
    /// <para>提交 provisioning 计划：描述符里声明的逻辑单元会真实建立，因此必须先经用户确认。</para>
    /// </summary>
    internal async void OnEdlUfsProvisionClick(object? sender, RoutedEventArgs e)
    {
        if (_edlUfsProfile is null || _edlUfsProfilePath is not { } profilePath)
        {
            AppendLog(_localizer.GetString("Flash_EdlUfsProfileMissing"));
            return;
        }

        await RunEdlUfsOperationAsync("Flash_EdlUfsProvision", "Flash_EdlUfsProvisionConfirm",
            (device, initialization, programmerPath, token) => EdlUfsProvisionService.ProvisionAsync(
                device, profilePath, confirmed: true,
                new Progress<FlashProgress>(update => AppendLog(update.Status)), token, programmerPath,
                initialization));
    }

    /// <summary>
    /// Requests a device UFS reset; the command is independent of the descriptor file, so no profile
    /// selection is required first.
    /// <para>请求设备执行 UFS reset；该命令独立于描述符文件，因此不需要先选配置。</para>
    /// </summary>
    internal async void OnEdlUfsResetClick(object? sender, RoutedEventArgs e)
        => await RunEdlUfsOperationAsync("Flash_EdlUfsReset", "Flash_EdlUfsResetConfirm",
            (device, initialization, programmerPath, token) => EdlUfsProvisionService.ResetAsync(
                device, confirmed: true, new Progress<FlashProgress>(update => AppendLog(update.Status)), token,
                programmerPath, initialization));

    /// <summary>
    /// Picks a programmer file overriding the package-supplied one for later EDL work.
    /// <para>选择用于覆盖包内 programmer 的引导文件；覆盖只影响后续 EDL 刷写与回读。</para>
    /// </summary>
    internal async void OnEdlChooseProgrammerClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Flash_EdlChooseCustomProgrammer"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (_busy || _confirming || files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;
        _edlProgrammerOverride = File.Exists(path) ? path : null;
        UpdateEdlProgrammerUiState();
        if (_edlProgrammerOverride is { } selected)
        {
            AppendLog(Path.GetFileName(selected));
        }
        UpdateEdlButton();
    }

    /// <summary>
    /// Picks a programmer folder and positively matches it against the Sahara identity the target already reported:
    /// the scan builds rows under the loader naming convention of that folder and only a unique match yields an
    /// upload-eligible verdict. Ambiguous, contradictory and insufficient-evidence outcomes only produce a verdict
    /// line; they never replace the in-package candidate picker and never open a file on their own.
    /// <para>选定 programmer 目录并按目标已上报的 Sahara 身份做正匹配：扫描按该目录的 loader 命名约定建立条目，
    /// 唯一匹配才给出可上传结论；歧义、矛盾或证据不足都只给结论行，不替代包内候选选择，也不自动打开任何文件。</para>
    /// </summary>
    internal async void OnEdlChooseProgrammerDirClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var language = _localizer;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = language.GetString("Flash_EdlChooseProgrammerDir"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } directory) return;
        _edlProgrammerDirectory = directory;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device)
        {
            EdlProgrammerDirectoryResult scan;
            try
            {
                scan = EdlProgrammerDirectoryService.ScanAsync(directory, CancellationToken.None);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                _edlProgrammerDirectoryResult = null;
                Field<TextBlock>("EdlProgrammerDirText").Text = string.Empty;
                AppendLog(ex.Message);
                return;
            }

            _edlProgrammerDirectoryResult = scan;
            Field<TextBlock>("EdlProgrammerDirText").Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                "{0} ({1})", directory, scan.CandidateCount);
            ToolTip.SetTip(Field<TextBlock>("EdlProgrammerDirText"), directory);
            AppendLog(scan.CandidateCount == 0
                ? language.GetString("Flash_EdlProgrammerDirEmpty")
                : $"{scan.CandidateCount} / {scan.SkippedCount}");
            return;
        }

        EdlProgrammerDirectoryResult result;
        try
        {
            result = EdlProgrammerDirectoryService.MatchAsync(device, directory, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            _edlProgrammerDirectoryResult = null;
            Field<TextBlock>("EdlProgrammerDirText").Text = string.Empty;
            AppendLog(ex.Message);
            return;
        }

        _edlProgrammerDirectoryResult = result;
        var summary = result.Decision switch
        {
            EdlProgrammerMatchDecision.Matched => language.GetString("Flash_EdlProgrammerDirMatched",
                result.Descriptor?.FileName ?? string.Empty),
            EdlProgrammerMatchDecision.Ambiguous => language.GetString("Flash_EdlProgrammerDirAmbiguous"),
            EdlProgrammerMatchDecision.NoMatch => language.GetString("Flash_EdlProgrammerDirNoMatch"),
            _ when result.CandidateCount == 0 => language.GetString("Flash_EdlProgrammerDirEmpty"),
            _ => language.GetString("Flash_EdlProgrammerDirNeedsIdentity")
        };
        Field<TextBlock>("EdlProgrammerDirText").Text = $"{Path.GetFileName(directory)} · {summary}";
        ToolTip.SetTip(Field<TextBlock>("EdlProgrammerDirText"), directory);
        AppendLog(summary);
    }

    /// <summary>
    /// Reads the selected EDL partitions back into a user directory; with XML generation enabled,
    /// a rawprogram manifest is written from the results and the generalized option rewrites
    /// entries abutting the disk end as relative <c>NUM_DISK_SECTORS-</c> start sectors.
    /// <para>回读所选 EDL 分区到用户目录；勾选生成 XML 时按回读结果写出 rawprogram 清单，
    /// 通用化选项把与磁盘末尾相邻的条目改写为 <c>NUM_DISK_SECTORS-</c> 相对起始扇区。</para>
    /// </summary>
    internal async void OnEdlReadbackClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Edl } device ||
            TopLevel.GetTopLevel(Surface) is not { } topLevel) return;
        var selected = Partitions
            .Where(row => row.IsSelected && row.CanSelect)
            .ToArray();
        if (selected.Length == 0) return;
        if (!TryBuildEdlInitialization(out var initialization)) return;
        if (!TryReadEdlGptEntryOverrides(out var gptEntryLba, out var gptEntryCount, out var gptEntrySize)) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString("Flash_EdlReadbackSelected"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } destination) return;
        var generateXml = Field<CheckBox>("EdlGenerateXmlBox").IsChecked == true;
        var generalized = generateXml && Field<CheckBox>("EdlGeneralizedXmlBox").IsChecked == true;
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        Field<Button>("EdlReadbackButton").IsEnabled = false;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            List<EdlReadbackManifestEntry> manifest = [];
            var sectorSize = 0L;
            var numDiskSectors = 0L;
            SetRowsQueued(selected);
            FlashPartitionEntry? readbackRow = null;
            var readbackProgress = new Progress<BackupProgress>(update =>
            {
                if (readbackRow is null)
                {
                    return;
                }

                readbackRow.Percent = Math.Clamp(update.Percent, 0, 100);
                UpdateOverall();
            });
            await App.DeviceMonitor.RunEdlAsync(device, async edl =>
            {
                var firehose = await ConfigureEdlAsync(edl, initialization, cancellation.Token).ConfigureAwait(true);
                if (generalized)
                {
                    numDiskSectors = (await firehose.GetStorageGeometryAsync(
                        SelectedEdlLun, cancellation.Token).ConfigureAwait(true)).SectorCount;
                }

                FirehoseGptSnapshot? deviceGpt = null;
                foreach (var row in selected)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var entry = _edlEntries.TryGetValue(row, out var manifestEntry)
                        ? manifestEntry
                        : EdlGptPartitionService.ResolveEntry(
                            deviceGpt ??= await firehose.ReadGptWithPartitionEntryOverridesAsync(SelectedEdlLun,
                                gptEntryLba, gptEntryCount, gptEntrySize,
                                cancellationToken: cancellation.Token).ConfigureAwait(true),
                            row.Name);
                    SetRowRunning(row, "Flash_Status_Flashing");
                    readbackRow = row;
                    var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;
                    var outputPath = Path.Combine(destination,
                        entry.Filename.Length > 0 ? entry.Filename : $"{displayName}.img");
                    var result = await BackupService.BackupEdlPartitionAsync(
                        edl, entry, outputPath, readbackProgress, cancellation.Token).ConfigureAwait(true);
                    AppendLog(result.Summary);
                    SetRowFinished(row, result.Success);
                    if (!result.Success) return false;
                    if (!generateXml) continue;
                    var geometry = await firehose.GetStorageGeometryAsync(
                        entry.PhysicalPartitionNumber, cancellation.Token).ConfigureAwait(true);
                    sectorSize = entry.SectorSizeInBytes > 0 ? entry.SectorSizeInBytes : geometry.SectorSize;
                    manifest.Add(new EdlReadbackManifestEntry
                    {
                        PartitionName = displayName,
                        StartSector = FlashService.ParseStartSectorValue(entry.StartSector, geometry.SectorCount),
                        SectorCount = entry.NumPartitionSectors,
                        PhysicalPartitionNumber = entry.PhysicalPartitionNumber,
                        FileName = Path.GetFileName(outputPath)
                    });
                }

                return true;
            }, cancellation.Token);
            if (generateXml && manifest.Count > 0)
            {
                var manifestPath = Path.Combine(destination, "rawprogram_readback.xml");
                await EdlReadbackManifestWriter.WriteAsync(
                    manifestPath, manifest, sectorSize, numDiskSectors, generalized,
                    cancellation.Token).ConfigureAwait(true);
                AppendLog(Path.GetFileName(manifestPath));
            }
        }
        catch (OperationCanceledException) { MarkUnfinishedSkipped(); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            SetBusy(false);
            Field<Button>("CancelFlashButton").IsEnabled = false;
            UpdateOverall();
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>对已准备好的 MTK 计划做二次确认并执行：确认框给出操作数与总量，进度按步骤回写到对应行，
    /// 结束后统一补完未标记的行并写入结果摘要。刷写与擦除共用该流程，仅标题资源键不同。</para>
    /// Confirms and executes a prepared MTK plan: the dialog reports the operation count and total
    /// size, progress is written back to each row by step, and the run finishes every unmarked row
    /// and writes one result summary. Flashing and erasing share this flow and differ only in the
    /// title resource key.
    /// </summary>
    /// <param name="preparation">The prepared one-shot plan. <para>已准备好的一次性计划。</para></param>
    /// <param name="rows">Rows whose progress tracks the plan's steps. <para>进度对应计划各步骤的行。</para></param>
    /// <param name="owner">Window owning the confirmation dialog. <para>持有确认对话框的窗口。</para></param>
    /// <param name="token">Cancellation for the execution. <para>执行阶段的取消令牌。</para></param>
    /// <param name="titleKey">Resource key for the confirmation title. <para>确认标题的资源键。</para></param>
    private async Task ExecuteMtkPlanAsync(IMtkFlashPlanPreview preparation, IReadOnlyList<FlashPartitionEntry> rows,
        Window owner, CancellationToken token, string titleKey)
    {
        var language = _localizer;
        _confirming = true;
        bool go;
        try
        {
            go = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                string.Format(language.GetString("Flash_MtkPlanPreview"), preparation.OperationCount,
                    preparation.TotalBytes / 1024d / 1024d));
        }
        finally { _confirming = false; }
        if (!go)
        {
            AppendLog(language.GetString("Flash_Canceled"));
            return;
        }

        var progress = new Progress<MtkFlashPlanProgress>(update =>
        {
            if (update.StepIndex < 0 || update.StepIndex >= rows.Count)
            {
                return;
            }

            var row = rows[update.StepIndex];
            row.State = FlashRowState.Running;
            row.Percent = update.TotalBytes > 0
                ? (int)Math.Clamp(update.ProcessedBytes * 100 / update.TotalBytes, 0, 100)
                : 0;
            row.StatusText = update.Phase switch
            {
                MtkFlashPhase.Writing => language.GetString("Flash_Status_Flashing"),
                MtkFlashPhase.Erasing => language.GetString("Flash_Status_Erasing"),
                MtkFlashPhase.Verifying => language.GetString("Flash_Status_Verifying"),
                MtkFlashPhase.OperationCompleted => language.GetString("Flash_Status_Succeeded"),
                _ => row.StatusText
            };
            if (update.Phase == MtkFlashPhase.OperationCompleted)
            {
                row.State = FlashRowState.Succeeded;
                row.Percent = 100;
            }

            UpdateOverall();
        });
        var result = await preparation.ExecuteAsync(progress, 120000, token);
        foreach (var row in rows.Where(row => row.State != FlashRowState.Succeeded))
        {
            SetRowFinished(row, success: true);
        }

        AppendLog(string.Format(language.GetString("Flash_MtkResultSummary"),
                result.CompletedOperations, result.WrittenBytes / 1024d / 1024d) +
            (result.VerifiedBytes > 0
                ? string.Format(language.GetString("Flash_MtkResultVerified"),
                    result.VerifiedBytes / 1024d / 1024d)
                : string.Empty));
    }

    /// <summary>
    /// Erases the selected scatter partitions: it covers both flashable rows that carry an image and
    /// policy-eligible rows without one; the range is the manifest-declared capacity and the live GPT
    /// re-checks it. Erasure is irreversible, so the user must confirm first, and both transports share
    /// the same connection selection.
    /// <para>擦除所选 scatter 分区：既覆盖有镜像的可刷写行，也覆盖无镜像但策略允许的行；范围取清单声明
    /// 的容量并由实机 GPT 复核。擦除不可恢复，执行前需用户确认，串口与 USB 共用同一套连接选择。</para>
    /// </summary>
    internal async void OnMtkEraseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _mtkResolution is not { } resolution ||
            TopLevel.GetTopLevel(Surface) is not Window owner ||
            !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        var selected = Partitions
            .Where(row => row.IsSelected && row.CanSelect && _mtkPartitions.ContainsKey(row)).ToArray();
        if (selected.Length == 0) return;
        var language = _localizer;
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        if (selected.Length > 256)
        {
            AppendLog(language.GetString("Flash_MtkTooManySteps"));
            return;
        }

        var kind = SelectedMtkStorageKind;
        if (BuildMtkFamilyPlan(erase: true, selected, verify: false) is not { } plan)
        {
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkErase"),
                $"{language.GetString("Flash_MtkEraseConfirm")}{Environment.NewLine}" +
                $"{resolution.StorageLabel} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed ||
            selected.Any(row => !row.IsSelected || !Partitions.Contains(row)) ||
            !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkFlashButton").IsEnabled = false;
        Field<Button>("FlashSelectedButton").IsEnabled = false;
        Field<DataGrid>("PartitionGrid").IsEnabled = false;
        Field<TextBox>("PackagePathBox").IsEnabled = false;
        Field<Button>("ChooseFileButton").IsEnabled = false;
        Field<Button>("ChooseFolderButton").IsEnabled = false;
        SetRowsQueued(selected);
        IMtkFlashPlanPreview? preparation = null;
        try
        {
            var physicalPartition = plan.PhysicalPartition;
            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                preparation = plan.Family switch
                {
                    MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyAsync(connect, plan.LegacyRegion,
                        plan.LegacySteps, cancellation.Token),
                    MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlAsync(connect, plan.XmlRegion!,
                        plan.XmlSteps, cancellation.Token),
                    _ => await MtkFlashService.PrepareAsync(connect, physicalPartition, plan.XFlashEraseSteps,
                        cancellation.Token)
                };
                await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkErase");
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    preparation = plan.Family switch
                    {
                        MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyOnSessionAsync(session,
                            matched, plan.LegacyRegion, plan.LegacySteps, cancellation.Token),
                        MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlOnSessionAsync(session,
                            matched, plan.XmlRegion!, plan.XmlSteps, cancellation.Token),
                        _ => await MtkFlashService.PrepareOnSessionAsync(session, plan.Family,
                            matched, physicalPartition, plan.XFlashEraseSteps, cancellation.Token)
                    };
                    await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkErase");
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            if (preparation is not null)
            {
                try { preparation.Dispose(); }
                catch (Exception ex) { AppendLog(ex.Message); }
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            Field<Button>("FlashSelectedButton").IsEnabled = true;
            Field<DataGrid>("PartitionGrid").IsEnabled = true;
            Field<TextBox>("PackagePathBox").IsEnabled = true;
            Field<Button>("ChooseFileButton").IsEnabled = true;
            Field<Button>("ChooseFolderButton").IsEnabled = true;
            UpdateEdlButton();
        }
    }

    internal async void OnMtkFlashClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _mtkResolution is not { } resolution ||
            TopLevel.GetTopLevel(Surface) is not Window owner ||
            !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal)) return;
        var selected = Partitions
            .Where(row => row.IsSelected && row.CanSelect && _mtkRows.ContainsKey(row)).ToArray();
        if (selected.Length == 0) return;
        var language = _localizer;
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        if (selected.Length > 256)
        {
            AppendLog(language.GetString("Flash_MtkTooManySteps"));
            return;
        }

        var kind = SelectedMtkStorageKind;
        var verify = Field<CheckBox>("VerifyAfterFlashBox").IsChecked == true;
        if (BuildMtkFamilyPlan(erase: false, selected, verify) is not { } plan)
        {
            return;
        }

        var target = useSerial ? port! : usbDevice!.DisplayName;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkFlash"),
                $"{resolution.StorageLabel} · {target} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed ||
            selected.Any(row => !row.IsSelected || !Partitions.Contains(row)) ||
            !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal)) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkFlashButton").IsEnabled = false;
        Field<Button>("FlashSelectedButton").IsEnabled = false;
        Field<DataGrid>("PartitionGrid").IsEnabled = false;
        Field<TextBox>("PackagePathBox").IsEnabled = false;
        Field<Button>("ChooseFileButton").IsEnabled = false;
        Field<Button>("ChooseFolderButton").IsEnabled = false;
        SetRowsQueued(selected);
        IMtkFlashPlanPreview? preparation = null;
        try
        {
            var physicalPartition = plan.PhysicalPartition;
            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                preparation = plan.Family switch
                {
                    MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyAsync(connect, plan.LegacyRegion,
                        plan.LegacySteps, cancellation.Token),
                    MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlAsync(connect, plan.XmlRegion!,
                        plan.XmlSteps, cancellation.Token),
                    _ => await MtkFlashService.PrepareScatterAsync(connect,
                        physicalPartition, resolution.Storage, plan.ScatterSteps, cancellation.Token)
                };
                await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkFlash");
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    preparation = plan.Family switch
                    {
                        MtkDaProtocol.Legacy => await MtkFlashService.PrepareLegacyOnSessionAsync(session,
                            matched, plan.LegacyRegion, plan.LegacySteps, cancellation.Token),
                        MtkDaProtocol.Xml => await MtkFlashService.PrepareXmlOnSessionAsync(session,
                            matched, plan.XmlRegion!, plan.XmlSteps, cancellation.Token),
                        _ => await MtkFlashService.PrepareScatterOnSessionAsync(session,
                            plan.Family, matched, physicalPartition, resolution.Storage, plan.ScatterSteps,
                            cancellation.Token)
                    };
                    await ExecuteMtkPlanAsync(preparation, selected, owner, cancellation.Token, "Flash_MtkFlash");
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(language.GetString("Flash_Canceled"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            if (preparation is not null)
            {
                try { preparation.Dispose(); }
                catch (Exception ex) { AppendLog(ex.Message); }
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            Field<Button>("FlashSelectedButton").IsEnabled = true;
            Field<DataGrid>("PartitionGrid").IsEnabled = true;
            Field<TextBox>("PackagePathBox").IsEnabled = true;
            Field<Button>("ChooseFileButton").IsEnabled = true;
            Field<Button>("ChooseFolderButton").IsEnabled = true;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// <para>读取 MTK 面板当前选择并按顺序校验；失败时把本地化原因写入日志并返回 false。
    /// 家族与存储类型的不可能组合排在最前，因为用户不必为一个所选代理永远无法服务的请求去找加载器、
    /// 串口和波特率；XML 家族不使用 EMI 参数，通过校验时只写一条本地化提示，不做静默丢弃。
    /// 附加模式只有 Legacy 家族被拒绝，XFlash 与 XML 代理都能按各自通路附加。</para>
    /// Reads the current MTK pane selection and validates it in order; on failure the localized
    /// reason is written to the log and <see langword="false"/> is returned. An impossible family and
    /// storage combination is reported first, because the user should not have to hunt for a loader, a
    /// port and a baud rate for a request the chosen agent can never serve. The XML family uses no EMI
    /// parameters, so a passing selection only writes one localized note instead of dropping them quietly.
    /// Attach mode refuses only the Legacy family; XFlash and XML agents are attached over their own paths.
    /// </summary>
    /// <param name="useSerial">Whether the operation targets a serial port instead of a USB device.
    /// <para>操作目标是串口而非 USB 设备。</para></param>
    /// <param name="usbDevice">The selected USB MTK device, or null. <para>所选 USB MTK 设备，或为 null。</para></param>
    /// <param name="port">The selected serial port, or null. <para>所选串口，或为 null。</para></param>
    /// <param name="baudRate">The selected baud rate; 0 without a serial target. <para>所选波特率；非串口目标时为 0。</para></param>
    /// <param name="attach">Whether a running agent should be attached instead of booting one.
    /// <para>是否改为附加已运行的代理而非启动新代理。</para></param>
    /// <param name="autoMatch">Whether the loader entry is matched from the reported identity.
    /// <para>是否按上报身份匹配加载器条目。</para></param>
    /// <returns><see langword="true"/> when the selection is usable. <para>选择可用时为 true。</para></returns>
    internal bool TryReadMtkSelection(out bool useSerial, out DetectedDevice? usbDevice, out string? port,
        out int baudRate, out bool attach, out bool autoMatch)
    {
        var language = _localizer;
        usbDevice = SelectedDevice is { Protocol: DeviceProtocolKind.Mtk, Transport: DeviceTransportKind.Usb }
            ? SelectedDevice
            : null;
        useSerial = usbDevice is null;
        port = null;
        baudRate = 0;
        attach = Field<CheckBox>("MtkAttachAgentBox").IsChecked == true;
        autoMatch = Field<CheckBox>("MtkAutoMatchBox").IsChecked == true;

        string? failure = null;
        if (SelectedMtkProtocol == MtkDaProtocol.Legacy && SelectedMtkStorageKind != MtkXFlashStorageKind.Emmc)
        {
            failure = "Flash_MtkLegacyStorageUnsupported";
        }

        if (failure is null && useSerial)
        {
            if (Field<ComboBox>("MtkPortBox").SelectedItem is not string selectedPort)
            {
                failure = "Flash_MtkPortRequired";
            }
            else
            {
                port = selectedPort;
            }
        }

        if (failure is null && !attach && _mtkLoaderPath is not { })
        {
            failure = "Flash_MtkDaRequired";
        }

        if (failure is null && !attach && !autoMatch &&
            (_mtkLoaderEntries.Count == 0 || Field<ComboBox>("MtkEntryIndexBox").SelectedIndex < 0))
        {
            failure = "Flash_MtkEntryRequired";
        }

        if (failure is null && useSerial)
        {
            if (Field<ComboBox>("MtkBaudBox").SelectedItem is not int selectedBaud)
            {
                failure = "Flash_MtkBaudRequired";
            }
            else
            {
                baudRate = selectedBaud;
            }
        }
        else if (failure is null && attach)
        {
            failure = "Flash_MtkUsbAttachUnsupported";
        }
        else if (failure is null && !autoMatch)
        {
            failure = "Flash_MtkUsbAutoMatchRequired";
        }

        if (failure is null && attach && SelectedMtkProtocol == MtkDaProtocol.Legacy)
        {
            failure = "Flash_MtkAttachFamilyUnsupported";
        }

        if (failure is null)
        {
            if (SelectedMtkProtocol == MtkDaProtocol.Xml && _mtkEmi is not null)
            {
                AppendLog(language.GetString("Flash_MtkXmlEmiIgnored"));
            }

            return true;
        }

        AppendLog(language.GetString(failure));
        return false;
    }

    /// <summary>
    /// The download-agent family is taken from the selector and never probed: XFlash for current chips, Legacy
    /// for legacy chips and XML for the region-named v6 agent.
    /// <para>下载代理家族由选择框决定，不做探测：XFlash 用于新芯片，Legacy 用于旧芯片，XML 用于按区域名
    /// 寻址的 v6 代理。</para>
    /// </summary>
    /// <value>The explicitly selected protocol. <para>显式选定的协议。</para></value>
    private MtkDaProtocol SelectedMtkProtocol => Field<ComboBox>("MtkProtocolBox").SelectedIndex switch
    {
        1 => MtkDaProtocol.Legacy,
        2 => MtkDaProtocol.Xml,
        _ => MtkDaProtocol.XFlash
    };

    /// <summary>
    /// The storage family behind the storage selection; only eMMC and UFS are offered, NOR and NAND are
    /// outside this pane.
    /// <para>存储类型选择框对应的存储族；只有 eMMC 与 UFS 两种，NOR/NAND 不在此面板之内。</para>
    /// </summary>
    /// <value>The selected storage family. <para>所选存储族。</para></value>
    private MtkXFlashStorageKind SelectedMtkStorageKind => Field<ComboBox>("MtkStorageKindBox").SelectedIndex switch
    {
        1 => MtkXFlashStorageKind.Ufs,
        2 => MtkXFlashStorageKind.Nor,
        3 => MtkXFlashStorageKind.Nand,
        _ => MtkXFlashStorageKind.Emmc
    };

    /// <summary>
    /// <para>XML 代理的 DRAM 资源走它自己的主机文件通道，不接受 XFlash 的 EMI 参数，因此家族为 XML 时
    /// 主机必须丢弃面板上残留的 EMI 选择，否则 Comm 会在启动中途拒绝该配置。</para>
    /// The XML agent takes its DRAM resources over its own host-file channel and rejects XFlash EMI
    /// parameters, so the host must drop a leftover EMI selection for the XML family; otherwise Comm
    /// refuses the startup configuration halfway through.
    /// </summary>
    /// <param name="family">The selected download-agent family. <para>所选下载代理家族。</para></param>
    /// <returns><see langword="true"/> when the family consumes EMI parameters.
    /// <para>家族使用 EMI 参数时为 true。</para></returns>
    internal static bool FamilyUsesEmiParameters(MtkDaProtocol family) => family != MtkDaProtocol.Xml;

    /// <summary>
    /// <para>电源请求的家族守卫：XML 代理只定义启动模式重启，没有关机请求，因此关机必须在接触设备之前
    /// 以本地化文案拒绝，返回拒绝用的资源键；家族可以服务该请求时返回 null。</para>
    /// The family guard for a power request: the XML agent defines boot-mode reboot only, so a power-off
    /// must be refused with a localized message before any device contact, returning the refusal resource
    /// key; <see langword="null"/> is returned when the family serves the request.
    /// </summary>
    /// <param name="family">The selected download-agent family. <para>所选下载代理家族。</para></param>
    /// <param name="mode">The requested power or boot transition. <para>请求的电源或启动模式切换。</para></param>
    /// <returns>The refusal resource key, or <see langword="null"/>. <para>拒绝用的资源键，或为 null。</para></returns>
    internal static string? MtkPowerRequestRefusalKey(MtkDaProtocol family, MtkXFlashShutdownMode mode)
        => family == MtkDaProtocol.Xml && mode == MtkXFlashShutdownMode.PowerOff
            ? "Flash_MtkXmlPowerOffUnsupported"
            : null;

    /// <summary>
    /// <para>把面板选择的下载代理家族落成一份不接触设备的执行选择：按家族构造步骤数组与区域标识，
    /// XFlash 用清单索引寻址，Legacy 与 XML 用 GPT 分区名寻址各自的区域。刷写与擦除共用该入口，
    /// 只在步骤种类上不同；构造失败时把原因写入日志并返回 null，绝不回退到其他家族。</para>
    /// Materializes the download-agent family chosen on the pane into one device-free execution selection:
    /// the step arrays and region identifiers are built per family, with XFlash addressed through manifest
    /// index tokens and Legacy and XML through GPT partition names in their own regions. Flashing and
    /// erasing share this entry and differ only in the step kind; a build failure writes the reason to the
    /// log and returns <see langword="null"/> and never falls back to another family.
    /// </summary>
    /// <param name="erase">Whether the selection erases instead of writing. <para>选择是擦除而非写入。</para></param>
    /// <param name="selected">The rows the caller has selected. <para>调用方所选的行。</para></param>
    /// <param name="verify">Whether written extents require readback. <para>写入范围是否要求回读。</para></param>
    /// <returns>The family selection, or <see langword="null"/> after the failure was logged.
    /// <para>家族选择；失败并已写日志时为 null。</para></returns>
    internal MtkFamilyPlan? BuildMtkFamilyPlan(bool erase, IReadOnlyList<FlashPartitionEntry> selected, bool verify)
    {
        var family = SelectedMtkProtocol;
        var kind = SelectedMtkStorageKind;
        if (_mtkResolution is not { } resolution)
        {
            AppendLog(_localizer.GetString("Flash_MtkLoadScatterHint"));
            return null;
        }

        try
        {
            MtkScatterFlashStep[] scatterSteps = [];
            MtkPartitionFlashStep[] xFlashEraseSteps = [];
            MtkLegacyPartitionFlashStep[] legacySteps = [];
            MtkXmlPartitionFlashStep[] xmlSteps = [];
            switch (family)
            {
                case MtkDaProtocol.Legacy:
                    legacySteps = [.. selected.Select(row => erase
                        ? MtkScatterPackage.CreateLegacyEraseStep(_mtkPartitions[row])
                        : MtkScatterPackage.CreateLegacyStep(resolution, _mtkRows[row], verify))];
                    break;
                case MtkDaProtocol.Xml:
                    xmlSteps = [.. selected.Select(row => erase
                        ? MtkScatterPackage.CreateXmlEraseStep(resolution, _mtkPartitions[row])
                        : MtkScatterPackage.CreateXmlStep(resolution, _mtkRows[row], verify))];
                    break;
                default:
                    if (erase)
                    {
                        xFlashEraseSteps = [.. selected.Select(row =>
                            MtkScatterPackage.CreateEraseStep(_mtkPartitions[row]))];
                    }
                    else
                    {
                        scatterSteps = [.. selected.Select(row =>
                            MtkScatterPackage.CreateStep(resolution, _mtkRows[row], verify))];
                    }

                    break;
            }

            return new MtkFamilyPlan
            {
                Family = family,
                PhysicalPartition = MtkScatterPackage.GetPhysicalPartition(kind),
                LegacyRegion = family == MtkDaProtocol.Legacy
                    ? MtkScatterPackage.GetLegacyPhysicalPartition(kind)
                    : (byte)0,
                XmlRegion = family == MtkDaProtocol.Xml ? MtkScatterPackage.GetXmlRegionName(kind) : null,
                ScatterSteps = scatterSteps,
                XFlashEraseSteps = xFlashEraseSteps,
                LegacySteps = legacySteps,
                XmlSteps = xmlSteps
            };
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The immutable result of the pane's family selection: only the step array matching the selected
    /// family is populated and the region identifiers are filled per family as well, so the forwarding
    /// switch afterwards reads this one record instead of deriving the same choices again.
    /// <para>面板家族选择的不可变结果：三种步骤数组中只有与所选家族对应的一组非空，区域标识同样按家族填充，
    /// 因此后续的转发切换只需读取这一份数据，不必重复推导。</para>
    /// </summary>
    internal sealed record MtkFamilyPlan
    {
        /// <summary>Gets the family the selector resolved to. <para>选择框解析出的家族。</para></summary>
        public required MtkDaProtocol Family { get; init; }

        /// <summary>Gets the XFlash physical user region. <para>XFlash 物理用户区。</para></summary>
        public required uint PhysicalPartition { get; init; }

        /// <summary>Gets the Legacy region identifier, zero for the other families.
        /// <para>Legacy 区域标识符；其他家族为 0。</para></summary>
        public required byte LegacyRegion { get; init; }

        /// <summary>Gets the XML region token, null for the other families.
        /// <para>XML 区域标记；其他家族为 null。</para></summary>
        public required string? XmlRegion { get; init; }

        /// <summary>Gets the scatter-indexed XFlash write steps. <para>按索引寻址的 XFlash 写入步骤。</para></summary>
        public required IReadOnlyList<MtkScatterFlashStep> ScatterSteps { get; init; }

        /// <summary>Gets the GPT-named XFlash erase steps. <para>按 GPT 分区名寻址的 XFlash 擦除步骤。</para></summary>
        public required IReadOnlyList<MtkPartitionFlashStep> XFlashEraseSteps { get; init; }

        /// <summary>Gets the Legacy named steps. <para>Legacy 命名步骤。</para></summary>
        public required IReadOnlyList<MtkLegacyPartitionFlashStep> LegacySteps { get; init; }

        /// <summary>Gets the XML named steps. <para>XML 命名步骤。</para></summary>
        public required IReadOnlyList<MtkXmlPartitionFlashStep> XmlSteps { get; init; }
    }

    /// <summary>
    /// <para>为所选家族补齐启动设置：Legacy 用主机操作系统与默认包长且不预设 DA1 响应断言，XML 用参考工具的
    /// 默认运行时策略且不预设属性信封名；XML 家族不接受 EMI 参数，因此这里把它清空，避免启动中途被拒绝。</para>
    /// Completes the startup settings for the selected family: Legacy uses the host operating system and
    /// default packet sizes without a DA1 response assertion, and XML uses the reference tool's default runtime
    /// policy without a preset property envelope. The XML family takes no EMI parameters, so they are cleared
    /// here instead of being rejected halfway through startup.
    /// </summary>
    /// <param name="startup">The identity-matched startup to extend. <para>待补充的身份匹配启动配置。</para></param>
    /// <returns>The startup configuration for the selected family. <para>所选家族的启动配置。</para></returns>
    private MtkAutoStartup ApplyMtkFamily(MtkAutoStartup startup)
    {
        var family = SelectedMtkProtocol;
        var emi = FamilyUsesEmiParameters(family) ? startup.EmiParameters : null;
        return family switch
        {
            MtkDaProtocol.Legacy => startup with { Legacy = BuildLegacyOptions(), EmiParameters = emi },
            MtkDaProtocol.Xml => startup with { Xml = BuildXmlStartupOptions(), EmiParameters = emi },
            _ => startup with { EmiParameters = emi }
        };
    }

    private MtkLegacyOptions BuildLegacyOptions()
    {
        var language = _localizer;
        if (!ushort.TryParse(Field<TextBox>("MtkLegacyNorSelectBox").Text?.Trim(), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var norSelect)
            || !byte.TryParse(Field<TextBox>("MtkLegacyNandChipSelectBox").Text?.Trim(), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var nandChipSelect)
            || !uint.TryParse(Field<TextBox>("MtkLegacyNandTimingBox").Text?.Trim(), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var nandTiming)
            || !byte.TryParse(Field<TextBox>("MtkLegacyBmtFlagBox").Text?.Trim(), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var bmtFlag)
            || !uint.TryParse(Field<TextBox>("MtkLegacyBmtPoolBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var bmtPool)
            || !int.TryParse(Field<TextBox>("MtkLegacyAgentPacketBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var agentPacket)
            || !int.TryParse(Field<TextBox>("MtkLegacyStoragePacketBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var storagePacket)
            || agentPacket is < 1 or > 16 * 1024 * 1024
            || storagePacket is < 1 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException(language.GetString("Flash_MtkLegacyParamsInvalid"));
        }

        var profiles = Enum.GetValues<MtkLegacyCompletionProfile>();
        return new MtkLegacyOptions
        {
            HostOs = MtkHostOs,
            StageOneResponseHex = null,
            NorChipSelect = norSelect,
            NandChipSelect = nandChipSelect,
            NandAccessTiming = nandTiming,
            NandBmtFlag = bmtFlag,
            NandBmtPoolBytes = bmtPool,
            AgentPacketLength = agentPacket,
            StoragePacketLength = storagePacket,
            CompletionProfile = profiles[Math.Clamp(Field<ComboBox>("MtkLegacyCompletionBox").SelectedIndex, 0, profiles.Length - 1)],
            NandSize64Bit = Field<CheckBox>("MtkLegacyNand64BitBox").IsChecked == true,
            ForceCharging = Field<CheckBox>("MtkLegacyForceChargeBox").IsChecked == true,
        };
    }

    private static MtkXmlBootstrapOptions BuildXmlStartupOptions() => new()
    {
        Runtime = new MtkXmlRuntimeOptions
        {
            Checksum = MtkXmlChecksumLevel.None,
            HostOs = MtkHostOs,
            InitializeDram = true
        },
        SlaEnvelopeName = null,
        HostInfo = string.Empty
    };

    private static MtkXmlHostOs MtkHostOs => OperatingSystem.IsWindows()
        ? MtkXmlHostOs.Windows
        : MtkXmlHostOs.Linux;

    /// <summary>
    /// <para>按面板选择构造串口 MTK 连接器：手动条目走显式启动配置，自动匹配按身份选条目，
    /// 附加模式直接挂到已运行的代理。</para>
    /// Builds the serial MTK connector from the pane selection: counted entries use an explicit
    /// startup configuration, auto-match picks the entry by identity, and attach mode connects to an
    /// agent that is already running.
    /// </summary>
    /// <param name="port">The selected serial port. <para>所选串口。</para></param>
    /// <param name="baudRate">The selected baud rate. <para>所选波特率。</para></param>
    /// <param name="kind">The storage family the agent must serve. <para>代理需服务的存储族。</para></param>
    /// <param name="attach">Whether to attach instead of booting an agent. <para>是否改为附加而非启动代理。</para></param>
    /// <param name="autoMatch">Whether the entry is matched from the reported identity.
    /// <para>是否按上报身份匹配条目。</para></param>
    /// <returns>A connector returning a storage-ready session. <para>返回存储就绪会话的连接器。</para></returns>
    private Func<CancellationToken, Task<MtkSession>> BuildMtkSerialConnector(string port, int baudRate,
        MtkXFlashStorageKind kind, bool attach, bool autoMatch)
    {
        async Task<MtkSession> StartAgentAsync(CancellationToken token)
        {
            var serial = new SerialConnectionOptions
            {
                PortName = port,
                BaudRate = baudRate,
                Handshake = Handshake.None
            };
            if (!autoMatch)
            {
                var entry = _mtkLoaderEntries[Field<ComboBox>("MtkEntryIndexBox").SelectedIndex];
                var protocol = SelectedMtkProtocol;
                var startup = new MtkBootstrapOptions
                {
                    LoaderPath = _mtkLoaderPath!,
                    EntryIndex = entry.Index,
                    StageOneRegionIndex = entry.StageOneRegion,
                    StageTwoRegionIndex = entry.StageTwoRegion,
                    StageTwoContent = protocol == MtkDaProtocol.Legacy
                        ? MtkDaRegionContent.Complete
                        : MtkDaRegionContent.WithoutSignatureSuffix,
                    StorageKind = kind,
                    EmiParameters = FamilyUsesEmiParameters(protocol) ? _mtkEmi : null,
                    BootResourceProvider = BuildMtkBootResourceProvider()
                };
                if (protocol == MtkDaProtocol.Legacy)
                {
                    startup = startup with { Legacy = BuildLegacyOptions() };
                }
                else if (protocol == MtkDaProtocol.Xml)
                {
                    startup = startup with { Xml = BuildXmlStartupOptions() };
                }
                return await MtkBootstrap.ConnectSerialAsync(serial, protocol, startup, token);
            }

            var matched = ApplyMtkFamily(await BuildMtkAutoStartupAsync(kind, token));
            return await MtkBootstrap.MatchSerialAsync(serial, SelectedMtkProtocol, matched, token);
        }

        async Task<MtkSession> AttachAgentAsync(CancellationToken token)
        {
            var session = await MtkSession.OpenSerialAsync(port, baudRate, token);
            try
            {
                if (SelectedMtkProtocol == MtkDaProtocol.Xml)
                {
                    await session.AttachRunningXmlAgentAsync(120000, token);
                }
                else
                {
                    await session.AttachRunningXFlashAgentAsync(kind, 120000, token);
                }

                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        return attach ? AttachAgentAsync : StartAgentAsync;
    }

    /// <summary>
    /// <para>按面板的 DA 文件与密钥目录构造身份匹配启动配置；密钥目录提供 DAA 资源。</para>
    /// Builds the identity-matched startup configuration from the pane's DA file and key directory;
    /// the key directory supplies the DAA resources.
    /// </summary>
    /// <param name="kind">The storage family the agent must serve. <para>代理需服务的存储族。</para></param>
    /// <param name="token">Cancellation for loading the loader profile. <para>加载加载器配置的取消令牌。</para></param>
    /// <returns>The startup configuration. <para>启动配置。</para></returns>
    private async Task<MtkAutoStartup> BuildMtkAutoStartupAsync(MtkXFlashStorageKind kind, CancellationToken token)
    {
        var profile = await MtkLoaderProfile.FromFileAsync(_mtkLoaderPath!, token);
        return ApplyMtkFamily(new MtkAutoStartup
        {
            Profile = profile,
            StorageKind = kind,
            EmiParameters = _mtkEmi,
            BootResourceProvider = BuildMtkBootResourceProvider()
        });
    }

    /// <summary>
    /// Builds the DAA boot resource provider: the explicit auth file wins, otherwise the key directory is used.
    /// <para>构造 DAA 启动资源提供方：显式认证文件优先，否则回退到密钥目录。</para>
    /// </summary>
    /// <returns>The provider, or null when nothing was selected.
    /// <para>资源提供方；未选择任何内容时为 null。</para></returns>
    private IMtkBootResourceProvider? BuildMtkBootResourceProvider()
        => _mtkAuthFile is { } authFile
            ? new MtkFileBootResourceProvider(authenticationPath: authFile)
            : _mtkKeyDirectory is { } directory
                ? new MtkDirectoryKeyStore(directory)
                : null;

    /// <summary>
    /// Reads the selected scatter partitions into a user-chosen directory; it shares this pane's
    /// connection selection with flashing and shows byte progress per row.
    /// <para>把所选 scatter 分区回读到用户目录；与刷写共用本面板的连接选择，并逐行显示字节进度。</para>
    /// </summary>
    internal async void OnMtkReadbackClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || _mtkResolution is not { } resolution ||
            TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var selected = Partitions
            .Where(row => row.IsSelected && row.CanSelect && _mtkRows.ContainsKey(row))
            .ToArray();
        if (selected.Length == 0) return;
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var language = _localizer;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = language.GetString("Flash_MtkReadback"),
            AllowMultiple = false
        });
        if (_busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { } destination) return;

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkReadback"),
                $"{resolution.StorageLabel} · {string.Join(", ", selected.Select(row => row.Name))}");
        }
        finally { _confirming = false; }
        if (!confirmed || selected.Any(row => !row.IsSelected || !Partitions.Contains(row)) ||
            !string.Equals(PackagePath, _loadedPath, StringComparison.Ordinal)) return;

        var kind = SelectedMtkStorageKind;
        var physicalPartition = MtkScatterPackage.GetPhysicalPartition(kind);
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkFlashButton").IsEnabled = false;
        Field<DataGrid>("PartitionGrid").IsEnabled = false;
        Field<TextBox>("PackagePathBox").IsEnabled = false;
        SetRowsQueued(selected);
        try
        {
            async Task ReadBackAsync(MtkSession session, CancellationToken token)
            {
                var family = SelectedMtkProtocol;
                var legacySnapshot = family == MtkDaProtocol.Legacy
                    ? await session.ReadLegacyGptAsync(MtkScatterPackage.GetLegacyPhysicalPartition(kind), token)
                    : null;
                var xmlSnapshot = family == MtkDaProtocol.Xml
                    ? await session.ReadXmlGptAsync(MtkScatterPackage.GetXmlRegionName(kind), cancellationToken: token)
                    : null;
                var xFlashSnapshot = family == MtkDaProtocol.XFlash
                    ? await session.ReadXFlashGptAsync(physicalPartition, token)
                    : null;
                var table = legacySnapshot?.Table ?? xmlSnapshot?.Table ?? xFlashSnapshot!.Table;
                foreach (var row in selected)
                {
                    token.ThrowIfCancellationRequested();
                    var partition = table.FindPartition(row.Name);
                    SetRowRunning(row, "Flash_Status_Flashing");
                    var progress = new Progress<long>(value =>
                    {
                        row.Percent = partition.ByteLength > 0
                            ? (int)Math.Clamp(value * 100L / partition.ByteLength, 0L, 100L)
                            : 0;
                        UpdateOverall();
                    });
                    var outputPath = Path.Combine(destination,
                        row.FileName.Length > 0 ? row.FileName : $"{row.Name}.img");
                    if (legacySnapshot is not null)
                    {
                        await session.ReadLegacyPartitionToFileAsync(legacySnapshot, row.Name, outputPath,
                            progress, token);
                    }
                    else if (xmlSnapshot is not null)
                    {
                        await session.ReadXmlPartitionToFileAsync(xmlSnapshot, row.Name, outputPath,
                            progress, token);
                    }
                    else
                    {
                        await session.ReadXFlashPartitionToFileAsync(xFlashSnapshot!, row.Name, outputPath,
                            progress, token);
                    }
                    AppendLog(outputPath);
                    SetRowFinished(row, success: true);
                }
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await ReadBackAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await ReadBackAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            foreach (var row in selected.Where(row => row.State == FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }
        }
        finally
        {
            MarkUnfinishedSkipped();
            UpdateOverall();
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            Field<Button>("FlashSelectedButton").IsEnabled = true;
            Field<DataGrid>("PartitionGrid").IsEnabled = true;
            Field<TextBox>("PackagePathBox").IsEnabled = true;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Dumps the whole storage area region by region as the chosen family reports them; NAND and any region needing
    /// authenticated access are refused by name in the service, and the page never substitutes an addressing
    /// scheme of its own.
    /// <para>整块存储转储：按所选家族上报的区域逐块读取，NAND 与需要认证的区域由服务层指名拒绝，
    /// 界面不做任何替代寻址。</para>
    /// </summary>
    /// <param name="sender">The dump button. <para>转储按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnMtkDumpWholeFlashClick(object? sender, RoutedEventArgs e)
    {
        var directory = await PickMtkDumpDirectoryAsync("Flash_MtkDumpWholeFlash").ConfigureAwait(true);
        if (directory is null) return;
        var prompt = _localizer;
        var family = SelectedMtkProtocol;
        await RunMtkSecurityOperationAsync("Flash_MtkDumpWholeFlash", "Flash_MtkDumpWholeFlashConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var result = await MtkBulkReadService.ReadWholeFlashAsync(session, family, directory, progress, token)
                    .ConfigureAwait(true);
                AppendLog(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    prompt.GetString("Flash_MtkDumpDone"),
                    result.Regions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    result.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            });
    }

    /// <summary>
    /// Reads a raw sector window whose region token, start sector and count the user supplies; the service then
    /// validates them against the reported capacity, and an out-of-range window or digest mismatch fails before
    /// anything is published.
    /// <para>读取裸扇区窗口：区域令牌、起始扇区与扇区数都由用户给出，服务层再按上报容量校验，
    /// 越界或摘要不符都在落盘前失败。</para>
    /// </summary>
    /// <param name="sender">The read button. <para>读取按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnMtkReadSectorsClick(object? sender, RoutedEventArgs e)
    {
        var prompt = _localizer;
        var region = Field<TextBox>("MtkSectorRegionBox").Text?.Trim() ?? string.Empty;
        if (region.Length == 0 ||
            !long.TryParse(Field<TextBox>("MtkSectorStartBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var start) || start < 0 ||
            !long.TryParse(Field<TextBox>("MtkSectorCountBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            AppendLog(prompt.GetString("Flash_MtkSectorInputInvalid"));
            return;
        }

        var directory = await PickMtkDumpDirectoryAsync("Flash_MtkReadSectors").ConfigureAwait(true);
        if (directory is null) return;
        var family = SelectedMtkProtocol;
        var destination = Path.Combine(directory, $"sectors_{region}_{start}_{count}.bin");
        await RunMtkSecurityOperationAsync("Flash_MtkReadSectors", "Flash_MtkDumpWholeFlashConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var record = await MtkBulkReadService.ReadSectorsAsync(
                    session, family, region, start, count, destination, progress: progress, ct: token)
                    .ConfigureAwait(true);
                AppendLog(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    prompt.GetString("Flash_MtkDumpDone"), "1",
                    record.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            });
    }

    /// <summary>
    /// Reads an absolute byte range that is independent of any region token: the user supplies the offset and
    /// length, the service splices the read across the regions the chosen family reported, and an out-of-range,
    /// mis-aligned or short staged capture fails before publishing so existing files stay untouched.
    /// <para>按区域无关的绝对字节范围读取：偏移与长度由用户给出，服务层用所选家族上报的区域拼接跨区读取，
    /// 越界、未对齐或暂存长度不足都在落盘前失败，既有文件保持原样。</para>
    /// </summary>
    /// <param name="sender">The read button. <para>读取按钮。</para></param>
    /// <param name="e">The routed click event. <para>点击路由事件。</para></param>
    internal async void OnMtkReadAreaClick(object? sender, RoutedEventArgs e)
    {
        var prompt = _localizer;
        if (!long.TryParse(Field<TextBox>("MtkAreaOffsetBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var offset) || offset < 0 ||
            !long.TryParse(Field<TextBox>("MtkAreaLengthBox").Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 1)
        {
            AppendLog(prompt.GetString("Flash_MtkAreaInputInvalid"));
            return;
        }

        var directory = await PickMtkDumpDirectoryAsync("Flash_MtkReadArea").ConfigureAwait(true);
        if (directory is null) return;
        var family = SelectedMtkProtocol;
        var destination = Path.Combine(directory, $"area_{offset}_{length}.bin");
        await RunMtkSecurityOperationAsync("Flash_MtkReadArea", "Flash_MtkDumpWholeFlashConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var record = await MtkFlashAreaService.CaptureBytesAsync(session, family, offset, length,
                    destination, progress, token).ConfigureAwait(true);
                AppendLog(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    prompt.GetString("Flash_MtkDumpDone"),
                    record.Regions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    record.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            });
    }

    /// <summary>
    /// Picks the output directory for a read-only walk; returns null when nothing was chosen or the page became busy,
    /// so the caller can exit quietly.
    /// <para>为只读走查选择输出目录；未选或忙碌时返回 null，调用方据此安静退出。</para>
    /// </summary>
    /// <param name="titleKey">The localized picker title key. <para>本地化选择框标题的键。</para></param>
    /// <returns>The chosen directory, or <see langword="null"/>.
    /// <para>所选目录；未选时为 <see langword="null"/>。</para></returns>
    private async Task<string?> PickMtkDumpDirectoryAsync(string titleKey)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _localizer.GetString(titleKey),
            AllowMultiple = false
        }).ConfigureAwait(true);
        return _busy || _confirming || folders.Count != 1 || folders[0].TryGetLocalPath() is not { Length: > 0 } path
            ? null
            : path;
    }

    /// <summary>
    /// Parses the RPMB offset and length texts: the frame shape maps onto a frame range, the byte shape
    /// onto a byte span, and the whole-area shape ignores both; malformed or out-of-bounds text returns
    /// false.
    /// <para>解析 RPMB 的偏移与长度文本：帧形态映射到帧范围，字节形态映射到字节区间，整区形态忽略两者；
    /// 文本非法或数值越界时返回 false。</para>
    /// </summary>
    /// <param name="mode">The addressing shape. <para>寻址形态。</para></param>
    /// <param name="offsetText">The offset text. <para>偏移文本。</para></param>
    /// <param name="lengthText">The length text. <para>长度文本。</para></param>
    /// <param name="frames">The parsed frame range. <para>解析出的帧范围。</para></param>
    /// <param name="byteOffset">The parsed byte offset. <para>解析出的字节偏移。</para></param>
    /// <param name="byteLength">The parsed byte length. <para>解析出的字节长度。</para></param>
    /// <returns><see langword="true"/> when the shape is usable. <para>形态可用时为
    /// <see langword="true"/>。</para></returns>
    internal static bool TryReadRpmbShape(MtkRpmbCaptureMode mode, string? offsetText, string? lengthText,
        out MtkRpmbFrameRange frames, out long byteOffset, out long byteLength)
    {
        frames = new MtkRpmbFrameRange(0, 1);
        byteOffset = 0;
        byteLength = 0;
        if (mode == MtkRpmbCaptureMode.FullArea)
        {
            return true;
        }

        if (!long.TryParse(offsetText?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var offset) ||
            !long.TryParse(lengthText?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var length) ||
            offset < 0 || length <= 0)
        {
            return false;
        }

        if (mode == MtkRpmbCaptureMode.Frames)
        {
            if (length > int.MaxValue)
            {
                return false;
            }

            frames = new MtkRpmbFrameRange(offset, (int)length);
        }
        else
        {
            byteOffset = offset;
            byteLength = length;
        }

        return true;
    }

    /// <summary>
    /// Queries the RPMB capacity read-only: it is the companion read half of the capture, so the user can confirm
    /// the region size before producing a file. The reply stays undecoded exactly as the reference client leaves
    /// it, so the log interprets only the geometry capacity field and never guesses at the raw bytes.
    /// <para>只读查询 RPMB 容量：这是捕获读取的配套读端，让用户在没有落盘文件时也能先确认区域大小。应答按参考
    /// 客户端的做法不解码，因此日志只解释几何信息里的容量字段，不猜测未解码字节的含义。</para>
    /// </summary>
    internal async void OnMtkRpmbCapacityClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || !TryReadMtkSelection(out var useSerial, out var usbDevice, out _, out _, out _, out _)
            || useSerial || usbDevice is null)
        {
            return;
        }

        var language = _localizer;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkRpmbCapacityButton").IsEnabled = false;
        try
        {
            var status = await MtkRpmbService.QueryCapacityOnEndpointAsync(usbDevice, cancellation.Token)
                .ConfigureAwait(true);
            AppendLog(status.ReportedRpmbCapacityBytes is { } capacity
                ? language.GetString("Flash_MtkRpmbCapacityResult", capacity)
                : language.GetString("Flash_MtkRpmbCapacityUnknown"));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Captures RPMB read-only for the download-agent family chosen in the pane: the family's supported
    /// addressing shape is consulted first, then a destination file is picked, and every publish is staged
    /// and verified; the captured area can contain keys, so the user must confirm before it runs.
    /// <para>按面板选定的代理家族只读捕获 RPMB：先查该家族支持的寻址形态，再选择落盘文件，任何写入都经
    /// 暂存与校验后替换；捕获内容可能包含密钥，执行前需用户确认。</para>
    /// </summary>
    internal async void OnMtkRpmbReadClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner ||
            !TryReadMtkSelection(out var useSerial, out var usbDevice, out _, out _, out _, out _))
        {
            return;
        }

        var language = _localizer;
        if (useSerial || usbDevice is null)
        {
            AppendLog(language.GetString("Flash_MtkUsbOnlyOperation"));
            return;
        }

        var mode = Enum.GetValues<MtkRpmbCaptureMode>()[Math.Max(Field<ComboBox>("MtkRpmbModeBox").SelectedIndex, 0)];
        if (MtkRpmbService.DescribeShapeRefusal(SelectedMtkProtocol, mode) is { } refusal)
        {
            AppendLog(refusal);
            return;
        }

        if (!TryReadRpmbShape(mode, Field<TextBox>("MtkRpmbOffsetBox").Text, Field<TextBox>("MtkRpmbLengthBox").Text, out var frames,
                out var byteOffset, out var byteLength))
        {
            AppendLog(language.GetString("Flash_MtkRpmbRangeInvalid"));
            return;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = language.GetString("Flash_MtkRpmbRead"),
            SuggestedFileName = "rpmb.bin",
            DefaultExtension = "bin"
        });
        if (_busy || _confirming || file?.TryGetLocalPath() is not { } destination) return;
        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkRpmbRead"),
                $"{language.GetString("Flash_MtkRpmbReadConfirm")}{Environment.NewLine}{usbDevice.DisplayName}");
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkRpmbReadButton").IsEnabled = false;
        try
        {
            var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
            var result = await MtkRpmbService.CaptureOnEndpointAsync(usbDevice, new MtkRpmbCaptureRequest
            {
                Family = SelectedMtkProtocol,
                DestinationPath = destination,
                Mode = mode,
                FrameRange = frames,
                ByteOffset = byteOffset,
                ByteLength = byteLength
            }, progress, cancellation.Token).ConfigureAwait(true);
            AppendLog(result.Summary);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Boots the agent to the state the secure-boot operations need from the pane selection and runs the
    /// given operation on that session; the serial and auto-match paths share this single entry.
    /// <para>按面板选择把代理带到能执行安全配置操作的状态，再在该会话上运行给定操作；串口与自动匹配两条路径
    /// 共用这一入口。</para>
    /// </summary>
    private async Task RunWithReadyMtkSessionAsync(Func<MtkSession, CancellationToken, Task> operation,
        CancellationToken token)
    {
        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var kind = SelectedMtkStorageKind;
        if (useSerial)
        {
            var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
            using var session = await connect(token).ConfigureAwait(true);
            await operation(session, token).ConfigureAwait(true);
        }
        else
        {
            var matched = await BuildMtkAutoStartupAsync(kind, token).ConfigureAwait(true);
            await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
            {
                var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched, token)
                    .ConfigureAwait(true);
                await operation(ready, token).ConfigureAwait(true);
                return true;
            }, token).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The shared shell for secure-boot operations: an optional user confirmation, the busy and
    /// cancellation state, and a recomputed control state once the operation ends.
    /// <para>安全配置操作的统一外壳：可选的用户确认、忙碌与取消状态、以及结束后按面板状态重算可用性。</para>
    /// </summary>
    private async Task RunMtkSecurityOperationAsync(string titleKey, string? confirmKey,
        Func<MtkSession, CancellationToken, Task> operation)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        if (confirmKey is not null)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                    language.GetString(confirmKey));
            }
            finally { _confirming = false; }
            if (!confirmed) return;
        }

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkFullDumpButton").IsEnabled = false;
        try
        {
            await RunWithReadyMtkSessionAsync(operation, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Reads the whole user data area the selected agent family reported into one contiguous image: the user
    /// picks the destination file and confirms, then the read-only collection runs on a ready session, staging
    /// the bytes and replacing the destination only when the staged length equals the reported length.
    /// <para>把所选代理家族上报的整个用户数据区读取为一份连续镜像：先让用户挑选目标文件并确认，再在已就绪的会话
    /// 上执行只读采集；字节先写暂存文件，只有暂存长度与上报长度一致才替换目标文件。</para>
    /// </summary>
    internal async void OnMtkFullDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var prompt = _localizer;
        var family = SelectedMtkProtocol;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = prompt.GetString("Flash_MtkFullDump"),
            SuggestedFileName = $"mtk-{family.ToString().ToLowerInvariant()}-user-area.img",
            DefaultExtension = "img",
            FileTypeChoices =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is not { Length: > 0 } destination) return;

        await RunMtkSecurityOperationAsync("Flash_MtkFullDump", "Flash_MtkFullDumpConfirm",
            async (session, token) =>
            {
                var progress = new Progress<FlashProgress>(update => AppendLog(update.Status));
                var record = await MtkFullDumpService.CaptureAsync(session, family, destination, progress, token)
                    .ConfigureAwait(true);
                AppendLog(prompt.GetString(record.CapacityUsedAsReported ? "Flash_MtkFullDumpReported" :
                    "Flash_MtkFullDumpClamped"));
            });
    }

    /// <summary>
    /// Parses only the shape of a memory window: the address accepts hexadecimal with an optional 0x prefix and the
    /// length a positive decimal integer.
    /// <para>解析内存窗口的形状：地址接受可选 0x 前缀的十六进制，长度接受十进制正整数。</para>
    /// </summary>
    /// <param name="addressText">The address text. <para>地址文本。</para></param>
    /// <param name="lengthText">The length text. <para>长度文本。</para></param>
    /// <param name="address">The parsed address. <para>解析出的地址。</para></param>
    /// <param name="lengthBytes">The parsed byte count. <para>解析出的字节数。</para></param>
    /// <returns>True when the shape parses. <para>形状可解析时为真。</para></returns>
    internal static bool TryReadMemoryWindow(string? addressText, string? lengthText, out uint address,
        out int lengthBytes)
    {
        if (addressText?.Trim() is not { Length: > 0 } text || lengthText?.Trim() is not { Length: > 0 } length)
        {
            address = 0;
            lengthBytes = 0;
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var addressParsed = uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedAddress);
        var lengthParsed = int.TryParse(length, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedLength);
        if (!addressParsed || !lengthParsed || parsedLength <= 0)
        {
            address = 0;
            lengthBytes = 0;
            return false;
        }

        address = parsedAddress;
        lengthBytes = parsedLength;
        return true;
    }

    /// <summary>
    /// Lays the captured bytes out as a hex window with an address and an ASCII column for the log pane.
    /// <para>把读到的字节排成带地址与 ASCII 列的十六进制窗口，供日志面板显示。</para>
    /// </summary>
    /// <param name="startAddress">The window start address. <para>窗口起始地址。</para></param>
    /// <param name="bytes">The bytes read. <para>读到的字节。</para></param>
    /// <returns>One line per sixteen bytes. <para>每行十六字节的文本。</para></returns>
    internal static string FormatMemoryWindow(uint startAddress, ReadOnlyMemory<byte> bytes)
    {
        var builder = new StringBuilder();
        for (var row = 0; row < bytes.Length; row += 16)
        {
            var width = Math.Min(16, bytes.Length - row);
            _ = builder.Append($"0x{startAddress + row:X8} ");
            for (var column = 0; column < 16; column++)
            {
                _ = builder.Append(column < width ? $"{bytes.Span[row + column]:X2} " : "   ");
            }

            for (var column = 0; column < width; column++)
            {
                var value = bytes.Span[row + column];
                _ = builder.Append(value is >= (byte)'!' and <= (byte)'~' or (byte)' ' ? (char)value : '.');
            }

            _ = builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Runs the given operation on a fresh preloader command-stage session: every click takes a newly initialized
    /// lease with no download-agent uploaded, so a protocol fault is rebuilt by the next click rather than retried
    /// on the faulted session.
    /// <para>在 preloader 命令阶段的新会话上运行给定操作：每次点击都经租约取一份全新、已初始化但未上传下载代理的
    /// 会话，因此协议 fault 后下一次点击自动重建，不在故障会话上重试。</para>
    /// </summary>
    private async Task RunMtkPreloaderAsync(string? confirmKey, Func<MtkSession, CancellationToken, Task> operation)
    {
        if (_busy || _confirming ||
            SelectedDevice is not { Protocol: DeviceProtocolKind.Mtk, Transport: DeviceTransportKind.Usb } usbDevice ||
            usbDevice.State != "preloader")
        {
            return;
        }

        var language = _localizer;
        if (confirmKey is not null && TopLevel.GetTopLevel(Surface) is Window owner)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString("Flash_MtkPreloaderGroup"),
                    language.GetString(confirmKey));
            }
            finally { _confirming = false; }
            if (!confirmed) return;
        }

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            await App.DeviceMonitor.RunMtkAsync(usbDevice, async session =>
            {
                await session.InitializeAsync(cancellation.Token).ConfigureAwait(true);
                await operation(session, cancellation.Token).ConfigureAwait(true);
                return true;
            }, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            AppendLog(language.GetString("Flash_MtkPreloaderSessionFaulted"));
        }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    internal static bool TryReadPreloaderAddress(string? text, out uint value)
    {
        var trimmed = text?.Trim();
        if (trimmed is not null && trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadPreloaderCount(string? text, out int value)
    {
        if (TryReadPreloaderAddress(text, out var hex))
        {
            value = checked((int)hex);
            return true;
        }

        return int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    internal async void OnMtkPreloaderPeekClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadPreloaderAddress(Field<TextBox>("MtkPreloaderOffsetBox").Text, out var address) ||
            !TryReadPreloaderCount(Field<TextBox>("MtkPreloaderLengthBox").Text, out var length) || length <= 0)
        {
            AppendLog(_localizer.GetString("Flash_MtkPreloaderOffset"));
            return;
        }

        await RunMtkPreloaderAsync(null, async (session, token) =>
        {
            var data = await MtkPreloaderService.PeekWindowAsync(session, address, length, token)
                .ConfigureAwait(true);
            AppendLog($"0x{address:X8}: {Convert.ToHexString(data)}");
        });
    }

    internal async void OnMtkPreloaderReturnToBromClick(object? sender, RoutedEventArgs e)
        => await RunMtkPreloaderAsync("Flash_MtkPreloaderReturnToBromConfirm", async (session, token) =>
            await MtkPreloaderService.ReturnToBootRomAsync(session, timeoutSeconds: 0, confirmed: true, token)
                .ConfigureAwait(true));

    internal async void OnMtkPreloaderDisableWatchdogClick(object? sender, RoutedEventArgs e)
        => await RunMtkPreloaderAsync("Flash_MtkPreloaderDisableWatchdogConfirm", async (session, token) =>
            await MtkPreloaderService.DisableWatchdogAsync(session, confirmed: true, token).ConfigureAwait(true));

    internal async void OnMtkPreloaderJumpClick(object? sender, RoutedEventArgs e)
    {
        if (Field<TextBox>("MtkPreloaderPartitionNameBox").Text?.Trim() is not { Length: > 0 } partitionName)
        {
            AppendLog(_localizer.GetString("Flash_MtkPreloaderPartitionName"));
            return;
        }

        await RunMtkPreloaderAsync("Flash_MtkPreloaderJumpConfirm", async (session, token) =>
            await MtkPreloaderService.JumpToPartitionAsync(session, partitionName, confirmed: true, token)
                .ConfigureAwait(true));
    }

    /// <summary>
    /// Jumps the preloader to its boot loader after confirmation: execution is transferred and the link is
    /// expected to drop, so the session is retired together with the protocol fault and must be reopened
    /// afterwards.
    /// <para>在确认后让 preloader 跳转到 Boot Loader：执行权被转移且链路预计会断开，会话随协议 fault 一起作废，
    /// 因此之后必须重新打开会话。</para>
    /// </summary>
    internal async void OnMtkPreloaderBootloaderJumpClick(object? sender, RoutedEventArgs e)
        => await RunMtkPreloaderAsync("Flash_MtkPreloaderBootloaderJumpConfirm", async (session, token) =>
            await MtkPreloaderService.JumpToBootLoaderAsync(session, confirmed: true, token)
                .ConfigureAwait(true));

    internal static bool TryParseHexBytes(string text, out byte[] bytes)
    {
        Span<char> digits = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var count = 0;
        foreach (var ch in text)
        {
            if (Uri.IsHexDigit(ch))
            {
                digits[count++] = ch;
            }
        }

        if (count == 0 || count % 2 != 0)
        {
            bytes = [];
            return false;
        }

        bytes = Convert.FromHexString(digits[..count]);
        return true;
    }

    internal async void OnMtkSetSpeedClick(object? sender, RoutedEventArgs e)
    {
        var language = _localizer;
        await RunWithReadyMtkOperationAsync(async (session, token) =>
        {
            var applied = SelectedMtkProtocol switch
            {
                MtkDaProtocol.Legacy => (await session.SetLegacyTransferSpeedAsync(
                    SelectedMtkBaudrate, token).ConfigureAwait(true)).ToString(),
                MtkDaProtocol.XFlash => (await session.SetXFlashUsbSpeedAsync(token).ConfigureAwait(true)).ToString(),
                _ => null
            };
            AppendLog(applied is null
                ? language.GetString("Flash_MtkSpeedFamilyUnsupported")
                : string.Format(language.GetString("Flash_MtkSpeedApplied"), applied));
        });
    }

    private async Task RunWithReadyMtkOperationAsync(Func<MtkSession, CancellationToken, Task> operation)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            await RunWithReadyMtkSessionAsync(operation, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            SetBusy(false);
            UpdateEdlButton();
        }
    }

    private int SelectedMtkBaudrate => Field<ComboBox>("MtkBaudBox").SelectedItem is int baud ? baud : 921600;

    internal void OnMtkRebootClick(object? sender, RoutedEventArgs e)
        => RunMtkShutdownAsync(MtkXFlashShutdownMode.NormalBoot,
            "Flash_MtkReboot", "Flash_MtkRebootConfirm", "Flash_MtkRebootDone");
    internal void OnMtkRebootFastbootClick(object? sender, RoutedEventArgs e)
        => RunMtkShutdownAsync(MtkXFlashShutdownMode.Fastboot,
            "Flash_MtkRebootFastboot", "Flash_MtkRebootFastbootConfirm", "Flash_MtkRebootFastbootDone");
    internal void OnMtkPowerOffClick(object? sender, RoutedEventArgs e)
        => RunMtkShutdownAsync(MtkXFlashShutdownMode.PowerOff,
            "Flash_MtkPowerOff", "Flash_MtkPowerOffConfirm", "Flash_MtkPowerOffDone");

    internal void OnMtkRebootMetaClick(object? sender, RoutedEventArgs e)
        => RunMtkXmlRebootAsync(MtkXmlBootMode.Meta,
            "Flash_MtkRebootMeta", "Flash_MtkRebootMetaConfirm", "Flash_MtkRebootMetaDone");

    internal void OnMtkRebootAndroidTestClick(object? sender, RoutedEventArgs e)
        => RunMtkXmlRebootAsync(MtkXmlBootMode.AndroidTest,
            "Flash_MtkRebootAndroidTest", "Flash_MtkRebootAndroidTestConfirm", "Flash_MtkRebootAndroidTestDone");

    /// <summary>
    /// META and Android-test reboots are defined only by the XML download-agent family: a non-XML family is refused
    /// locally before any device contact; when the family matches, a storage-ready XML agent is booted first and the
    /// boot destination is requested afterwards under confirmation.
    /// <para>META 与 Android 测试模式重启只由 XML 下载代理家族定义：非 XML 家族在接触设备前本地拒绝；家族匹配时先
    /// 启动到存储就绪的 XML 代理，再请求该启动目标，执行前需用户确认。</para>
    /// </summary>
    private async void RunMtkXmlRebootAsync(MtkXmlBootMode mode, string titleKey, string confirmKey, string doneKey)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner) return;
        var language = _localizer;
        if (SelectedMtkProtocol != MtkDaProtocol.Xml)
        {
            AppendLog(language.GetString("Flash_MtkXmlRebootFamilyRequired"));
            return;
        }

        if (!TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                language.GetString(confirmKey));
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        try
        {
            async Task FinishAsync(MtkSession session, CancellationToken token)
            {
                await session.RebootXmlAsync(new MtkXmlRebootOptions { Mode = mode }, cancellationToken: token)
                    .ConfigureAwait(true);
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, SelectedMtkStorageKind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await FinishAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(SelectedMtkStorageKind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await FinishAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }

            AppendLog(language.GetString(doneKey));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Reads one XML system property by exact key on a ready XML agent: the path is provided only by the
    /// XML family, so any other family is refused locally before device contact; the query is read-only,
    /// and the result together with its envelope name and version is logged without any authorization
    /// interpretation.
    /// <para>在已就绪的 XML 代理上按精确键名读取一条系统属性：该通路只由 XML 家族提供，其他家族在接触设备前
    /// 本地拒绝；查询是只读的，结果连同信封名与版本一起写进日志，不做任何授权解释。</para>
    /// </summary>
    internal async void OnMtkXmlPropertyQueryClick(object? sender, RoutedEventArgs e)
    {
        var name = Field<TextBox>("MtkXmlPropertyBox").Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            AppendLog(_localizer.GetString("Flash_MtkXmlPropertyRequired"));
            return;
        }

        var language = _localizer;
        if (SelectedMtkProtocol != MtkDaProtocol.Xml)
        {
            AppendLog(language.GetString("Flash_MtkXmlPropertyFamilyRequired"));
            return;
        }

        await RunMtkSecurityOperationAsync("Flash_MtkXmlPropertyQuery", null, async (session, token) =>
        {
            var property = await session.QueryXmlSystemPropertyAsync(name, cancellationToken: token)
                .ConfigureAwait(true);
            AppendLog(string.Format(language.GetString("Flash_MtkXmlPropertyResult"),
                property.Key, property.Value, property.EnvelopeName, property.Version ?? "-"));
        });
    }

    /// <summary>
    /// <para>先按面板选择启动到存储就绪的代理，再向代理请求关机或启动模式切换；执行前需用户确认。</para>
    /// Boots a storage-ready agent from the pane selection and then requests a power-off or a boot-mode
    /// transition from that agent; the user must confirm before anything is sent.
    /// </summary>
    /// <param name="mode">The authorized power/boot request. <para>经授权的电源/启动请求。</para></param>
    /// <param name="titleKey">Confirmation title resource key. <para>确认标题的资源键。</para></param>
    /// <param name="confirmKey">Confirmation text resource key. <para>确认正文的资源键。</para></param>
    /// <param name="doneKey">Success message resource key. <para>成功消息的资源键。</para></param>
    private async void RunMtkShutdownAsync(MtkXFlashShutdownMode mode, string titleKey, string confirmKey,
        string doneKey)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner ||
            !TryReadMtkSelection(out var useSerial, out var usbDevice, out var port, out var baudRate,
                out var attach, out var autoMatch))
        {
            return;
        }

        var language = _localizer;
        var kind = SelectedMtkStorageKind;
        if (MtkPowerRequestRefusalKey(SelectedMtkProtocol, mode) is { } refusal)
        {
            AppendLog(language.GetString(refusal));
            return;
        }

        _confirming = true;
        bool confirmed;
        try
        {
            confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString(titleKey), language.GetString(confirmKey));
        }
        finally { _confirming = false; }
        if (!confirmed) return;

        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        Field<Button>("CancelFlashButton").IsEnabled = true;
        Field<Button>("MtkFlashButton").IsEnabled = false;
        try
        {
            async Task FinishAsync(MtkSession session, CancellationToken token)
            {
                switch (SelectedMtkProtocol)
                {
                    case MtkDaProtocol.Legacy:
                        await session.ShutdownLegacyAsync(mode switch
                        {
                            MtkXFlashShutdownMode.PowerOff => MtkLegacyShutdownMode.PowerOff,
                            MtkXFlashShutdownMode.Fastboot => MtkLegacyShutdownMode.Fastboot,
                            _ => MtkLegacyShutdownMode.NormalBoot
                        }, token);
                        return;
                    case MtkDaProtocol.Xml
                        when mode is not (MtkXFlashShutdownMode.NormalBoot or MtkXFlashShutdownMode.Fastboot):
                        throw new InvalidOperationException(
                            "The XML download agent defines boot-mode reboot only; the pane refuses a power-off request for this family before any device contact.");
                    case MtkDaProtocol.Xml:
                        await session.RebootXmlAsync(new MtkXmlRebootOptions
                        {
                            Mode = mode == MtkXFlashShutdownMode.Fastboot
                                ? MtkXmlBootMode.Fastboot
                                : MtkXmlBootMode.Normal
                        }, cancellationToken: token);
                        return;
                    default:
                        await session.ShutdownXFlashAsync(mode, token);
                        return;
                }
            }

            if (useSerial)
            {
                var connect = BuildMtkSerialConnector(port!, baudRate, kind, attach, autoMatch);
                using var session = await connect(cancellation.Token);
                await FinishAsync(session, cancellation.Token);
            }
            else
            {
                var matched = await BuildMtkAutoStartupAsync(kind, cancellation.Token);
                await App.DeviceMonitor.RunMtkAsync(usbDevice!, async session =>
                {
                    var ready = await MtkBootstrap.MatchAsync(session, SelectedMtkProtocol, matched,
                        cancellation.Token);
                    await FinishAsync(ready, cancellation.Token);
                    return true;
                }, cancellation.Token);
            }

            AppendLog(language.GetString(doneKey));
        }
        catch (OperationCanceledException) { AppendLog(language.GetString("Flash_Canceled")); }
        catch (Exception ex) { AppendLog(ex.Message); }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            Field<Button>("CancelFlashButton").IsEnabled = false;
            UpdateEdlButton();
        }
    }

    internal Task<FlashResult> FlashImageAsync(DeviceTarget target, string partition, string imagePath,
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
        => _flashService.FlashImageAsync(target, partition, imagePath, progress, cancellationToken);

    internal static bool FlashModeAllowed(DeviceProtocolKind mode, bool flatBin,
        bool hasProgrammer, bool hasRockchipParameter)
        => mode == DeviceProtocolKind.Fastboot ||
            mode == DeviceProtocolKind.Rkdl && hasRockchipParameter ||
            flatBin && (mode == DeviceProtocolKind.Adb ||
                        mode == DeviceProtocolKind.Edl && hasProgrammer);

    internal static string FormatByteSize(long size)
    {
        if (size <= 0) return string.Empty;
        if (size >= 1024L * 1024 * 1024)
            return $"{size / 1024d / 1024d / 1024d:F2} GiB";
        if (size >= 1024L * 1024)
            return $"{size / 1024d / 1024d:F1} MiB";
        if (size >= 1024)
            return $"{size / 1024d:F1} KiB";
        return $"{size} B";
    }

    internal static List<FlashPartitionEntry> ParseScript(string path, string[] lines)
    {
        var scriptDir = Path.GetDirectoryName(path) ?? string.Empty;
        var regex = new Regex(
            @"(?:@\s*)?fastboot\s+(?:(?:%\*|\$\*)\s+)?(flash|erase|set_active|reboot|wipe-super|create-logical-partition|delete-logical-partition|create|delete)(?:\s+([^\s]+))?(?:\s+(.*?))?(?=\s*(?:\||&|;|>|$))",
            RegexOptions.IgnoreCase);

        var scriptRows = new List<FlashPartitionEntry>();
        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var command = match.Groups[1].Value.ToLowerInvariant();
            var partitionOrArg = match.Groups[2].Value.Trim();
            var relativeFilePath = match.Groups[3].Value.Trim();

            var fullPath = string.Empty;
            var fileName = string.Empty;
            var sizeStr = string.Empty;
            var partName = partitionOrArg;

            if (command == "set_active")
            {
                partName = partitionOrArg;
            }
            else if (command == "reboot")
            {
                partName = string.Empty;
            }
            else if (command == "wipe-super")
            {
                command = "wipe_super";
                partName = "super_empty";
                if (string.IsNullOrEmpty(relativeFilePath))
                {
                    relativeFilePath = partitionOrArg;
                }
            }
            else if (command == "create-logical-partition")
            {
                command = "create";
            }
            else if (command == "delete-logical-partition")
            {
                command = "delete";
            }

            if (!string.IsNullOrEmpty(relativeFilePath) && command != "set_active" && command != "reboot")
            {
                var normalizedPath = relativeFilePath
                    .Replace("%~dp0", "")
                    .Replace("`dirname $0`", "")
                    .Replace("$(dirname $0)", "")
                    .Replace("\"", "")
                    .Replace("'", "")
                    .Trim();

                if (normalizedPath.StartsWith("./", StringComparison.Ordinal) ||
                    normalizedPath.StartsWith(".\\", StringComparison.Ordinal))
                {
                    normalizedPath = normalizedPath[2..];
                }

                if (normalizedPath.Length >= 2 && normalizedPath[1] == ':')
                {
                    fullPath = normalizedPath;
                }
                else
                {
                    normalizedPath = normalizedPath.TrimStart('/', '\\');
                    fullPath = Path.Combine(scriptDir, normalizedPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                }
                fileName = Path.GetFileName(fullPath);

                if (partName.Replace(" ", "").Equals("super", StringComparison.OrdinalIgnoreCase) && !File.Exists(fullPath))
                {
                    var zstPath = Path.ChangeExtension(fullPath, ".zst");
                    if (File.Exists(zstPath))
                    {
                        fullPath = zstPath;
                        fileName = Path.GetFileName(zstPath);
                    }
                }

                if (File.Exists(fullPath))
                {
                    sizeStr = FormatByteSize(new FileInfo(fullPath).Length);
                }
            }

            var cleanName = partName.Replace(" ", "");
            var fileLength = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            var isSuperEmpty = cleanName.Equals("super_empty", StringComparison.OrdinalIgnoreCase);
            var stepCommand = isSuperEmpty ? "wipe_super" : command;
            var step = new FlashStep(stepCommand, cleanName, fullPath, fileLength);

            scriptRows.Add(new FlashPartitionEntry
            {
                Step = step,
                Name = cleanName.Length > 0 ? cleanName : command,
                Command = command,
                Size = sizeStr,
                FileName = fileName,
                FilePath = fullPath,
                CanSelect = true,
                IsSelected = false
            });
        }

        return scriptRows;
    }

    internal static List<FlashPartitionEntry> ParseManifestText(string path, string[] lines, out string? codeName, out bool requiresFastbootd)
    {
        var scriptDir = Path.GetDirectoryName(path) ?? string.Empty;
        var txtRows = new List<FlashPartitionEntry>();
        codeName = null;
        requiresFastbootd = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
            if (trimmedLine.StartsWith('#')) continue;

            if (trimmedLine.StartsWith("codename:", StringComparison.OrdinalIgnoreCase))
            {
                codeName = trimmedLine["codename:".Length..].Trim();
                continue;
            }
            if (trimmedLine.Equals("mode:fastbootd", StringComparison.OrdinalIgnoreCase))
            {
                requiresFastbootd = true;
                continue;
            }

            var parts = trimmedLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var partName = parts[0];
            var command = "flash";
            var relativeFilePath = string.Empty;
            var fullPath = string.Empty;
            var fileName = string.Empty;
            var sizeStr = string.Empty;

            if (parts.Length == 1)
            {
                relativeFilePath = Path.Combine("images", $"{partName}.img");
                fileName = $"{partName}.img";
            }
            else if (parts.Length >= 2)
            {
                var arg = parts[1];
                if (arg.Equals("delete", StringComparison.OrdinalIgnoreCase))
                {
                    command = "delete";
                }
                else if (arg.Equals("create", StringComparison.OrdinalIgnoreCase))
                {
                    command = "create";
                }
                else
                {
                    relativeFilePath = arg;
                }
            }

            if (!string.IsNullOrEmpty(relativeFilePath))
            {
                var normalizedPath = relativeFilePath
                    .Replace("\"", "")
                    .Replace("'", "")
                    .Trim();

                if (normalizedPath.StartsWith("./", StringComparison.Ordinal) ||
                    normalizedPath.StartsWith(".\\", StringComparison.Ordinal))
                {
                    normalizedPath = normalizedPath[2..];
                }

                if (normalizedPath.Length >= 2 && normalizedPath[1] == ':')
                {
                    fullPath = normalizedPath;
                }
                else
                {
                    normalizedPath = normalizedPath.TrimStart('/', '\\');
                    fullPath = Path.Combine(scriptDir, normalizedPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                }
                fileName = Path.GetFileName(fullPath);

                if (!File.Exists(fullPath))
                {
                    var zstPath = Path.ChangeExtension(fullPath, ".zst");
                    if (File.Exists(zstPath))
                    {
                        fullPath = zstPath;
                        fileName = Path.GetFileName(zstPath);
                    }
                }

                if (File.Exists(fullPath))
                {
                    sizeStr = FormatByteSize(new FileInfo(fullPath).Length);
                }
            }

            var cleanName = partName.Replace(" ", "");
            var fileLength = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            var isSuperEmpty = cleanName.Equals("super_empty", StringComparison.OrdinalIgnoreCase);
            var stepCommand = isSuperEmpty ? "wipe_super" : command.Replace(" ", "");
            var step = new FlashStep(stepCommand, cleanName, fullPath, fileLength);

            txtRows.Add(new FlashPartitionEntry
            {
                Step = step,
                Name = cleanName,
                Size = sizeStr,
                Command = command.Replace(" ", ""),
                FileName = fileName,
                FilePath = fullPath,
                CanSelect = true,
                IsSelected = false
            });
        }

        return txtRows;
    }

    internal static List<FlashPartitionEntry> ParseDirectory(string path)
    {
        var targetDir = path;
        var imagesSubdir = Path.Combine(path, "images");
        if (Directory.Exists(imagesSubdir) &&
            Directory.EnumerateFiles(imagesSubdir, "*", SearchOption.TopDirectoryOnly).Any() &&
            !Directory.EnumerateFiles(path, "*.img", SearchOption.TopDirectoryOnly).Any())
        {
            targetDir = imagesSubdir;
        }

        var imgFiles = Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly);
        var dirRows = new List<FlashPartitionEntry>(imgFiles.Length);
        foreach (var file in imgFiles)
        {
            var fileInfo = new FileInfo(file);
            var fileName = fileInfo.Name;
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is ".bat" or ".sh" or ".exe" or ".cmd" or ".ps1" or ".txt")
            {
                continue;
            }

            var partName = Path.GetFileNameWithoutExtension(fileName).Replace(" ", "");
            if (fileName.EndsWith(".img.zst", StringComparison.OrdinalIgnoreCase))
            {
                partName = Path.GetFileNameWithoutExtension(partName);
            }
            var sizeStr = FormatByteSize(fileInfo.Length);
            var isSuperEmpty = partName.Equals("super_empty", StringComparison.OrdinalIgnoreCase);
            var command = isSuperEmpty ? "wipe_super" : "flash";
            var step = new FlashStep(command, partName, file, fileInfo.Length);

            dirRows.Add(new FlashPartitionEntry
            {
                Step = step,
                Name = partName,
                Size = sizeStr,
                Command = command,
                FileName = fileName,
                FilePath = file,
                CanSelect = true,
                IsSelected = false
            });
        }

        return dirRows;
    }
}