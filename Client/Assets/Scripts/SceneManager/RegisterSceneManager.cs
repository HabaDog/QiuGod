// RegisterSceneManager.cs
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System.Collections;
using System.IO;
using GameNetworkCommon; // 添加GameNetworkCommon命名空间引用

public class RegisterSceneManager : MonoBehaviour
{
    [Header("Register UI Components")]
    [SerializeField] private TMP_InputField accountInputField;
    [SerializeField] private TMP_InputField passwordInputField;
    [SerializeField] private TMP_InputField confirmPasswordInputField;
    [SerializeField] private Button registerButton;
    [SerializeField] private Button backButton;
    [SerializeField] private Button closeButton; // 关闭按钮（右上角叉号）
    private TMP_InputField pendingFocusField;
    private int pendingFocusFrame = -1;
    private string pendingRegistrationAccount = string.Empty;
    private string pendingRegistrationPassword = string.Empty;
    private bool waitingForRegisteredLogin;

    private void Start()
    {
        RebuildPasswordFieldsFromWorkingTemplate();
        ConfigureInputFields();
        AttachInputDiagnostics(accountInputField);
        AttachInputDiagnostics(passwordInputField);
        AttachInputDiagnostics(confirmPasswordInputField);

        // 绑定按钮事件
        if (registerButton != null)
        {
            registerButton.onClick.AddListener(OnRegisterClicked);
        }

        if (backButton != null)
        {
            // 问题二的解决方案：直接跳转，不显示弹窗
            backButton.onClick.AddListener(() => {
                // 直接返回登录场景，不显示弹窗
                if (SceneTransitionManager.Instance != null)
                {
                    SceneTransitionManager.Instance.GoBackToLoginFromRegister();
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
            NetworkManager.Instance.OnRegisterResult += OnRegisterResult;
            NetworkManager.Instance.OnLoginResult += OnRegisteredLoginResult;
        }
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            RequestFocusAtScreenPosition(accountInputField, Input.mousePosition);
            RequestFocusAtScreenPosition(passwordInputField, Input.mousePosition);
            RequestFocusAtScreenPosition(confirmPasswordInputField, Input.mousePosition);
        }
    }

    private void LateUpdate()
    {
        if (pendingFocusField == null || pendingFocusFrame != Time.frameCount)
        {
            return;
        }

        FocusInputField(pendingFocusField);
        StartCoroutine(ConfirmInputFocusNextFrame(pendingFocusField));
        pendingFocusField = null;
        pendingFocusFrame = -1;
    }

    private void ConfigureInputFields()
    {
        ConfigurePasswordField(passwordInputField);
        ConfigurePasswordField(confirmPasswordInputField);
        ConfigureTextField(accountInputField);
    }

    private static void ConfigureTextField(TMP_InputField inputField)
    {
        if (inputField == null)
        {
            return;
        }

        inputField.readOnly = false;
        inputField.interactable = true;
        inputField.lineType = TMP_InputField.LineType.SingleLine;
        inputField.resetOnDeActivation = false;

        // Child text, placeholder, and icon graphics are visual-only. The input
        // root remains the sole pointer target across all Canvas scale factors.
        foreach (var graphic in inputField.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic.gameObject != inputField.gameObject)
            {
                graphic.raycastTarget = false;
            }
        }

        inputField.ForceLabelUpdate();
    }

    private static void AttachInputDiagnostics(TMP_InputField inputField)
    {
        if (inputField == null)
        {
            FileLogger.LogWarning("[RegisterInput] A configured input field is missing.");
            return;
        }

        FileLogger.Log(
            $"[RegisterInput] Ready field={inputField.name} " +
            $"interactable={inputField.interactable} readOnly={inputField.readOnly} " +
            $"contentType={inputField.contentType}.");
        inputField.onSelect.AddListener(_ =>
            FileLogger.Log($"[RegisterInput] Selected field={inputField.name}."));
        inputField.onDeselect.AddListener(_ =>
            FileLogger.Log($"[RegisterInput] Deselected field={inputField.name}."));
        inputField.onValueChanged.AddListener(value =>
            FileLogger.Log($"[RegisterInput] Changed field={inputField.name} length={value.Length}."));
    }

    private static void ConfigurePasswordField(TMP_InputField inputField)
    {
        if (inputField == null)
        {
            return;
        }

        inputField.contentType = TMP_InputField.ContentType.Password;
        ConfigureTextField(inputField);
    }

    private void RebuildPasswordFieldsFromWorkingTemplate()
    {
        passwordInputField = CloneWorkingInputField(passwordInputField, "Password_Input_Large");
        confirmPasswordInputField = CloneWorkingInputField(confirmPasswordInputField, "Password_Comfirm_Large");
    }

    private TMP_InputField CloneWorkingInputField(TMP_InputField originalField, string fieldName)
    {
        if (accountInputField == null || originalField == null || originalField.transform.parent == null)
        {
            Debug.LogWarning($"[RegisterInput] Cannot rebuild {fieldName}; the template or original field is missing.");
            return originalField;
        }

        var originalObject = originalField.gameObject;
        var originalRect = originalObject.transform as RectTransform;
        var cloneObject = Instantiate(accountInputField.gameObject, originalObject.transform.parent, false);
        var cloneField = cloneObject.GetComponent<TMP_InputField>();

        cloneObject.name = fieldName;
        cloneObject.transform.SetSiblingIndex(originalObject.transform.GetSiblingIndex());
        CopyRectTransform(originalRect, cloneObject.transform as RectTransform);
        CopyInputAppearance(originalField, cloneField);
        cloneField.text = string.Empty;
        cloneField.contentType = TMP_InputField.ContentType.Password;

        Destroy(originalObject);
        Debug.Log($"[RegisterInput] Rebuilt {fieldName} from Username_Input_Large.");
        return cloneField;
    }

    private static void CopyRectTransform(RectTransform source, RectTransform destination)
    {
        if (source == null || destination == null)
        {
            return;
        }

        destination.anchorMin = source.anchorMin;
        destination.anchorMax = source.anchorMax;
        destination.pivot = source.pivot;
        destination.anchoredPosition = source.anchoredPosition;
        destination.sizeDelta = source.sizeDelta;
        destination.localRotation = source.localRotation;
        destination.localScale = source.localScale;
    }

    private static void CopyInputAppearance(TMP_InputField source, TMP_InputField destination)
    {
        if (destination == null)
        {
            return;
        }

        var sourceImages = source.GetComponentsInChildren<Image>(true);
        var destinationImages = destination.GetComponentsInChildren<Image>(true);
        var imageCount = Mathf.Min(sourceImages.Length, destinationImages.Length);
        for (var index = 0; index < imageCount; index++)
        {
            destinationImages[index].sprite = sourceImages[index].sprite;
            destinationImages[index].color = sourceImages[index].color;
            destinationImages[index].material = sourceImages[index].material;
            destinationImages[index].type = sourceImages[index].type;
            destinationImages[index].preserveAspect = sourceImages[index].preserveAspect;
        }

        if (source.placeholder is TMP_Text sourcePlaceholder
            && destination.placeholder is TMP_Text destinationPlaceholder)
        {
            destinationPlaceholder.text = sourcePlaceholder.text;
        }
    }

    private void RequestFocusAtScreenPosition(TMP_InputField inputField, Vector2 screenPosition)
    {
        if (inputField == null || !inputField.interactable || inputField.readOnly)
        {
            return;
        }

        var rectTransform = inputField.transform as RectTransform;
        var canvas = inputField.GetComponentInParent<Canvas>();
        var eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        if (rectTransform == null
            || !RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPosition, eventCamera))
        {
            return;
        }

        // EventSystem processes pointer clicks during Update. Deferring activation
        // until LateUpdate prevents its click handling from clearing the selection.
        pendingFocusField = inputField;
        pendingFocusFrame = Time.frameCount;
        FileLogger.Log($"[RegisterInput] Focus requested field={inputField.name} frame={Time.frameCount}.");
    }

