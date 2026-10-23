using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UotanStudio.Compress.Zip;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Native daemon app transfer and bundle-manager completion messages.
/// <para>原生守护进程应用传输与 bundle-manager 完成消息。</para>
/// </summary>
/// <param name="connection">Established HDC connection used to open the app task channels. <para>用于打开应用任务通道的已建立 HDC 连接。</para></param>
public sealed class HdcAppClient(HdcConnection connection) : Contracts.IHdcAppClient
{
    /// <summary>
    /// Transfers and installs one HAP/HSP/APP package using AppCheck/Begin/Data/Finish.
    /// <para>使用 AppCheck/Begin/Data/Finish 传输并安装单个 HAP/HSP/APP 包。</para>
    /// </summary>
    /// <param name="path">Local package file, or a directory that is installed via <see cref="InstallDirectory"/>. <para>本地包文件；若为目录则经 <see cref="InstallDirectory"/> 安装。</para></param>
    /// <param name="cancellationToken">Cancels before any device exchange begins. <para>在任何设备交换开始前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException">The file extension is not <c>.hap</c>, <c>.hsp</c> or <c>.app</c>. <para>文件扩展名不是 <c>.hap</c>、<c>.hsp</c> 或 <c>.app</c>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed install result. <para>守护进程报告安装结果失败。</para></exception>
    public string Install(string path, CancellationToken cancellationToken = default)
        => Install(path, "", cancellationToken);

