using System.Net.Sockets;
using System.Net;

namespace MyGateway;

/// <summary>
/// KCP 可靠传输通道占位符。
/// 完整实现需要引入 kcp-csharp NuGet 包 (https://github.com/limpo1989/kcp-csharp)。
/// 当前使用 TCP 作为可靠通道的替代方案。KCP 通道将在后续迭代中启用。
/// </summary>
public sealed class KcpChannel : IDisposable
{
    public bool IsConnected { get; private set; }

    public event Action<byte[]>? OnDataReceived;

    /// <summary>
    /// 创建 KCP 通道占位实例
    /// </summary>
    public KcpChannel(UdpClient udpClient, IPEndPoint remoteEndPoint)
    {
        // TODO: 集成 kcp-csharp 库后实现
        // 当前阶段，关键事件（炸弹、技能、冲刺）仍走 TCP 协议保证可靠性
        Console.WriteLine($"[KCP] KCP 通道已创建 (占位)，远程端点: {remoteEndPoint}");
    }

    /// <summary>
    /// 通过 KCP 发送数据（占位）
    /// </summary>
    public void Send(byte[] data)
    {
        // TODO: 通过 KCP 发送
    }

    /// <summary>
    /// 更新 KCP 状态机（占位）
    /// </summary>
    public void Update(uint currentMs)
    {
        // TODO: KCP Update
    }

    /// <summary>
    /// 释放 KCP 通道资源
    /// </summary>
    public void Dispose()
    {
        IsConnected = false;
    }
}