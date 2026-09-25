using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

/// <summary>Explicit inputs for a combined fstab and vbmeta verification-disable transaction.</summary>
public sealed record AndroidSecurityDisableRequest
{
    public IReadOnlyList<string> FstabPaths { get; init; } = [];
    public string? VbmetaPath { get; init; }
    public required string OutputDirectory { get; init; }
    public bool DisableVerity { get; init; } = true;
    public bool DisableForceEncryption { get; init; } = true;
    public bool DisableVbmetaVerification { get; init; } = true;
}

/// <summary>Outputs published by <see cref="AndroidSecurityDisableService"/>.</summary>
public sealed record AndroidSecurityDisableResult(IReadOnlyList<string> OutputPaths, IReadOnlyList<string> Changes);

/// <summary>Stages and publishes a combined fstab/vbmeta verification-disable operation as one transaction.</summary>
public static class AndroidSecurityDisableService
{
    private const int MaximumFstabBytes = 4 * 1024 * 1024;

    public static async Task<AndroidSecurityDisableResult> ApplyAsync(
        AndroidSecurityDisableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        if (!request.DisableVerity && !request.DisableForceEncryption && !request.DisableVbmetaVerification)
        {
            throw new ArgumentException("At least one security change must be requested.", nameof(request));
        }

        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputRoot);
        var transactionId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(outputRoot, $".security-disable-{transactionId}.staging");
        var backup = Path.Combine(outputRoot, $".security-disable-{transactionId}.backup");
        Directory.CreateDirectory(staging);
        var staged = new List<(string Staged, string Target)>();
        var changes = new List<string>();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        try
        {
            foreach (var fstabPath in request.FstabPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(fstabPath);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("Fstab input was not found.", source);
                }
                var file = new FileInfo(source);
                if (file.Length > MaximumFstabBytes)
                {
                    throw new InvalidDataException($"Fstab input exceeds {MaximumFstabBytes} bytes: '{source}'.");
                }
                var name = Path.GetFileName(source);
                if (!names.Add(name))
                {
                    throw new InvalidDataException($"Duplicate output filename '{name}'.");
                }
                var text = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
                var updated = FstabVerificationEditor.Patch(text, request.DisableVerity, request.DisableForceEncryption);
                var stagedPath = Path.Combine(staging, name);
                await File.WriteAllTextAsync(stagedPath, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                staged.Add((stagedPath, Path.Combine(outputRoot, name)));
                if (!string.Equals(text, updated, StringComparison.Ordinal))
                {
                    changes.Add($"Patched {name}.");
                }
            }

            if (request.DisableVbmetaVerification)
            {
                if (string.IsNullOrWhiteSpace(request.VbmetaPath))
                {
                    throw new ArgumentException("A vbmeta image is required when verification disabling is enabled.", nameof(request));
                }
                var source = Path.GetFullPath(request.VbmetaPath);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("VBMeta input was not found.", source);
                }
                var name = Path.GetFileName(source);
                if (!names.Add(name))
                {
                    throw new InvalidDataException($"Duplicate output filename '{name}'.");
                }
                var stagedPath = Path.Combine(staging, name);
                var result = await AvbSigner.DisableVbmetaVerificationAsync(source, stagedPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidDataException(result.ErrorMessage ?? "Failed to disable VBMeta verification.");
                }
                staged.Add((stagedPath, Path.Combine(outputRoot, name)));
                changes.Add($"Disabled verification in {name}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Publish(staged, backup, cancellationToken);
            return new(staged.Select(item => item.Target).ToArray(), changes);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteEmptyDirectory(backup);
        }
    }

    private static void Publish(IReadOnlyList<(string Staged, string Target)> files, string backupDirectory,
        CancellationToken cancellationToken)
    {
        var published = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? saved = null;
                if (File.Exists(file.Target))
                {
                    Directory.CreateDirectory(backupDirectory);
                    saved = Path.Combine(backupDirectory, Path.GetFileName(file.Target));
                    File.Move(file.Target, saved, overwrite: false);
                }
                try
                {
                    File.Move(file.Staged, file.Target, overwrite: false);
                }
                catch
                {
                    if (saved is not null)
                    {
                        File.Move(saved, file.Target, overwrite: false);
                    }
                    throw;
                }
                published.Add((file.Target, saved));
            }
            TryDeleteDirectory(backupDirectory);
        }
        catch (Exception publicationError)
        {
            List<Exception>? rollbackErrors = null;
            for (var i = published.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Delete(published[i].Target);
                    if (published[i].Backup is { } saved)
                    {
                        File.Move(saved, published[i].Target, overwrite: false);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors ??= [];
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors is not null)
            {
                rollbackErrors.Insert(0, publicationError);
                throw new AggregateException("Security outputs failed to publish and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }
}