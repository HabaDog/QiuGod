using System;
using System.Collections;
using UnityEngine;

public class ReconnectManager : MonoBehaviour
{
    [Header("Reconnect settings")]
    [Tooltip("Fast retry attempts before switching to lower-frequency recovery attempts.")]
    public int maxReconnectAttempts = 5;
    [Tooltip("Delay between the initial fast reconnect attempts.")]
    public float reconnectInterval = 1f;
    [Tooltip("Total time that an authenticated client will keep attempting session recovery.")]
    public float sessionResumeWindowSeconds = 60f;
    [Tooltip("Delay between recovery attempts after the fast retry phase.")]
    public float longReconnectInterval = 3f;
    public float resumeResponseTimeout = 5f;

    [Header("State")]
    [SerializeField] private int currentAttempt;
    [SerializeField] private bool isReconnecting;
    [SerializeField] private bool hasReconnectFailed;
    [SerializeField] private string lastFailureReason = string.Empty;

    private NetworkManager networkManager;
    private HeartbeatManager heartbeatManager;
    private Coroutine reconnectCoroutine;

    public event Action<int> OnReconnectAttempt;
    public event Action OnReconnectSuccess;
    public event Action OnReconnectFailed;

    public bool IsReconnecting => isReconnecting;
    public bool HasReconnectFailed => hasReconnectFailed;
    public int CurrentAttempt => currentAttempt;
    public int MaxAttempts => maxReconnectAttempts;
    public string LastFailureReason => lastFailureReason;

    private void Start()
    {
        networkManager = GetComponent<NetworkManager>();
        heartbeatManager = GetComponent<HeartbeatManager>();
        if (networkManager == null)
        {
            Debug.LogError("[Reconnect] ReconnectManager must be attached to the NetworkManager GameObject");
            enabled = false;
            return;
        }

        networkManager.OnConnectionProblemEvent += HandleConnectionProblem;
    }

    private void OnDestroy()
    {
        if (networkManager != null)
        {
            networkManager.OnConnectionProblemEvent -= HandleConnectionProblem;
        }
        StopReconnect();
    }

    private void HandleConnectionProblem()
    {
        if (hasReconnectFailed || isReconnecting || networkManager == null || networkManager.IsConnected)
        {
            return;
        }

        Debug.Log("[Reconnect] Transport disconnected; starting reconnect flow");
        MessagePopup.Instance?.ShowInfo("Connection lost. Reconnecting...", false);
        StartReconnect(requireSessionResume: networkManager.HasResumableSession);
    }

    public void StartReconnect(bool requireSessionResume = false)
    {
        if (hasReconnectFailed || isReconnecting || networkManager == null)
        {
            return;
        }

        if (reconnectCoroutine != null)
        {
            StopCoroutine(reconnectCoroutine);
        }
        reconnectCoroutine = StartCoroutine(ReconnectRoutine(requireSessionResume));
    }

    private IEnumerator ReconnectRoutine(bool requiresSessionResume)
    {
        isReconnecting = true;
        currentAttempt = 0;
        lastFailureReason = string.Empty;

        if (!requiresSessionResume)
        {
            Debug.Log("[Reconnect] Restoring transport only; no authenticated session needs to be resumed.");
        }

        var recoveryDeadline = requiresSessionResume
            ? Time.realtimeSinceStartup + Mathf.Max(1f, sessionResumeWindowSeconds)
            : Time.realtimeSinceStartup + Mathf.Max(1f, maxReconnectAttempts * reconnectInterval);

        while (isReconnecting && Time.realtimeSinceStartup < recoveryDeadline)
        {
            currentAttempt++;
            OnReconnectAttempt?.Invoke(currentAttempt);
            var isFastAttempt = currentAttempt <= maxReconnectAttempts;
            Debug.Log($"[Reconnect] {(isFastAttempt ? "Fast" : "Long")}" +
                $" attempt {currentAttempt}; remaining {Mathf.Max(0f, recoveryDeadline - Time.realtimeSinceStartup):F0}s");

            if (currentAttempt > 1)
            {
                var interval = isFastAttempt ? reconnectInterval : longReconnectInterval;
                yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, interval));
            }

            if (networkManager.TryConnect())
            {
                if (!requiresSessionResume)
                {
                    HandleReconnectSuccess();
                    yield break;
                }

                bool resumeCompleted = false;
                bool resumeSucceeded = false;
                networkManager.ResumeSession(success =>
                {
                    resumeSucceeded = success;
                    resumeCompleted = true;
                });

                float deadline = Time.realtimeSinceStartup + resumeResponseTimeout;
                while (!resumeCompleted
                    && networkManager.IsConnected
                    && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (resumeCompleted && resumeSucceeded)
                {
                    HandleReconnectSuccess();
                    yield break;
                }

                // A process restart drops the in-memory gateway session. The TCP
                // transport is healthy, but this client must not be treated as logged in.
                if (!networkManager.HasResumableSession)
                {
                    var failure = string.IsNullOrWhiteSpace(networkManager.LastSessionResumeFailure)
                        ? "The session resume window has expired. Please sign in again."
                        : networkManager.LastSessionResumeFailure;
                    HandleReconnectFailure(failure);
                    yield break;
                }

                Debug.LogWarning("[Reconnect] TCP connected but logical session resume did not complete");
                networkManager.Disconnect();
            }
        }

        HandleReconnectFailure(requiresSessionResume
            ? "The 60-second session resume window has expired. Please sign in again."
            : "Unable to connect to the game server.");
    }

    private void HandleReconnectSuccess()
    {
        isReconnecting = false;
        hasReconnectFailed = false;
        reconnectCoroutine = null;
        heartbeatManager?.Reset();
        MessagePopup.Instance?.Hide();
        OnReconnectSuccess?.Invoke();
        Debug.Log("[Reconnect] Transport and player session restored");
    }

    private void HandleReconnectFailure(string reason)
    {
        isReconnecting = false;
        hasReconnectFailed = true;
        reconnectCoroutine = null;
        lastFailureReason = string.IsNullOrWhiteSpace(reason) ? "Reconnect failed." : reason;
        OnReconnectFailed?.Invoke();
        Debug.LogError($"[Reconnect] Failed after {currentAttempt} attempts: {lastFailureReason}");
    }

    public void StopReconnect()
    {
        if (reconnectCoroutine != null)
        {
            StopCoroutine(reconnectCoroutine);
            reconnectCoroutine = null;
        }
        isReconnecting = false;
    }

    public void ResetReconnectState()
    {
        StopReconnect();
        currentAttempt = 0;
        hasReconnectFailed = false;
        lastFailureReason = string.Empty;
    }

    public string GetReconnectStatus()
    {
        if (hasReconnectFailed) return "Reconnect failed";
        if (isReconnecting) return $"Attempt {currentAttempt}/{maxReconnectAttempts}";
        return "Not reconnecting";
    }
}