    private static void FocusInputField(TMP_InputField inputField)
    {
        if (inputField == null || !inputField.isActiveAndEnabled)
        {
            return;
        }

        EventSystem.current?.SetSelectedGameObject(inputField.gameObject);
        inputField.Select();
        inputField.ActivateInputField();
        FileLogger.Log(
            $"[RegisterInput] Focus applied field={inputField.name} " +
            $"focused={inputField.isFocused} selected={EventSystem.current?.currentSelectedGameObject?.name ?? "<none>"}.");
    }

    private IEnumerator ConfirmInputFocusNextFrame(TMP_InputField inputField)
    {
        yield return null;
        FocusInputField(inputField);
    }

    /// <summary>
    /// 注册按钮点击事件
    /// </summary>
    private void OnRegisterClicked()
    {
        string account = accountInputField?.text ?? "";
        string password = passwordInputField?.text ?? "";
        string confirmPassword = confirmPasswordInputField?.text ?? "";

        // 客户端验证
        if (string.IsNullOrEmpty(account))
        {
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Please enter account", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            return;
        }

        if (account.Length < 3)
        {
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Account must be at least 3 characters", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Please enter password", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            return;
        }

        if (password.Length < 6)
        {
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Password must be at least 6 characters", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
            return;
        }

        // 检查是否需要确认密码
        if (confirmPasswordInputField != null && !string.IsNullOrEmpty(confirmPassword))
        {
            if (password != confirmPassword)
            {
                if (MessagePopup.Instance != null)
                {
                    MessagePopup.Instance.ShowError("Passwords do not match", false);
                }
                else
                {
                    Debug.LogError("MessagePopup instance not found");
                }
                return;
            }
        }

        // 显示注册中弹窗
        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.ShowInfo("Registering account, please wait...", false);
        }
        else
        {
            Debug.LogError("MessagePopup instance not found");
        }

