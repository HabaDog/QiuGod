@echo off
setlocal
dotnet publish "%~dp0HostServer\MyDemo.HostServer.csproj" -c Release -r win-x64 --self-contained false -o "%~dp0HostServer\publish"
if errorlevel 1 (
  echo HostServer build failed.
  exit /b 1
)
echo HostServer published to "%~dp0HostServer\publish"
