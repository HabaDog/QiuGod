param([int]$UdpPort = 18889, [string]$RoomId = 'demo')
$udp = [System.Net.Sockets.UdpClient]::new()
$udp.Client.ReceiveTimeout = 3000
$bytes = [Text.Encoding]::UTF8.GetBytes("MYDEMO_DISCOVER $RoomId")
[void]$udp.Send($bytes, $bytes.Length, '127.0.0.1', $UdpPort)
$endpoint = [System.Net.IPEndPoint]::new([IPAddress]::Any, 0)
try {
  $reply = $udp.Receive([ref]$endpoint)
  $text = [Text.Encoding]::UTF8.GetString($reply)
  $data = $text | ConvertFrom-Json
  if ($data.type -ne 'HOST_ROOM' -or $data.roomId -ne $RoomId) { throw "Unexpected discovery response: $text" }
  Write-Host "UDP discovery passed: $text"
} finally { $udp.Dispose() }
