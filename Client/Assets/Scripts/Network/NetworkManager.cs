using UnityEngine;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetworkCommon;
using UnityEngine.SceneManagement;

public class NetworkManager : MonoBehaviour
{
    private const int MaxPacketBodyBytes = 64 * 1024;

    // === 单例模式实现 ===
    public static NetworkManager Instance { get; private set; }

private void Awake()
    {
        Debug.Log("[NetworkManager] ========== Awake 开始 ==========");
        serverHost = RuntimeEndpointConfig.TcpHost(serverHost);
        serverPort = RuntimeEndpointConfig.TcpPort(serverPort);

        if (Instance == null)
        {
            Debug.Log("[NetworkManager] 创建单例...");
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeNetworkManager();
            Debug.Log("[NetworkManager] 单例创建完成");
        }
        else if (!ReferenceEquals(Instance, this))
        {
            // A scene copy must never be able to run its OnDestroy cleanup
            // against the persistent transport instance.
            Debug.LogWarning("[NetworkManager] 单例已存在，销毁重复对象");
            Destroy(gameObject);
            return;
        }

        Debug.Log("[NetworkManager] ========== Awake 结束 ==========");
    }

    private void InitializeNetworkManager()
    {
        Debug.Log("[NetworkManager] ========== 开始初始化 ==========");
        
        // 确保PlayerDataManager存在
        EnsurePlayerDataManagerExists();
        RegisterBuiltInProtocolHandlers();
        
        Debug.Log("[NetworkManager] ========== 初始化完成 ==========");
    }
    
    /// <summary>
    /// 确保PlayerDataManager存在
    /// </summary>
    private void EnsurePlayerDataManagerExists()
    {
        try
        {
            var playerDataManager = PlayerDataManager.Instance;
            if (playerDataManager == null)
            {
                GameObject playerDataGameObject = new GameObject("PlayerDataManager");
                playerDataGameObject.AddComponent<PlayerDataManager>();
                Debug.Log("[NetworkManager] 确保PlayerDataManager存在");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"确保PlayerDataManager存在时发生异常: {ex.Message}");
        }
    }

    private TcpClient _client;
    private NetworkStream _stream;
    private Thread _receiveThread;

    // 线程安全的连接状态管理
    private readonly object _connectionLock = new object();
    private readonly object _sendLock = new object();
    private bool _isConnected = false;
    private int _connectionGeneration = 0;

    // 线程安全的消息队列
    private readonly Queue<BaseMessage> _messageQueue = new Queue<BaseMessage>();
    private readonly object _queueLock = new object();
    private readonly MessageFrameDecoder _frameDecoder = new MessageFrameDecoder(MaxPacketBodyBytes);
    private readonly Dictionary<ushort, Action<BaseMessage>> _protocolHandlers = new Dictionary<ushort, Action<BaseMessage>>();

    // 线程安全的事件声明
    private event Action _onConnectionProblemEvent;
    public event Action OnConnectionProblemEvent
    {
        add { lock (_connectionLock) { _onConnectionProblemEvent += value; } }
        remove { lock (_connectionLock) { _onConnectionProblemEvent -= value; } }
    }
    
    // 登录结果事件
    public event System.Action<bool, string> OnLoginResult;
    
    // 注册结果事件
    public event System.Action<bool, string> OnRegisterResult;
    
    // 登出结果事件
    public event System.Action<bool, string> OnLogoutResult;
    
    // 进入房间结果事件
    public event System.Action<bool, string, List<RoomPlayerInfo>> OnEnterRoomResult;
    
    // 准备状态切换结果事件
    public event System.Action<bool, string> OnToggleReadyResult;
    
    // 开始游戏结果事件
    public event System.Action<bool, string> OnStartGameResult;
    
    // 房间状态更新事件
    public event System.Action<List<RoomPlayerInfo>, bool> OnRoomStateUpdate;

    // 房间玩法和 GamePlayThree 小球选择结果
    public event System.Action<bool, string, string> OnGameModeSelectionResult;
    public event System.Action<bool, string, string> OnGameThreeBallSelectionResult;
    public event System.Action<System.Collections.Generic.IReadOnlyDictionary<int, string>> OnGameThreeBallSelectionStateChanged;
    
    // 游戏开始通知事件（携带SlotIndex和服务端选择的场景）
    public event System.Action<int, string, string> OnGameStartNotification;

    // 存储当前登录请求，以便在响应时获取用户名
    private LoginRequest currentLoginRequest;
    private int authenticatedPlayerId;
    private string authenticatedUsername = string.Empty;
    private string sessionToken = string.Empty;
    private string lastSessionResumeFailure = string.Empty;
    private string currentRoomGameMode = "game_three";
    private List<RoomPlayerInfo> latestRoomPlayers = new List<RoomPlayerInfo>();
    private bool latestRoomCanStart;
    private bool hasLatestRoomState;
    private readonly Dictionary<int, string> gameThreeBallSelections = new Dictionary<int, string>();
    private Action<bool> resumeSessionCallback;
    private bool awaitingRecoverySnapshot;
    private ushort pendingRecoveryStateProtocol;
    private byte[] pendingRecoveryStateSync;
    // A match can begin delivering its pre-round state while the gameplay
    // scene is still loading. Keep only that setup snapshot for the bridge.
    private byte[] pendingGameThreePreRoundStateSync;

    public bool HasResumableSession => authenticatedPlayerId > 0 && !string.IsNullOrWhiteSpace(sessionToken);
    public bool IsLoginGameRecoveryPending { get; private set; }
    public bool IsLoginRoomRecoveryPending { get; private set; }
    public string LoginDestinationSceneName { get; private set; } = string.Empty;
    public string LastSessionResumeFailure => lastSessionResumeFailure;
    public System.Collections.Generic.IReadOnlyDictionary<int, string> GameThreeBallSelections => gameThreeBallSelections;
    public string CurrentRoomGameMode => currentRoomGameMode;

    public bool TryGetLatestRoomState(out List<RoomPlayerInfo> players, out bool canStart)
    {
        players = hasLatestRoomState
            ? new List<RoomPlayerInfo>(latestRoomPlayers)
            : new List<RoomPlayerInfo>();
        canStart = latestRoomCanStart;
        return hasLatestRoomState;
    }
    
    /// <summary>
    /// 获取当前登录请求
    /// </summary>
    /// <returns>LoginRequest对象</returns>
    private LoginRequest GetCurrentLoginRequest()
    {
        return currentLoginRequest;
    }
    
    /// <summary>
    /// 设置当前登录请求
    /// </summary>
    /// <param name="loginRequest">登录请求对象</param>
    public void SetCurrentLoginRequest(LoginRequest loginRequest)
    {
        currentLoginRequest = loginRequest;
    }
    
    [Header("组件依赖")]
    [SerializeField] private HeartbeatManager heartbeatManager;

    [Header("服务器配置")]
    [SerializeField] private string serverHost = "127.0.0.1";
    [SerializeField] private int serverPort = 8888;

    [Header("UDP设置")]
    [SerializeField] private bool enableUdp = true;
    [SerializeField] private UdpService udpService;

    [Header("协议选择")]
    [SerializeField] private bool heartbeatViaUdp = true;
    [SerializeField] private bool playerMoveViaUdp = true;

    [Header("连接验证设置")]
    [SerializeField] private int maxConnectionAttempts = 3;
    [SerializeField] private float connectionRetryDelay = 2f;
    [SerializeField] private float connectionTimeout = 10f;

    // 连接验证相关字段
    private int currentConnectionAttempt = 0;
    private bool isAttemptingConnection = false;
    private float connectionStartTime;
    private Coroutine connectionCoroutine;

    public bool IsUdpEnabled => enableUdp
        && udpService != null
        && udpService.IsConnected
        && udpService.IsSessionBound;

    /// <summary>
    /// 检查当前连接状态
    /// </summary>
    public bool IsConnected
    {
        get
        {
            lock (_connectionLock)
            {
                return _isConnected && _client != null && _client.Connected;
            }
        }
    }

    /// <summary>
    /// 尝试连接服务器（带重试机制）
    /// </summary>
    public void AttemptServerConnection(Action<bool> onConnectionResult)
    {
        if (isAttemptingConnection)
        {
            Debug.LogWarning("[NetworkManager] 已在尝试连接中，忽略重复请求");
            return;
        }

        if (connectionCoroutine != null)
        {
            StopCoroutine(connectionCoroutine);
        }

        connectionCoroutine = StartCoroutine(ConnectionAttemptRoutine(onConnectionResult));
    }

