[CmdletBinding()]
param(
    [string]$PanelAddress,

    [string]$Secret,

    [string]$ReleaseBaseUrl = "https://github.com/WayneDuan/RelayForge-Release/releases/latest/download",

    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ServiceName = "RelayForgeAgent"
$InstallDir = Join-Path $env:ProgramData "RelayForge\Agent"
$Executable = Join-Path $InstallDir "relayforge-agent.exe"
$ConfigFile = Join-Path $InstallDir "config.json"
$GostConfigFile = Join-Path $InstallDir "gost.json"

$ReleaseBaseUrl = $ReleaseBaseUrl.TrimEnd('/')

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function Remove-Agent {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force
        }
        & sc.exe delete $ServiceName | Out-Null
    }
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
}

Assert-Administrator

if ($Uninstall) {
    Remove-Agent
    Write-Host "RelayForge Agent removed."
    exit 0
}

$releaseUri = $null
if (-not [Uri]::TryCreate($ReleaseBaseUrl, [UriKind]::Absolute, [ref]$releaseUri) -or $releaseUri.Scheme -ne "https") {
    throw "ReleaseBaseUrl must be an HTTPS URL."
}
$ChecksumsUrl = "$ReleaseBaseUrl/checksums.txt"
$ManifestUrl = "$ReleaseBaseUrl/agent-manifest.json"

function Get-AssetChecksum {
    param(
        [string]$ChecksumText,
        [string]$AssetName
    )

    $escapedAssetName = [regex]::Escape($AssetName)
    foreach ($line in ($ChecksumText -split "`r?`n")) {
        if ($line -match "^\s*([A-Fa-f0-9]{64})\s+\*?(?:\.\/)?$escapedAssetName\s*$") {
            return $Matches[1].ToUpperInvariant()
        }
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($PanelAddress) -or [string]::IsNullOrWhiteSpace($Secret)) {
    throw "PanelAddress and Secret are required unless -Uninstall is used."
}
$PanelAddress = $PanelAddress.Trim()
$Secret = $Secret.Trim()
if ([string]::IsNullOrWhiteSpace($PanelAddress) -or [string]::IsNullOrWhiteSpace($Secret)) {
    throw "PanelAddress and Secret cannot be blank."
}

if ([Environment]::Is64BitOperatingSystem -eq $false) {
    throw "Only 64-bit Windows is supported."
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$temporary = "$Executable.download.$PID"
Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri "$($ReleaseBaseUrl.TrimEnd('/'))/gost-windows-amd64.exe" -OutFile $temporary
if ((Get-Item -LiteralPath $temporary).Length -eq 0) {
    Remove-Item -LiteralPath $temporary -Force
    throw "The Agent download is empty."
}
$checksumText = (Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri $ChecksumsUrl).Content
$expectedHash = Get-AssetChecksum -ChecksumText $checksumText -AssetName "gost-windows-amd64.exe"
if (-not $expectedHash) {
    # Older releases may have checksums.txt generated before the Windows asset
    # was added. The agent manifest still carries the same binary hash.
    try {
        $manifest = ((Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri $ManifestUrl).Content | ConvertFrom-Json)
        $manifestHash = [string]$manifest.assets.'windows-amd64'.sha256
        if ($manifestHash -match "^[A-Fa-f0-9]{64}$") {
            $expectedHash = $manifestHash.ToUpperInvariant()
        }
    } catch {
        $expectedHash = $null
    }
}
if (-not $expectedHash) {
    Remove-Item -LiteralPath $temporary -Force
    throw "The release does not contain a valid SHA-256 for gost-windows-amd64.exe in checksums.txt or agent-manifest.json."
}
$actualHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $temporary -Force
    throw "The Agent checksum does not match the published release checksum."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceWasRunning = $service -and $service.Status -ne "Stopped"
$serviceCreated = $false
$previousExecutable = "$Executable.previous"
$previousConfig = "$ConfigFile.previous"
$hadExecutable = Test-Path -LiteralPath $Executable
$hadConfig = Test-Path -LiteralPath $ConfigFile
$config = @{ addr = $PanelAddress; secret = $Secret; http = 0; tls = 0; socks = 0; autoUpdate = $true; updateManifestUrl = $ManifestUrl }

try {
    if ($serviceWasRunning) {
        Stop-Service -Name $ServiceName -Force
    }
    if ($hadExecutable) {
        Remove-Item -LiteralPath $previousExecutable -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $Executable -Destination $previousExecutable -Force
    }
    if ($hadConfig) {
        Copy-Item -LiteralPath $ConfigFile -Destination $previousConfig -Force
    }
    Move-Item -LiteralPath $temporary -Destination $Executable -Force
    $config | ConvertTo-Json -Compress | Set-Content -LiteralPath $ConfigFile -Encoding Ascii
    if (-not (Test-Path -LiteralPath $GostConfigFile)) {
        "{}" | Set-Content -LiteralPath $GostConfigFile -Encoding Ascii
    }

    if (-not $service) {
        $serviceCommand = '"{0}" -C "{1}"' -f $Executable, $GostConfigFile
        New-Service -Name $ServiceName -BinaryPathName $serviceCommand -DisplayName "RelayForge Agent" -Description "RelayForge node Agent" -StartupType Automatic
        $serviceCreated = $true
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000 | Out-Null
    }

    Start-Service -Name $ServiceName
    Remove-Item -LiteralPath $previousExecutable, $previousConfig -Force -ErrorAction SilentlyContinue
    Write-Host "RelayForge Agent is running."
}
catch {
    Remove-Item -LiteralPath $temporary, $Executable -Force -ErrorAction SilentlyContinue
    if ($hadExecutable -and (Test-Path -LiteralPath $previousExecutable)) {
        Move-Item -LiteralPath $previousExecutable -Destination $Executable -Force
    }
    if ($hadConfig -and (Test-Path -LiteralPath $previousConfig)) {
        Move-Item -LiteralPath $previousConfig -Destination $ConfigFile -Force
    } elseif (-not $hadConfig) {
        Remove-Item -LiteralPath $ConfigFile -Force -ErrorAction SilentlyContinue
    }
    if ($serviceCreated) {
        & sc.exe delete $ServiceName | Out-Null
    }
    if ($serviceWasRunning) {
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }
    throw
}
