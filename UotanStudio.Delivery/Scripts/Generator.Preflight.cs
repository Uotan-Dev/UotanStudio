namespace UotanStudio.Services.Delivery;

internal static partial class DeliveryScriptGenerator
{
    private static bool NeedsFastbootd(DeliveryFile file) => file.Fastbootd || file.IncludeInSuper;

    /// <summary><para>组织式 Super 交付在刷 super 前比对设备读数与烘焙进 LP 几何的大小：不一致直接中止，
    /// 因为几何大于实际分区会让 liblp 拒绝加载元数据，小于实际分区会静默留下未分配尾部并破坏后续快照分配。
    /// 设备不实现该变量时如实打印未校验提示并继续，不谎报已校验。</para>
    /// Emits the Super size preflight used before flashing super: the device reading must equal the size baked into the
    /// LP geometry, because a geometry larger than the real partition makes liblp refuse the metadata while a smaller
    /// one silently leaves unallocated tail space and breaks later snapshot allocation. When the device does not
    /// implement the variable the fragment prints an honest unverified notice and continues instead of claiming a check.</summary>
    private static string ShellSuperSizeGuard(DeliveryPlan plan, Func<string, string>? localize) => string.Concat(
        "    declared=$(\"$TOOLS/fastboot\" getvar super-partition-size 2>&1 | tr -d '\\r' | awk -F': *' '/super-partition-size/ {value=$2+0; if (value>0) {print value; exit}}')\n",
        $"    if [ -n \"$declared\" ] && [ \"$declared\" != {plan.Super.DeviceSize} ]; then die {QuoteShellVariable(T(localize, "SuperSizeMismatch", "The Super image was built for a {0}-byte super but the device reports {1} bytes; rebuild the delivery with the device size.", plan.Super.DeviceSize, "@@DECLARED@@"))}; fi\n",
        $"    [ -n \"$declared\" ] || printf '%s\\n' {Sh(T(localize, "SuperSizeUnknown", "Device does not report super-partition-size; the Super size was not verified."))} >&2\n");

