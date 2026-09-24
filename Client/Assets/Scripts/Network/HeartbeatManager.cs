using UnityEngine;
using System.Collections.Generic;
using System.IO;
using GameNetworkCommon;

public class HeartbeatManager : MonoBehaviour
{
    [Header("��������")]
    // Keep the sample fresh enough for the in-game latency indicator while
    // remaining negligible compared with gameplay traffic.
    public float heartbeatInterval = 1f;
    public float heartbeatTimeout = 15f;
    public bool enableHeartbeat = true;

    [Header("״̬���")]
    [SerializeField] private float lastHeartbeatSendTime = 0f;
    [SerializeField] private float lastHeartbeatResponseTime = 0f;
    [SerializeField] private int currentSequence = 0;
    [SerializeField] private bool waitingForResponse = false;
    [SerializeField] private float currentRtt = 0f;
    [SerializeField] private int consecutiveTimeouts = 0;

    private NetworkManager networkManager;
    private ReconnectManager reconnectManager;
    private Queue<float> rttSamples = new Queue<float>();
    private const int MAX_RTT_SAMPLES = 10;

    public float CurrentRtt => currentRtt;
    public float AverageRtt => GetAverageRtt();
    public bool HasRttSample => rttSamples.Count > 0;
    public bool WaitingForResponse => waitingForResponse;

    [Header("������������")]
    [SerializeField] public bool useUdpForHeartbeat = true;

    void Start()
    {
        networkManager = GetComponent<NetworkManager>();
        reconnectManager = GetComponent<ReconnectManager>();

        if (networkManager == null)
        {
            Debug.LogError("HeartbeatManager ��Ҫ NetworkManager �����");
            enabled = false;
            return;
        }

        // �޸�����ȷ���� NetworkManager �����������¼�
        networkManager.OnConnectionProblemEvent += HandleNetworkConnectionProblem;

        lastHeartbeatResponseTime = Time.time;
        Debug.Log("����������������");
    }

    void OnDestroy()
    {
        // �޸���ȡ���¼����ģ���ֹ�ڴ�й©
        if (networkManager != null)
        {
            networkManager.OnConnectionProblemEvent -= HandleNetworkConnectionProblem;
        }
    }

    void Update()
    {
        if (!enableHeartbeat || networkManager == null || !networkManager.IsConnected)
            return;

        // ��ʱ��������
        if (Time.time - lastHeartbeatSendTime > heartbeatInterval && !waitingForResponse)
        {
            SendHeartbeat();
        }

        // ���������ʱ
        if (waitingForResponse && Time.time - lastHeartbeatSendTime > heartbeatTimeout)
        {
            OnHeartbeatTimeout();
        }
    }

    /// <summary>
    /// ����������
    /// </summary>
    public void SendHeartbeat()
    {
        if (networkManager == null || !networkManager.IsConnected)
        {
            Debug.LogWarning("����δ���ӣ��޷���������");
            return;
        }

        HeartbeatRequest heartbeatReq = new HeartbeatRequest
        {
            ClientTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence = currentSequence++
        };

        using (var ms = new System.IO.MemoryStream())
        {
            ProtoBufHelper.Serialize(ms, heartbeatReq);
            byte[] data = ms.ToArray();

            // TCP is the transport liveness signal. UDP packet loss must not tear down a healthy TCP session.
            networkManager.SendMessage(Protocols.HeartbeatRequest, data);
        }

        lastHeartbeatSendTime = Time.time;
        waitingForResponse = true;

        Debug.Log($"[Heartbeat] �������������к�: {heartbeatReq.Sequence}");
    }

    /// <summary>
    /// ����������Ӧ
    /// </summary>
    public void HandleHeartbeatResponse(BaseMessage msg)
    {
        try
        {
            using (var ms = new System.IO.MemoryStream(msg.Body))
            {
                HeartbeatResponse heartbeatResp = ProtoBufHelper.Deserialize<HeartbeatResponse>(ms);

                long currentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long rttMs = currentTime - heartbeatResp.ClientTimestamp;
                currentRtt = rttMs;

                RecordRttSample((float)rttMs);

                waitingForResponse = false;
                lastHeartbeatResponseTime = Time.time;
                consecutiveTimeouts = 0;

                Debug.Log($"[Heartbeat] �յ�������Ӧ�����к�: {heartbeatResp.Sequence}, RTT: {rttMs}ms");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Heartbeat] ����������Ӧʧ��: {ex.Message}");
        }
    }

    private void OnHeartbeatTimeout()
    {
        Debug.LogWarning($"[Heartbeat] ������ʱ�������Ӧ: {Time.time - lastHeartbeatResponseTime:F1}��ǰ");
        waitingForResponse = false;
        consecutiveTimeouts++;

        if (consecutiveTimeouts < 2)
        {
            Debug.LogWarning("[Heartbeat] First missed heartbeat; keeping the TCP session and retrying");
            return;
        }

        // �޸���ȷ��ͨ�����̵߳���������
        if (MainThreadDispatcher.Instance != null && networkManager != null)
        {
            MainThreadDispatcher.Instance.Enqueue(() => {
                networkManager.NotifyConnectionProblem("HeartbeatTimeout");
            });
        }
        else if (networkManager != null)
        {
            networkManager.NotifyConnectionProblem("HeartbeatTimeout");
        }
    }

    /// <summary>
    /// �����������������¼�
    /// </summary>
    private void HandleNetworkConnectionProblem()
    {
        Debug.Log("[Heartbeat] �յ�������������֪ͨ");
        waitingForResponse = false;
        Debug.Log("[Heartbeat] �ѱ������״̬Ϊ�ȴ�����");
    }

    private void RecordRttSample(float rtt)
    {
        rttSamples.Enqueue(rtt);
        if (rttSamples.Count > MAX_RTT_SAMPLES)
            rttSamples.Dequeue();
    }

    public float GetAverageRtt()
    {
        if (rttSamples.Count == 0) return 0f;

        float sum = 0f;
        foreach (var rtt in rttSamples)
            sum += rtt;

        return sum / rttSamples.Count;
    }

    /// <summary>
    /// ��������״̬������������
    /// </summary>
    public void Reset()
    {
        waitingForResponse = false;
        lastHeartbeatSendTime = 0f;
        // ʹ�ð�ȫ�ķ�ʽ���ó�ʼʱ��
        lastHeartbeatResponseTime = 0f; // ���߱��ֵ�ǰֵ�����´��յ���Ӧʱ����
        rttSamples.Clear();
        currentSequence = 0;
        consecutiveTimeouts = 0;
        lastHeartbeatResponseTime = Time.time;

        Debug.Log("[Heartbeat] ����״̬������");
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus)
        {
            Reset();
        }
        else
        {
            waitingForResponse = false;
        }
    }

    public string GetStatusSummary()
    {
        if (!enableHeartbeat) return "�����ѽ���";
        if (networkManager == null || !networkManager.IsConnected) return "δ����";

        float timeSinceLastResponse = Time.time - lastHeartbeatResponseTime;
        float avgRtt = GetAverageRtt();

        return $"����״̬: {(waitingForResponse ? "�ȴ���Ӧ" : "����")}, " +
               $"�����Ӧ: {timeSinceLastResponse:F1}��ǰ, " +
               $"ƽ���ӳ�: {avgRtt:F0}ms";
    }
}
