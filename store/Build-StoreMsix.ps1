[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.7.0',
    [string]$IdentityName = 'Hoakim.AutoMarkerReID',
    [string]$Publisher = 'CN=06970FBE-6DFA-4FD9-BB5F-DCC0D8D933FB',
    [string]$PublisherDisplayName = 'Hoakim',
    [switch]$TestIdentity
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\AutoMarkerReID.App\AutoMarkerReID.App.csproj'
$manifestTemplate = Join-Path $PSScriptRoot 'Package.appxmanifest.template'
$iconPath = Join-Path $repoRoot 'src\AutoMarkerReID.App\Assets\app.ico'
$outputDirectory = Join-Path $repoRoot 'artifacts\store'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("AutoMarkerReID-store-" + [Guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $workRoot 'Package.appxmanifest'
$assetsPath = Join-Path $workRoot 'Assets'

if ($TestIdentity) {
    $IdentityName = 'Hoakim.AutoMarkerReID.Test'
    $Publisher = 'CN=Hoakim Test'
    $PublisherDisplayName = 'Hoakim Test'
}
elseif (-not $IdentityName -or -not $Publisher) {
    throw 'Reserve the app in Partner Center, then pass -IdentityName and -Publisher exactly as shown in Product identity.'
}

function ConvertTo-XmlText([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function New-Logo([string]$Path, [int]$Width, [int]$Height, [double]$Scale = 0.72) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $icon = [Drawing.Icon]::new($iconPath, 256, 256)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $side = [int]([Math]::Min($Width, $Height) * $Scale)
        $left = [int](($Width - $side) / 2)
        $top = [int](($Height - $side) / 2)
        $graphics.DrawIcon($icon, [Drawing.Rectangle]::new($left, $top, $side, $side))
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $icon.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-Item -ItemType Directory -Path $workRoot, $assetsPath, $outputDirectory -Force | Out-Null
try {
    Add-Type -AssemblyName System.Drawing
    New-Logo (Join-Path $assetsPath 'StoreLogo.png') 50 50 0.82
    New-Logo (Join-Path $assetsPath 'Square44x44Logo.png') 44 44 0.82
    New-Logo (Join-Path $assetsPath 'Square150x150Logo.png') 150 150
    New-Logo (Join-Path $assetsPath 'Square310x310Logo.png') 310 310
    New-Logo (Join-Path $assetsPath 'Wide310x150Logo.png') 310 150 0.72

    $manifest = [IO.File]::ReadAllText($manifestTemplate)
    $manifest = $manifest.Replace('{{IDENTITY_NAME}}', (ConvertTo-XmlText $IdentityName))
    $manifest = $manifest.Replace('{{PUBLISHER}}', (ConvertTo-XmlText $Publisher))
    $manifest = $manifest.Replace('{{PUBLISHER_DISPLAY_NAME}}', (ConvertTo-XmlText $PublisherDisplayName))
    $manifest = $manifest.Replace('{{VERSION}}', $Version)
    [IO.File]::WriteAllText($manifestPath, $manifest, [Text.UTF8Encoding]::new($false))

    $semanticVersion = ($Version -split '\.')[0..2] -join '.'
    $arguments = @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '-p:Platform=x64',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:StoreBuild=true',
        "-p:StoreManifestPath=$manifestPath",
        "-p:StoreAssetsPath=$assetsPath",
        "-p:AppxPackageDir=$outputDirectory\",
        "-p:Version=$semanticVersion",
        "-p:FileVersion=$Version",
        "-p:AssemblyVersion=$Version"
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Store MSIX build failed.' }

    $packages = Get-ChildItem -LiteralPath $outputDirectory -File -Recurse |
        Where-Object Extension -In @('.msix', '.msixupload') |
        Sort-Object LastWriteTime -Descending
    if (-not $packages) { throw 'Build completed without an MSIX or MSIXUPLOAD artifact.' }

    foreach ($package in $packages | Select-Object -First 2) {
        $hash = Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
        Write-Host "Store artifact: $($package.FullName)"
        Write-Host "Size: $([Math]::Round($package.Length / 1MB, 2)) MB"
        Write-Host "SHA256: $($hash.Hash)"
    }
    if ($TestIdentity) {
        Write-Warning 'This package uses a test identity. Rebuild with the exact Identity name and Publisher from Partner Center before submission.'
    }
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot).TrimEnd('\') + '\'
    if ($resolvedWorkRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
