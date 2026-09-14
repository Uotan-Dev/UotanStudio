using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;

namespace UotanStudio.Services.Delivery;

/// <summary>Metadata and user-owned certificate for ZIP delivery.</summary>
internal sealed record DeliveryPackageOptions
{
    public bool Ota { get; init; }
    public string CertificatePath { get; init; } = "";
    public string SigningKeyPath { get; init; } = "";
    public string BuildFingerprint { get; init; } = "";
    public string Incremental { get; init; } = "";
    public uint SdkLevel { get; init; }
    /// <summary>Original ROM's Edify updater; empty discovers META-INF alongside the selected DAT files.</summary>
    public string RecoveryUpdaterPath { get; init; } = "";
}

/// <summary>Streams full A/B OTA and custom Recovery packages; signs ZIP comments using Android's whole-file format.</summary>
internal static partial class DeliveryPackageBuilder
{
    internal static void Validate(DeliveryPlan plan)
    {
        var options = plan.Package;
        if (!plan.PackPayload && !options.Ota && !plan.Recovery) return;
        if ((options.CertificatePath.Length == 0) != (options.SigningKeyPath.Length == 0))
            throw new InvalidDataException("外层签名需要同时指定证书与私钥。");
        if (options.Ota && !plan.PackPayload)
            throw new InvalidDataException("OTA ZIP requires Payload mode.");
        foreach (var value in new[] { options.BuildFingerprint, options.Incremental })
            if (value.Length > 1024 || value.Any(char.IsControl)) throw new InvalidDataException("OTA 元数据包含无效字符。");
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper && (!plan.OrganizeDirectory || plan.PackPayload || plan.AskWipe || plan.Reboot
            || plan.Slot == "all" || plan.Files.Any(f => f.Generated || f.IncludeInSuper || f.Fastbootd || f.Partition is "super" or "super_empty"
                || !IsRecoveryArtifact(f.FileName))))
            throw new NotSupportedException("Recovery ZIP 仅支持物理 IMG 分区或成组的 Android DAT 文件；不支持动态分区、容器、双槽同时刷写、自动清除或重启。");
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper)
            ValidateDatArtifacts(Selected(plan));
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper && Selected(plan).Length == 0) throw new InvalidDataException("Recovery 包没有默认选中的镜像。");
    }

    internal static async Task BuildAsync(DeliveryPlan plan, string stage, string images, CancellationToken token,
        IProgress<DeliveryProgress>? progress = null, IDeliverySigningKeyProvider? signingKeys = null)
    {
        Validate(plan);
        if (plan.PackPayload && plan.OrganizeDirectory)
        {
            var path = Path.Combine(stage, "ota.zip");
            var metadata = "ota-type=AB\n";
            if (plan.DeviceCodename.Length > 0) metadata += $"pre-device={plan.DeviceCodename}\n";
            if (plan.Package.BuildFingerprint.Length > 0) metadata += $"post-build={plan.Package.BuildFingerprint}\n";
            if (plan.Package.Incremental.Length > 0) metadata += $"post-build-incremental={plan.Package.Incremental}\n";
            if (plan.Package.SdkLevel > 0) metadata += $"post-sdk-level={plan.Package.SdkLevel}\n";
            if (plan.Payload.SecurityPatchLevel.Length > 0) metadata += $"post-security-patch-level={plan.Payload.SecurityPatchLevel}\n";
            if (plan.Payload.MaxTimestamp > 0) metadata += $"post-timestamp={plan.Payload.MaxTimestamp}\n";
            string Properties(string streaming, string all) => "ota-streaming-property-files=" + streaming.PadRight(512) + "\nota-property-files=" + all.PadRight(512) + "\n";
            async Task WriteZip(string text)
            {
                token.ThrowIfCancellationRequested();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
                using var writer = WriterFactory.OpenWriter(output, ArchiveType.Zip, new ZipWriterOptions(CompressionType.None) { UseZip64 = true });
                writer.Write("payload.bin", new FileInfo(Path.Combine(images, "payload.bin")));
                writer.Write("payload_properties.txt", new FileInfo(Path.Combine(images, "payload_properties.txt")));
                writer.Write("META-INF/com/android/metadata", new MemoryStream(Encoding.UTF8.GetBytes(text)));
                if (plan.Package.CertificatePath.Length > 0)
                {
                    using var cert = LoadCertificate(plan.Package.CertificatePath);
                    writer.Write("META-INF/com/android/otacert", new MemoryStream(Encoding.UTF8.GetBytes(cert.ExportCertificatePem())));
                }
            }
            await WriteZip(metadata + Properties("", ""));
            var ranges = ReadStoredRanges(path);
            var payload = ranges["payload.bin"];
            var properties = ranges["payload_properties.txt"];
            var meta = ranges["META-INF/com/android/metadata"];
            using var input = File.OpenRead(Path.Combine(images, "payload.bin"));
            var header = new byte[24]; input.ReadExactly(header);
            var metadataLength = checked(24UL + BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
            var streaming = $"payload.bin:{payload.Offset}:{payload.Size},payload_properties.txt:{properties.Offset}:{properties.Size},metadata:{meta.Offset}:{meta.Size}";
            var all = $"payload_metadata.bin:{payload.Offset}:{metadataLength}," + streaming;
            if (all.Length > 512) throw new InvalidDataException("OTA property ranges exceed reserved metadata space.");
            File.Delete(path);
            await WriteZip(metadata + Properties(streaming, all));
            var actual = ReadStoredRanges(path);
            if (ranges.Any(p => actual[p.Key] != p.Value)) throw new InvalidDataException("OTA ZIP entry offsets changed while finalizing metadata.");
            if (plan.Package.SigningKeyPath.Length > 0) await SignAsync(path, plan.Package, token, signingKeys);
        }
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper)
        {
            var path = Path.Combine(stage, "recovery.zip");
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var hashes = new StringBuilder();
                foreach (var file in Selected(plan))
                {
                    token.ThrowIfCancellationRequested();
                    var source = Path.Combine(images, file.FileName);
                    var compressedPatch = file.FileName.EndsWith(".patch.dat.br", StringComparison.OrdinalIgnoreCase);
                    string? temporary = null;
                    try
                    {
                        if (compressedPatch) source = temporary = await ExpandPatchAsync(source, stage, token);
                        await using var input = IsDatArtifact(file.FileName) ? File.OpenRead(source) : MetadataReader.OpenImage(source);
                        var member = "images/" + (compressedPatch ? file.FileName[..^3]
                            : file.FileName.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ? file.FileName[..^3] + ".br" : file.FileName);
                        await AddHashedMemberAsync(zip, member, input, hashes, token, progress);
                    }
                    finally { if (temporary is not null) File.Delete(temporary); }
                }
                if (HasDat(plan))
                {
                    foreach (var set in DatSets(plan).Where(s => s.Patch is null))
                    {
                        using var empty = new MemoryStream();
                        await AddHashedMemberAsync(zip, "images/" + set.PatchName, empty, hashes, token, progress);
                    }
                    await using var engine = File.OpenRead(DeliveryRecoveryUpdater.Resolve(plan));
                    await AddHashedMemberAsync(zip, DeliveryRecoveryUpdater.ArchivePath, engine, hashes, token, progress);
                }
                if (Selected(plan).Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var name in new[] { "zstd", "zstd-arm64", "zstd-x86_64", "zstd-loong64" })
                    {
                        var source = Path.Combine(stage, "bin", "android", name);
                        if (!File.Exists(source)) continue;
                        await using var input = File.OpenRead(source);
                        await AddHashedMemberAsync(zip, "bin/android/" + name, input, hashes, token, progress);
                    }
                }
                using var edify = new MemoryStream(Encoding.UTF8.GetBytes(plan.Scripts.GetValueOrDefault("updater-script", RecoveryUpdaterScript(plan))));
                await AddHashedMemberAsync(zip, DeliveryRecoveryUpdater.ScriptPath, edify, hashes, token, progress);
                await AddTextAsync(zip, "checksums.sha256", hashes.ToString(), token);
                var updater = Entry(zip, "META-INF/com/google/android/update-binary");
                updater.ExternalAttributes = 0x81ed << 16;
                await using (var entry = updater.Open()) await entry.WriteAsync(Encoding.UTF8.GetBytes(plan.Scripts.GetValueOrDefault("Recovery.sh", RecoveryScript(plan))), token);
            }
            if (plan.Package.SigningKeyPath.Length > 0) await SignAsync(path, plan.Package, token, signingKeys);
        }
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
        {
            // A ZIP-organized Super delivery is a complete Recovery package
            // directory. The host publishes the directory atomically; Recovery
            // consumes META-INF/com/google/android/update-binary directly.
            var meta = Path.Combine(stage, "META-INF", "com", "google", "android");
            Directory.CreateDirectory(meta);
            await File.WriteAllTextAsync(Path.Combine(meta, "update-binary"), plan.Scripts.GetValueOrDefault("Recovery.sh", RecoveryScript(plan)), new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(Path.Combine(meta, "updater-script"), "#Powered by UotanStudio\n", new UTF8Encoding(false), token);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(meta, "update-binary"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    // Inspect actual local headers rather than predicting ZIP64 extra field lengths.
    private static Dictionary<string, (long Offset, ulong Size)> ReadStoredRanges(string path)
    {
        using var input = File.OpenRead(path);
        var result = new Dictionary<string, (long, ulong)>(StringComparer.Ordinal);
        var header = new byte[30];
        while (input.Position < input.Length)
        {
            input.ReadExactly(header);
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (signature == 0x02014b50) break;
            if (signature != 0x04034b50
                || (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) & 8) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != 0)
                throw new InvalidDataException("OTA requires seekable, stored ZIP entries.");
            var name = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26))]; input.ReadExactly(name);
            var extra = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))]; input.ReadExactly(extra);
            ulong size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22));
            if (size == uint.MaxValue)
            {
                var found = false;
                for (var offset = 0; offset + 4 <= extra.Length;)
                {
                    var type = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(offset));
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(offset + 2));
                    if (offset + 4 + length > extra.Length) throw new InvalidDataException("Invalid ZIP extra field.");
                    if (type == 1 && length >= 16) { size = BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(offset + 4)); found = true; break; }
                    offset += 4 + length;
                }
                if (!found) throw new InvalidDataException("ZIP64 entry is missing its sizes.");
            }
            result.Add(Encoding.UTF8.GetString(name), (input.Position, size));
            input.Position = checked(input.Position + (long)size);
        }
        return result;
    }

    private static DeliveryFile[] Selected(DeliveryPlan plan) => plan.Files.Where(f => !f.Optional || f.Default).ToArray();

    internal static bool IsDatArtifact(string fileName) =>
        fileName.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".new.dat.br", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".patch.dat", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".patch.dat.br", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRecoveryArtifact(string fileName) =>
        fileName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) || IsDatArtifact(fileName);

    internal static bool SameDatSet(string first, string second) => IsDatArtifact(first) && IsDatArtifact(second)
        && DatStem(first).Equals(DatStem(second), StringComparison.OrdinalIgnoreCase);

    internal static string DatStem(string fileName)
    {
        foreach (var suffix in new[] { ".transfer.list", ".new.dat.br", ".new.dat", ".patch.dat.br", ".patch.dat" })
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        return fileName;
    }

    private static void ValidateDatArtifacts(IReadOnlyList<DeliveryFile> files)
    {
        foreach (var group in files.Where(f => IsDatArtifact(f.FileName)).GroupBy(f => DatStem(f.FileName), StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(f => f.FileName).ToArray();
            var transfers = names.Count(n => n.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase));
            var newData = names.Count(n => n.EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".new.dat.br", StringComparison.OrdinalIgnoreCase));
            var patches = names.Count(n => n.EndsWith(".patch.dat", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".patch.dat.br", StringComparison.OrdinalIgnoreCase));
            if (group.Select(f => f.Partition).Distinct(StringComparer.Ordinal).Count() != 1
                || transfers != 1 || newData != 1 || patches > 1)
                throw new InvalidDataException($"DAT 文件组须包含唯一的 transfer.list 与 new.dat，最多一个 patch.dat，且分区名一致：{group.Key}");
        }
    }

    private static async Task AddHashedMemberAsync(ZipArchive zip, string member, Stream input, StringBuilder hashes,
        CancellationToken token, IProgress<DeliveryProgress>? progress)
    {
        var hash = await SHA256.HashDataAsync(input, token);
        input.Position = 0;
        hashes.Append(Convert.ToHexString(hash).ToLowerInvariant()).Append("  ").Append(member).Append('\n');
        await using var output = Entry(zip, member).Open();
        await CopyPackageMemberAsync(input, output, member, token, progress);
    }

    // AOSP only decompresses new.dat.br. Patch offsets always address uncompressed, STORED patch bytes.
    private static async Task<string> ExpandPatchAsync(string source, string stage, CancellationToken token)
    {
        var temporary = Path.Combine(stage, ".patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var input = File.OpenRead(source);
            using var decoder = new BrotliDecoder();
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            var decoded = new byte[65536];
            long total = 0;
            var available = 0;
            var status = OperationStatus.NeedMoreData;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (status == OperationStatus.NeedMoreData)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(available), token);
                    if (read == 0) throw new InvalidDataException("Truncated Brotli DAT patch.");
                    available += read;
                }
                status = decoder.Decompress(buffer.AsSpan(0, available), decoded, out var consumed, out var written);
                if (status == OperationStatus.InvalidData) throw new InvalidDataException("Invalid Brotli DAT patch.");
                available -= consumed;
                buffer.AsSpan(consumed, available).CopyTo(buffer);
                total = checked(total + written);
                if (total > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("DAT patch exceeds 64 GiB.");
                await output.WriteAsync(decoded.AsMemory(0, written), token);
                if (status != OperationStatus.Done) continue;
                if (available != 0 || await input.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
                    throw new InvalidDataException("Trailing data after Brotli DAT patch.");
                return temporary;
            }
        }
        catch { File.Delete(temporary); throw; }
    }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2009, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }
    private static async Task AddFileAsync(ZipArchive zip, string name, string source, CancellationToken token,
        IProgress<DeliveryProgress>? progress = null)
    {
        await using var input = File.OpenRead(source);
        await using var output = Entry(zip, name).Open();
        await CopyPackageMemberAsync(input, output, name, token, progress);
    }

    private static async Task CopyPackageMemberAsync(Stream input, Stream output, string name,
        CancellationToken token, IProgress<DeliveryProgress>? progress)
    {
        var length = input.Length;
        void Report(long count) => progress?.Report(new(85 + count * 4.0 / Math.Max(1, length), "Packaging ZIP",
            (count, length), "Delivery_ProgressPackage", Detail: name));
        Report(0);
        using var tracked = new DeliveryWriteProgressStream(output, Report, token);
        await input.CopyToAsync(tracked, 65536, token);
        Report(tracked.Length);
    }
    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken token)
    {
        await using var output = Entry(zip, name).Open();
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), token);
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 65536) throw new InvalidDataException("签名证书不存在或超过 64 KiB。");
        var bytes = File.ReadAllBytes(path);
        return Encoding.ASCII.GetString(bytes).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(bytes)) : X509CertificateLoader.LoadCertificate(bytes);
    }

    internal static async Task SignAsync(string path, DeliveryPackageOptions options, CancellationToken token,
        IDeliverySigningKeyProvider? signingKeys = null)
    {
        using var cert = LoadCertificate(options.CertificatePath);
        using var key = DeliverySigning.Load(signingKeys, options.SigningKeyPath);
        using var publicKey = cert.GetRSAPublicKey() ?? throw new InvalidDataException("外层证书需要 RSA 公钥。");
        if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
            throw new InvalidDataException("外层签名私钥与证书不匹配。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536, true);
        if (file.Length < 22) throw new InvalidDataException("Invalid ZIP.");
        file.Position = file.Length - 22;
        var tail = new byte[22]; await file.ReadExactlyAsync(tail, token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != 0x06054b50 || tail[20] != 0 || tail[21] != 0)
            throw new InvalidDataException("ZIP already has a comment.");
        file.Position = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long remaining = file.Length - 2;
        while (remaining > 0)
        {
            var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (count == 0) throw new EndOfStreamException();
            digest.AppendData(buffer, 0, count); remaining -= count;
        }
        var signature = key.SignHash(digest.GetHashAndReset(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cms = EncodeCms(cert, signature);
        var message = Encoding.ASCII.GetBytes("signed by UotanStudio\0");
        var total = checked(message.Length + cms.Length + 6);
        if (total > ushort.MaxValue) throw new InvalidDataException("ZIP signature exceeds comment size.");
        var comment = new byte[total]; message.CopyTo(comment, 0); cms.CopyTo(comment, message.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(total - 6), checked((ushort)(cms.Length + 6)));
        comment[total - 4] = 255; comment[total - 3] = 255;
        BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(total - 2), (ushort)total);
        if (comment.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x05, 0x06 }) >= 0) throw new System.Security.Cryptography.CryptographicException("Spurious ZIP end marker in signature.");
        file.Position = file.Length - 2;
        var length = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)total);
        await file.WriteAsync(length, token); await file.WriteAsync(comment, token);
    }

    // Direct RSA signature, no authenticated attributes: matches signapk -w and recovery verifier.
    private static byte[] EncodeCms(X509Certificate2 cert, byte[] signature)
    {
        const string sha256 = "2.16.840.1.101.3.4.2.1", data = "1.2.840.113549.1.7.1";
        var writer = new AsnWriter(AsnEncodingRules.DER);
        var context = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        void Algorithm(string oid) { writer.PushSequence(); writer.WriteObjectIdentifier(oid); writer.WriteNull(); writer.PopSequence(); }
        writer.PushSequence(); writer.WriteObjectIdentifier("1.2.840.113549.1.7.2"); writer.PushSequence(context);
        writer.PushSequence(); writer.WriteInteger(1); writer.PushSetOf(); Algorithm(sha256); writer.PopSetOf();
        writer.PushSequence(); writer.WriteObjectIdentifier(data); writer.PopSequence();
        writer.PushSetOf(context); writer.WriteEncodedValue(cert.RawData); writer.PopSetOf(context);
        writer.PushSetOf(); writer.PushSequence(); writer.WriteInteger(1);
        writer.PushSequence(); writer.WriteEncodedValue(cert.IssuerName.RawData);
        var serial = cert.GetSerialNumber(); Array.Reverse(serial); writer.WriteIntegerUnsigned(serial); writer.PopSequence();
        Algorithm(sha256); Algorithm("1.2.840.113549.1.1.1"); writer.WriteOctetString(signature);
        writer.PopSequence(); writer.PopSetOf(); writer.PopSequence(); writer.PopSequence(context); writer.PopSequence();
        return writer.Encode();
    }

}