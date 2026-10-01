using System.Globalization;
using System.Text;
using UotanStudio.Firmware.OtaDelta.Dat;

namespace UotanStudio.Services.Delivery;

internal static partial class DeliveryPackageBuilder
{
    /// <summary><para>判断计划是否需要 DAT Recovery 包（纯 Recovery、非 Payload/Super，且选中了 DAT 工件）。</para>
    /// Determines whether the plan needs a DAT Recovery package (pure Recovery, not Payload/Super, with a selected DAT artifact).</summary>
    internal static bool HasDat(DeliveryPlan plan) => plan.Recovery && !plan.PackPayload && !plan.PackSuper
        && Selected(plan).Any(f => IsDatArtifact(f.FileName));

    private sealed record DatSet(DeliveryFile Transfer, DeliveryFile Data, DeliveryFile? Patch)
    {
        public string DataName => Data.FileName.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
            ? Data.FileName[..^3] + ".br" : Data.FileName;
        public string PatchName => Patch is null ? DatStem(Data.FileName) + ".patch.dat"
            : Patch.FileName.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ? Patch.FileName[..^3] : Patch.FileName;
    }

    private static DatSet[] DatSets(DeliveryPlan plan) => Selected(plan).Where(f => IsDatArtifact(f.FileName))
        .GroupBy(f => DatStem(f.FileName), StringComparer.OrdinalIgnoreCase)
        .Select(g => new DatSet(
            g.Single(f => DatFileSet.IsTransferList(f.FileName)),
            g.Single(f => DatFileSet.IsNewData(f.FileName)),
            g.SingleOrDefault(f => DatFileSet.IsPatchData(f.FileName)))).ToArray();

    private sealed record RecoveryImage(DeliveryFile File, string Member, DatSet? Dat = null);

    private static RecoveryImage[] RecoveryImages(DeliveryPlan plan)
    {
        var sets = DatSets(plan).ToDictionary(d => DatStem(d.Data.FileName), StringComparer.OrdinalIgnoreCase);
        var result = new List<RecoveryImage>();
        foreach (var file in Selected(plan))
        {
            if (file.Partition == "super_empty" || file.Partition == "payload") continue;
            if (IsDatArtifact(file.FileName))
            {
                if (sets.Remove(DatStem(file.FileName), out var set))
                    result.Add(new(set.Data, "images/" + set.DataName, set));
                continue;
            }
            var image = file.Generated && file.Partition == "super"
                ? file with { FileName = DeliveryOutputService.SuperFileName(plan.Super) } : file;
            result.Add(new(image, plan.PackSuper ? DeliveryOutputService.OrganizedImageRelativePath(image).Replace('\\', '/')
                : "images/" + image.FileName));
        }
        return [.. result];
    }

