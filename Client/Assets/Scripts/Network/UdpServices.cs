using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using GameNetworkCommon;

public class UdpService : MonoBehaviour
{
    [Header("UDP配置")]
    [SerializeField] private string serverAddress = "127.0.0.1";
    [SerializeField] private int serverPort = 8889;

    [Header("状态")]
    [SerializeField] private bool isInitialized = false;
    [SerializeField] private bool isConnected = false;
    [SerializeField] private bool isSessionBound = false;

    private UdpClient _udpClient;
    private Thread _receiveThread;
    private volatile bool _isReceiving = false;
    private IPEndPoint _serverEndPoint;

    public event Action<byte[]> OnDataReceived;
    public event Action<bool> OnConnectionStateChanged;

    public bool IsConnected => isConnected;
    public bool IsSessionBound => isSessionBound;

    void Start()
    {
        Initialize();
    }

    public void Initialize()
    {
        if (isInitialized) return;

        try
        {
            serverAddress = RuntimeEndpointConfig.UdpHost(serverAddress);
            serverPort = RuntimeEndpointConfig.UdpPort(serverPort);
            // 先绑定临时本地端口。Windows 上未绑定的 socket 直接 Receive 会持续返回 InvalidArgument。
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _udpClient.Client.ReceiveTimeout = 1000;
            _serverEndPoint = new IPEndPoint(IPAddress.Parse(serverAddress), serverPort);

            // 启动接收线程
            _isReceiving = true;
            _receiveThread = new Thread(new ThreadStart(ReceiveData));
            _receiveThread.IsBackground = true;
            _receiveThread.Start();

            isInitialized = true;
            SetSessionBound(false);
            SetConnected(true);

            Debug.Log($"[UdpService] 已初始化，目标服务器: {_serverEndPoint}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UdpService] 初始化失败: {e.Message}");
            SetConnected(false);
        }
    }

    private void ReceiveData()
    {
        while (_isReceiving)
        {
            try
            {
                // 阻塞接收数据
                IPEndPoint remoteEndPoint = null;
                byte[] receivedBytes = _udpClient.Receive(ref remoteEndPoint);

                // 验证数据来源是否为预期的服务器
                if (remoteEndPoint.Address.Equals(_serverEndPoint.Address) &&
                    remoteEndPoint.Port == _serverEndPoint.Port)
                {
                    // 通过主线程调度器处理数据
                    MainThreadDispatcher.Instance?.Enqueue(() => {
                        OnDataReceived?.Invoke(receivedBytes);
                    });
                }
            }
            catch (SocketException e)
            {
                if (!_isReceiving
                    || e.SocketErrorCode == SocketError.Interrupted
                    || e.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }

                if (e.SocketErrorCode != SocketError.TimedOut)
                {
                    Debug.LogWarning($"[UdpService] 接收数据时网络错误: {e.SocketErrorCode}");
                    Thread.Sleep(100);
                }
                // 超时是正常的，用于检查_isReceiving标志
            }
            catch (Exception e)
            {
                if (_isReceiving)
                {
                    Debug.LogError($"[UdpService] 接收数据时异常: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 发送数据到服务器
    /// </summary>
    public void Send(byte[] data)
    {
        if (!isConnected || _udpClient == null || _serverEndPoint == null)
        {
            Debug.LogWarning("[UdpService] 未连接，无法发送数据");
            return;
        }

        try
        {
            _udpClient.Send(data, data.Length, _serverEndPoint);
            // Debug.Log($"[UdpService] 已发送UDP数据，长度: {data.Length}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UdpService] 发送数据失败: {e.Message}");
            SetConnected(false);
        }
    }

    /// <summary>
    /// 发送BaseMessage格式的消息
    /// </summary>
    public void SendMessage(ushort protocolId, byte[] bodyData)
    {
        BaseMessage msg = new BaseMessage(protocolId, bodyData);
        byte[] dataToSend = msg.ToBytes();
        Send(dataToSend);
    }

    /// <summary>
    /// 使用登录响应中的会话令牌，将当前UDP端点绑定到TCP登录会话。
    /// </summary>
    public bool BindSession(int playerId, string sessionToken)
    {
        if (playerId <= 0 || string.IsNullOrWhiteSpace(sessionToken))
        {
            Debug.LogWarning("[UdpService] 会话信息无效，UDP将继续使用TCP降级路径");
            SetSessionBound(false);
            return false;
        }

        if (!isInitialized || !isConnected)
        {
            Initialize();
        }

        if (!isConnected)
        {
            Debug.LogWarning("[UdpService] UDP未初始化，无法绑定登录会话");
            return false;
        }

        try
        {
            var request = new UdpBindRequest
            {
                PlayerId = playerId,
                SessionToken = sessionToken
            };

            using (var stream = new MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, request);
                SetSessionBound(false);
                SendMessage(Protocols.UdpBindRequest, stream.ToArray());
            }

            Debug.Log($"[UdpService] 已发送UDP会话绑定请求，PlayerId: {playerId}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[UdpService] UDP会话绑定请求发送失败: {e.Message}");
            SetSessionBound(false);
            return false;
        }
    }

    public void SetSessionBound(bool bound)
    {
        if (isSessionBound == bound) return;

        isSessionBound = bound;
        Debug.Log($"[UdpService] UDP会话状态: {(bound ? "已绑定" : "未绑定")}");
    }

    public void ResetSessionBinding()
    {
        SetSessionBound(false);
    }

    private void SetConnected(bool connected)
    {
        if (isConnected != connected)
        {
            isConnected = connected;
            OnConnectionStateChanged?.Invoke(connected);
        }
    }

    public void Disconnect()
    {
        _isReceiving = false;
        SetSessionBound(false);

        // 先关闭Socket，以便立即中断阻塞中的Receive。
        _udpClient?.Close();

        if (_receiveThread != null && _receiveThread.IsAlive)
        {
            if (!_receiveThread.Join(1000))
            {
                Debug.LogWarning("[UdpService] 接收线程未正常终止");
            }
        }

        _udpClient = null;
        _receiveThread = null;
        isInitialized = false;
        SetConnected(false);

        Debug.Log("[UdpService] 已断开连接");
    }

    void OnDestroy()
    {
        Disconnect();
    }
}
