param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'Update-Branding.ps1')
    & (Join-Path $PSScriptRoot 'Build-Shell.ps1')
    if (-not $SkipTests) {
        & (Join-Path $PSScriptRoot 'Test-Shell.ps1')
        dotnet run --project tests/FileShift.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    $payload = Join-Path $repo 'artifacts/payload'
    $release = Join-Path $repo 'artifacts/release'
    New-Item -ItemType Directory -Path $payload,$release -Force | Out-Null
    dotnet publish src/FileShift.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    $archive = Join-Path $repo 'artifacts/payload.zip'
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    dotnet publish src/FileShift.Setup -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
    Get-FileHash -Algorithm SHA256 (Join-Path $release 'FileShiftSetup.exe') | Format-List
    Write-Host "Installer: $release\FileShiftSetup.exe"
} finally { Pop-Location }
