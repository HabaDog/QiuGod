using UnityEngine;
using UnityEngine.UI;

public class UdpTest : MonoBehaviour
{
    [Header("组件引用")]
    public NetworkManager networkManager;
    public HeartbeatManager heartbeatManager;

    [Header("UI 元素")]
    public Text statusText;
    public Text tcpStatusText;
    public Text udpStatusText;
    public Text rttText;
    public Button sendHeartbeatButton;
    public Button toggleProtocolButton;

    private bool useUdpForHeartbeat = true;

    void Start()
    {
        // 自动获取引用（如果未在 Inspector 中赋值）
        if (networkManager == null)
            networkManager = FindObjectOfType<NetworkManager>();
        if (heartbeatManager == null)
            heartbeatManager = FindObjectOfType<HeartbeatManager>();

        // 绑定按钮事件
        sendHeartbeatButton.onClick.AddListener(ManualSendHeartbeat);
        toggleProtocolButton.onClick.AddListener(ToggleProtocol);

        Debug.Log("UDP 测试脚本已启动");
    }

    void Update()
    {
        UpdateStatusUI();
    }

    private void UpdateStatusUI()
    {
        if (networkManager == null) return;

        // 更新 TCP 状态
        tcpStatusText.text = $"TCP: {(networkManager.IsConnected ? "已连接" : "未连接")}";
        tcpStatusText.color = networkManager.IsConnected ? Color.green : Color.red;

        // 更新 UDP 状态
        bool udpConnected = networkManager.IsUdpEnabled; // 假设您在 NetworkManager 中实现了此属性
        udpStatusText.text = $"UDP: {(udpConnected ? "已连接" : "未连接")}";
        udpStatusText.color = udpConnected ? Color.green : Color.red;

        // 更新整体状态
        statusText.text = networkManager.IsConnected ? "网络正常" : "网络断开";
        statusText.color = networkManager.IsConnected ? Color.green : Color.red;

        // 更新延迟信息
        if (heartbeatManager != null)
        {
            float avgRtt = heartbeatManager.GetAverageRtt(); // 假设 HeartbeatManager 有此方法
            rttText.text = $"延迟: {avgRtt:F0}ms";
            rttText.color = avgRtt < 100 ? Color.green : (avgRtt < 300 ? Color.yellow : Color.red);
        }
    }

    private void ManualSendHeartbeat()
    {
        if (heartbeatManager != null)
        {
            heartbeatManager.SendHeartbeat();
            Debug.Log("手动发送心跳包");
        }
    }

    private void ToggleProtocol()
    {
        useUdpForHeartbeat = !useUdpForHeartbeat;
        heartbeatManager.useUdpForHeartbeat = useUdpForHeartbeat; // 控制心跳协议选择
        Debug.Log($"心跳协议切换为: {(useUdpForHeartbeat ? "UDP" : "TCP")}");
    }
}