[CmdletBinding()]
param(
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$repositoryDirectory = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
$environmentFile = Join-Path $repositoryDirectory '.env'

# Keep build artifacts tied to the interactive Windows user rather than an
# editor or automation-specific NuGet cache path.
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

try {
    if (-not (Test-Path -LiteralPath $environmentFile)) {
        throw "Missing $environmentFile. Copy .env.example to .env and set the passwords first."
    }

    Get-Content -LiteralPath $environmentFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith('#')) {
            $parts = $line -split '=', 2
            if ($parts.Count -eq 2) {
                Set-Item -Path "Env:$($parts[0].Trim())" -Value $parts[1].Trim()
            }
        }
    }

    Invoke-NativeCommand 'Docker Compose startup' {
        docker compose --project-directory $repositoryDirectory --env-file $environmentFile up -d --wait --wait-timeout 90 mysql redis
    }

    $env:ConnectionStrings__MySql = "Server=127.0.0.1;Port=$env:MYSQL_PORT;Database=$env:MYSQL_DATABASE;Uid=$env:MYSQL_USER;Pwd=$env:MYSQL_PASSWORD;"
    $env:ConnectionStrings__Redis = "127.0.0.1:$env:REDIS_PORT"

    Push-Location $projectDirectory
    if (-not $SkipRestore) {
        Invoke-NativeCommand '.NET restore' {
            dotnet restore --ignore-failed-sources
        }
    }

    Invoke-NativeCommand 'Game server process' {
        dotnet run --no-restore -p:SkipProtocolGeneration=true
    }
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ((Get-Location).Path -eq $projectDirectory) {
        Pop-Location
    }
}
