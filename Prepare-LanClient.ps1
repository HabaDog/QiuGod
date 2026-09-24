[CmdletBinding()]
param(
    [string]$ServerHost,
    [string]$OutputRoot,
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot 'Delivery'
}
$sourceRoot = Join-Path $PSScriptRoot 'ClientBuild'

function Get-DetectedLanIpv4 {
    $matches = [regex]::Matches((ipconfig | Out-String), '(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])')
    foreach ($match in $matches) {
        $candidate = $match.Value
        $address = $null
        if ([System.Net.IPAddress]::TryParse($candidate, [ref]$address) -and
            $address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork -and
            -not $candidate.StartsWith('127.') -and
            -not $candidate.StartsWith('169.254.')) {
            return $candidate
        }
    }

    throw 'Could not detect a LAN IPv4 address. Run with -ServerHost <host IPv4 or LAN DNS name>.'
}

if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'MyClient.exe'))) {
    throw "Client build was not found at $sourceRoot. Build the Windows client first."
}

if ([string]::IsNullOrWhiteSpace($ServerHost)) {
    $ServerHost = Get-DetectedLanIpv4
    Write-Host "[INFO] Detected LAN server address: $ServerHost"
}

$safeHost = $ServerHost -replace '[^a-zA-Z0-9._-]', '_'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$deliveryDirectory = Join-Path $OutputRoot "MyDemo-LAN-$safeHost-$timestamp"
New-Item -ItemType Directory -Path $deliveryDirectory | Out-Null

Get-ChildItem -LiteralPath $sourceRoot -Force | ForEach-Object {
    if ($_.Name -in @('Logs', 'MyClient.log', 'MyClient_BackUpThisFolder_ButDontShipItWithYourGame', 'MyClient_BurstDebugInformation_DoNotShip')) {
        return
    }

    Copy-Item -LiteralPath $_.FullName -Destination $deliveryDirectory -Recurse -Force
}

$config = [ordered]@{
    tcpHost = $ServerHost
    tcpPort = 8888
    udpHost = $ServerHost
    udpPort = 8889
    resourceServerUrl = "http://$ServerHost`:8090/"
    matchmakingEnabled = $false
}
$configPath = Join-Path $deliveryDirectory 'MyClient.config.json'
$config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8

Write-Host "[OK] LAN client delivery created: $deliveryDirectory"
Write-Host "[OK] Endpoint config: $configPath"
if (-not $NoArchive) {
    $archivePath = "$deliveryDirectory.zip"
    Compress-Archive -LiteralPath $deliveryDirectory -DestinationPath $archivePath -CompressionLevel Optimal
    Write-Host "[OK] Shareable ZIP created: $archivePath"
}
Write-Host '[NEXT] Run Enable-LanFirewall.ps1 as Administrator once, start Start-Server.bat, then share this entire folder.'
