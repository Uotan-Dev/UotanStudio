param(
    [Parameter(Mandatory = $true)]
    [string]$RunId
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$RunDir = Join-Path $RepoRoot "artifacts/gki-fs-lab/$RunId"
$Kernels = @('5.10', '6.6', '6.18')
$Formats = @('EXT4', 'EROFS', 'F2FS')
$cases = @()
$failed = 0

foreach ($kernel in $Kernels) {
    $log = Join-Path $RunDir "logs/qemu-$kernel.log"
    $seen = @{}
    if (Test-Path $log) {
        foreach ($match in Select-String -Path $log -Pattern '^RESULT\s+(\S+)\s+(PASS|FAIL)$') {
            $seen[$match.Matches[0].Groups[1].Value] = $match.Matches[0].Groups[2].Value
        }
    }

    $metaPath = Join-Path $RepoRoot "artifacts/gki-fs-lab/kernel-$kernel/kernels/$kernel-meta.json"
    $meta = if (Test-Path $metaPath) { Get-Content $metaPath -Raw | ConvertFrom-Json } else { $null }
    foreach ($format in $Formats) {
        $result = if ($seen.ContainsKey($format.ToLowerInvariant())) { $seen[$format.ToLowerInvariant()] } else { 'MISSING' }
        if ($result -ne 'PASS') { $failed++ }
        $cases += [ordered]@{
            kernel = $kernel
            kernelRelease = if ($meta) { $meta.release } else { '' }
            format = $format
            result = $result
        }
    }
}

$managed = @()
foreach ($format in $Formats) {
    $path = Join-Path $RunDir "reports/managed-$($format.ToLowerInvariant()).json"
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

$acceleration = 'unknown'
$accelPath = Join-Path $RunDir 'logs/acceleration.txt'
if (Test-Path $accelPath) { $acceleration = (Get-Content $accelPath -Raw).Trim() }
$status = if ($failed -eq 0) { 'pass' } else { 'fail' }
$report = [ordered]@{
    runId = $RunId
    status = $status
    acceleration = $acceleration
    caseCount = $cases.Count
    cases = $cases
    managed = $managed
    note = 'EXT4 write probe image is grown to 128 MiB with resize2fs before boot to provide free space; EROFS remains read-only and its rebuilt image can exceed the source image size by layout metadata.'
    artifacts = $RunDir
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $RunDir 'report.json') -Encoding utf8

$lines = @(
    '# GKI 文件系统往返报告',
    '',
    "- 运行：``$RunId``",
    "- 状态：``$status``",
    "- 加速：``$acceleration``",
    "- 用例：$($cases.Count)",
    '',
    '| 内核 | 格式 | 结果 |',
    '| --- | --- | --- |'
)
foreach ($case in $cases) {
    $lines += "| $($case.kernel) ($($case.kernelRelease)) | $($case.format) | $($case.result) |"
}
$lines += ''
$lines += '| 格式 | preserved | manifestEqual | entries |'
$lines += '| --- | --- | --- | --- |'
foreach ($item in $managed) {
    $lines += "| $($item.format) | $($item.preserved) | $($item.manifestEqual) | $($item.entryCount) |"
}
$lines += ''
$lines += 'EXT4 写入探针前把重建镜像扩到 128 MiB 以提供写余量；EROFS 保持只读。'
$lines | Set-Content (Join-Path $RunDir 'report.md') -Encoding utf8

Write-Host "[report] $status -> $(Join-Path $RunDir 'report.md')"
if ($failed -ne 0) { exit 1 }
