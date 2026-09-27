using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Runs every numbered rawprogram and matching patch through one EDL session.</summary>
internal static class FlashEdlBundleService
{
    /// <summary>Inspects all bundle members before opening a device, then flashes them in manifest order.</summary>
    public static async Task FlashAsync(DetectedDevice device, string folder, string programmerPath, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Edl || !File.Exists(programmerPath))
        {
            throw new InvalidOperationException("An EDL device and Firehose programmer are required.");
        }
        var options = InspectFolder(folder, false, ct);
        await App.DeviceMonitor.RunEdlAsync(device, async session =>
        {
            await InspectAllAsync(session, options, ct).ConfigureAwait(false);
            using var programmer = File.OpenRead(programmerPath);
            await session.EnsureInitializedAsync(programmer, ct).ConfigureAwait(false);
            await ExecuteAllAsync(session, options, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    internal static async Task FlashSerialCoreAsync(Func<CancellationToken, Task<EdlSession>> connect,
        string folder, bool includeUfsProvision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        var options = InspectFolder(folder, !includeUfsProvision, cancellationToken);
        using var session = await connect(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The serial connector returned no EDL session.");
        cancellationToken.ThrowIfCancellationRequested();
        await InspectAllAsync(session, options, cancellationToken).ConfigureAwait(false);
        await ExecuteAllAsync(session, options, cancellationToken).ConfigureAwait(false);
    }

    private static FlashBundleOptions[] InspectFolder(string folder, bool skipUfsProvision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ct.ThrowIfCancellationRequested();
        folder = Path.GetFullPath(folder);
        var directory = Directory.Exists(Path.Combine(folder, "images"))
            ? Path.Combine(folder, "images") : folder;
        var manifests = Directory.EnumerateFiles(directory, "rawprogram*.xml", SearchOption.TopDirectoryOnly)
            .Take(257)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (manifests.Length is 0 or > 256)
        {
            throw new InvalidDataException("A bundle requires between one and 256 rawprogram manifests.");
        }
        var programs = RawProgramParser.ParseAll(manifests, directory);
        foreach (var entry in programs.Where(entry => !string.IsNullOrEmpty(entry.Filename)))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(entry.SourceFile))
            {
                throw new FileNotFoundException("EDL bundle image is missing.", entry.SourceFile);
            }
        }
        return manifests.Select((manifest, index) => new FlashBundleOptions
        {
            RawProgramPath = manifest,
            PatchPath = File.Exists(Path.Combine(directory,
                "patch" + Path.GetFileNameWithoutExtension(manifest)["rawprogram".Length..] + ".xml"))
                ? Path.Combine(directory,
                    "patch" + Path.GetFileNameWithoutExtension(manifest)["rawprogram".Length..] + ".xml")
                : null,
            ImageDirectory = directory,
            // Provisioning is a package-wide operation, after all program/patch manifests.
            SkipUfsProvision = skipUfsProvision || index != manifests.Length - 1
        }).ToArray();
    }

    private static async Task InspectAllAsync(EdlSession session, FlashBundleOptions[] options, CancellationToken ct)
    {
        var orchestrator = new FlashBundleOrchestrator(session);
        foreach (var option in options)
        {
            ct.ThrowIfCancellationRequested();
            option.DryRun = true;
            var preflight = await orchestrator.FlashBundleAsync(option, null, ct).ConfigureAwait(false);
            if (!preflight.IsSuccess)
            {
                throw new InvalidDataException(DescribeFailure(preflight));
            }
            option.DryRun = false;
        }
    }

    private static async Task ExecuteAllAsync(EdlSession session, FlashBundleOptions[] options, CancellationToken ct)
    {
        var orchestrator = new FlashBundleOrchestrator(session);
        var results = await orchestrator.FlashPackageAsync(options, null, ct).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                throw new IOException(DescribeFailure(result));
            }
        }
    }

    /// <summary>
    /// <para>从结构化执行证据构造失败说明：取消、首个失败、部分完成与重连需求分别来自
    /// FinalOutcome/FailureKind/Items/RequiresReconnect，而不是解析响应文本。</para>
    /// Builds the failure description from structured execution evidence: cancellation, first
    /// failure, partial completion, and reconnect need come from FinalOutcome/FailureKind/Items/
    /// RequiresReconnect rather than from parsed response text.
    /// </summary>
    private static string DescribeFailure(FlashBundleResult result)
    {
        var interrupted = result.Items.FirstOrDefault(item =>
            item.Outcome is not UotanStudio.Comm.Protocols.Common.Evidence.CommandOutcomeKind.Completed);
        return $"EDL bundle failed: outcome={result.FinalOutcome}, failure={(result.FailureKind?.ToString() ?? "unclassified")}, " +
            $"item={(interrupted is null ? "-" : $"{interrupted.Kind}:{interrupted.Outcome}/{interrupted.FailureKind?.ToString() ?? "-"}")}, " +
            $"requiresReconnect={result.RequiresReconnect}, acknowledgedBytes={result.TotalBytesWritten}, " +
            $"programmerHash={(result.ResourceSha256Hex ?? "-")}. Detail: {string.Join("; ", result.Errors)}";
    }
}