    /// <summary>Generates a POSIX shell installer; ZIP data stays in streams and native Edify handles DAT.</summary>
    internal static string RecoveryScript(DeliveryPlan plan, Func<string, string>? localize = null)
    {
        var images = RecoveryImages(plan);
        var compressed = images.Any(i => i.Member.EndsWith(".zst", StringComparison.OrdinalIgnoreCase));
        var script = new StringBuilder("""
            #!/sbin/sh
            set -eu
            [ "$#" -ge 3 ] || exit 1
            case "$1" in 1|2|3) ;; *) exit 1 ;; esac
            case "$2" in ''|*[!0-9]*) exit 1 ;; esac
            OUTFD=/proc/self/fd/$2
            ZIPFILE="$3"
            case "$ZIPFILE" in /*) ;; *) ZIPFILE="$PWD/$ZIPFILE" ;; esac
            ui_print() { printf 'ui_print %s\nui_print\n' "$*" >>"$OUTFD"; }
            fail() { ui_print "$*"; exit 1; }
            set_progress() { printf 'set_progress %s\n' "$1" >>"$OUTFD"; }
            """);
        script.Append('\n');
        foreach (var line in DeliveryScriptGenerator.BannerLines(plan, localize))
            script.AppendLine("ui_print " + DeliveryScriptGenerator.QuoteShell(line));
        foreach (var line in DeliveryScriptGenerator.PrerequisiteLines(localize, true))
            script.AppendLine("ui_print " + DeliveryScriptGenerator.QuoteShell(line));
        script.AppendLine("""
            for tool in unzip sha256sum blockdev dd wc getprop readlink mkfifo mktemp awk rm cat chmod sync; do
              command -v "$tool" >/dev/null || fail "Missing $tool"
            done
            WORK=$(mktemp -d /tmp/uotan.XXXXXX) || fail 'Cannot create temporary directory'
            trap 'rm -rf "$WORK"' 0
            trap 'exit 1' 1 2 15
            set_progress 0
            """);
        if (plan.DeviceCodename.Length > 0)
        {
            script.AppendLine("MATCHED=no\nfor prop in ro.product.device ro.build.product ro.product.vendor.device ro.vendor.product.device; do");
            script.AppendLine($"  [ \"$(getprop \"$prop\")\" != '{plan.DeviceCodename}' ] || MATCHED=yes\ndone");
            script.AppendLine($"[ \"$MATCHED\" = yes ] || fail 'Device mismatch: expected {plan.DeviceCodename}'");
        }
        script.AppendLine(plan.Slot.Length > 0 ? $"SLOT='_{plan.Slot}'" : "SLOT=$(getprop ro.boot.slot_suffix)\nif [ -z \"$SLOT\" ]; then SLOT=$(getprop ro.boot.slot); [ -z \"$SLOT\" ] || SLOT=\"_$SLOT\"; fi");
        script.AppendLine("case \"$SLOT\" in ''|_a|_b) ;; *) fail 'Invalid slot' ;; esac");
        script.AppendLine(StreamFunctions);
        script.AppendLine("unzip -p \"$ZIPFILE\" checksums.sha256 >\"$WORK/checksums.sha256\" || fail 'Missing checksums'");
        foreach (var image in images)
        {
            script.AppendLine($"verify_member '{image.Member}'");
            if (image.Dat is { } set)
                script.AppendLine($"verify_member 'images/{set.Transfer.FileName}'\nverify_member 'images/{set.PatchName}'");
        }
        if (compressed)
        {
            script.AppendLine("case \"$(uname -m)\" in aarch64|arm64) ZSTD_MEMBER=bin/android/zstd ;; x86_64|amd64) ZSTD_MEMBER=bin/android/zstd-x86_64 ;; loongarch64) ZSTD_MEMBER=bin/android/zstd-loong64 ;; *) fail 'Unsupported Recovery CPU architecture' ;; esac");
            script.AppendLine("verify_member \"$ZSTD_MEMBER\"");
            script.AppendLine("unzip -p \"$ZIPFILE\" \"$ZSTD_MEMBER\" >\"$WORK/zstd\" || fail 'Cannot extract zstd'\nchmod 755 \"$WORK/zstd\"\nZSTD=\"$WORK/zstd\"");
        }
        script.AppendLine("set_progress 0.2");
        for (var i = 0; i < images.Length; i++)
        {
            var image = images[i];
            script.AppendLine($"TARGET{i}=$(resolve_target '{image.File.Partition}') || fail 'Partition missing: {image.File.Partition}'");
            script.AppendLine($"REAL{i}=$(readlink -f \"$TARGET{i}\") || fail 'Cannot resolve target'");
            for (var j = 0; j < i; j++)
                script.AppendLine($"[ \"$REAL{i}\" != \"$REAL{j}\" ] || fail 'Duplicate target partition'");
            if (image.Dat is null)
            {
                script.AppendLine($"SIZE=$(stream_image '{image.Member}' wc -c) || fail 'Cannot read image: {image.File.FileName}'");
                script.AppendLine($"CAPACITY=$(blockdev --getsize64 \"$TARGET{i}\") || fail 'Cannot read partition size'\n[ \"$SIZE\" -le \"$CAPACITY\" ] || fail 'Image too large: {image.File.Partition}'");
            }
            script.AppendLine($"printf 'target{i}=%s\\n' \"$TARGET{i}\" >>\"$WORK/targets.prop\"");
        }
        script.AppendLine("while read -r device mountpoint rest; do case \"$device\" in /dev/*) resolved=$(readlink -f \"$device\") || fail 'Cannot resolve mount';");
        for (var i = 0; i < images.Length; i++)
            script.AppendLine($"[ \"$resolved\" != \"$REAL{i}\" ] || fail 'Target partition is mounted'");
        script.AppendLine(";; esac; done < /proc/mounts\nset_progress 0.3");
        for (var i = 0; i < images.Length; i++)
        {
            var image = images[i];
            script.AppendLine($"ui_print 'Writing {image.File.Partition}'\nstream_image '{image.Member}' dd of=\"$TARGET{i}\" bs=1048576 conv=fsync || fail 'Write failed: {image.File.Partition}'");
            script.AppendLine("set_progress " + (0.3 + 0.7 * (i + 1) / images.Length).ToString("0.######", CultureInfo.InvariantCulture));
        }
        script.AppendLine("sync || fail 'Sync failed'\nset_progress 1\nui_print 'Completed'");
        return script.ToString().ReplaceLineEndings("\n");
    }