        pendingRegistrationAccount = account;
        pendingRegistrationPassword = password;

        // 发送注册请求到服务器
        SendRegisterRequestToServer(account, password);
    }

    /// <summary>
    /// 发送注册请求到服务器
    /// </summary>
    private void SendRegisterRequestToServer(string account, string password)
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
            return;
        }

        try
        {
            // 创建注册请求消息
            RegisterRequest registerReq = new RegisterRequest
            {
                Username = account,
                Password = password
            };

            // 序列化消息体
            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, registerReq);
                bodyData = ms.ToArray();
            }

            // 发送注册请求
            NetworkManager.Instance.SendGameMessage(Protocols.RegisterRequest, bodyData);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送注册请求失败: {ex.Message}");
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowError("Failed to send register request", false);
            }
            else
            {
                Debug.LogError("MessagePopup instance not found");
            }
        }
    }

    /// <summary>
    /// 清空所有输入框
    /// </summary>
    public void ClearAllInputFields()
    {
        if (accountInputField != null) accountInputField.text = "";
        if (passwordInputField != null) passwordInputField.text = "";
        if (confirmPasswordInputField != null) confirmPasswordInputField.text = "";
    }

    /// <summary>
    /// 处理注册结果
    /// </summary>
    /// <param name="success">注册是否成功</param>
    /// <param name="message">服务器返回的消息</param>
    private void OnRegisterResult(bool success, string message)
    {
        if (!success)
        {
            pendingRegistrationPassword = string.Empty;
            return;
        }

        if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
        {
            pendingRegistrationPassword = string.Empty;
            MessagePopup.Instance?.ShowError("Account created, but the server connection was lost. Please sign in.", false);
            SceneTransitionManager.Instance?.GoBackToLoginFromRegister();
            return;
        }

        waitingForRegisteredLogin = true;
        MessagePopup.Instance?.ShowInfo("Account created. Signing in...", false);

        var loginRequest = new LoginRequest
        {
            Username = pendingRegistrationAccount,
            Password = pendingRegistrationPassword
        };

        try
        {
            using (var stream = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, loginRequest);
                NetworkManager.Instance.SetCurrentLoginRequest(loginRequest);
                NetworkManager.Instance.SendGameMessage(Protocols.LoginRequest, stream.ToArray());
            }
        }
        catch (System.Exception exception)
        {
            waitingForRegisteredLogin = false;
            pendingRegistrationPassword = string.Empty;
            Debug.LogError($"[RegisterSceneManager] Automatic login failed: {exception.Message}");
            MessagePopup.Instance?.ShowError("Account created, but automatic sign-in failed.", false);
        }
    }

    private void OnRegisteredLoginResult(bool success, string message)
    {
        if (!waitingForRegisteredLogin)
        {
            return;
        }

        waitingForRegisteredLogin = false;
        pendingRegistrationPassword = string.Empty;
        if (!success)
        {
            return;
        }

        MessagePopup.Instance?.Hide();
        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.GoToLobbyFromLogin();
        }
        else
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene(SceneNames.Lobby);
        }
    }

    /// <summary>
    /// 关闭按钮点击事件（退出程序）
    /// </summary>
    private void OnCloseButtonClicked()
    {
        Debug.Log("[RegisterSceneManager] 关闭按钮被点击，退出程序");
        
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnDestroy()
    {
        // 清理事件绑定
        if (registerButton != null) registerButton.onClick.RemoveListener(OnRegisterClicked);
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseButtonClicked);
        
        // 取消订阅网络事件
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnRegisterResult -= OnRegisterResult;
            NetworkManager.Instance.OnLoginResult -= OnRegisteredLoginResult;
        }
    }
}
