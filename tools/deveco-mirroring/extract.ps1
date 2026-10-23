[CmdletBinding()]
param(
    [string]$DevEcoRoot = 'C:\Program Files\Huawei\DevEco Studio',
    [string]$Destination = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $PSScriptRoot '..\..\UotanStudio.Mirroring\Assets\ohos\device'
}

$jar = Join-Path $DevEcoRoot 'plugins\screen-mirroring\lib\hosScrcpy-1.0.20-beta.jar'
if (-not (Test-Path -LiteralPath $jar -PathType Leaf)) {
    throw "The DevEco screen-mirroring JAR was not found at '$jar'. Install DevEco Studio or pass -DevEcoRoot."
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath $destinationPath -PathType Container)) {
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($jar)
try {
    $entries = @($archive.Entries | Where-Object {
        $_.FullName -match '^libscrcpy/[^/]+\.so$' -or $_.FullName -match '^uitest_agent[^/]*\.so$'
    })
    if ($entries.Count -ne 15) {
        throw "Expected 15 device runtime entries in '$jar', found $($entries.Count)."
    }

    foreach ($entry in $entries) {
        $name = [System.IO.Path]::GetFileName($entry.FullName)
        $target = Join-Path $destinationPath $name
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        $length = (Get-Item -LiteralPath $target).Length
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
        Write-Output ("{0,10} {1} {2}" -f $length, $hash, $name)
    }

    $jarHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $jar).Hash.ToLowerInvariant()
    Write-Output "JAR SHA-256: $jarHash"
    Write-Output "Extracted $($entries.Count) files to $destinationPath"
}
finally {
    $archive.Dispose()
}
