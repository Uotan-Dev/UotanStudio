$ErrorActionPreference = 'Stop'

$androidSdk = 'D:\CodeEnv\Android\Sdk'
$javaSdk = 'D:\CodeEnv\Jdks\Zulu21.48+17-CA'

foreach ($path in @($androidSdk, $javaSdk)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required directory does not exist: $path"
    }
}

foreach ($executable in @(
        (Join-Path $javaSdk 'bin\java.exe'),
        (Join-Path $javaSdk 'bin\javac.exe'))) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Required JDK executable does not exist: $executable"
    }
}

$variables = @{
    ANDROID_HOME = $androidSdk
    ANDROID_SDK_ROOT = $androidSdk
    JAVA_HOME = $javaSdk
}

foreach ($entry in $variables.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'User')
    Set-Item -Path "Env:$($entry.Key)" -Value $entry.Value
}

Write-Host "Configured user environment variables:"
foreach ($entry in $variables.GetEnumerator()) {
    Write-Host "  $($entry.Key)=$($entry.Value)"
}

& (Join-Path $javaSdk 'bin\java.exe') -version
