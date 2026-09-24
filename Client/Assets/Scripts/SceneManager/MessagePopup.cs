using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class MessagePopup : MonoBehaviour
{
    [Header("UI组件引用")]
    [SerializeField] private GameObject messagePopupPanel;  // 弹窗面板
    [SerializeField] private TMP_Text messageText;          // 消息文本
    [SerializeField] private Button confirmButton;          // 确认按钮

    [Header("弹窗设置")]
    [SerializeField] private float autoHideDelay = 3f;      // 自动隐藏时间（秒）
    [SerializeField] private float fadeDuration = 0.3f;     // 淡入淡出动画时间
    [SerializeField] private bool enableAutoHide = true;    // 启用自动隐藏

    [Header("弹窗类型颜色")]
    [SerializeField] private Color successColor = new Color(0.2f, 0.8f, 0.2f, 1f);    // 绿色
    [SerializeField] private Color errorColor = new Color(0.8f, 0.2f, 0.2f, 1f);      // 红色
    [SerializeField] private Color warningColor = new Color(1f, 0.8f, 0.2f, 1f);      // 黄色
    [SerializeField] private Color infoColor = new Color(0.2f, 0.6f, 1f, 1f);         // 蓝色

    [Header("弹窗类型图标（可选）")]
    [SerializeField] private Sprite successIcon;
    [SerializeField] private Sprite errorIcon;
    [SerializeField] private Sprite warningIcon;
    [SerializeField] private Sprite infoIcon;

    // 私有变量
    private CanvasGroup canvasGroup;
    private Image panelImage;
    private Coroutine autoHideCoroutine;
    private System.Action onConfirmCallback;
    private bool isShowing = false;

    // 单例实例
    public static MessagePopup Instance { get; private set; }

    /***************************
     * 生命周期方法
     ***************************/

    private void Awake()
    {
        // 单例模式实现
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // 初始化组件
            InitializeComponents();

            // 确保弹窗初始隐藏
            HideImmediate();

            Debug.Log("MessagePopup 初始化完成");
        }
        else
        {
            // 如果已存在实例，销毁新创建的这个
            Destroy(gameObject);
        }
    }

    private void InitializeComponents()
    {
        // 获取或添加CanvasGroup组件
        canvasGroup = messagePopupPanel.GetComponent<CanvasGroup>();
        if (canvasGroup == null)
        {
            canvasGroup = messagePopupPanel.AddComponent<CanvasGroup>();
        }

        // 获取Panel的Image组件（用于设置颜色）
        panelImage = messagePopupPanel.GetComponent<Image>();

        // 绑定确认按钮事件
        if (confirmButton != null)
        {
            confirmButton.onClick.AddListener(OnConfirmButtonClicked);
        }

        // 验证所有引用
        if (messagePopupPanel == null)
            Debug.LogError("MessagePopup: messagePopupPanel 引用为空！");
        if (messageText == null)
            Debug.LogError("MessagePopup: messageText 引用为空！");
        if (confirmButton == null)
            Debug.LogError("MessagePopup: confirmButton 引用为空！");
    }

    private void Start()
    {
        // 确保弹窗初始隐藏
        if (messagePopupPanel != null && messagePopupPanel.activeSelf)
        {
            messagePopupPanel.SetActive(false);
        }
    }

    /***************************
     * 公共显示方法
     ***************************/

    /// <summary>
    /// 显示成功消息
    /// </summary>
    public void ShowSuccess(string message, bool autoHide = true)
    {
        ShowMessage(message, MessageType.Success, autoHide);
    }

    /// <summary>
    /// 显示错误消息
    /// </summary>
    public void ShowError(string message, bool autoHide = false)
    {
        ShowMessage(message, MessageType.Error, autoHide);
    }

    /// <summary>
    /// 显示警告消息
    /// </summary>
    public void ShowWarning(string message, bool autoHide = true)
    {
        ShowMessage(message, MessageType.Warning, autoHide);
    }

    /// <summary>
    /// 显示信息消息
    /// </summary>
    public void ShowInfo(string message, bool autoHide = true)
    {
        ShowMessage(message, MessageType.Info, autoHide);
    }

    /// <summary>
    /// 通用显示消息方法
    /// </summary>
    public void ShowMessage(string message, MessageType type = MessageType.Info,
                          bool autoHide = true, System.Action onConfirm = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            Debug.LogWarning("MessagePopup: 尝试显示空消息");
            return;
        }

        if (isShowing)
        {
            // 如果已有弹窗显示，先隐藏它
            HideImmediate();
        }

        // 设置消息内容
        messageText.text = message;

        // 设置弹窗样式
        SetPopupStyle(type);

        // 保存确认回调
        onConfirmCallback = onConfirm;

        // 显示弹窗
        Show();

        // 设置自动隐藏
        if (autoHide && enableAutoHide)
        {
            StartAutoHideTimer();
        }

        Debug.Log($"MessagePopup: 显示 {type} 消息 - {message}");
    }

    /***************************
     * 私有辅助方法
     ***************************/

    /// <summary>
    /// 设置弹窗样式
    /// </summary>
    private void SetPopupStyle(MessageType type)
    {
        // 设置背景颜色
        if (panelImage != null)
        {
            panelImage.color = type switch
            {
                MessageType.Success => successColor * 0.1f,  // 降低透明度
                MessageType.Error => errorColor * 0.1f,
                MessageType.Warning => warningColor * 0.1f,
                MessageType.Info => infoColor * 0.1f,
                _ => infoColor * 0.1f
            };
        }

        // 设置文本颜色
        Color textColor = type switch
        {
            MessageType.Success => successColor,
            MessageType.Error => errorColor,
            MessageType.Warning => warningColor,
            MessageType.Info => infoColor,
            _ => Color.white
        };

        messageText.color = textColor;

        // 这里可以添加设置图标的代码（如果您添加了图标Image组件）
        // if (iconImage != null) { ... }
    }

    /// <summary>
    /// 显示弹窗（带淡入动画）
    /// </summary>
    private void Show()
    {
        if (messagePopupPanel == null) return;

        messagePopupPanel.SetActive(true);
        isShowing = true;

        // 开始淡入动画
        StartCoroutine(FadeIn());
    }

    /// <summary>
    /// 隐藏弹窗（带淡出动画）
    /// </summary>
    public void Hide()
    {
        if (!isShowing || messagePopupPanel == null) return;

        // 停止自动隐藏计时器
        if (autoHideCoroutine != null)
        {
            StopCoroutine(autoHideCoroutine);
            autoHideCoroutine = null;
        }

        // 开始淡出动画
        StartCoroutine(FadeOut());
    }

    /// <summary>
    /// 立即隐藏弹窗（无动画）
    /// </summary>
    public void HideImmediate()
    {
        if (messagePopupPanel == null) return;

        messagePopupPanel.SetActive(false);
        isShowing = false;

        if (canvasGroup != null)
            canvasGroup.alpha = 0f;
    }

    /// <summary>
    /// 确认按钮点击事件
    /// </summary>
    private void OnConfirmButtonClicked()
    {
        // 执行确认回调
        onConfirmCallback?.Invoke();

        // 隐藏弹窗
        Hide();

        Debug.Log("MessagePopup: 用户点击了确认按钮");
    }

    /// <summary>
    /// 开始自动隐藏计时器
    /// </summary>
    private void StartAutoHideTimer()
    {
        if (autoHideCoroutine != null)
        {
            StopCoroutine(autoHideCoroutine);
        }

        autoHideCoroutine = StartCoroutine(AutoHideTimer());
    }

    /***************************
     * 协程方法
     ***************************/

    /// <summary>
    /// 淡入动画
    /// </summary>
    private IEnumerator FadeIn()
    {
        if (canvasGroup == null) yield break;

        float timer = 0f;
        canvasGroup.alpha = 0f;

        while (timer < fadeDuration)
        {
            timer += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(0f, 1f, timer / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = 1f;
    }

    /// <summary>
    /// 淡出动画
    /// </summary>
    private IEnumerator FadeOut()
    {
        if (canvasGroup == null) yield break;

        float timer = 0f;
        canvasGroup.alpha = 1f;

        while (timer < fadeDuration)
        {
            timer += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(1f, 0f, timer / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = 0f;
        messagePopupPanel.SetActive(false);
        isShowing = false;
    }

    /// <summary>
    /// 自动隐藏计时器
    /// </summary>
    private IEnumerator AutoHideTimer()
    {
        yield return new WaitForSeconds(autoHideDelay);
        Hide();
    }

    /***************************
     * 工具方法
     ***************************/

    /// <summary>
    /// 检查弹窗是否正在显示
    /// </summary>
    public bool IsShowing()
    {
        return isShowing;
    }

    /// <summary>
    /// 设置自动隐藏时间
    /// </summary>
    public void SetAutoHideDelay(float delay)
    {
        autoHideDelay = Mathf.Max(0.5f, delay);
    }

    /// <summary>
    /// 设置弹窗文本最大宽度（修复版）
    /// </summary>
    public void SetTextMaxWidth(float maxWidth)
    {
        if (messageText != null)
        {
            // 1. 启用自动换行
            messageText.enableWordWrapping = true;

            // 2. 设置溢出模式为省略号（推荐用于消息弹窗）
            messageText.overflowMode = TextOverflowModes.Ellipsis;

            // 3. 调整文本区域大小
            var rectTransform = messageText.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.sizeDelta = new Vector2(maxWidth, rectTransform.sizeDelta.y);
            }

            Debug.Log($"文本区域设置完成: 最大宽度={maxWidth}, 换行已启用");
        }
    }

    /// <summary>
    /// 调试信息
    /// </summary>
    public void PrintDebugInfo()
    {
        Debug.Log($"=== MessagePopup 调试信息 ===");
        Debug.Log($"单例实例: {Instance != null}");
        Debug.Log($"弹窗显示状态: {isShowing}");
        Debug.Log($"CanvasGroup: {canvasGroup != null}");
        Debug.Log($"面板引用: {messagePopupPanel != null}");
        Debug.Log($"文本引用: {messageText != null}");
        Debug.Log($"按钮引用: {confirmButton != null}");
        Debug.Log($"===============================");
    }
}

/// <summary>
/// 消息类型枚举
/// </summary>
public enum MessageType
{
    Success,    // 成功
    Error,      // 错误
    Warning,    // 警告
    Info        // 信息
}