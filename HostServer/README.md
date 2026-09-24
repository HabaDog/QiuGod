# MyDemo HostServer

This is the standalone LAN room host used by the multiplayer create-room flow.
It intentionally has no account, database, Redis, or Docker dependency.

## Build and run

```powershell
dotnet build .\HostServer\MyDemo.HostServer.csproj
dotnet run --project .\HostServer\MyDemo.HostServer.csproj -- --room-id demo --password demo --allow-ai-fill true --tcp-port 18888 --udp-port 18889
```

The default release ports are TCP `8888` and UDP `8889`. Use alternate ports for local tests when an older server is already running.

## Smoke tests

With the server running on the alternate ports above, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\HostServer\HostServer.SmokeTests.ps1 -TcpPort 18888
powershell -ExecutionPolicy Bypass -File .\HostServer\HostServer.DiscoveryTest.ps1 -UdpPort 18889 -RoomId demo
```

The tests cover room join/state, password rejection, and UDP room discovery. The current Unity client launcher can start this process; the client-side join transport is still the next integration step.
