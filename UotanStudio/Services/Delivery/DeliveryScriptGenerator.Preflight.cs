namespace UotanStudio.Services.Delivery;

internal static partial class DeliveryScriptGenerator
{
    private static bool NeedsFastbootd(DeliveryFile file) => file.Fastbootd || file.IncludeInSuper;

    private static string ShellIntegrity(string platform, DeliveryPlan plan, Func<string, string>? localize) => "cd -- \"$ROOT\"\n[ -f checksums.sha256 ] || exit 1\n"
        + (platform == "macOS" ? "shasum -a 256 -c checksums.sha256" : "sha256sum --strict -c checksums.sha256")
        + $" || {{ printf '%s\\n' {Sh(T(localize, "ChecksumFailed", "Checksum verification failed. Nothing was flashed."))} >&2; exit 1; }}\n"
        + string.Concat(plan.Files.Select(f => $"grep -Eq {Sh("^[0-9a-f]{64}  " + DeliveryScriptGenerator.ImagePath(plan, f).Replace(".", @"\.") + "$")} checksums.sha256 || exit 1\n"));

    private static string BatchIntegrity(DeliveryPlan plan) => "powershell.exe -NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; try { $root=$env:ROOT; $lines=Get-Content -LiteralPath (Join-Path $root 'checksums.sha256') -Encoding UTF8; if (!$lines) {throw 'Empty checksums'}; "
        + string.Concat(plan.Files.Select(f => $"if (!($lines -match '^[0-9a-f]{{64}}  {DeliveryScriptGenerator.ImagePath(plan, f).Replace(".", @"\.")}$')) {{throw 'Missing image checksum'}}; "))
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
