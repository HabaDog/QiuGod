using UnityEngine;
using GameNetworkCommon; // 添加协议命名空间

/// <summary>
/// 玩家数据管理器 - 用于存储当前登录玩家的信息
/// </summary>
public class PlayerDataManager : MonoBehaviour
{
    public static PlayerDataManager Instance { get; private set; }

    // 玩家信息
    public int PlayerId { get; private set; } = 0;
    public string Username { get; private set; } = "";
    public int SlotIndex { get; private set; } = -1; // 玩家在游戏中的槽位索引(0-3)
    public int RealPlayerCount { get; private set; } = 1; // 房间内真实玩家数量（非AI）

    // 玩家是否已登录
    public bool IsLoggedIn => !string.IsNullOrEmpty(Username) && PlayerId > 0;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// 设置玩家登录信息
    /// </summary>
    /// <param name="playerId">玩家ID</param>
    /// <param name="username">用户名</param>
    public void SetPlayerInfo(int playerId, string username)
    {
        PlayerId = playerId;
        Username = username;
        Debug.Log($"[PlayerDataManager] 玩家信息已设置 - ID: {PlayerId}, Username: {Username}");
        
        // 验证设置是否成功
        if (PlayerId <= 0 || string.IsNullOrEmpty(Username))
        {
            Debug.LogWarning("[PlayerDataManager] 玩家信息设置不完整或无效");
        }
    }

    /// <summary>
    /// 设置玩家槽位索引（由服务端分配）
    /// </summary>
    public void SetSlotIndex(int slotIndex)
    {
        SlotIndex = slotIndex;
        Debug.Log($"[PlayerDataManager] SlotIndex set to: {SlotIndex}");
    }
    
    /// <summary>
    /// 设置真实玩家数量（房间内非AI的玩家数）
    /// </summary>
    public void SetRealPlayerCount(int count)
    {
        RealPlayerCount = count;
        Debug.Log($"[PlayerDataManager] RealPlayerCount set to: {RealPlayerCount}");
    }

    /// <summary>
    /// 清除玩家登录信息（登出时调用）
    /// </summary>
    public void ClearPlayerInfo()
    {
        PlayerId = 0;
        Username = "";
        SlotIndex = -1;
        RealPlayerCount = 1;
        Debug.Log("[PlayerDataManager] 玩家信息已清除");
    }

    /// <summary>
    /// 获取当前玩家的登出请求数据
    /// </summary>
    /// <returns>LogoutRequest对象</returns>
    public LogoutRequest GetLogoutRequest()
    {
        return new LogoutRequest
        {
            PlayerId = PlayerId,
            Username = Username
        };
    }
}