    /// <summary>
    /// 连接尝试协程
    /// </summary>
    private IEnumerator ConnectionAttemptRoutine(Action<bool> onConnectionResult)
    {
        isAttemptingConnection = true;
        currentConnectionAttempt = 0;
        connectionStartTime = Time.time;
        bool connectionSuccessful = false;

        Debug.Log("[NetworkManager] 开始服务器连接验证流程");

        while (currentConnectionAttempt < maxConnectionAttempts && !connectionSuccessful)
        {
            currentConnectionAttempt++;
            Debug.Log($"[NetworkManager] 尝试连接服务器 ({currentConnectionAttempt}/{maxConnectionAttempts})");

            // 尝试连接服务器
            bool attemptResult = false;
            bool connectionAttemptFailed = false;

            // 第一步：尝试连接
            try
            {
                ConnectToServer();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkManager] 连接异常: {ex.Message}");
                connectionAttemptFailed = true;
            }

            // 第二步：等待连接结果
            if (!connectionAttemptFailed)
            {
                float waitStartTime = Time.time;
                while (Time.time - waitStartTime < 3f)
                {
                    if (IsConnected)
                    {
                        attemptResult = true;
                        break;
                    }
                    yield return new WaitForSeconds(0.1f);
                }
            }

            if (attemptResult)
            {
                connectionSuccessful = true;
                Debug.Log($"[NetworkManager] 第 {currentConnectionAttempt} 次连接尝试成功");
                break;
            }
            else
            {
                Debug.LogWarning($"[NetworkManager] 第 {currentConnectionAttempt} 次连接尝试失败");

                // 检查是否超时
                if (Time.time - connectionStartTime > connectionTimeout)
                {
                    Debug.LogError("[NetworkManager] 连接超时");
                    break;
                }

                // 如果还有重试机会，等待后重试
                if (currentConnectionAttempt < maxConnectionAttempts)
                {
                    Debug.Log($"[NetworkManager] 等待 {connectionRetryDelay} 秒后重试...");
                    yield return new WaitForSeconds(connectionRetryDelay);
                }
            }
        }

        isAttemptingConnection = false;
        onConnectionResult?.Invoke(connectionSuccessful);

