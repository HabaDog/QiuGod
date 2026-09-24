[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window: right-click PowerShell and choose Run as administrator.'
}

$rules = @(
    @{ Name = 'MyDemo LAN TCP 8888'; Protocol = 'TCP'; Port = 8888 },
    @{ Name = 'MyDemo LAN UDP 8889'; Protocol = 'UDP'; Port = 8889 },
    @{ Name = 'MyDemo LAN Asset HTTP 8090'; Protocol = 'TCP'; Port = 8090 }
)

foreach ($rule in $rules) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
            -Protocol $rule.Protocol -LocalPort $rule.Port `
            -Profile Private,Public | Out-Null
        Write-Host "[OK] Created firewall rule: $($rule.Name)"
    }
    else {
        Set-NetFirewallRule -DisplayName $rule.Name -Enabled True -Profile Private,Public | Out-Null
        Get-NetFirewallRule -DisplayName $rule.Name |
            Get-NetFirewallAddressFilter |
            Set-NetFirewallAddressFilter -RemoteAddress Any | Out-Null
        Write-Host "[OK] Updated firewall rule: $($rule.Name)"
    }
}

# HttpListener uses HTTP.sys. A wildcard listener needs a URL reservation for
# the normal Windows user running Start-Server.bat.
$url = 'http://+:8090/'
$urlAcl = netsh http show urlacl url=$url 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $urlAcl -notmatch [regex]::Escape($url)) {
    netsh http add urlacl url=$url user="$env:USERDOMAIN\$env:USERNAME"
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not reserve the AssetBundle HTTP URL.'
    }
    Write-Host "[OK] Reserved $url for $env:USERDOMAIN\$env:USERNAME"
}
else {
    Write-Host "[INFO] HTTP URL reservation already exists: $url"
}

Write-Host '[OK] LAN host networking is ready. MySQL and Redis remain private to this computer.'
