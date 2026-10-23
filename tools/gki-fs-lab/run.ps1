param(
    [string[]]$Kernels = @('5.10', '6.6', '6.18'),
    [string[]]$Formats = @('EXT4', 'EROFS', 'F2FS'),
    [ValidateSet('All', 'Kernel', 'Fixtures', 'Managed', 'Qemu')]
    [string]$Stage = 'All',
    [ValidateSet('auto', 'kvm', 'tcg')]
    [string]$Accel = 'auto',
    [switch]$ReuseKernels,
    [switch]$KeepWork,
    [switch]$KeepImages,
    [switch]$PruneCache,
    [switch]$SkipImageBuild,
    [string]$RunId = '',
    [int]$Jobs = [Environment]::ProcessorCount
)

$ErrorActionPreference = 'Stop'
$Image = 'uotan-gki-fs-lab:latest'
$Volume = 'uotan-gki-fs-lab'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$RepoWsl = $RepoRoot -replace '\\', '/'
if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
}
$Artifacts = Join-Path $RepoRoot "artifacts/gki-fs-lab/$RunId"
$ArtifactsWsl = "/repo/artifacts/gki-fs-lab/$RunId"
$KernelList = $Kernels -join ','
$FormatList = $Formats -join ','

foreach ($path in @('logs', 'kernels', 'reports', 'manifests', 'images/original')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Artifacts $path) | Out-Null
}

function Invoke-Wslc {
    param([string[]]$Arguments)
    & wslc @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "wslc failed with exit code $LASTEXITCODE"
    }
}

function Invoke-Lab {
    param([string[]]$Arguments)
    $run = @(
        'run', '--rm',
        '-v', "${RepoWsl}:/repo",
        '-v', "${Volume}:/lab",
        '-w', '/repo',
        '--entrypoint', '/bin/bash',
        $Image
    ) + $Arguments
    Invoke-Wslc -Arguments $run
}

if (-not $SkipImageBuild) {
    Write-Host "[image] building $Image"
    Invoke-Wslc -Arguments @(
        'build', '-t', $Image,
        '-f', (Join-Path $PSScriptRoot 'lab/Dockerfile'),
        $PSScriptRoot
    )
}

& wslc volume create $Volume 2>$null | Out-Null

$runKernel = $Stage -in @('All', 'Kernel')
$runFixtures = $Stage -in @('All', 'Fixtures')
$runManaged = $Stage -in @('All', 'Managed')
$runQemu = $Stage -in @('All', 'Qemu')

if ($runKernel) {
    $arguments = @(
        '/repo/tools/gki-fs-lab/lab/build-kernels.sh',
        '--run-dir', $ArtifactsWsl,
        '--kernels', $KernelList,
        '--jobs', $Jobs.ToString()
    )
    if ($ReuseKernels) { $arguments += '--reuse' }
    Invoke-Lab -Arguments $arguments
}

if ($runFixtures) {
    Invoke-Lab -Arguments @(
        '/repo/tools/gki-fs-lab/lab/make-fixtures.sh',
        '--run-dir', $ArtifactsWsl,
        '--formats', $FormatList
    )
}

if ($runManaged) {
    $driver = Join-Path $Artifacts 'driver'
    Write-Host '[managed] publishing self-contained linux-musl driver'
    & dotnet publish (Join-Path $RepoRoot 'UotanStudio.FilesystemFixture/UotanStudio.FilesystemFixture.csproj') `
        -c Release -r linux-musl-x64 --self-contained true -p:PublishSingleFile=false `
        -o $driver --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish for linux-musl failed'
    }
    $driverBinary = Join-Path $driver 'UotanStudio.FilesystemFixture'
    if (-not (Test-Path $driverBinary)) {
        throw "published driver not found: $driverBinary"
    }

    Invoke-Lab -Arguments @(
        '-c',
        "rm -rf /lab/bin/driver && mkdir -p /lab/bin/driver && cp -a '$ArtifactsWsl/driver/.' /lab/bin/driver/ && chmod +x /lab/bin/driver/UotanStudio.FilesystemFixture"
    )
    Invoke-Lab -Arguments @(
        '/repo/tools/gki-fs-lab/lab/run-managed.sh',
        '--driver', '/lab/bin/driver/UotanStudio.FilesystemFixture',
        '--run-dir', $ArtifactsWsl,
        '--formats', $FormatList
    )
}

