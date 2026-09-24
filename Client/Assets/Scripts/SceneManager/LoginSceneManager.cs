// LoginSceneManager.cs
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.IO;
using GameNetworkCommon; // 添加GameNetworkCommon命名空间引用

public class LoginSceneManager : MonoBehaviour
{
    private const string TitleSpriteResourcePath = "GameUI/Brand/BallGodTitle";

    [Header("Login UI Components")]
    [SerializeField] private TMP_InputField accountInputField;
    [SerializeField] private TMP_InputField passwordInputField;
    [SerializeField] private Button loginButton;
    [SerializeField] private Button registerButton;
    [SerializeField] private Button closeButton; // 关闭按钮（右上角叉号）

    [Header("Network Status")]
    [SerializeField] private GameObject networkStatusPanel;
    [SerializeField] private TMP_Text networkStatusText;

    private bool isConnecting;

    private void Start()
    {
        EnsureGameTitle();
        ConfigurePasswordField();
        ConfigureLanIdentityUi();
        
        // 绑定按钮事件
        if (loginButton != null)
        {
            loginButton.onClick.AddListener(OnLoginClicked);
        }

        if (registerButton != null)
        {
            // 问题二的解决方案：直接跳转，不显示弹窗
            registerButton.onClick.AddListener(() => {
                // 直接跳转到注册场景，不显示弹窗
                if (SceneTransitionManager.Instance != null)
                {
                    SceneTransitionManager.Instance.GoToRegisterFromLogin();
                }
            });
        }

        // 绑定关闭按钮事件（退出程序）
        if (closeButton != null)
        {
            closeButton.onClick.AddListener(OnCloseButtonClicked);
        }

        // 订阅网络事件
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnLoginResult += OnLoginResult;
        }

