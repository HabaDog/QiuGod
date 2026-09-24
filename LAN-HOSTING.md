# MyDemo 局域网联机

本机作为主机时，先以管理员身份运行一次：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Enable-LanFirewall.ps1
```

它在 Windows Private/Public 网络配置文件放行 TCP 8888、UDP 8889、TCP 8090，但远端仅限本地子网，并为 AssetBundle HTTP 服务注册 `http://+:8090/`。MySQL 和 Redis 保持仅本机访问。

然后双击 `Build-LanClientPackage.bat` 生成 ZIP，并使用局域网模式启动服务器。它会自动检测主机 IP：

```powershell
.\Build-LanClientPackage.bat
.\Start-Server.bat -Lan
```

自动检测异常或需要指定 IP 时，使用 `Build-LanClientPackage.bat 192.168.94.122`。每次主机 IP 发生变化时，重新生成一次客户端 ZIP，或只修改交付目录中的 `MyClient.config.json`。

脚本同时生成同名 `.zip`，将该 ZIP 发给同一局域网内的玩家即可。玩家解压后运行其中的 `MyClient.exe`，不要只复制 EXE。

服务器启动日志应显示 TCP/UDP 监听 `0.0.0.0:8888` / `0.0.0.0:8889`，并显示 AssetBundle HTTP 服务 `http://0.0.0.0:8090/`。Gateway 的内部 gRPC 回调仍应显示为 `127.0.0.1:5000`，不对局域网开放。测试时可先让另一台电脑浏览 `http://192.168.94.122:8090/art_background`；能下载或显示二进制内容后再启动客户端。

此配置只适用于同一局域网，不包含路由器端口映射或公网暴露。