    /// <summary>
    /// Installs a package with bundle-manager options such as -r (replace while preserving data).
    /// <para>安装带 bundle-manager 选项（如 -r：覆盖安装并保留数据）的包。</para>
    /// </summary>
    /// <param name="path">Local package file, or a directory that is installed via <see cref="InstallDirectory"/>. <para>本地包文件；若为目录则经 <see cref="InstallDirectory"/> 安装。</para></param>
    /// <param name="options">Bundle-manager option string; only ASCII letters, digits, space, <c>-</c>, <c>_</c> and <c>.</c> are accepted. <para>bundle-manager 选项串；仅接受 ASCII 字母、数字、空格、<c>-</c>、<c>_</c> 与 <c>.</c>。</para></param>
    /// <param name="cancellationToken">Cancels before any device exchange begins. <para>在任何设备交换开始前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException">The extension is unsupported or <paramref name="options"/> contains rejected characters. <para>扩展名不受支持，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>. <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed install result. <para>守护进程报告安装结果失败。</para></exception>
    public string Install(string path, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (Directory.Exists(path)) return InstallDirectory(path, options, cancellationToken);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".hap" or ".hsp" or ".app")) throw new ArgumentException("Unsupported HDC package extension.", nameof(path));
        return InstallPackage(path, options, cancellationToken);
    }

    /// <summary>
    /// Installs distinct package paths in the supplied order, stopping at the first failure.
    /// <para>按给定顺序安装互不重复的包路径，遇到第一个失败即停止。</para>
    /// </summary>
    /// <param name="paths">Package files or directories; duplicates are collapsed by full path (case-insensitive on Windows) and the batch is capped at 256 entries. <para>包文件或目录；按完整路径去重（Windows 上不区分大小写），批次上限 256 项。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>. <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked before each input is normalized and before each install. <para>在每个输入规范化之前以及每次安装之前检查。</para></param>
    /// <returns>One completion-text entry per installed package, in install order. <para>按安装顺序为每个已安装包返回一条完成文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>. <para><paramref name="paths"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The batch is empty, exceeds 256 inputs, or <paramref name="options"/> contains rejected characters. <para>批次为空、输入超过 256 项，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="FileNotFoundException">An input path exists as neither file nor directory. <para>某个输入路径既不是文件也不是目录。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public IReadOnlyList<string> Install(IEnumerable<string> paths, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var packages = NormalizeInstallBatch(paths, cancellationToken);
        return [.. packages.Select(path => Install(path, options, cancellationToken))];
    }

    /// <summary>
    /// Installs a bounded package directory as one upstream TAR task so related modules are submitted together.
    /// <para>将有界的包目录作为单个上游 TAR 任务安装，使相关模块一起提交。</para>
    /// </summary>
    /// <param name="path">Directory scanned recursively for <c>.hap</c>, <c>.hsp</c> and <c>.app</c> files; links are never followed. <para>递归扫描 <c>.hap</c>、<c>.hsp</c>、<c>.app</c> 文件的目录；绝不跟随链接。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>. <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked during enumeration and TAR writing; the temporary TAR file is deleted either way. <para>在枚举与写入 TAR 期间检查；临时 TAR 文件无论如何都会删除。</para></param>
    /// <returns>The bundle-manager completion text for the single TAR transfer. <para>单个 TAR 传输的 bundle-manager 完成文本。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="path"/> is not an existing directory. <para><paramref name="path"/> 不是现有目录。</para></exception>
    /// <exception cref="IOException">A reparse point was hit, the 100000-entry, 256-package or 4 GiB budget was exceeded, no package was found, or the daemon reported failure. <para>遇到重分析点、超过 100000 条目 / 256 包 / 4 GiB 预算、未找到包，或守护进程报告失败。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> contains rejected characters. <para><paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string InstallDirectory(string path, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var root = new DirectoryInfo(path);
        if (!root.Exists) throw new DirectoryNotFoundException(path);
        var packages = CollectInstallPackages(root, cancellationToken);
        var tar = CreatePackageTar(root, packages, cancellationToken);
        try
        {
            return InstallPackage(tar, options, cancellationToken);
        }
        finally { File.Delete(tar); }
    }

    private string InstallPackage(string path, string options, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var input = File.OpenRead(path);
        var config = BuildInstallConfig(input.Length, extension, options);
        using var channel = connection.OpenChannel(HdcCommand.AppCheck, HdcTransferCodec.EncodeConfig(config));
        HdcTransferEngine.Expect(channel, HdcCommand.AppBegin, cancellationToken);
        HdcTransferEngine.SendData(channel, input, input.Length, HdcCommand.AppData, false, null, cancellationToken);
        return ReadResult(channel, 1, cancellationToken);
    }

    /// <summary>Installs the signed modules in an APP container that match a device type, without re-signing them.
    /// <para>安装 APP 容器中与目标设备类型匹配的已签名模块，不进行重新签名。</para></summary>
    /// <param name="path">APP (ZIP) container path opened read-only.
    /// <para>以只读方式打开的 APP（ZIP）容器路径。</para></param>
    /// <param name="deviceType">Device type matched against the <c>deviceType</c> arrays in <c>pack.info</c>.
    /// <para>与 <c>pack.info</c> 中各 <c>deviceType</c> 数组匹配的机型代号。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>.
    /// <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked while extracting modules; the staging directory is deleted either way.
    /// <para>解压模块期间检查；暂存目录无论如何都会删除。</para></param>
    /// <returns>The bundle-manager completion text for the single-module install or the combined TAR transfer.
    /// <para>单模块安装或合并 TAR 传输的 bundle-manager 完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="deviceType"/> is blank or <paramref name="options"/> contains rejected characters.
    /// <para><paramref name="deviceType"/> 为空，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="InvalidDataException">The container, its <c>pack.info</c> metadata or a module entry violates the size, naming or uniqueness constraints.
    /// <para>容器、其 <c>pack.info</c> 元数据或模块条目违反大小、命名或唯一性约束。</para></exception>
    /// <exception cref="NotSupportedException">The container has no installable module for <paramref name="deviceType"/>.
    /// <para>容器中没有匹配 <paramref name="deviceType"/> 的可安装模块。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string InstallAppBundle(string path, string deviceType, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipContainer.OpenRead(path);
        var staging = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var package = ExtractAppBundle(archive, deviceType, staging, cancellationToken);
            return InstallPackage(package, options, cancellationToken);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    /// <summary>
    /// Extracts the modules of an APP container that match a device type into a staging directory and returns the
    /// single module file, or a combined TAR when several modules were selected.
    /// <para>将 APP 容器中与设备类型匹配的模块解压到暂存目录，并返回单模块文件；选中多个模块时返回合并的 TAR。</para>
    /// </summary>
    /// <param name="archive">Opened APP (ZIP) container. <para>已打开的 APP（ZIP）容器。</para></param>
    /// <param name="deviceType">Device type matched against the <c>deviceType</c> arrays in <c>pack.info</c>.
    /// <para>与 <c>pack.info</c> 中各 <c>deviceType</c> 数组匹配的机型代号。</para></param>
    /// <param name="staging">Existing staging directory that receives the extracted modules.
    /// <para>接收解压模块的现有暂存目录。</para></param>
    /// <param name="cancellationToken">Checked while extracting. <para>解压期间检查。</para></param>
    /// <returns>Path of the extracted module or of the combined TAR inside <paramref name="staging"/>.
    /// <para><paramref name="staging"/> 中已解压模块或合并 TAR 的路径。</para></returns>
    /// <exception cref="InvalidDataException">The container, its metadata or a module entry violates the size, naming
    /// or uniqueness constraints. <para>容器、其元数据或模块条目违反大小、命名或唯一性约束。</para></exception>
    /// <exception cref="NotSupportedException">No module matches <paramref name="deviceType"/>.
    /// <para>没有模块匹配 <paramref name="deviceType"/>。</para></exception>
    internal static string ExtractAppBundle(ZipArchive archive, string deviceType, string staging,
        CancellationToken cancellationToken)
    {
        var selected = SelectAppModules(archive, deviceType);
        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = entry.Open();
            using var output = File.Create(Path.Combine(staging, entry.Name));
            var buffer = new byte[65536];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copied = checked(copied + read);
                if (copied > entry.Length) throw new InvalidDataException("APP module exceeds its declared length.");
                output.Write(buffer, 0, read);
            }

            if (copied != entry.Length) throw new InvalidDataException("Truncated APP module.");
        }

        if (selected.Count == 1) return Path.Combine(staging, selected[0].Name);
        var tar = Path.Combine(staging, "modules.tar");
        using (var output = File.Create(tar))
        using (var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Ustar))
            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteEntry(Path.Combine(staging, entry.Name), entry.Name);
            }

        return tar;
    }

    /// <summary>
    /// Normalizes one install batch into distinct full paths, rejecting an empty or oversized batch.
    /// <para>将单个安装批次规范化为互不重复的完整路径，并拒绝空批次或超限批次。</para>
    /// </summary>
    /// <param name="paths">Package files or directories. <para>包文件或目录。</para></param>
    /// <param name="cancellationToken">Checked before each input is normalized. <para>在每个输入规范化之前检查。</para></param>
    /// <returns>Distinct full paths in input order. <para>按输入顺序排列的互不重复完整路径。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.
    /// <para><paramref name="paths"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The batch is empty or exceeds 256 inputs.
    /// <para>批次为空或输入超过 256 项。</para></exception>
    /// <exception cref="FileNotFoundException">An input exists as neither file nor directory.
    /// <para>某个输入既不是文件也不是目录。</para></exception>
    internal static List<string> NormalizeInstallBatch(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var packages = new List<string>();
        var inputCount = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inputCount > 256) throw new ArgumentException("HDC install batch exceeds 256 packages.", nameof(paths));
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new FileNotFoundException("HDC install input not found.", full);
            if (!packages.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                packages.Add(full);
        }

        if (packages.Count == 0) throw new ArgumentException("HDC install batch is empty.", nameof(paths));
        return packages;
    }

    /// <summary>
    /// Enumerates the package files of one directory task within the entry, package and byte budgets.
    /// <para>在条目、包数与字节预算内枚举单个目录任务的包文件。</para>
    /// </summary>
    /// <param name="root">Root directory scanned recursively; links are never followed.
    /// <para>递归扫描的根目录；绝不跟随链接。</para></param>
    /// <param name="cancellationToken">Checked during enumeration. <para>枚举期间检查。</para></param>
    /// <returns>The package files in walk order. <para>按遍历顺序排列的包文件。</para></returns>
    /// <exception cref="IOException">A reparse point was hit, a budget was exceeded, or no package was found.
    /// <para>遇到重分析点、预算超限，或未找到包。</para></exception>
    internal static List<FileInfo> CollectInstallPackages(DirectoryInfo root, CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var packages = new List<FileInfo>();
        long size = 0;
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("HDC install does not follow links.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 100000) throw new IOException("HDC install directory entry limit exceeded.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("HDC install does not follow links.");
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file && file.Extension.ToLowerInvariant() is ".hap" or ".hsp" or ".app")
                {
                    size = checked(size + file.Length);
                    if (size > 4L * 1024 * 1024 * 1024 || packages.Count >= 256)
                        throw new IOException("HDC install package budget exceeded.");
                    packages.Add(file);
                }
            }
        }

        if (packages.Count == 0) throw new IOException("HDC install directory contains no package.");
        return packages;
    }

    /// <summary>
    /// Writes one upstream Ustar archive containing every package, keyed by its root-relative path.
    /// <para>写入一个包含全部包的上游 Ustar 归档，条目名为相对根目录的路径。</para>
    /// </summary>
    /// <param name="root">Root the relative entry names are computed from. <para>计算条目相对路径的根目录。</para></param>
    /// <param name="packages">Package files to include. <para>要包含的包文件。</para></param>
    /// <param name="cancellationToken">Checked during TAR writing. <para>写入 TAR 期间检查。</para></param>
    /// <returns>Path of the created TAR file in the temporary directory; the caller deletes it.
    /// <para>临时目录中已创建的 TAR 文件路径；由调用方删除。</para></returns>
    /// <exception cref="IOException">A package changed size during preparation. <para>某个包在准备期间大小改变。</para></exception>
    internal static string CreatePackageTar(DirectoryInfo root, IReadOnlyList<FileInfo> packages, CancellationToken cancellationToken)
    {
        var tar = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N") + ".tar");
        using (var output = new FileStream(tar, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Ustar))
            foreach (var package in packages.OrderBy(p => p.FullName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = package.OpenRead();
                if (input.Length != package.Length) throw new IOException("HDC package changed during preparation.");
                var entry = new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile,
                    Path.GetRelativePath(root.FullName, package.FullName).Replace('\\', '/'))
                { DataStream = input };
                writer.WriteEntry(entry);
            }

        cancellationToken.ThrowIfCancellationRequested();
        return tar;
    }

    /// <summary>
    /// Builds the install configuration advertised to the daemon.
    /// <para>构建声明给守护进程的安装配置。</para>
    /// </summary>
    /// <param name="fileSize">Exact package byte count. <para>包的精确字节数。</para></param>
    /// <param name="extension">Package extension kept on the device-side temporary name.
    /// <para>设备侧临时名保留的包扩展名。</para></param>
    /// <param name="options">Validated bundle-manager option string. <para>已校验的 bundle-manager 选项串。</para></param>
    /// <returns>The configuration carried by the app check frame. <para>应用检查帧携带的配置。</para></returns>
    internal static TransferConfig BuildInstallConfig(long fileSize, string extension, string options)
        => new()
        {
            FileSize = fileSize,
            OptionalName = Guid.NewGuid().ToString("N") + extension,
            HoldTimestamp = false,
            FunctionName = "install",
            Options = options
        };

    internal static List<ZipArchiveEntry> SelectAppModules(ZipArchive archive, string deviceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceType);
        if (archive.Entries.Count > 1024) throw new InvalidDataException("Too many APP entries.");
        var metadata = archive.Entries.SingleOrDefault(e => e.FullName == "pack.info")
            ?? throw new InvalidDataException("APP package has no pack.info.");
        if (metadata.Length > 4 * 1024 * 1024) throw new InvalidDataException("APP metadata exceeds limit.");
        using var input = metadata.Open();
        var bytes = new byte[checked((int)metadata.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("APP metadata exceeds declared size.");
        using var json = JsonDocument.Parse(bytes);
        var selected = new List<ZipArchiveEntry>();
        long total = 0;
        foreach (var package in json.RootElement.GetProperty("packages").EnumerateArray())
        {
            if (!package.GetProperty("deviceType").EnumerateArray().Any(t => t.GetString() == deviceType)) continue;
            if (package.TryGetProperty("deliveryWithInstall", out var delivery) && !delivery.GetBoolean()) continue;
            var name = package.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing APP module name.");
            if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe APP module name.");
            var entry = archive.Entries.SingleOrDefault(e => e.FullName == name + ".hap" || e.FullName == name + ".hsp")
                ?? throw new InvalidDataException("Missing APP module: " + name);
            total = checked(total + entry.Length);
            if (total > 4L * 1024 * 1024 * 1024 || selected.Count >= 256 || selected.Contains(entry))
                throw new InvalidDataException("APP module count, size or uniqueness constraint failed.");
            selected.Add(entry);
        }
        if (selected.Count == 0) throw new NotSupportedException("APP contains no installable module for device type " + deviceType);
        return selected;
    }

    internal static void ValidateOptions(string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid bundle-manager options.", nameof(options));
    }

    /// <summary>
    /// Validates a bundle name accepted by the daemon: ASCII letters, digits, <c>.</c> and <c>_</c>.
    /// <para>校验守护进程接受的 bundle 名称：ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para>
    /// </summary>
    /// <param name="bundle">Bundle name to validate. <para>待校验的 bundle 名称。</para></param>
    /// <exception cref="ArgumentException">The name is blank or contains rejected characters.
    /// <para>名称为空或含被拒绝的字符。</para></exception>
    internal static void ValidateBundle(string bundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        if (bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("Invalid bundle name.", nameof(bundle));
    }

    /// <summary>
    /// Uninstalls the exact named bundle.
    /// <para>卸载指定精确名称的 bundle。</para>
    /// </summary>
    /// <param name="bundle">Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted. <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para></param>
    /// <param name="cancellationToken">Cancels before the uninstall task is opened. <para>在卸载任务打开前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> is blank or contains rejected characters. <para><paramref name="bundle"/> 为空或含被拒绝的字符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed uninstall result. <para>守护进程报告卸载结果失败。</para></exception>
    public string Uninstall(string bundle, CancellationToken cancellationToken = default)
        => Uninstall(bundle, "", cancellationToken);

    /// <summary>
    /// Uninstalls a bundle with options such as -k (keep data), without changing default behavior.
    /// <para>带选项（如 -k：保留数据）卸载 bundle，不改变默认行为。</para>
    /// </summary>
    /// <param name="bundle">Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted. <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para></param>
    /// <param name="options">Bundle-manager option string; when empty only the bundle name is sent. <para>bundle-manager 选项串；为空时仅发送 bundle 名称。</para></param>
    /// <param name="cancellationToken">Cancels before the uninstall task is opened. <para>在卸载任务打开前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> or <paramref name="options"/> contains rejected characters. <para><paramref name="bundle"/> 或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed uninstall result. <para>守护进程报告卸载结果失败。</para></exception>
    public string Uninstall(string bundle, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        ValidateBundle(bundle);
        using var channel = connection.OpenChannel(HdcCommand.AppUninstall, Encoding.UTF8.GetBytes(options.Length == 0 ? bundle : options + " " + bundle));
        return ReadResult(channel, 2, cancellationToken);
    }

    /// <summary>
    /// Reads the daemon's app result frame and rejects failure modes.
    /// <para>读取守护进程的应用结果帧并拒绝失败模式。</para>
    /// </summary>
    /// <param name="channel">Channel of the app task. <para>应用任务的通道。</para></param>
    /// <param name="expectedMode">Expected result mode: 1 for install, 2 for uninstall.
    /// <para>预期的结果模式：1 为安装，2 为卸载。</para></param>
    /// <param name="ct">Cancels the wait. <para>取消等待。</para></param>
    /// <returns>The bundle-manager completion text. <para>bundle-manager 的完成文本。</para></returns>
    /// <exception cref="InvalidDataException">The frame is truncated or carries another mode.
    /// <para>帧被截断或携带其它模式。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed result. <para>守护进程报告结果失败。</para></exception>
    internal static string ReadResult(IHdcTaskChannel channel, byte expectedMode, CancellationToken ct)
        => ParseResult(HdcTransferEngine.Expect(channel, HdcCommand.AppFinish, ct, timeoutMs: 300000).Payload ?? [], expectedMode);

    /// <summary>
    /// Parses a daemon app result payload and rejects failure modes.
    /// <para>解析守护进程的应用结果负载并拒绝失败模式。</para>
    /// </summary>
    /// <param name="payload">Result payload: mode byte, status byte and the completion text.
    /// <para>结果负载：模式字节、状态字节与完成文本。</para></param>
    /// <param name="expectedMode">Expected result mode: 1 for install, 2 for uninstall.
    /// <para>预期的结果模式：1 为安装，2 为卸载。</para></param>
    /// <returns>The bundle-manager completion text. <para>bundle-manager 的完成文本。</para></returns>
    /// <exception cref="InvalidDataException">The payload is truncated or carries another mode.
    /// <para>负载被截断或携带其它模式。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed result. <para>守护进程报告结果失败。</para></exception>
    internal static string ParseResult(byte[] payload, byte expectedMode)
    {
        if (payload.Length < 2) throw new InvalidDataException("Truncated HDC app result.");
        if (payload[0] != expectedMode) throw new InvalidDataException("Unexpected HDC app result mode.");
        var text = Encoding.UTF8.GetString(payload.AsSpan(2));
        var vendorSuccess = payload[1] == 0 && text.Trim().Equals(
            expectedMode == 1 ? "install bundle successfully." : "uninstall bundle successfully.", StringComparison.Ordinal);
        if ((payload[1] != 1 && !vendorSuccess) || text.TrimStart().StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"HDC application operation failed (status {payload[1]}): " + text);
        return text;
    }

    /// <summary>
    /// Validates the text form of an uninstall result, which the server relays as console output instead of a
    /// structured app frame. <para>校验文本形式的卸载结果；server 以控制台输出而非结构化应用帧中转该结果。</para>
    /// </summary>
    /// <param name="text">Result text relayed by the server. <para>由 server 中转的结果文本。</para></param>
    /// <returns>The original text when it reports success. <para>报告成功时返回原始文本。</para></returns>
    /// <exception cref="IOException">The text is empty, reports an error or omits the success marker.
    /// <para>文本为空、报告错误或缺少成功标记。</para></exception>
    internal static string ValidateUninstallText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) throw new IOException("HDC application operation returned no result.");
        if (!trimmed.Contains("uninstall bundle successfully.", StringComparison.Ordinal)
            || trimmed.Contains("msg:error:", StringComparison.Ordinal)
            || trimmed.Contains("[Fail]", StringComparison.Ordinal))
        {
            throw new IOException("HDC application operation did not report success: " + trimmed);
        }

        return text;
    }
}