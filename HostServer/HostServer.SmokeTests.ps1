param([int]$TcpPort = 8888)
$client = [System.Net.Sockets.TcpClient]::new('127.0.0.1', $TcpPort)
$stream = $client.GetStream(); $reader = [System.IO.StreamReader]::new($stream); $writer = [System.IO.StreamWriter]::new($stream); $writer.AutoFlush = $true
function Send($obj) { $writer.WriteLine(($obj | ConvertTo-Json -Compress)); ($reader.ReadLine() | ConvertFrom-Json) }
$join = Send @{ command='JOIN'; roomId='demo'; password='demo'; playerId='smoke-host'; name='SmokeHost' }
if (-not $join.success) { throw "JOIN failed: $($join.message)" }
$state = Send @{ command='STATE'; roomId='demo'; password='demo' }
if ($state.players.Count -ne 1) { throw "STATE expected one player" }
$start = Send @{ command='START'; roomId='demo'; password='demo'; playerId='smoke-host' }
if (-not $start.success -or $start.players.Count -ne 4) { throw "START expected AI fill to four players" }
$bad = Send @{ command='PING'; roomId='demo'; password='wrong' }
if ($bad.success) { throw 'Wrong password unexpectedly succeeded' }
Write-Host 'HostServer smoke test passed.'
$client.Dispose()