if ($runQemu) {
    Invoke-Lab -Arguments @(
        '/repo/tools/gki-fs-lab/lab/run-matrix.sh',
        '--run-dir', $ArtifactsWsl,
        '--formats', $FormatList,
        '--kernels', $KernelList,
        '--accel', $Accel
    )
}

$cases = @()
$failed = 0
if ($runQemu) {
    foreach ($kernel in $Kernels) {
        $log = Join-Path $Artifacts "logs/qemu-$kernel.log"
        $seen = @{}
        if (Test-Path $log) {
            foreach ($match in Select-String -Path $log -Pattern '^RESULT\s+(\S+)\s+(PASS|FAIL)$') {
                $seen[$match.Matches[0].Groups[1].Value] = $match.Matches[0].Groups[2].Value
            }
        }
        foreach ($format in $Formats) {
            $name = $format.ToLowerInvariant()
            $result = if ($seen.ContainsKey($name)) { $seen[$name] } else { 'MISSING' }
            if ($result -ne 'PASS') { $failed++ }
            $cases += [ordered]@{ kernel = $kernel; format = $format; result = $result }
        }
    }
}

$managed = @()
if ($runManaged) {
    foreach ($format in $Formats) {
        $path = Join-Path $Artifacts "reports/managed-$($format.ToLowerInvariant()).json"
        if (Test-Path $path) {
            $json = Get-Content $path -Raw | ConvertFrom-Json
            $managed += [ordered]@{
                format = $format
                preserved = $json.preserved
                manifestEqual = $json.manifestEqual
                entryCount = $json.entryCount
            }
            if (-not $json.preserved -or -not $json.manifestEqual) { $failed++ }
        } else {
            $managed += [ordered]@{ format = $format; preserved = $false; manifestEqual = $false; entryCount = 0 }
            $failed++
        }
    }
}

$acceleration = $Accel
$accelerationFile = Join-Path $Artifacts 'logs/acceleration.txt'
if (Test-Path $accelerationFile) {
    $acceleration = (Get-Content $accelerationFile -Raw).Trim()
}

$status = if ($failed -eq 0 -and $runQemu) { 'pass' } elseif ($failed -eq 0) { 'partial' } else { 'fail' }
$report = [ordered]@{
    runId = $RunId
    status = $status
    stage = $Stage
    kernels = $Kernels
    formats = $Formats
    acceleration = $acceleration
    managed = $managed
    cases = $cases
    artifacts = $Artifacts
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Artifacts 'report.json') -Encoding utf8

$lines = @(
    '# GKI 文件系统往返报告',
    '',
    "- 运行：``$RunId``",
    "- 状态：``$status``",
    "- 加速：``$acceleration``",
    "- 产物：``$Artifacts``",
    '',
    '| 内核 | 格式 | 结果 |',
    '| --- | --- | --- |'
)
foreach ($case in $cases) {
    $lines += "| $($case.kernel) | $($case.format) | $($case.result) |"
}
$lines += ''
$lines += '## 托管往返'
$lines += ''
$lines += '| 格式 | preserved | manifestEqual | entries |'
$lines += '| --- | --- | --- | --- |'
foreach ($item in $managed) {
    $lines += "| $($item.format) | $($item.preserved) | $($item.manifestEqual) | $($item.entryCount) |"
}
$lines | Set-Content (Join-Path $Artifacts 'report.md') -Encoding utf8

if (-not $KeepWork -and -not $KeepImages -and $Stage -eq 'All' -and $failed -eq 0) {
    Invoke-Lab -Arguments @('-c', 'rm -rf /lab/work/fixtures/* /lab/work/managed/* /lab/work/matrix/* /lab/cases/*')
    Remove-Item -LiteralPath (Join-Path $Artifacts 'images') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $Artifacts 'driver') -Recurse -Force -ErrorAction SilentlyContinue
}
if ($PruneCache) {
    & wslc volume remove $Volume 2>$null | Out-Null
}

Write-Host "[report] $status -> $(Join-Path $Artifacts 'report.md')"
if ($failed -ne 0) { exit 1 }