        EnsureServerConnection(false, null, null);
    }

    private void ConfigurePasswordField()
    {
        if (passwordInputField == null)
        {
            Debug.LogWarning("[LoginSceneManager] Password input field is not assigned.");
            return;
        }

        // The scene asset was authored as a standard TMP field, which made the
        // login password visible in builds. Configure it at runtime as well so
        // hot-updated and freshly built clients behave identically.
        passwordInputField.contentType = TMP_InputField.ContentType.Password;
        passwordInputField.inputType = TMP_InputField.InputType.Password;
        passwordInputField.lineType = TMP_InputField.LineType.SingleLine;
        passwordInputField.asteriskChar = '\u2022';
        passwordInputField.ForceLabelUpdate();
    }

    private void ConfigureLanIdentityUi()
    {
        if (accountInputField != null)
        {
            if (accountInputField.placeholder is TMP_Text placeholder)
            {
                placeholder.text = "NICKNAME";
            }
            accountInputField.characterLimit = 24;
        }

        if (passwordInputField != null)
        {
            passwordInputField.gameObject.SetActive(false);
        }

        if (registerButton != null)
        {
            registerButton.gameObject.SetActive(false);
        }
    }

    private void EnsureGameTitle()
    {
        var canvas = accountInputField != null
            ? accountInputField.GetComponentInParent<Canvas>()
            : FindObjectOfType<Canvas>();
        if (canvas == null || canvas.transform.Find("GameTitle") != null)
        {
            return;
        }

        var titleRoot = new GameObject("GameTitle", typeof(RectTransform));
        titleRoot.transform.SetParent(canvas.transform, false);
        titleRoot.transform.SetAsLastSibling();
        var titleRect = titleRoot.GetComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0.5f, 1f);
        titleRect.anchorMax = new Vector2(0.5f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.anchoredPosition = new Vector2(0f, -42f);
        titleRect.sizeDelta = new Vector2(900f, 150f);

        var titleSprite = Resources.Load<Sprite>(TitleSpriteResourcePath);
        if (titleSprite != null)
        {
            var image = titleRoot.AddComponent<Image>();
            image.sprite = titleSprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return;
        }

        CreateTitleText(titleRoot.transform, "TitleShadow", new Vector2(5f, -6f),
            new Color(0.015f, 0.025f, 0.08f, 0.9f), 76f);
        var title = CreateTitleText(titleRoot.transform, "TitleText", Vector2.zero,
            new Color(1f, 0.78f, 0.2f, 1f), 76f);
        title.outlineColor = new Color32(35, 94, 168, 255);
        title.outlineWidth = 0.22f;
    }

    private static TextMeshProUGUI CreateTitleText(
        Transform parent,
        string objectName,
        Vector2 offset,
        Color color,
        float fontSize)
    {
        var titleObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        titleObject.transform.SetParent(parent, false);
        var rect = titleObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = offset;
        rect.offsetMax = offset;

        var text = titleObject.GetComponent<TextMeshProUGUI>();
        text.text = "球神，启动！！";
        text.fontSize = fontSize;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.color = color;
        text.characterSpacing = 0f;
        text.enableAutoSizing = true;
        text.fontSizeMin = 42f;
        text.fontSizeMax = fontSize;
        text.raycastTarget = false;
        RuntimeUiFont.Apply(text);
        return text;
    }

    /// <summary>
    /// 登录按钮点击事件
    /// </summary>
    private void OnLoginClicked()
    {
        string account = accountInputField?.text ?? "";
        string password = "";

        // 客户端验证
        if (string.IsNullOrEmpty(account))
        {
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Please enter a nickname", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            return;
        }

        password = "";

        if (NetworkManager.Instance == null)
        {
            MessagePopup.Instance?.ShowError("Network system is unavailable", false);
            UpdateNetworkStatus("Network unavailable");
            return;
        }

        if (!NetworkManager.Instance.IsConnected)
        {
            EnsureServerConnection(true, account, password);
            return;
        }

        BeginLogin(account, password);
    }

    private void BeginLogin(string account, string password)
    {
        MessagePopup.Instance?.ShowInfo("Joining LAN room service...", false);
        UpdateNetworkStatus("Connecting to server...");
        SendLoginRequestToServer(account, password);
    }

    private void EnsureServerConnection(bool loginAfterConnect, string account, string password)
    {
        var network = NetworkManager.Instance;
        if (network == null)
        {
            if (loginButton != null) loginButton.interactable = false;
            UpdateNetworkStatus("Network unavailable");
            return;
        }

        if (network.IsConnected)
        {
            if (loginButton != null) loginButton.interactable = true;
            UpdateNetworkStatus("Ready to enter nickname");
            if (loginAfterConnect)
            {
                BeginLogin(account, password);
            }
            return;
        }

        if (isConnecting)
        {
            return;
        }

        isConnecting = true;
        if (loginButton != null) loginButton.interactable = false;
        UpdateNetworkStatus("Connecting to server...");
        network.AttemptServerConnection(success =>
        {
            if (this == null)
            {
                return;
            }

            isConnecting = false;
            if (loginButton != null) loginButton.interactable = true;
            if (!success)
            {
                UpdateNetworkStatus("Connection failed - click Login to retry");
                if (loginAfterConnect)
                {
                    MessagePopup.Instance?.ShowError("Not connected to server", false);
                }
                return;
            }

            UpdateNetworkStatus("Ready to enter nickname");
            if (loginAfterConnect)
            {
                BeginLogin(account, password);
            }
        });
    }

    /// <summary>
    /// 向服务器发送登录请求
    /// </summary>
    private void SendLoginRequestToServer(string account, string password)
    {
        // 检查网络连接
        if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
        {
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Not connected to server", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            UpdateNetworkStatus("Connection failed");
            return;
        }

        try
        {
            // LoginRequest is retained as the wire-compatible LAN identity request.
            LoginRequest loginReq = new LoginRequest
            {
                Username = account,
                Password = password
            };

            // 序列化消息体
            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, loginReq);
                bodyData = ms.ToArray();
            }

            // 通知NetworkManager保存当前登录请求，以便在响应时获取用户名
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.SetCurrentLoginRequest(loginReq);
                Debug.Log($"[LoginSceneManager] 已发送局域网昵称请求 - Nickname: {loginReq.Username}");
            }
            
            // 发送登录请求
            NetworkManager.Instance.SendGameMessage(Protocols.LoginRequest, bodyData);
            UpdateNetworkStatus("Login request sent, waiting for response...");
            
            // 清空输入框以保护用户密码安全
            ClearInputFields();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送登录请求失败: {ex.Message}");
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Failed to send login request", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            UpdateNetworkStatus("Send request failed");
        }
    }

    /// <summary>
    /// 更新网络状态显示
    /// </summary>
    private void UpdateNetworkStatus(string status)
    {
        if (networkStatusPanel != null)
        {
            networkStatusPanel.SetActive(!string.IsNullOrEmpty(status));
        }

        if (networkStatusText != null)
        {
            networkStatusText.text = status;
        }
    }

    /// <summary>
    /// 清空输入框
    /// </summary>
    public void ClearInputFields()
    {
        if (accountInputField != null) accountInputField.text = "";
        if (passwordInputField != null) passwordInputField.text = "";
    }

    /// <summary>
    /// 处理登录结果
    /// </summary>
    /// <param name="success">登录是否成功</param>
    /// <param name="message">服务器返回的消息</param>
    private void OnLoginResult(bool success, string message)
    {
        if (success)
        {
            Debug.Log("[LoginSceneManager] 登录成功，准备恢复上次所在流程");
            
            // 登录成功，跳转到房间场景
            if (SceneTransitionManager.Instance != null)
            {
                // 延迟一小段时间确保所有状态都已正确设置
                StartCoroutine(DelayedGoToRoom());
            }
        }
        else
        {
            Debug.LogWarning("[LoginSceneManager] 登录失败: " + message);
        }
        // 登录失败已经在NetworkManager中处理过弹窗提示
    }
    
    /// <summary>
    /// 延迟跳转到房间场景，确保状态正确
    /// </summary>
    private System.Collections.IEnumerator DelayedGoToRoom()
    {
        // Authentication state is committed before OnLoginResult is raised.
        // One frame is enough for the success UI to settle without adding input lag.
        yield return null;
        
        if (NetworkManager.Instance != null && NetworkManager.Instance.IsLoginGameRecoveryPending)
        {
            Debug.Log("[LoginSceneManager] Login recovery is entering the active game; lobby transition skipped.");
            yield break;
        }

        if (NetworkManager.Instance != null && NetworkManager.Instance.IsLoginRoomRecoveryPending)
        {
            Debug.Log("[LoginSceneManager] Login recovery is returning to the previous room.");
            SceneTransitionManager.Instance.GoToRoomFromLogin();
            yield break;
        }

        if (NetworkManager.Instance != null
            && string.Equals(NetworkManager.Instance.LoginDestinationSceneName, "BallSelection", System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log("[LoginSceneManager] Login recovery is returning to ball selection.");
            BallSelectionEntryContext.SetReturnScene(SceneNames.Lobby);
            GamePlayThree.GameThreeMatchOriginContext.IsMatchmaking = true;
            SceneTransitionManager.Instance.LoadScene("BallSelection");
            yield break;
        }

        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.GoToLobbyFromLogin();
        }
        else
        {
            Debug.LogError("[LoginSceneManager] SceneTransitionManager.Instance 为 null，无法跳转到房间场景");
        }
    }

    /// <summary>
    /// 关闭按钮点击事件（退出程序）
    /// </summary>
    private void OnCloseButtonClicked()
    {
        Debug.Log("[LoginSceneManager] 关闭按钮被点击，退出程序");
        
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnDestroy()
    {
        // 清理事件绑定
        if (loginButton != null) loginButton.onClick.RemoveListener(OnLoginClicked);
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseButtonClicked);
        
        // 取消订阅网络事件
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnLoginResult -= OnLoginResult;
        }
    }
}
