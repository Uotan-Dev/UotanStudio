using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Native daemon app transfer and bundle-manager completion messages.</summary>
public sealed class HdcAppClient(HdcConnection connection) : UotanStudio.Comm.Contracts.IHdcAppClient
{
    /// <summary>Transfers and installs one HAP/HSP/APP package using AppCheck/Begin/Data/Finish.</summary>
    public string Install(string path, CancellationToken cancellationToken = default)
        => Install(path, "", cancellationToken);

    /// <summary>Installs a package with bundle-manager options such as -r (replace while preserving data).</summary>
    public string Install(string path, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (Directory.Exists(path)) return InstallDirectory(path, options, cancellationToken);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".hap" or ".hsp" or ".app")) throw new ArgumentException("Unsupported HDC package extension.", nameof(path));
        return InstallPackage(path, options, cancellationToken);
    }

    /// <summary>Installs distinct package paths in the supplied order, stopping at the first failure.</summary>
    public IReadOnlyList<string> Install(IEnumerable<string> paths, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(paths);
        var packages = new List<string>();
        var inputCount = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inputCount > 256) throw new ArgumentException("HDC install batch exceeds 256 packages.", nameof(paths));
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("HDC install input not found.", full);
            if (!packages.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) packages.Add(full);
        }
        if (packages.Count == 0) throw new ArgumentException("HDC install batch is empty.", nameof(paths));
        return packages.Select(path => Install(path, options, cancellationToken)).ToArray();
    }

    /// <summary>Installs a bounded package directory as one upstream TAR task so related modules are submitted together.</summary>
    public string InstallDirectory(string path, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var root = new DirectoryInfo(path);
        if (!root.Exists) throw new DirectoryNotFoundException(path);
        var pending = new Stack<DirectoryInfo>(); pending.Push(root);
        var packages = new List<FileInfo>();
        long size = 0;
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("HDC install does not follow links.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 100000) throw new IOException("HDC install directory entry limit exceeded.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("HDC install does not follow links.");
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file && file.Extension.ToLowerInvariant() is ".hap" or ".hsp" or ".app")
                {
                    size = checked(size + file.Length);
                    if (size > 4L * 1024 * 1024 * 1024 || packages.Count >= 256) throw new IOException("HDC install package budget exceeded.");
                    packages.Add(file);
                }
            }
        }
        if (packages.Count == 0) throw new IOException("HDC install directory contains no package.");
        var tar = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
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
                    writer.WriteEntryAsync(entry, cancellationToken).GetAwaiter().GetResult();
                }
            cancellationToken.ThrowIfCancellationRequested();
            return InstallPackage(tar, options, cancellationToken);
        }
        finally { File.Delete(tar); }
    }

    private string InstallPackage(string path, string options, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var input = File.OpenRead(path);
        var config = new TransferConfig { FileSize = input.Length, OptionalName = Guid.NewGuid().ToString("N") + extension, HoldTimestamp = false, FunctionName = "install", Options = options };
        using var channel = connection.OpenChannel(HdcCommand.AppCheck, HdcTransferCodec.EncodeConfig(config));
        HdcSyncClient.Expect(channel, HdcCommand.AppBegin, cancellationToken);
        using var transfer = new HdcSyncClient(connection);
        transfer.SendData(channel, input, input.Length, HdcCommand.AppData, cancellationToken);
        return ReadResult(channel, 1, cancellationToken);
    }

    /// <summary>Installs the signed modules in an APP container that match a device type, without re-signing them.</summary>
    public string InstallAppBundle(string path, string deviceType, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path);
        var selected = SelectAppModules(archive, deviceType);
        var staging = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
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
            if (selected.Count == 1) return InstallPackage(Path.Combine(staging, selected[0].Name), options, cancellationToken);
            var tar = Path.Combine(staging, "modules.tar");
            using (var output = File.Create(tar))
            using (var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Ustar))
                foreach (var entry in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteEntryAsync(Path.Combine(staging, entry.Name), entry.Name, cancellationToken).GetAwaiter().GetResult();
                }
            return InstallPackage(tar, options, cancellationToken);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

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

    private static void ValidateOptions(string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid bundle-manager options.", nameof(options));
    }

    /// <summary>Uninstalls the exact named bundle.</summary>
    public string Uninstall(string bundle, CancellationToken cancellationToken = default)
        => Uninstall(bundle, "", cancellationToken);

    /// <summary>Uninstalls a bundle with options such as -k (keep data), without changing default behavior.</summary>
    public string Uninstall(string bundle, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        if (bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("Invalid bundle name.", nameof(bundle));
        using var channel = connection.OpenChannel(HdcCommand.AppUninstall, Encoding.UTF8.GetBytes(options.Length == 0 ? bundle : options + " " + bundle));
        return ReadResult(channel, 2, cancellationToken);
    }

    private static string ReadResult(HdcStream channel, byte expectedMode, CancellationToken ct)
    {
        var finish = HdcSyncClient.Expect(channel, HdcCommand.AppFinish, ct, timeoutMs: 300000);
        var payload = finish.Payload ?? [];
        if (payload.Length < 2) throw new InvalidDataException("Truncated HDC app result.");
        if (payload[0] != expectedMode) throw new InvalidDataException("Unexpected HDC app result mode.");
        var text = Encoding.UTF8.GetString(payload.AsSpan(2));
        var vendorSuccess = payload[1] == 0 && text.Trim().Equals(
            expectedMode == 1 ? "install bundle successfully." : "uninstall bundle successfully.", StringComparison.Ordinal);
        if ((payload[1] != 1 && !vendorSuccess) || text.TrimStart().StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"HDC application operation failed (status {payload[1]}): " + text);
        return text;
    }
}