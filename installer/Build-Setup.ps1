[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.15',
    [string]$CertificateThumbprint,
    [string]$PfxPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\AutoMarkerReID.App\AutoMarkerReID.App.csproj'
$publishDir = Join-Path ([IO.Path]::GetTempPath()) ("AutoMarkerReID-setup-" + [Guid]::NewGuid().ToString('N'))
$setupDir = Join-Path $repoRoot 'artifacts\setup'
$iconPath = Join-Path $repoRoot 'src\AutoMarkerReID.App\Assets\app.ico'
$issPath = Join-Path $PSScriptRoot 'AutoMarkerReID.iss'

function Find-SignTool {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($candidate) {
        return $candidate.FullName
    }

    throw 'signtool.exe was not found in the Windows SDK.'
}

function Invoke-CodeSign([string[]]$Files) {
    if (-not $CertificateThumbprint -and -not $PfxPath) {
        return
    }

    $signTool = Find-SignTool
    foreach ($file in $Files) {
        $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl, '/d', 'AutoMarker Re-ID')
        if ($CertificateThumbprint) {
            $arguments += @('/sha1', $CertificateThumbprint, '/s', 'My')
        }
        else {
            $resolvedPfx = (Resolve-Path -LiteralPath $PfxPath).Path
            $arguments += @('/f', $resolvedPfx)
            if ($env:AUTOMARKER_SIGN_PFX_PASSWORD) {
                $arguments += @('/p', $env:AUTOMARKER_SIGN_PFX_PASSWORD)
            }
        }
        $arguments += $file
        & $signTool @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Code signing failed: $file"
        }
        & $signTool verify /pa /q $file
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed: $file"
        }
    }
}

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $locations = @(
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($location in $locations) {
        if (Test-Path -LiteralPath $location) {
            return $location
        }
    }

    throw 'Inno Setup 6 is missing. Install it with: winget install --id JRSoftware.InnoSetup --exact'
}

New-Item -ItemType Directory -Path $publishDir, $setupDir -Force | Out-Null

try {
    dotnet publish $projectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:SatelliteResourceLanguages=en `
        -p:Version=$Version `
        -p:FileVersion="$Version.0" `
        -p:AssemblyVersion="$Version.0" `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    $ownBinaries = Get-ChildItem -LiteralPath $publishDir -File |
        Where-Object { $_.Name -eq 'AutoMarkerReID.App.exe' -or $_.Name -like 'AutoMarkerReID.*.dll' } |
        Select-Object -ExpandProperty FullName
    Invoke-CodeSign $ownBinaries

    $iscc = Find-InnoCompiler
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$setupDir" "/DSetupIcon=$iconPath" $issPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Installer compilation failed.'
    }

    $setupPath = Join-Path $setupDir "AutoMarkerReID-Setup-$Version-win-x64.exe"
    Invoke-CodeSign @($setupPath)

    $hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
    $sizeMb = [Math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
    Write-Host "Installer: $setupPath"
    Write-Host "Size: $sizeMb MB"
    Write-Host "SHA256: $($hash.Hash)"
    if (-not $CertificateThumbprint -and -not $PfxPath) {
        Write-Warning 'The installer is not Authenticode-signed. Use CertificateThumbprint or PfxPath for a public release.'
    }
}
finally {
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
}