        if (connectionSuccessful)
        {
            Debug.Log("[NetworkManager] 服务器连接验证成功");
        }
        else
        {
            Debug.LogError($"[NetworkManager] 服务器连接失败，已达到最大尝试次数 {maxConnectionAttempts}");
        }
    }

    private IEnumerator ConnectionAttemptWithTimeout()
    {
        float timeout = 10f; // 10秒超时
        float startTime = Time.time;
        bool connected = false;

        while (Time.time - startTime < timeout && !connected)
        {
            connected = TryConnect();
            if (!connected)
            {
                yield return new WaitForSeconds(1f);
            }
        }

        if (!connected)
        {
            Debug.LogError("连接超时");
            NotifyConnectionProblem("Timeout");
        }
    }

    void Start()
    {
        Debug.Log("[NetworkManager] ========== Start 开始 ==========");
        
        // 确保依赖组件存在
        heartbeatManager = GetComponent<HeartbeatManager>();
        if (heartbeatManager == null)
        {
            Debug.Log("[NetworkManager] 创建HeartbeatManager...");
            heartbeatManager = gameObject.AddComponent<HeartbeatManager>();
        }

        var reconnectManager = GetComponent<ReconnectManager>();
        if (reconnectManager == null)
        {
            Debug.Log("[NetworkManager] 创建ReconnectManager...");
            reconnectManager = gameObject.AddComponent<ReconnectManager>();
        }

        // 初始化UDP服务
        if (enableUdp)
        {
            Debug.Log("[NetworkManager] 初始化UDP服务...");
            InitializeUdpService();
        }
        
        Debug.Log("[NetworkManager] ========== Start 结束 ==========");
    }

    /// <summary>
    /// 初始化UDP
    /// </summary>
    private void InitializeUdpService()
    {
        if (udpService == null)
        {
            udpService = GetComponent<UdpService>();
            if (udpService == null)
            {
                udpService = gameObject.AddComponent<UdpService>();
            }
        }

        udpService.Initialize();

        // 订阅UDP数据到达事件
        udpService.OnDataReceived -= HandleUdpData;
        udpService.OnDataReceived += HandleUdpData;
        udpService.OnConnectionStateChanged -= HandleUdpConnectionStateChanged;
        udpService.OnConnectionStateChanged += HandleUdpConnectionStateChanged;
    }

    private void HandleUdpConnectionStateChanged(bool connected)
    {
        Debug.Log($"[NetworkManager] UDP连接状态: {(connected ? "已连接" : "已断开")}");
    }

    private void HandleUdpData(byte[] data)
    {
        if (data.Length >= BaseMessage.HeaderSize)
        {
            try
            {
                BaseMessage receivedMsg = BaseMessage.FromBytes(data, 0, data.Length);

                lock (_queueLock)
                {
                    _messageQueue.Enqueue(receivedMsg);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkManager] 解析UDP消息失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 智能发送消息 - 根据消息类型选择TCP或UDP
    /// </summary>
    public void SendGameMessage(ushort protocolId, byte[] bodyData)
    {
        switch (protocolId)
        {
            case Protocols.HeartbeatRequest:
                if (heartbeatViaUdp && IsUdpEnabled)
                {
                    SendViaUdp(protocolId, bodyData);
                }
                else
                {
                    SendMessage(protocolId, bodyData);
                }
                break;

            case Protocols.PlayerMoveRequest:
            case Protocols.GameThreeInput:
                if (playerMoveViaUdp && IsUdpEnabled)
                {
                    SendViaUdp(protocolId, bodyData);
                }
                else
                {
                    SendMessage(protocolId, bodyData);
                }
                break;

            default:
                SendMessage(protocolId, bodyData);
                break;
        }
    }

    /// <summary>
    /// 通过UDP发送消息
    /// </summary>
    public void SendViaUdp(ushort protocolId, byte[] bodyData)
    {
        if (!IsUdpEnabled)
        {
            Debug.LogWarning("[NetworkManager] UDP未启用，回退到TCP");
            SendMessage(protocolId, bodyData);
            return;
        }

        udpService.SendMessage(protocolId, bodyData);
        if (protocolId != Protocols.GameThreeInput && protocolId != Protocols.PlayerMoveRequest)
        {
            Debug.Log($"[NetworkManager] 通过UDP发送消息，协议号: {protocolId}");
        }
    }

    /// <summary>
    /// 连接到服务器
    /// </summary>
    public void ConnectToServer()
    {
        // More than one startup component can request a connection in the same
        // frame. Do not tear down a healthy transport just to create another one.
        if (IsConnected)
        {
            Debug.Log("[NetworkManager] TCP connection is already active; skipping duplicate connect request.");
            return;
        }

        Debug.Log("[NetworkManager] ========== 开始连接服务器 ==========");
        Debug.Log($"[NetworkManager] 目标地址: {serverHost}:{serverPort}");
        
        try
        {
            // 先断开任何现有连接
            Disconnect();

            var client = new TcpClient();
            Debug.Log("[NetworkManager] TcpClient已创建，尝试连接...");
            
            // 设置连接超时
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 10000;
            
            client.Connect(serverHost, serverPort);
            var stream = client.GetStream();
            int generation;

            lock (_connectionLock)
            {
                _client = client;
                _stream = stream;
                _isConnected = true;
                generation = ++_connectionGeneration;
            }

            Debug.Log("[NetworkManager] ========== 已成功连接到服务器！ ==========");

            // 创建并启动接收线程
            _receiveThread = new Thread(() => ReceiveData(client, stream, generation));
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "NetworkReceiveThread"; // 为线程命名，便于调试
            
            // 在锁内启动线程，避免竞争条件
            lock (_connectionLock)
            {
                _receiveThread.Start();
            }

            if (enableUdp)
            {
                InitializeUdpService();
            }
            
            Debug.Log("[NetworkManager] 接收线程已启动");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[NetworkManager] ========== 连接失败！ ==========");
            Debug.LogError($"[NetworkManager] 错误信息: {e.Message}");
            Debug.LogError($"[NetworkManager] 堆栈跟踪: {e.StackTrace}");
            
            // 确保连接状态被设置为false
            lock (_connectionLock)
            {
                _isConnected = false;
            }
            
            // 清理资源
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
            
            SafeTriggerConnectionProblemEvent();
        }
    }

    /// <summary>
    /// 接收数据线程方法
    /// </summary>
    private void ReceiveData(TcpClient client, NetworkStream stream, int generation)
    {
        byte[] buffer = new byte[4096];

        while (true)
        {
            // 在循环开始时检查连接状态，避免在断开连接后继续循环
            bool isConnected;
            lock (_connectionLock)
            {
                isConnected = _isConnected
                    && generation == _connectionGeneration
                    && ReferenceEquals(_client, client)
                    && client.Connected;
            }
            
            if (!isConnected)
            {
                break;
            }
            
            try
            {
                if (!client.Connected)
                {
                    ReportTransportFailure(generation, "TCP socket is no longer connected");
                    break;
                }
                
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    lock (_queueLock)
                    {
                        _frameDecoder.Append(buffer, 0, bytesRead);
                    }
                    ProcessReceivedCache();
                }
                else
                {
                    ReportTransportFailure(generation, "Server closed the TCP connection");
                    break;
                }
            }
            catch (System.Exception ex)
            {
                ReportTransportFailure(generation, ex.Message);
                break;
            }
        }
        
        Debug.Log("[NetworkManager] 接收数据线程结束");
    }

    private void ReportTransportFailure(int generation, string reason)
    {
        bool shouldNotify = false;
        lock (_connectionLock)
        {
            if (generation == _connectionGeneration && _isConnected)
            {
                _isConnected = false;
                shouldNotify = true;
            }
        }

        if (!shouldNotify)
        {
            return;
        }

        udpService?.ResetSessionBinding();
        Debug.LogWarning($"[NetworkManager] TCP connection lost: {reason}");
        SafeTriggerConnectionProblemEvent();
    }

    /// <summary>
    /// 处理接收缓存
    /// </summary>
    private void ProcessReceivedCache()
    {
        lock (_queueLock)
        {
            try
            {
                while (_frameDecoder.TryRead(out var receivedMsg))
                {
                    _messageQueue.Enqueue(receivedMsg);
                }
            }
            catch (System.IO.InvalidDataException exception)
            {
                Debug.LogError($"[NetworkManager] 收到非法数据包: {exception.Message}");
                _frameDecoder.Clear();
                int generation;
                lock (_connectionLock)
                {
                    generation = _connectionGeneration;
                }
                ReportTransportFailure(generation, exception.Message);
            }
        }
    }
        
    /// <summary>
    /// 延迟显示登录失败弹窗
    /// </summary>
    /// <param name="message">要显示的消息</param>
    private System.Collections.IEnumerator DelayedShowLoginError(string message)
    {
        yield return new UnityEngine.WaitForSeconds(0.1f); // 短暂延迟
        MessagePopup.Instance?.ShowError(message, false);
    }
        
    void Update()
    {
        lock (_queueLock)
        {
            while (_messageQueue.Count > 0)
            {
                BaseMessage msg = _messageQueue.Dequeue();
                HandleMessage(msg);
            }
        }
    }

    /// <summary>
    /// 处理消息
    /// </summary>
    private void HandleMessage(BaseMessage msg)
    {
        Debug.Log($"[NetworkManager] 收到消息，协议号: {msg.ProtocolId}");

        if (_protocolHandlers.TryGetValue(msg.ProtocolId, out var handler))
        {
            handler.Invoke(msg);
            return;
        }

        Debug.LogWarning($"[NetworkManager] 收到未处理的消息，协议号: {msg.ProtocolId}");
    }

    public void RegisterMessageHandler(ushort protocolId, Action<BaseMessage> handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        if (_protocolHandlers.TryGetValue(protocolId, out var existing))
        {
            _protocolHandlers[protocolId] = existing + handler;
        }
        else
        {
            _protocolHandlers[protocolId] = handler;
        }
    }

    public void UnregisterMessageHandler(ushort protocolId, Action<BaseMessage> handler)
    {
        if (!_protocolHandlers.TryGetValue(protocolId, out var existing))
        {
            return;
        }

        existing -= handler;
        if (existing == null)
        {
            _protocolHandlers.Remove(protocolId);
        }
        else
        {
            _protocolHandlers[protocolId] = existing;
        }
    }

    private void RegisterBuiltInProtocolHandlers()
    {
        RegisterMessageHandler(Protocols.HeartbeatResponse, msg => heartbeatManager?.HandleHeartbeatResponse(msg));
        RegisterMessageHandler(Protocols.LoginResponse, HandleLoginResponse);
        RegisterMessageHandler(Protocols.ResumeSessionResponse, HandleResumeSessionResponse);
        RegisterMessageHandler(Protocols.UdpBindResponse, HandleUdpBindResponse);
        RegisterMessageHandler(Protocols.RegisterResponse, HandleRegisterResponse);
        RegisterMessageHandler(Protocols.LogoutResponse, HandleLogoutResponse);
        RegisterMessageHandler(Protocols.EnterRoomResponse, HandleEnterRoomResponse);
        RegisterMessageHandler(Protocols.ToggleReadyResponse, HandleToggleReadyResponse);
        RegisterMessageHandler(Protocols.StartGameResponse, HandleStartGameResponse);
        RegisterMessageHandler(Protocols.RoomStateUpdate, HandleRoomStateUpdate);
        RegisterMessageHandler(Protocols.SelectGameModeResponse, HandleSelectGameModeResponse);
        RegisterMessageHandler(Protocols.SelectGameThreeBallResponse, HandleSelectGameThreeBallResponse);
        RegisterMessageHandler(Protocols.GameThreeBallSelectionStateUpdate, HandleGameThreeBallSelectionStateUpdate);
        RegisterMessageHandler(Protocols.GameStartNotification, HandleGameStartNotification);
        RegisterMessageHandler(Protocols.GameThreeStateSync, HandleGameThreeStateSync);
    }

    private void HandleGameThreeStateSync(BaseMessage message)
    {
        CacheRecoveryStateIfNeeded(message);
        CacheGameThreePreRoundStateIfNeeded(message);
    }

    private void CacheGameThreePreRoundStateIfNeeded(BaseMessage message)
    {
        try
        {
            using (var stream = new System.IO.MemoryStream(message.Body))
            {
                var state = ProtoBufHelper.Deserialize<GameThreeStateSync>(stream);
                if (!state.IsRoundActive && !state.IsRecoverySnapshot)
                {
                    pendingGameThreePreRoundStateSync = message.Body;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[NetworkManager] Failed to cache GameThree pre-round state: {exception.Message}");
        }
    }

    private void CacheRecoveryStateIfNeeded(BaseMessage message)
    {
        if (!awaitingRecoverySnapshot)
        {
            return;
        }

        pendingRecoveryStateProtocol = message.ProtocolId;
        pendingRecoveryStateSync = message.Body;
        awaitingRecoverySnapshot = false;
    }

    /// <summary>
    /// 处理登录响应
    /// </summary>
    private void HandleLoginResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                LoginResponse loginResp = ProtoBufHelper.Deserialize<LoginResponse>(ms);

                if (loginResp.Success)
                {
                    IsLoginGameRecoveryPending = false;
                    IsLoginRoomRecoveryPending = false;
                    LoginDestinationSceneName = loginResp.SceneName ?? string.Empty;
                    authenticatedPlayerId = loginResp.PlayerId;
                    authenticatedUsername = GetCurrentLoginRequest()?.Username ?? string.Empty;
                    sessionToken = loginResp.SessionToken ?? string.Empty;
                    if (loginResp.IsInGame)
                    {
                        IsLoginGameRecoveryPending = true;
                        awaitingRecoverySnapshot = true;
                        if (string.Equals(loginResp.SceneName, SceneNames.GameplayThree, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, loginResp.SceneName, StringComparison.OrdinalIgnoreCase))
                        {
                            GamePlayThree.GameThreeMatchIntro.SkipNextIntro();
                        }
                        var playerData = PlayerDataManager.Instance;
                        playerData?.SetSlotIndex(loginResp.SlotIndex);
                        playerData?.SetRealPlayerCount(loginResp.PlayerCount);
                        LoadResumeScene(string.IsNullOrWhiteSpace(loginResp.SceneName)
                            ? SceneNames.GameplayThree
                            : loginResp.SceneName);
                    }
                    else if (string.Equals(loginResp.SceneName, "Room", StringComparison.OrdinalIgnoreCase))
                    {
                        IsLoginRoomRecoveryPending = true;
                    }
                    BindUdpSession(loginResp.PlayerId, loginResp.SessionToken);
                }
                else
                {
                    udpService?.ResetSessionBinding();
                }
                
                // 在主线程中显示弹窗消息
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        if (loginResp.Success)
                        {
                            // 先隐藏任何可能存在的弹窗
                            MessagePopup.Instance?.Hide();
                            
                            // 设置玩家信息
                            PlayerDataManager playerDataManager = null;
                            try
                            {
                                playerDataManager = PlayerDataManager.Instance;
                                if (playerDataManager == null)
                                {
                                    // 如果PlayerDataManager不存在，创建一个新的
                                    GameObject playerDataGameObject = new GameObject("PlayerDataManager");
                                    playerDataManager = playerDataGameObject.AddComponent<PlayerDataManager>();
                                    Debug.Log("[NetworkManager] 创建了新的PlayerDataManager实例");
                                }
                                
                                if (playerDataManager != null)
                                {
                                    // 从当前登录请求获取用户名
                                    var currentLoginRequest = GetCurrentLoginRequest();
                                    string username = currentLoginRequest?.Username ?? "";
                                    
                                    // 如果用户名为空但响应成功，可以尝试使用登录的用户名
                                    if (string.IsNullOrEmpty(username))
                                    {
                                        Debug.LogWarning($"[NetworkManager] 登录响应中用户名为空，PlayerId: {loginResp.PlayerId}");
                                    }
                                    
                                    playerDataManager.SetPlayerInfo(loginResp.PlayerId, username);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"设置玩家信息时发生异常: {ex.Message}");
                            }
                            
                            // 触发登录成功事件
                            OnLoginResult?.Invoke(true, loginResp.Message);
                            
                            // 清除当前登录请求
                            currentLoginRequest = null;
                        }
                        else
                        {
                            // 先隐藏任何可能存在的弹窗
                            MessagePopup.Instance?.Hide();
                            
                            // 短暂延迟后显示登录失败弹窗
                            StartCoroutine(DelayedShowLoginError(loginResp.Message));
                            
                            // 触发登录失败事件
                            OnLoginResult?.Invoke(false, loginResp.Message);
                            
                            // 清除当前登录请求
                            currentLoginRequest = null;
                        }
                    });
                }
                else
                {
                    // 如果没有主线程调度器，则直接显示（可能不在主线程）
                    if (loginResp.Success)
                    {
                        MessagePopup.Instance?.Hide();
                        
                        // 设置玩家信息
                        PlayerDataManager playerDataManager = null;
                        string username = ""; // 在外部定义username变量
                        try
                        {
                            playerDataManager = PlayerDataManager.Instance;
                            if (playerDataManager == null)
                            {
                                // 如果PlayerDataManager不存在，创建一个新的
                                GameObject playerDataGameObject = new GameObject("PlayerDataManager");
                                playerDataManager = playerDataGameObject.AddComponent<PlayerDataManager>();
                                Debug.Log("[NetworkManager] 创建了新的PlayerDataManager实例");
                            }
                            
                            if (playerDataManager != null)
                            {
                                // 从当前登录请求获取用户名
                                var currentLoginRequest = GetCurrentLoginRequest();
                                username = currentLoginRequest?.Username ?? "";
                                
                                // 如果用户名为空但响应成功，可以尝试使用登录的用户名
                                if (string.IsNullOrEmpty(username))
                                {
                                    Debug.LogWarning($"[NetworkManager] 登录响应中用户名为空，PlayerId: {loginResp.PlayerId}");
                                }
                                
                                playerDataManager.SetPlayerInfo(loginResp.PlayerId, username);
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogError($"设置玩家信息时发生异常: {ex.Message}");
                        }
                        
                        // 触发登录成功事件
                        OnLoginResult?.Invoke(true, loginResp.Message);
                        
                        // 清除当前登录请求
                        currentLoginRequest = null;
                        
                        Debug.Log($"[NetworkManager] 登录成功事件已触发，PlayerId: {loginResp.PlayerId}, Username: {username}");
                    }
                    else
                    {
                        MessagePopup.Instance?.ShowError(loginResp.Message, false);
                        
                        // 触发登录失败事件
                        OnLoginResult?.Invoke(false, loginResp.Message);
                        
                        // 清除当前登录请求
                        currentLoginRequest = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle login response: {ex.Message}");
            
            // 显示错误消息
            if (MainThreadDispatcher.Instance != null)
            {
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    MessagePopup.Instance?.ShowError("Login response parsing failed", false);
                });
            }
        }
    }

    private void BindUdpSession(int playerId, string sessionToken)
    {
        if (!enableUdp || udpService == null)
        {
            return;
        }

        if (!udpService.BindSession(playerId, sessionToken))
        {
            Debug.LogWarning("[NetworkManager] UDP会话绑定未发出，实时消息将通过TCP发送");
        }
    }

    public void ResumeSession(Action<bool> callback)
    {
        if (!HasResumableSession || !IsConnected)
        {
            callback?.Invoke(!HasResumableSession && IsConnected);
            return;
        }

        lastSessionResumeFailure = string.Empty;
        resumeSessionCallback = callback;
        var request = new ResumeSessionRequest
        {
            PlayerId = authenticatedPlayerId,
            SessionToken = sessionToken
        };

        try
        {
            using (var stream = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, request);
                SendMessage(Protocols.ResumeSessionRequest, stream.ToArray());
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[NetworkManager] Failed to send session resume request: {exception.Message}");
            var pendingCallback = resumeSessionCallback;
            resumeSessionCallback = null;
            pendingCallback?.Invoke(false);
        }
    }

    public bool TryConsumeRecoveryState(out byte[] stateSync)
    {
        return TryConsumeRecoveryState(Protocols.GameThreeStateSync, out stateSync);
    }

    public bool TryConsumeRecoveryState(ushort protocolId, out byte[] stateSync)
    {
        if (pendingRecoveryStateProtocol != protocolId)
        {
            stateSync = null;
            return false;
        }

        stateSync = pendingRecoveryStateSync;
        pendingRecoveryStateSync = null;
        pendingRecoveryStateProtocol = 0;
        return stateSync != null && stateSync.Length > 0;
    }

    public bool TryConsumeGameThreePreRoundState(out byte[] stateSync)
    {
        stateSync = pendingGameThreePreRoundStateSync;
        pendingGameThreePreRoundStateSync = null;
        IsLoginGameRecoveryPending = false;
        return stateSync != null && stateSync.Length > 0;
    }

    private void HandleResumeSessionResponse(BaseMessage message)
    {
        bool success = false;
        try
        {
            using (var stream = new System.IO.MemoryStream(message.Body))
            {
                var response = ProtoBufHelper.Deserialize<ResumeSessionResponse>(stream);
                success = response.Success;
                if (response.Success)
                {
                    lastSessionResumeFailure = string.Empty;
                    authenticatedPlayerId = response.PlayerId;
                    sessionToken = response.SessionToken;
                    BindUdpSession(response.PlayerId, response.SessionToken);

                    var playerData = PlayerDataManager.Instance;
                    if (playerData != null)
                    {
                        playerData.SetPlayerInfo(response.PlayerId, authenticatedUsername);
                        if (response.IsInGame)
                        {
                            playerData.SetSlotIndex(response.SlotIndex);
                            playerData.SetRealPlayerCount(response.PlayerCount);
                        }
                    }

                    if (response.IsInGame)
                    {
                        awaitingRecoverySnapshot = true;
                        if (string.Equals(response.SceneName, SceneNames.GameplayThree, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, response.SceneName, StringComparison.OrdinalIgnoreCase))
                        {
                            GamePlayThree.GameThreeMatchIntro.SkipNextIntro();
                        }
                        LoadResumeScene(string.IsNullOrWhiteSpace(response.SceneName)
                            ? SceneNames.GameplayThree
                            : response.SceneName);
                    }
                    else if (response.IsInLobby)
                    {
                        LoadResumeScene(string.IsNullOrWhiteSpace(response.SceneName)
                            ? SceneNames.Room
                            : response.SceneName);
                    }

                    Debug.Log($"[NetworkManager] Session resumed. lobby={response.IsInLobby}, game={response.IsInGame}");
                }
                else
                {
                    var resumeFailure = string.IsNullOrWhiteSpace(response.Message)
                        ? "The session resume window has expired. Please sign in again."
                        : response.Message;
                    ClearAuthenticatedSession();
                    lastSessionResumeFailure = resumeFailure;
                    Debug.LogWarning($"[NetworkManager] Session resume rejected: {response.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[NetworkManager] Failed to process session resume response: {exception.Message}");
        }
        finally
        {
            var callback = resumeSessionCallback;
            resumeSessionCallback = null;
            callback?.Invoke(success);
        }
    }

    private void LoadResumeScene(string sceneName)
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == sceneName)
        {
            return;
        }

        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.LoadScene(sceneName);
        }
        else
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }

    private void ClearAuthenticatedSession()
    {
        authenticatedPlayerId = 0;
        authenticatedUsername = string.Empty;
        sessionToken = string.Empty;
        lastSessionResumeFailure = string.Empty;
        awaitingRecoverySnapshot = false;
        pendingRecoveryStateProtocol = 0;
        pendingRecoveryStateSync = null;
        pendingGameThreePreRoundStateSync = null;
        udpService?.ResetSessionBinding();
    }

    public void ClearSessionForLogout(bool keepTransport)
    {
        GetComponent<ReconnectManager>()?.ResetReconnectState();
        ClearAuthenticatedSession();
        PlayerDataManager.Instance?.ClearPlayerInfo();
        if (!keepTransport)
        {
            Disconnect();
        }
    }

    private void HandleUdpBindResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                UdpBindResponse response = ProtoBufHelper.Deserialize<UdpBindResponse>(ms);
                udpService?.SetSessionBound(response.Success);

                if (response.Success)
                {
                    Debug.Log("[NetworkManager] UDP会话绑定成功");
                }
                else
                {
                    Debug.LogWarning($"[NetworkManager] UDP会话绑定失败，将继续使用TCP: {response.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            udpService?.ResetSessionBinding();
            Debug.LogError($"[NetworkManager] UDP绑定响应解析失败，将继续使用TCP: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理注册响应
    /// </summary>
    private void HandleRegisterResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                RegisterResponse registerResp = ProtoBufHelper.Deserialize<RegisterResponse>(ms);
                
                // 在主线程中显示弹窗消息
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        if (registerResp.Success)
                        {
                            MessagePopup.Instance?.ShowSuccess(registerResp.Message, true);
                            
                            // 触发注册成功事件
                            OnRegisterResult?.Invoke(true, registerResp.Message);
                        }
                        else
                        {
                            MessagePopup.Instance?.ShowError(registerResp.Message, false);
                            
                            // 触发注册失败事件
                            OnRegisterResult?.Invoke(false, registerResp.Message);
                        }
                    });
                }
                else
                {
                    // 如果没有主线程调度器，则直接显示（可能不在主线程）
                    if (registerResp.Success)
                    {
                        MessagePopup.Instance?.ShowSuccess(registerResp.Message, true);
                        
                        // 触发注册成功事件
                        OnRegisterResult?.Invoke(true, registerResp.Message);
                    }
                    else
                    {
                        MessagePopup.Instance?.ShowError(registerResp.Message, false);
                        
                        // 触发注册失败事件
                        OnRegisterResult?.Invoke(false, registerResp.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle register response: {ex.Message}");
            
            // 显示错误消息
            if (MainThreadDispatcher.Instance != null)
            {
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    MessagePopup.Instance?.ShowError("Register response parsing failed", false);
                });
            }
        }
    }

    /// <summary>
    /// 处理登出响应
    /// </summary>
    private void HandleLogoutResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                LogoutResponse logoutResp = ProtoBufHelper.Deserialize<LogoutResponse>(ms);

                if (!logoutResp.Success
                    && logoutResp.Message.IndexOf("signed in elsewhere", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Action forcedLogout = () => HandleForcedLogout(logoutResp.Message);
                    if (MainThreadDispatcher.Instance != null)
                    {
                        MainThreadDispatcher.Instance.Enqueue(forcedLogout);
                    }
                    else
                    {
                        forcedLogout();
                    }
                    return;
                }
                
                // 在主线程中显示登出结果消息
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        if (logoutResp.Success)
                        {
                            Debug.Log("登出成功: " + logoutResp.Message);
                            ClearAuthenticatedSession();
                            
                            // 隐藏任何可能存在的弹窗
                            MessagePopup.Instance?.Hide();
                            
                            // 清除玩家信息
                            try
                            {
                                var playerDataManager = PlayerDataManager.Instance;
                                if (playerDataManager != null)
                                {
                                    playerDataManager.ClearPlayerInfo();
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"清除玩家信息时发生异常: {ex.Message}");
                            }
                        }
                        else
                        {
                            Debug.LogWarning("登出失败: " + logoutResp.Message);
                            
                            // 先隐藏任何可能存在的弹窗，再显示失败消息
                            MessagePopup.Instance?.Hide();
                            
                            // 显示登出失败消息
                            MessagePopup.Instance?.ShowError("Logout failed: " + logoutResp.Message, false);
                        }
                        
                        // 触发登出结果事件，让其他监听者知道登出结果
                        OnLogoutResult?.Invoke(logoutResp.Success, logoutResp.Message);
                    });
                }
                else
                {
                    // 如果没有主线程调度器，则直接处理（可能不在主线程）
                    if (logoutResp.Success)
                    {
                        Debug.Log("登出成功: " + logoutResp.Message);
                        ClearAuthenticatedSession();
                        
                        // 隐藏任何可能存在的弹窗
                        MessagePopup.Instance?.Hide();
                        
                        // 清除玩家信息
                        try
                        {
                            var playerDataManager = PlayerDataManager.Instance;
                            if (playerDataManager != null)
                            {
                                playerDataManager.ClearPlayerInfo();
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogError($"清除玩家信息时发生异常: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("登出失败: " + logoutResp.Message);
                        
                        // 先隐藏任何可能存在的弹窗，再显示失败消息
                        MessagePopup.Instance?.Hide();
                        
                        // 显示登出失败消息
                        MessagePopup.Instance?.ShowError("Logout failed: " + logoutResp.Message, false);
                    }
                    
                    // 触发登出结果事件，让其他监听者知道登出结果
                    OnLogoutResult?.Invoke(logoutResp.Success, logoutResp.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle logout response: {ex.Message}");
            
            // 显示错误消息
            if (MainThreadDispatcher.Instance != null)
            {
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    MessagePopup.Instance?.ShowError("Logout response parsing failed", false);
                    // 触发登出结果事件，传递失败信息
                    OnLogoutResult?.Invoke(false, "Logout response parsing failed");
                });
            }
            else
            {
                // 触发登出结果事件，传递失败信息
                OnLogoutResult?.Invoke(false, "Logout response parsing failed");
            }
        }
    }

    private void HandleForcedLogout(string reason)
    {
        Debug.LogWarning($"[NetworkManager] Forced logout: {reason}");
        ClearAuthenticatedSession();
        try
        {
            PlayerDataManager.Instance?.ClearPlayerInfo();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[NetworkManager] Failed to clear player info after forced logout: {exception.Message}");
        }

        Disconnect();
        const string message = "This account was signed in on another device. Click OK to return to login.";
        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.ShowMessage(
                message,
                MessageType.Warning,
                false,
                ReturnToLoginAfterForcedLogout);
        }
        else
        {
            ReturnToLoginAfterForcedLogout();
        }
    }

    private void ReturnToLoginAfterForcedLogout()
    {
        MessagePopup.Instance?.HideImmediate();
        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
        }
        else
        {
            SceneManager.LoadScene(SceneNames.Login);
        }
    }

    /// <summary>
    /// 处理进入房间响应
    /// </summary>
    private void HandleEnterRoomResponse(BaseMessage msg)
    {
        try
        {
            // 验证消息体长度
            if (msg.Body == null || msg.Body.Length == 0)
            {
                Debug.LogError("[NetworkManager] Enter room response body is null or empty");
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        MessagePopup.Instance?.ShowError("Enter room response body is empty", false);
                    });
                }
                return;
            }
            
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                // 使用ProtoBufHelper统一处理序列化，在IL2CPP下会自动使用RuntimeTypeModel
                FileLogger.Log($"[NetworkManager] Handling EnterRoomResponse, body length: {msg.Body?.Length ?? 0}");
                
                EnterRoomResponse enterRoomResp;
                try
                {
                    FileLogger.Log("[NetworkManager] Starting EnterRoomResponse deserialization...");
                    enterRoomResp = ProtoBufManualParser.ParseEnterRoomResponse(msg.Body);
                    FileLogger.Log($"[NetworkManager] EnterRoomResponse deserialized successfully. Success: {enterRoomResp?.Success}, Message: {enterRoomResp?.Message}, Players count: {enterRoomResp?.Players?.Count ?? 0}");
                    
                    // 详细记录玩家信息
                    if (enterRoomResp?.Players != null)
                    {
                        FileLogger.Log($"[NetworkManager] Players list is not null, count: {enterRoomResp.Players.Count}");
                        for (int i = 0; i < enterRoomResp.Players.Count; i++)
                        {
                            var player = enterRoomResp.Players[i];
                            FileLogger.Log($"[NetworkManager] Player[{i}]: Id={player?.PlayerId}, Username={player?.Username}, IsReady={player?.IsReady}, IsHost={player?.IsHost}");
                        }
                    }
                    else
                    {
                        FileLogger.LogWarning("[NetworkManager] Players list is null!");
                    }
                }
                catch (System.Reflection.TargetInvocationException targetEx)
                {
                    FileLogger.LogException(targetEx, "NetworkManager.HandleEnterRoomResponse.TargetInvocationException");
                    FileLogger.LogError($"[NetworkManager] ProtoBuf deserialization error (TargetInvocationException): {targetEx.InnerException?.Message}");
                    FileLogger.LogError($"[NetworkManager] Inner exception type: {targetEx.InnerException?.GetType().Name}");
                    FileLogger.LogError($"[NetworkManager] Inner exception stack trace: {targetEx.InnerException?.StackTrace}");
                    Debug.LogError($"[NetworkManager] ProtoBuf deserialization error: {targetEx.InnerException?.Message}");
                    if (MainThreadDispatcher.Instance != null)
                    {
                        MainThreadDispatcher.Instance.Enqueue(() =>
                        {
                            MessagePopup.Instance?.ShowError("Enter room response parsing failed due to AOT issue", false);
                        });
                    }
                    return;
                }
                catch (Exception ex)
                {
                    FileLogger.LogException(ex, "NetworkManager.HandleEnterRoomResponse");
                    FileLogger.LogError($"[NetworkManager] ProtoBuf deserialization error: {ex.Message}\n{ex.StackTrace}");
                    Debug.LogError($"[NetworkManager] ProtoBuf deserialization error: {ex.Message}\n{ex.StackTrace}");
                    if (MainThreadDispatcher.Instance != null)
                    {
                        MainThreadDispatcher.Instance.Enqueue(() =>
                        {
                            MessagePopup.Instance?.ShowError("Enter room response parsing failed: " + ex.Message, false);
                        });
                    }
                    return;
                }

                var roomGameMode = string.IsNullOrWhiteSpace(enterRoomResp.GameMode)
                    ? "game_three"
                    : enterRoomResp.GameMode.Trim().ToLowerInvariant();
                
                // 在主线程中处理
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        currentRoomGameMode = roomGameMode;
                        if (enterRoomResp.Success)
                        {
                            Debug.Log("[NetworkManager] 进入房间成功: " + enterRoomResp.Message);
                        }
                        else
                        {
                            Debug.LogWarning("[NetworkManager] 进入房间失败: " + enterRoomResp.Message);
                            MessagePopup.Instance?.ShowError("Failed to enter room: " + enterRoomResp.Message, false);
                        }
                        
                        // 触发进入房间结果事件
                        OnEnterRoomResult?.Invoke(enterRoomResp.Success, enterRoomResp.Message, enterRoomResp.Players);
                    });
                }
                else
                {
                    // 直接处理（可能不在主线程）
                    currentRoomGameMode = roomGameMode;
                    if (enterRoomResp.Success)
                    {
                        Debug.Log("[NetworkManager] 进入房间成功: " + enterRoomResp.Message);
                    }
                    else
                    {
                        Debug.LogWarning("[NetworkManager] 进入房间失败: " + enterRoomResp.Message);
                        MessagePopup.Instance?.ShowError("Failed to enter room: " + enterRoomResp.Message, false);
                    }
                    
                    // 触发进入房间结果事件
                    OnEnterRoomResult?.Invoke(enterRoomResp.Success, enterRoomResp.Message, enterRoomResp.Players);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle enter room response: {ex.Message}");
            
            // 显示错误消息
            if (MainThreadDispatcher.Instance != null)
            {
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    MessagePopup.Instance?.ShowError("Enter room response parsing failed: " + ex.Message, false);
                });
            }
        }
    }

    /// <summary>
    /// 处理准备状态切换响应
    /// </summary>
    private void HandleToggleReadyResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                ToggleReadyResponse toggleReadyResp = ProtoBufHelper.Deserialize<ToggleReadyResponse>(ms);
                
                // 在主线程中处理
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        if (toggleReadyResp.Success)
                        {
                            Debug.Log("[NetworkManager] 准备状态切换成功: " + toggleReadyResp.Message);
                        }
                        else
                        {
                            Debug.LogWarning("[NetworkManager] 准备状态切换失败: " + toggleReadyResp.Message);
                            MessagePopup.Instance?.ShowError("Failed to toggle ready: " + toggleReadyResp.Message, false);
                        }
                        
                        // 触发准备状态切换结果事件
                        OnToggleReadyResult?.Invoke(toggleReadyResp.Success, toggleReadyResp.Message);
                    });
                }
                else
                {
                    // 直接处理（可能不在主线程）
                    if (toggleReadyResp.Success)
                    {
                        Debug.Log("[NetworkManager] 准备状态切换成功: " + toggleReadyResp.Message);
                    }
                    else
                    {
                        Debug.LogWarning("[NetworkManager] 准备状态切换失败: " + toggleReadyResp.Message);
                        MessagePopup.Instance?.ShowError("Failed to toggle ready: " + toggleReadyResp.Message, false);
                    }
                    
                    // 触发准备状态切换结果事件
                    OnToggleReadyResult?.Invoke(toggleReadyResp.Success, toggleReadyResp.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle toggle ready response: {ex.Message}");
            
            // 显示错误消息
            if (MainThreadDispatcher.Instance != null)
            {
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    MessagePopup.Instance?.ShowError("Toggle ready response parsing failed", false);
                });
            }
        }
    }

    /// <summary>
    /// 处理开始游戏响应
    /// </summary>
    private void HandleStartGameResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                StartGameResponse startGameResp = ProtoBufHelper.Deserialize<StartGameResponse>(ms);
                
                // 在主线程中处理
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        if (startGameResp.Success)
                        {
                            Debug.Log("[NetworkManager] 开始游戏成功: " + startGameResp.Message);
                        }
                        else
                        {
                            Debug.LogWarning("[NetworkManager] 开始游戏失败: " + startGameResp.Message);
                            MessagePopup.Instance?.ShowError("Failed to start game: " + startGameResp.Message, false);
                        }
                        
                        // 触发开始游戏结果事件
                        OnStartGameResult?.Invoke(startGameResp.Success, startGameResp.Message);
                    });
                }
                else
                {
                    // 直接处理（可能不在主线程）
                    if (startGameResp.Success)
                    {
                        Debug.Log("[NetworkManager] 开始游戏成功: " + startGameResp.Message);
                    }
                    else
                    {
                        Debug.LogWarning("[NetworkManager] 开始游戏失败: " + startGameResp.Message);
                        MessagePopup.Instance?.ShowError("Failed to start game: " + startGameResp.Message, false);
                    }
                    
                    // 触发开始游戏结果事件
                    OnStartGameResult?.Invoke(startGameResp.Success, startGameResp.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle start game response: {ex.Message}");
            
            // 显示错误消息
            if (MainThreadDispatcher.Instance != null)
            {
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    MessagePopup.Instance?.ShowError("Start game response parsing failed", false);
                });
            }
        }
    }

    private void HandleSelectGameModeResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                var response = ProtoBufHelper.Deserialize<SelectGameModeResponse>(ms);
                Action complete = () =>
                {
                    if (response.Success)
                    {
                        currentRoomGameMode = string.IsNullOrWhiteSpace(response.GameMode)
                            ? "game_three"
                            : response.GameMode.Trim().ToLowerInvariant();
                    }
                    else
                    {
                        MessagePopup.Instance?.ShowError(response.Message, false);
                    }

                    OnGameModeSelectionResult?.Invoke(response.Success, response.Message, response.GameMode);
                };

                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(complete);
                }
                else
                {
                    complete();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle game mode selection response: {ex.Message}");
        }
    }

    private void HandleSelectGameThreeBallResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                var response = ProtoBufHelper.Deserialize<SelectGameThreeBallResponse>(ms);
                Action complete = () =>
                {
                    if (!response.Success)
                    {
                        MessagePopup.Instance?.ShowError(response.Message, false);
                    }

                    OnGameThreeBallSelectionResult?.Invoke(response.Success, response.Message, response.BallId);
                };

                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(complete);
                }
                else
                {
                    complete();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle GamePlayThree ball selection response: {ex.Message}");
        }
    }

    private void HandleGameThreeBallSelectionStateUpdate(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                var update = ProtoBufManualParser.ParseGameThreeBallSelectionStateUpdate(msg.Body);
                var receivedSelections = update.Selections
                    .Where(selection => selection.PlayerId > 0 && !string.IsNullOrWhiteSpace(selection.BallId))
                    .ToDictionary(selection => selection.PlayerId, selection => selection.BallId.Trim().ToLowerInvariant());
                Action complete = () =>
                {
                    gameThreeBallSelections.Clear();
                    foreach (var selection in receivedSelections)
                    {
                        gameThreeBallSelections[selection.Key] = selection.Value;
                    }

                    OnGameThreeBallSelectionStateChanged?.Invoke(gameThreeBallSelections);
                };

                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(complete);
                }
                else
                {
                    complete();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle GamePlayThree ball selection state: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理房间状态更新
    /// </summary>
    private void HandleRoomStateUpdate(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                RoomStateUpdate roomStateUpdate = ProtoBufManualParser.ParseRoomStateUpdate(msg.Body);
                var roomGameMode = string.IsNullOrWhiteSpace(roomStateUpdate.GameMode)
                    ? "game_three"
                    : roomStateUpdate.GameMode.Trim().ToLowerInvariant();
                
                // 在主线程中处理
                if (MainThreadDispatcher.Instance != null)
                {
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        currentRoomGameMode = roomGameMode;
                        latestRoomPlayers = new List<RoomPlayerInfo>(roomStateUpdate.Players);
                        latestRoomCanStart = roomStateUpdate.GameCanStart;
                        hasLatestRoomState = true;
                        Debug.Log($"[NetworkManager] 房间状态更新，玩家数量: {roomStateUpdate.Players.Count}, 可开始游戏: {roomStateUpdate.GameCanStart}");
                        
                        // 触发房间状态更新事件
                        OnRoomStateUpdate?.Invoke(roomStateUpdate.Players, roomStateUpdate.GameCanStart);
                        
                        // 添加额外的调试信息
                        Debug.Log($"[NetworkManager] 触发房间状态更新事件，玩家数量: {roomStateUpdate.Players.Count}, 游戏可开始: {roomStateUpdate.GameCanStart}");
                        foreach (var player in roomStateUpdate.Players)
                        {
                            Debug.Log($"[NetworkManager] 房间玩家详情 - ID: {player.PlayerId}, Username: {player.Username}, IsHost: {player.IsHost}, IsReady: {player.IsReady}");
                        }
                    });
                }
                else
                {
                    // 直接处理（可能不在主线程）
                    currentRoomGameMode = roomGameMode;
                    latestRoomPlayers = new List<RoomPlayerInfo>(roomStateUpdate.Players);
                    latestRoomCanStart = roomStateUpdate.GameCanStart;
                    hasLatestRoomState = true;
                    Debug.Log($"[NetworkManager] 房间状态更新，玩家数量: {roomStateUpdate.Players.Count}, 可开始游戏: {roomStateUpdate.GameCanStart}");
                    
                    // 触发房间状态更新事件
                    OnRoomStateUpdate?.Invoke(roomStateUpdate.Players, roomStateUpdate.GameCanStart);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to handle room state update: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理游戏开始通知
    /// </summary>
    private void HandleGameStartNotification(BaseMessage msg)
    {
        try
        {
            FileLogger.Log($"[GameStartFlow] Received protocol={msg.ProtocolId}, bodyLength={msg.Body?.Length ?? 0}.");
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                GameStartNotification gameStartNotification = ProtoBufHelper.Deserialize<GameStartNotification>(ms);

                string sceneName = string.IsNullOrWhiteSpace(gameStartNotification.SceneName)
                    ? SceneNames.GameplayThree
                    : gameStartNotification.SceneName.Trim();
                string gameMode = string.IsNullOrWhiteSpace(gameStartNotification.GameMode)
                    && string.Equals(sceneName, SceneNames.GameplayThree, StringComparison.OrdinalIgnoreCase)
                        ? "game_three"
                        : gameStartNotification.GameMode?.Trim() ?? string.Empty;
                int slotIndex = gameStartNotification.YourSlotIndex;

                FileLogger.Log(
                    $"[GameStartFlow] Parsed success={gameStartNotification.Success}, mode='{gameMode}', " +
                    $"scene='{sceneName}', slot={slotIndex}.");

                Action complete = () =>
                {
                    try
                    {
                        Delegate[] subscribers = OnGameStartNotification?.GetInvocationList() ?? Array.Empty<Delegate>();
                        FileLogger.Log(
                            $"[GameStartFlow] Dispatching on main thread. scene='{SceneManager.GetActiveScene().name}', " +
                            $"subscribers={subscribers.Length}.");
                        if (string.Equals(sceneName, SceneNames.GameplayThree, StringComparison.OrdinalIgnoreCase))
                        {
                            MessagePopup.Instance?.ShowSuccess("选择已完成，游戏马上开始", false);
                        }
                        else
                        {
                            MessagePopup.Instance?.HideImmediate();
                        }
                        foreach (Delegate subscriber in subscribers)
                        {
                            try
                            {
                                ((Action<int, string, string>)subscriber)(slotIndex, gameMode, sceneName);
                            }
                            catch (Exception subscriberException)
                            {
                                FileLogger.LogException(
                                    subscriberException,
                                    $"GameStartFlow.Subscriber({subscriber.Method.DeclaringType?.Name}.{subscriber.Method.Name})");
                            }
                        }
                    }
                    catch (Exception dispatchException)
                    {
                        FileLogger.LogException(dispatchException, "GameStartFlow.Dispatch");
                    }
                };

                // HandleMessage is invoked by NetworkManager.Update after the receive
                // thread queues the packet, so this is already Unity's main thread.
                // Dispatch immediately to avoid losing a scene event during an extra
                // frame of object lifecycle changes.
                FileLogger.Log("[GameStartFlow] Dispatching notification from NetworkManager.Update.");
                complete();
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "GameStartFlow.Deserialize");
            Debug.LogError($"Failed to handle game start notification: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    public void SendMessage(ushort protocolId, byte[] bodyData)
    {
        if (bodyData == null || bodyData.Length > MaxPacketBodyBytes)
        {
            Debug.LogError($"[NetworkManager] 拒绝发送非法包长度: {bodyData?.Length ?? 0}");
            return;
        }

        if (!IsConnected)
        {
            Debug.LogWarning("尝试发送消息但连接不可用");
            return;
        }

        try
        {
            BaseMessage msg = new BaseMessage(protocolId, bodyData);
            byte[] dataToSend = msg.ToBytes();

            // 获取网络流的安全引用以避免多线程问题
            NetworkStream stream;
            lock (_connectionLock)
            {
                if (!_isConnected || _stream == null) return;
                stream = _stream;
            }
            
            if (stream != null)
            {
                lock (_sendLock)
                {
                    stream.Write(dataToSend, 0, dataToSend.Length);
                }
            }

            if (protocolId != Protocols.GameThreeInput && protocolId != Protocols.PlayerMoveRequest)
            {
                Debug.Log($"已发送消息，协议号: {protocolId}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"发送消息失败: {ex.Message}");
            int generation;
            lock (_connectionLock)
            {
                generation = _connectionGeneration;
            }
            ReportTransportFailure(generation, ex.Message);
        }
    }

    /// <summary>
    /// 安全触发连接问题事件
    /// </summary>
    private void SafeTriggerConnectionProblemEvent()
    {
        if (MainThreadDispatcher.Instance != null)
        {
            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                _onConnectionProblemEvent?.Invoke();
            });
        }
        else
        {
            _onConnectionProblemEvent?.Invoke();
        }
    }

    /// <summary>
    /// 停止连接尝试过程
    /// </summary>
    public void StopConnectionAttempt()
    {
        if (connectionCoroutine != null)
        {
            StopCoroutine(connectionCoroutine);
            connectionCoroutine = null;
        }
        isAttemptingConnection = false;
        Debug.Log("[NetworkManager] 连接尝试已停止");
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public void Disconnect()
    {
        bool hadTcpConnection;
        lock (_connectionLock)
        {
            hadTcpConnection = _isConnected || _stream != null || _client != null;
            _isConnected = false;
            _connectionGeneration++;
        }

        if (hadTcpConnection)
        {
            try
            {
                // 先关闭流，立即唤醒阻塞中的接收线程，再等待线程退出。
                _stream?.Close();
                _stream?.Dispose();
                _client?.Close();
                _receiveThread?.Join(1000);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"断开连接时发生异常: {ex.Message}");
            }
        }

        _stream = null;
        _client = null;
        _receiveThread = null;
        
        // 断开UDP服务连接
        try
        {
            udpService?.Disconnect();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"断开UDP服务时发生异常: {ex.Message}");
        }

        // 清理消息队列
        lock (_queueLock)
        {
            _messageQueue.Clear();
            _frameDecoder.Clear();
        }

        Debug.Log("[NetworkManager] 已断开连接");
    }

    /// <summary>
    /// 尝试连接服务器
    /// </summary>
    public bool TryConnect()
    {
        try
        {
            if (IsConnected) return true;

            ConnectToServer();
            return IsConnected;
        }
        catch (Exception e)
        {
            Debug.LogError($"尝试连接失败: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 通知连接问题（供心跳管理器和其他组件调用）
    /// </summary>
    public void NotifyConnectionProblem(string source = "Unknown")
    {
        Debug.LogWarning($"[NetworkManager] 连接问题通知来自: {source}");
        int generation;
        lock (_connectionLock)
        {
            generation = _connectionGeneration;
        }
        ReportTransportFailure(generation, source);
    }

    public bool SimulateTransportDropForTest()
    {
        NetworkStream stream;
        lock (_connectionLock)
        {
            stream = _stream;
        }

        if (stream == null || !IsConnected)
        {
            Debug.LogWarning("[ReconnectTest] No active TCP connection to drop");
            return false;
        }

        Debug.LogWarning("[ReconnectTest] Simulating an unexpected TCP transport drop");
        stream.Close();
        return true;
    }

    /// <summary>
    /// 发送进入房间请求
    /// </summary>
    public void SendEnterRoomRequest(int playerId, string username)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法发送进入房间请求");
            return;
        }

        try
        {
            EnterRoomRequest enterRoomReq = new EnterRoomRequest
            {
                PlayerId = playerId,
                Username = username
            };

            // 序列化消息体
            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, enterRoomReq);
                bodyData = ms.ToArray();
            }

            // 发送进入房间请求
            SendMessage(Protocols.EnterRoomRequest, bodyData);
            Debug.Log("[NetworkManager] 发送进入房间请求");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送进入房间请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送准备状态切换请求
    /// </summary>
    public void SendToggleReadyRequest(int playerId, bool ready)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法发送准备状态切换请求");
            return;
        }

        try
        {
            ToggleReadyRequest toggleReadyReq = new ToggleReadyRequest
            {
                PlayerId = playerId,
                Ready = ready
            };

            // 序列化消息体
            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, toggleReadyReq);
                bodyData = ms.ToArray();
            }

            // 发送准备状态切换请求
            SendMessage(Protocols.ToggleReadyRequest, bodyData);
            Debug.Log($"[NetworkManager] 发送准备状态切换请求，准备状态: {ready}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送准备状态切换请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送开始游戏请求
    /// </summary>
    public void SendStartGameRequest(int playerId, string gameMode = "")
    {
        if (!IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法发送开始游戏请求");
            return;
        }

        try
        {
            StartGameRequest startGameReq = new StartGameRequest
            {
                PlayerId = playerId,
                GameMode = string.IsNullOrWhiteSpace(gameMode) ? "game_three" : gameMode.Trim().ToLowerInvariant()
            };

            // 序列化消息体
            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, startGameReq);
                bodyData = ms.ToArray();
            }

            // 发送开始游戏请求
            SendMessage(Protocols.StartGameRequest, bodyData);
            Debug.Log("[NetworkManager] 发送开始游戏请求");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送开始游戏请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Selects the room game mode. The server validates that only the host may change it.
    /// </summary>
    public void SendSelectGameModeRequest(int playerId, string gameMode)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法选择游戏模式");
            return;
        }

        try
        {
            var request = new SelectGameModeRequest
            {
                PlayerId = playerId,
                GameMode = string.IsNullOrWhiteSpace(gameMode) ? "game_three" : gameMode.Trim().ToLowerInvariant()
            };

            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, request);
                bodyData = ms.ToArray();
            }

            SendMessage(Protocols.SelectGameModeRequest, bodyData);
            Debug.Log($"[NetworkManager] 发送游戏模式选择请求: {request.GameMode}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"发送游戏模式选择请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Selects the local player's GamePlayThree ball.
    /// </summary>
    public void SendSelectGameThreeBallRequest(int playerId, string ballId)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法选择 GamePlayThree 小球");
            return;
        }

        try
        {
            var request = new SelectGameThreeBallRequest
            {
                PlayerId = playerId,
                BallId = string.IsNullOrWhiteSpace(ballId) ? "sprinter" : ballId.Trim().ToLowerInvariant()
            };

            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, request);
                bodyData = ms.ToArray();
            }

            SendMessage(Protocols.SelectGameThreeBallRequest, bodyData);
            Debug.Log($"[NetworkManager] 发送 GamePlayThree 小球选择请求: {request.BallId}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"发送 GamePlayThree 小球选择请求失败: {ex.Message}");
        }
    }

    public void SendGameThreeCancelBallSelectionRequest(int playerId)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("Cannot cancel ball selection while disconnected.");
            return;
        }

        try
        {
            using (var stream = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, new GameThreeCancelBallSelectionRequest { PlayerId = playerId });
                SendMessage(Protocols.GameThreeCancelBallSelectionRequest, stream.ToArray());
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to cancel ball selection: {exception.Message}");
        }
    }

    public void SendGameThreeLeaveGameRequest(int playerId)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("Cannot confirm returning to the room while disconnected.");
            return;
        }

        try
        {
            using (var stream = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, new GameThreeLeaveGame { PlayerId = playerId });
                SendMessage(Protocols.GameThreeLeaveGame, stream.ToArray());
            }
            Debug.Log($"[NetworkManager] Confirmed GamePlayThree return to room for player {playerId}.");
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to confirm GamePlayThree return to room: {exception.Message}");
        }
    }

private void OnDestroy()
    {
        // Scene copies can be destroyed during a transition. Their cleanup
        // must not close the persistent NetworkManager connection.
        if (!ReferenceEquals(Instance, this))
        {
            Debug.LogWarning("[NetworkManager] 忽略重复实例的 OnDestroy 清理");
            return;
        }

        Instance = null;
        try
        {
            // 取消UDP事件订阅（在Disconnect之前执行，避免访问已被清理的资源）
            if (udpService != null)
            {
                udpService.OnDataReceived -= HandleUdpData;
                udpService.OnConnectionStateChanged -= HandleUdpConnectionStateChanged;
            }

            Disconnect();

            lock (_queueLock)
            {
                _messageQueue.Clear();
            }

            Debug.Log("[NetworkManager] 已销毁");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"NetworkManager销毁时发生异常: {ex.Message}");
        }
    }
}