    /// <summary>
    /// Double-quotes a shell message and restores only <c>@@DECLARED@@</c> as the expansion of the device reading; every
    /// other <c>$</c>, backtick, backslash and double quote stays escaped, so literal characters in localized text are
    /// never reinterpreted by the shell.
    /// <para>双引号化一段 shell 文案，并只把 <c>@@DECLARED@@</c> 还原成设备读数的变量展开；其余 <c>$</c>、反引号、
    /// 反斜杠与双引号全部转义，因此本地化文案里的字面量不会被 shell 二次解释。</para>
    /// </summary>
    /// <param name="value">The raw message text. <para>要输出的原始文案。</para></param>
    /// <returns>The double-quoted text that expands the reading. <para>可展开设备读数的双引号文本。</para></returns>
    private static string QuoteShellVariable(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("`", "\\`").Replace("$", "\\$").Replace("@@DECLARED@@", "$declared") + "\"";

    /// <summary><para>批处理版的 Super 容量预检子过程：与 Shell 版同一规则，读数缺失时打印未校验提示后继续。</para>
    /// The batch equivalent of the Super size preflight: the same rule as the Shell guard, continuing with an honest
    /// unverified notice when the device does not report the variable.</summary>
    private static string BatchSuperSizeGuard(DeliveryPlan plan) => $"""
        :ensure_super_size
        "%TOOLS%\fastboot.exe" getvar super-partition-size >"%WORK%\super.txt" 2>&1
        findstr /r /c:"super-partition-size: *[0-9]" "%WORK%\super.txt" >"%WORK%\supervar.txt"
        if errorlevel 1 goto super_size_unknown
        set "SUPERSIZE="
        for /f "usebackq tokens=1,* delims=:" %%K in ("%WORK%\supervar.txt") do set "SUPERSIZE=%%L"
        if defined SUPERSIZE set "SUPERSIZE=%SUPERSIZE: =%"
        if not defined SUPERSIZE goto super_size_unknown
        if "%SUPERSIZE%"=="{plan.Super.DeviceSize}" exit /b 0
        echo The Super image was built for a {plan.Super.DeviceSize}-byte super but the device reports %SUPERSIZE% bytes; rebuild the delivery with the device size.
        exit /b 1
        :super_size_unknown
        echo Device does not report super-partition-size; the Super size was not verified.
        exit /b 0

        """;

    private static string ShellIntegrity(string platform, DeliveryPlan plan, Func<string, string>? localize) => "cd -- \"$ROOT\"\n[ -f checksums.sha256 ] || exit 1\n"
        + (platform == "macOS" ? "shasum -a 256 -c checksums.sha256" : "sha256sum --strict -c checksums.sha256")
        + $" || {{ printf '%s\\n' {Sh(T(localize, "ChecksumFailed", "Checksum verification failed. Nothing was flashed."))} >&2; exit 1; }}\n"
        + string.Concat(plan.Files.Select(f => $"grep -Eq {Sh("^[0-9a-f]{64}  " + ImagePath(plan, f).Replace(".", @"\.") + "$")} checksums.sha256 || exit 1\n"));

    private static string BatchIntegrity(DeliveryPlan plan) => "powershell.exe -NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; try { $root=$env:ROOT; $lines=Get-Content -LiteralPath (Join-Path $root 'checksums.sha256') -Encoding UTF8; if (!$lines) {throw 'Empty checksums'}; "
        + string.Concat(plan.Files.Select(f => $"if (!($lines -match '^[0-9a-f]{{64}}  {ImagePath(plan, f).Replace(".", @"\.")}$')) {{throw 'Missing image checksum'}}; "))
        + "foreach ($line in $lines) {if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {throw 'Invalid checksums'}; $expected=$matches[1]; $path=Join-Path $root $matches[2]; if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) {throw 'Checksum mismatch'}}; exit 0 } catch {Write-Error $_; exit 1}\"\nif errorlevel 1 goto failed";

    private static string ShellMode(Func<string, string>? localize) => """
        SERIAL=$("$TOOLS/fastboot" devices | awk 'NF {print $1}')
        ensure_mode() {
          local expected="$1" state serial attempts=0
          serial=$("$TOOLS/fastboot" devices | awk 'NF {print $1}')
          [ "$serial" = "$SERIAL" ] || die @@DEVICE_CHANGED@@
          state=$("$TOOLS/fastboot" getvar is-userspace 2>&1) || die @@MODE_UNKNOWN@@
          if printf '%s\n' "$state" | tr -d '\r' | grep -Eq "^(\(bootloader\) )?is-userspace: *$expected$"; then return; fi
          if [ "$expected" = yes ]; then
            "$TOOLS/fastboot" reboot fastboot || die @@ENTER_FASTBOOTD@@
          else
            "$TOOLS/fastboot" reboot bootloader || die @@ENTER_BOOTLOADER@@
          fi
          while [ "$attempts" -lt 60 ]; do
            sleep 1; attempts=$((attempts + 1))
            serial=$("$TOOLS/fastboot" devices | awk 'NF {print $1}')
            [ "$serial" = "$SERIAL" ] || continue
            state=$("$TOOLS/fastboot" getvar is-userspace 2>&1) || continue
            if printf '%s\n' "$state" | tr -d '\r' | grep -Eq "^(\(bootloader\) )?is-userspace: *$expected$"; then return; fi
          done
          die @@MODE_TIMEOUT@@
        }
        """
        .Replace("@@DEVICE_CHANGED@@", Sh(T(localize, "DeviceChanged", "Device changed.")))
        .Replace("@@MODE_UNKNOWN@@", Sh(T(localize, "CannotDetermineMode", "Cannot determine Fastboot mode.")))
        .Replace("@@ENTER_FASTBOOTD@@", Sh(T(localize, "CannotEnterFastbootd", "Cannot enter fastbootd.")))
        .Replace("@@ENTER_BOOTLOADER@@", Sh(T(localize, "CannotEnterBootloader", "Cannot enter bootloader.")))
        .Replace("@@MODE_TIMEOUT@@", Sh(T(localize, "ModeTransitionTimeout", "Fastboot mode transition timed out.")));

    private static string BatchMode() => """
        :ensure_mode
        "%TOOLS%\fastboot.exe" devices >"%WORK%\current.txt" 2>&1
        if errorlevel 1 exit /b 1
        fc /b "%WORK%\devices.txt" "%WORK%\current.txt" >nul
        if errorlevel 1 exit /b 1
        "%TOOLS%\fastboot.exe" getvar is-userspace >"%WORK%\mode.txt" 2>&1
        if errorlevel 1 exit /b 1
        findstr /r /x /c:"is-userspace: *%~1" /c:"(bootloader) is-userspace: *%~1" "%WORK%\mode.txt" >nul
        if not errorlevel 1 exit /b 0
        if "%~1"=="yes" ("%TOOLS%\fastboot.exe" reboot fastboot) else ("%TOOLS%\fastboot.exe" reboot bootloader)
        if errorlevel 1 exit /b 1
        set ATTEMPTS=0
        :mode_wait
        timeout /t 1 /nobreak >nul
        set /a ATTEMPTS+=1 >nul
        if %ATTEMPTS% GEQ 60 exit /b 1
        "%TOOLS%\fastboot.exe" devices >"%WORK%\current.txt" 2>&1
        fc /b "%WORK%\devices.txt" "%WORK%\current.txt" >nul
        if errorlevel 1 goto mode_wait
        "%TOOLS%\fastboot.exe" getvar is-userspace >"%WORK%\mode.txt" 2>&1
        if errorlevel 1 goto mode_wait
        findstr /r /x /c:"is-userspace: *%~1" /c:"(bootloader) is-userspace: *%~1" "%WORK%\mode.txt" >nul
        if errorlevel 1 goto mode_wait
        exit /b 0
        """;
}