    private const string StreamFunctions = """
        package_extract_file() (
          member=$1; shift
          fifo="$WORK/member.pipe"
          mkfifo "$fifo" || return 1
          unzip -p "$ZIPFILE" "$member" >"$fifo" & producer=$!
          trap 'kill "$producer" 2>/dev/null || :; wait "$producer" 2>/dev/null || :; rm -f "$fifo"' 0
          if "$@" <"$fifo"; then reader_status=0; else reader_status=$?; kill "$producer" 2>/dev/null || :; fi
          if wait "$producer"; then producer_status=0; else producer_status=$?; fi
          trap - 0
          rm -f "$fifo"
          [ "$reader_status" -eq 0 ] && [ "$producer_status" -eq 0 ]
        )
        decode_image() (
          fifo="$WORK/decoded.pipe"
          mkfifo "$fifo" || return 1
          exec 3<&0
          "$ZSTD" -d -c <&3 >"$fifo" & producer=$!
          trap 'kill "$producer" 2>/dev/null || :; wait "$producer" 2>/dev/null || :; rm -f "$fifo"' 0
          if "$@" <"$fifo"; then reader_status=0; else reader_status=$?; kill "$producer" 2>/dev/null || :; fi
          if wait "$producer"; then producer_status=0; else producer_status=$?; fi
          trap - 0
          rm -f "$fifo"
          [ "$reader_status" -eq 0 ] && [ "$producer_status" -eq 0 ]
        )
        stream_image() {
          case "$1" in
            *.zst) member=$1; shift; package_extract_file "$member" decode_image "$@" ;;
            *) package_extract_file "$@" ;;
          esac
        }
        verify_member() {
          expected=$(awk -v name="$1" '$2 == name {print $1}' "$WORK/checksums.sha256")
          [ "${#expected}" -eq 64 ] || fail "Missing or duplicate checksum: $1"
          actual=$(package_extract_file "$1" sha256sum) || fail "Cannot read package member: $1"
          [ "${actual%% *}" = "$expected" ] || fail "Checksum mismatch: $1"
        }
        resolve_target() {
          partition=$1
          case "$partition" in *_a|*_b|super) suffix='' ;; *) suffix=$SLOT ;; esac
          for base in /dev/block/by-name /dev/block/bootdevice/by-name; do
            if [ -n "$suffix" ] && [ -b "$base/$partition$suffix" ]; then printf '%s\n' "$base/$partition$suffix"; return; fi
            if [ -b "$base/$partition" ]; then printf '%s\n' "$base/$partition"; return; fi
          done
          return 1
        }
        """;

    /// <summary>
    /// <para>生成使用真实 ZIP 成员名与解析后分区路径的原生 Edify 脚本：A/B 分区按
    /// getprop("ro.boot.slot_suffix") 拼接，显式 _a/_b 分区不加后缀，DAT 分区用 block_image_update 写镜像。</para>
    /// Emits native Edify with actual ZIP member names and resolved partition paths: A/B partitions append
    /// getprop("ro.boot.slot_suffix"), explicit _a/_b partitions take none, and DAT partitions are written via block_image_update.
    /// </summary>
    internal static string RecoveryUpdaterScript(DeliveryPlan plan, Func<string, string>? localize = null)
    {
        if (!HasDat(plan)) return "# UotanStudio custom Recovery shell installer\n";
        var images = RecoveryImages(plan);
        var script = new StringBuilder("# Executed by the native updater at META-INF/com/google/android/update-binary.\n");
        foreach (var line in DeliveryScriptGenerator.BannerLines(plan, localize))
            script.AppendLine("ui_print(" + DeliveryScriptGenerator.QuoteEdify(line) + ");");
        foreach (var line in DeliveryScriptGenerator.PrerequisiteLines(localize, true))
            script.AppendLine("ui_print(" + DeliveryScriptGenerator.QuoteEdify(line) + ");");
        string Target(int i)
        {
            var partition = images[i].File.Partition;
            var explicitSlot = partition.EndsWith("_a", StringComparison.OrdinalIgnoreCase)
                || partition.EndsWith("_b", StringComparison.OrdinalIgnoreCase);
            return explicitSlot
                ? $"\"/dev/block/bootdevice/by-name/{partition}\""
                : $"\"/dev/block/bootdevice/by-name/{partition}\" + getprop(\"ro.boot.slot_suffix\")";
        }
        string Args(int i, DatSet set) => $"{Target(i)}, package_extract_file(\"images/{set.Transfer.FileName}\"), \"images/{set.DataName}\", \"images/{set.PatchName}\"";
        for (var i = 0; i < images.Length; i++)
            if (images[i].Dat is { } set)
                script.AppendLine($"block_image_verify({Args(i, set)}) || abort(\"E2000: Failed to verify {set.Data.Partition} image.\");");
        script.AppendLine("show_progress(0.7, 0);");
        for (var i = 0; i < images.Length; i++)
        {
            var image = images[i];
            script.AppendLine($"ui_print(\"Writing {image.File.Partition}\");");
            script.AppendLine(image.Dat is { } set
                ? $"block_image_update({Args(i, set)}) || abort(\"E2001: Failed to update {image.File.Partition} image.\");"
                : $"package_extract_file(\"{image.Member}\", {Target(i)}) || abort(\"E2002: Failed to write {image.File.Partition} image.\");");
            script.AppendLine("set_progress(" + ((double)(i + 1) / images.Length).ToString("0.######", CultureInfo.InvariantCulture) + ");");
        }
        return script.ToString().ReplaceLineEndings("\n");
    }
}