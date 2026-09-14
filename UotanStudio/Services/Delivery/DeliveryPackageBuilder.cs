using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Services.Keys;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

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
}

/// <summary>Streams full A/B OTA and custom Recovery packages; signs ZIP comments using Android's whole-file format.</summary>
internal static class DeliveryPackageBuilder
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
        IProgress<DeliveryProgress>? progress = null)
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
            if (plan.Package.SigningKeyPath.Length > 0) await SignAsync(path, plan.Package, token);
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
                    await using var input = IsDatArtifact(file.FileName)
                        ? new FileStream(Path.Combine(images, file.FileName), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true)
                        : MetadataReader.OpenImage(Path.Combine(images, file.FileName));
                    var hash = await SHA256.HashDataAsync(input, token); input.Position = 0;
                    hashes.Append(Convert.ToHexString(hash).ToLowerInvariant()).Append("  images/").Append(file.FileName).Append('\n');
                    await using var entry = Entry(zip, "images/" + file.FileName).Open();
                    await CopyPackageMemberAsync(input, entry, file.FileName, token, progress);
                }
                await AddTextAsync(zip, "checksums.sha256", hashes.ToString(), token);
                var updater = Entry(zip, "META-INF/com/google/android/update-binary");
                updater.ExternalAttributes = 0x81ed << 16;
                await using (var entry = updater.Open()) await entry.WriteAsync(Encoding.UTF8.GetBytes(plan.Scripts.GetValueOrDefault("Recovery.sh", RecoveryScript(plan))), token);
                await AddTextAsync(zip, "META-INF/com/google/android/updater-script", RecoveryUpdaterScript(plan), token);
            }
            if (plan.Package.SigningKeyPath.Length > 0) await SignAsync(path, plan.Package, token);
        }
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
        {
            // A ZIP-organized Super delivery is a complete Recovery package
            // directory. The host publishes the directory atomically; Recovery
            // consumes META-INF/com/google/android/update-binary directly.
            var meta = Path.Combine(stage, "META-INF", "com", "google", "android");
            Directory.CreateDirectory(meta);
            await File.WriteAllTextAsync(Path.Combine(meta, "update-binary"), RecoveryScript(plan), new UTF8Encoding(false), token);
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

    private static bool IsRecoveryArtifact(string fileName) =>
        fileName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) || IsDatArtifact(fileName);

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
            if (group.Select(f => f.Partition).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
                || transfers != 1 || newData != 1 || patches > 1)
                throw new InvalidDataException($"DAT 文件组缺少 transfer.list 或 new.dat：{group.Key}");
        }
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

    internal static async Task SignAsync(string path, DeliveryPackageOptions options, CancellationToken token)
    {
        using var cert = LoadCertificate(options.CertificatePath);
        using var key = KeyLibraryService.LoadPrivateKey(options.SigningKeyPath);
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

    internal static string RecoveryScript(DeliveryPlan plan)
    {
        if (plan.PackSuper)
        {
            var imageName = plan.Super.Zstd ? "super.zst" : "super.img";
            var extraction = plan.Super.Zstd
                ? $"package_extract_zstd() {{ ui_print '- Writing super'; mkdir -p /tmp/bin/android; unzip -p \"$ZIPFILE\" \"bin/android/zstd\" >/tmp/bin/android/zstd || return 1; chmod 755 /tmp/bin/android/zstd; unzip -p \"$ZIPFILE\" \"{imageName}\" | /tmp/bin/android/zstd -c -d >\"$1\"; }}"
                : $"package_extract_file() {{ ui_print '- Writing super'; unzip -p \"$ZIPFILE\" \"{imageName}\" >\"$1\"; }}";
            var invoke = plan.Super.Zstd ? "package_extract_zstd" : "package_extract_file";
            return $"#!/sbin/bash\nOUTFD=/proc/self/fd/$2\nZIPFILE=\"$3\"\nui_print() {{ echo -e \"ui_print $1\\nui_print\" >>\"$OUTFD\"; }}\nui_print \"====================================\"\nui_print \"          UotanStudio Super package          \"\nui_print \"====================================\"\n{extraction}\nfor base in /dev/block/by-name /dev/block/bootdevice/by-name; do\n  if [ -b \"$base/super\" ]; then {invoke} \"$base/super\"; exit 0; fi\ndone\nui_print \"- super partition not found\"\nexit 1\n";
        }
        var script = new StringBuilder("""
            #!/sbin/sh
            set -eu
            OUTFD="$2"
            ZIP="$3"
            ui_print() { printf 'ui_print %s\nui_print\n' "$*" >"/proc/self/fd/$OUTFD"; }
            fail() { ui_print "$*"; exit 1; }
            for tool in unzip sha256sum blockdev dd stat getprop readlink; do command -v "$tool" >/dev/null || fail "Missing $tool"; done
            WORK=$(mktemp -d /tmp/uotan.XXXXXX)
            trap 'rm -rf "$WORK"' EXIT
            unzip -o "$ZIP" 'images/*' checksums.sha256 -d "$WORK" >/dev/null || fail 'Extraction failed'
            cd "$WORK"
            sha256sum -c checksums.sha256 || fail 'Checksum mismatch'
            """);
        script.Append('\n');
        if (plan.DeviceCodename.Length > 0) script.AppendLine($"[ \"$(getprop ro.product.device)\" = '{plan.DeviceCodename}' ] || fail 'Device mismatch'");
        script.AppendLine(plan.Slot.Length > 0 ? $"SLOT='_{plan.Slot}'" : "SLOT=$(getprop ro.boot.slot_suffix)\ncase \"$SLOT\" in ''|_a|_b) ;; *) fail 'Invalid slot' ;; esac");
        var selected = Selected(plan);
        var physical = selected.Where(f => !IsDatArtifact(f.FileName)).ToArray();
        var datGroups = selected.Where(f => IsDatArtifact(f.FileName))
            .GroupBy(f => DatStem(f.FileName), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Stem: g.Key, Files: g.ToArray(), Transfer: g.First(f => f.FileName.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase)),
                NewData: g.First(f => f.FileName.EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase)
                    || f.FileName.EndsWith(".new.dat.br", StringComparison.OrdinalIgnoreCase)),
                Patch: g.FirstOrDefault(f => f.FileName.EndsWith(".patch.dat", StringComparison.OrdinalIgnoreCase)
                    || f.FileName.EndsWith(".patch.dat.br", StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        for (var i = 0; i < physical.Length; i++)
        {
            var f = physical[i];
            script.AppendLine($"TARGET{i}=''\nfor base in /dev/block/by-name /dev/block/bootdevice/by-name; do\n  if [ -b \"$base/{f.Partition}$SLOT\" ]; then TARGET{i}=\"$base/{f.Partition}$SLOT\"; break; fi\n  if [ -b \"$base/{f.Partition}\" ]; then TARGET{i}=\"$base/{f.Partition}\"; break; fi\ndone");
            script.AppendLine($"[ -n \"$TARGET{i}\" ] || fail 'Partition missing: {f.Partition}'\n[ \"$(stat -c %s 'images/{f.FileName}')\" -le \"$(blockdev --getsize64 \"$TARGET{i}\")\" ] || fail 'Image too large: {f.Partition}'");
        }
        for (var i = 0; i < datGroups.Length; i++)
        {
            var group = datGroups[i];
            var partition = group.Transfer.Partition;
            script.AppendLine($"DAT_TARGET{i}=''\nfor base in /dev/block/by-name /dev/block/bootdevice/by-name; do\n  if [ -b \"$base/{partition}$SLOT\" ]; then DAT_TARGET{i}=\"$base/{partition}$SLOT\"; break; fi\n  if [ -b \"$base/{partition}\" ]; then DAT_TARGET{i}=\"$base/{partition}\"; break; fi\ndone");
            script.AppendLine($"[ -n \"$DAT_TARGET{i}\" ] || fail 'Partition missing: {partition}'");
        }
        script.AppendLine("while read -r device mountpoint rest; do case \"$device\" in /dev/*) resolved=$(readlink -f \"$device\") || fail 'Cannot resolve mount';");
        for (var i = 0; i < physical.Length; i++)
            script.AppendLine($"[ \"$resolved\" != \"$(readlink -f \"$TARGET{i}\")\" ] || fail 'Target partition is mounted'");
        for (var i = 0; i < datGroups.Length; i++)
            script.AppendLine($"[ \"$resolved\" != \"$(readlink -f \"$DAT_TARGET{i}\")\" ] || fail 'Target partition is mounted'");
        script.AppendLine(";; esac; done < /proc/mounts");
        if (datGroups.Length > 0)
        {
            script.AppendLine("command -v block_image_update >/dev/null || fail 'Missing block_image_update'");
            foreach (var (group, i) in datGroups.Select((group, i) => (group, i)))
            {
                var patch = group.Patch is null ? "\"\"" : $"\"$WORK/images/{group.Patch.FileName}\"";
                script.AppendLine($"ui_print 'Updating {group.Transfer.Partition}'\nblock_image_update \"$DAT_TARGET{i}\" \"$WORK/images/{group.Transfer.FileName}\" \"$WORK/images/{group.NewData.FileName}\" {patch} || fail 'DAT update failed: {group.Transfer.Partition}'");
            }
        }
        for (var i = 0; i < physical.Length; i++)
            script.AppendLine($"ui_print 'Writing {physical[i].Partition}'\ndd if='images/{physical[i].FileName}' of=\"$TARGET{i}\" bs=1048576 conv=fsync || fail 'Write failed'");
        script.AppendLine("sync\nui_print 'Completed'");
        return script.ToString().Replace("\r\n", "\n");
    }

    /// <summary>Generates the standard Edify companion script for DAT recovery entries.</summary>
    internal static string RecoveryUpdaterScript(DeliveryPlan plan)
    {
        if (!Selected(plan).Any(f => IsDatArtifact(f.FileName)))
            return "# UotanStudio custom Recovery shell installer\n";
        var script = new StringBuilder("# UotanStudio Recovery updater-script\n");
        foreach (var group in Selected(plan).Where(f => IsDatArtifact(f.FileName))
                     .GroupBy(f => DatStem(f.FileName), StringComparer.OrdinalIgnoreCase))
        {
            var transfer = group.Single(f => f.FileName.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase));
            var data = group.Single(f => f.FileName.EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase)
                || f.FileName.EndsWith(".new.dat.br", StringComparison.OrdinalIgnoreCase));
            var patch = group.FirstOrDefault(f => f.FileName.EndsWith(".patch.dat", StringComparison.OrdinalIgnoreCase)
                || f.FileName.EndsWith(".patch.dat.br", StringComparison.OrdinalIgnoreCase));
            var patchName = patch is null ? string.Empty : patch.FileName;
            script.Append("block_image_update(\"/dev/block/bootdevice/by-name/")
                .Append(transfer.Partition)
                .Append("\", package_extract_file(\"")
                .Append(transfer.FileName)
                .Append("\"), \"")
                .Append(data.FileName)
                .Append("\", \"")
                .Append(patchName)
                .AppendLine("\") || abort(\"E2001: Failed to update image.\");");
        }
        return script.ToString();
    }
}
