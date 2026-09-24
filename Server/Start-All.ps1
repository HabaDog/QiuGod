[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$Lan,
    [switch]$GatewayOnly,
    [switch]$LobbyOnly,
    [switch]$MatchOnly,
    [switch]$GameServerOnly
)

$ErrorActionPreference = 'Stop'
$repositoryDirectory = 'C:\MyDemo'
$environmentFile = 'C:\MyDemo\.env'
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Start-ServerProcess {
    param(
        [string]$Name,
        [string]$ProjectPath,
        [string]$Args = ""
    )
    $projectDir = Split-Path -Parent $ProjectPath
    $process = Start-Process -FilePath "dotnet" -ArgumentList "run --no-restore -p:SkipProtocolGeneration=true --project `"$ProjectPath`" $Args" -PassThru -NoNewWindow -WorkingDirectory $projectDir
    Write-Host "[$Name] Started (PID: $($process.Id))"
    return $process
}

function Get-LanIpv4Address {
    $addresses = @(
        Get-NetIPAddress -AddressFamily IPv4 -Type Unicast -ErrorAction SilentlyContinue |
            Where-Object {
                $_.IPAddress -notlike '127.*' -and
                $_.IPAddress -notlike '169.254.*' -and
                $_.PrefixOrigin -ne 'WellKnown'
            }
    )

    $preferred = $addresses |
        Where-Object { $_.InterfaceAlias -notmatch 'Loopback|Virtual|VPN|Docker|WSL|Hyper-V' } |
        Select-Object -First 1
    if ($preferred) {
        return $preferred.IPAddress
    }

    $fallback = $addresses | Select-Object -First 1
    if ($fallback) {
        return $fallback.IPAddress
    }

    throw 'Could not detect a LAN IPv4 address. Check the network adapter or run Start-Server.bat without -Lan.'
}

try {
    if (-not (Test-Path -LiteralPath $environmentFile)) {
        throw "Missing $environmentFile. Please copy .env.example to .env and set passwords."
    }

    # Load .env file
    Get-Content -LiteralPath $environmentFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith('#')) {
            $parts = $line -split '=', 2
            if ($parts.Count -eq 2) {
                [Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim())
            }
        }
    }

    # Start Docker (MySQL + Redis)
    Invoke-NativeCommand 'Docker Compose startup' {
        docker compose --project-directory $repositoryDirectory --env-file $environmentFile up -d --wait --wait-timeout 90 mysql redis
    }

    $mysqlPort = [Environment]::GetEnvironmentVariable('MYSQL_PORT')
    if (-not $mysqlPort) { $mysqlPort = '3306' }
    $mysqlDb = [Environment]::GetEnvironmentVariable('MYSQL_DATABASE')
    if (-not $mysqlDb) { $mysqlDb = 'gamedb' }
    $mysqlUser = [Environment]::GetEnvironmentVariable('MYSQL_USER')
    if (-not $mysqlUser) { $mysqlUser = 'gamedemo' }
    $mysqlPwd = [Environment]::GetEnvironmentVariable('MYSQL_PASSWORD')
    if (-not $mysqlPwd) { $mysqlPwd = 'ChangeMe' }
    $redisPort = [Environment]::GetEnvironmentVariable('REDIS_PORT')
    if (-not $redisPort) { $redisPort = '6379' }

    $env:ConnectionStrings__MySql = "Server=127.0.0.1;Port=$mysqlPort;Database=$mysqlDb;Uid=$mysqlUser;Pwd=$mysqlPwd;"
    $env:ConnectionStrings__Redis = "127.0.0.1:$redisPort"

    $displayHost = '127.0.0.1'
    if ($Lan) {
        $displayHost = Get-LanIpv4Address
        # Only the client-facing Gateway and AssetBundle listeners use LAN
        # binding. Gateway's internal gRPC callback remains loopback-only.
        $env:Server__ListenAddress = '0.0.0.0'
        $env:AssetBundles__ListenHost = '0.0.0.0'
        $env:Server__GrpcListenAddress = '127.0.0.1'
        $env:Features__MatchmakingEnabled = 'false'
        $env:Features__RoomOnly = 'true'
        Write-Host "[INFO] LAN mode enabled. Host address: $displayHost"
    }
    else {
        Remove-Item Env:Server__ListenAddress -ErrorAction SilentlyContinue
        Remove-Item Env:AssetBundles__ListenHost -ErrorAction SilentlyContinue
        Remove-Item Env:Server__GrpcListenAddress -ErrorAction SilentlyContinue
        Remove-Item Env:Features__MatchmakingEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:Features__RoomOnly -ErrorAction SilentlyContinue
    }

    if (-not $SkipRestore) {
        Write-Host "Restoring projects..."
        dotnet restore "$PSScriptRoot\MyMatch\MyMatch.csproj" --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw "MyMatch restore failed" }
        dotnet restore "$PSScriptRoot\MyLobby\MyLobby.csproj" --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw "MyLobby restore failed" }
        dotnet restore "$PSScriptRoot\MyGameServer\MyGameServer.csproj" --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw "MyGameServer restore failed" }
        dotnet restore "$PSScriptRoot\MyGateway\MyGateway.csproj" --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw "MyGateway restore failed" }
        Write-Host "Restore complete."
    }

    $processes = @()

    # Start in dependency order: Match -> Lobby -> GameServer -> Gateway
    if ($MatchOnly -or (-not ($GatewayOnly -or $LobbyOnly -or $GameServerOnly))) {
        $processes += Start-ServerProcess -Name "Match" -ProjectPath (Join-Path $PSScriptRoot 'MyMatch\MyMatch.csproj')
        Start-Sleep -Seconds 2
    }

    if ($LobbyOnly -or (-not ($GatewayOnly -or $MatchOnly -or $GameServerOnly))) {
        $processes += Start-ServerProcess -Name "Lobby" -ProjectPath (Join-Path $PSScriptRoot 'MyLobby\MyLobby.csproj')
        Start-Sleep -Seconds 2
    }

    if ($GameServerOnly -or (-not ($GatewayOnly -or $LobbyOnly -or $MatchOnly))) {
        $processes += Start-ServerProcess -Name "GameServer" -ProjectPath (Join-Path $PSScriptRoot 'MyGameServer\MyGameServer.csproj')
        Start-Sleep -Seconds 2
    }

    if ($GatewayOnly -or (-not ($LobbyOnly -or $MatchOnly -or $GameServerOnly))) {
        $processes += Start-ServerProcess -Name "Gateway" -ProjectPath (Join-Path $PSScriptRoot 'MyGateway\MyGateway.csproj')
    }

    Write-Host ""
    Write-Host "=== All services started ===" -ForegroundColor Green
    Write-Host "Gateway:    tcp://$displayHost`:8888  udp://$displayHost`:8889  grpc://127.0.0.1:5000"
    if ($Lan) {
        Write-Host "Resources:  http://$displayHost`:8090/"
        Write-Host "Clients:    use MyClient.config.json with host $displayHost"
    }
    Write-Host "Lobby:      http://localhost:5001 (h2c)"
    Write-Host "Match:      http://localhost:5002 (h2c)"
    Write-Host "GameServer: http://localhost:5003 (h2c)"
    Write-Host ""
    Write-Host "Press Ctrl+C to stop all services..." -ForegroundColor Yellow

    # Wait for Ctrl+C
    while ($true) {
        Start-Sleep -Seconds 1
    }
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Write-Host ""
    Write-Host "Stopping all services..." -ForegroundColor Yellow
    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit(5000)
        }
    }
    Write-Host "All services stopped." -ForegroundColor Green
}
