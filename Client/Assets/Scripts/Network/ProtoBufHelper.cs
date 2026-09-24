using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using GameNetworkCommon;
using ProtoBuf.Meta;

/// <summary>
/// ProtoBuf序列化辅助类，统一处理IL2CPP和Mono环境下的序列化
/// 关键：始终使用RuntimeTypeModel并预编译，避免IL2CPP下的反射问题
/// </summary>
public static class ProtoBufHelper
{
    private static RuntimeTypeModel _model;
    private static bool _modelInitialized = false;
    private static bool _warmupAttempted = false;
    private static readonly object _lockObject = new object();
    
    /// <summary>
    /// 获取或初始化RuntimeTypeModel
    /// 这是解决IL2CPP下GetTypeModifiers错误的关键
    /// </summary>
    private static RuntimeTypeModel Model
    {
        get
        {
            if (_model == null || !_modelInitialized)
            {
                lock (_lockObject)
                {
                    if (_model == null)
                    {
                        _model = RuntimeTypeModel.Default;
                        // IL2CPP does not support Reflection.Emit/DynamicMethod. Leave the
                        // protobuf-net model in interpreter mode on every player platform.
                        _model.AutoCompile = false;
                        // 注意：RuntimeTypeModel.Default 必须允许缺失类型
                        // 但我们会预先添加所有类型，所以不会触发反射
                        _model.AutoAddMissingTypes = true; // 必须为 true（protobuf-net 的限制）
                    }
                    
                    if (!_modelInitialized)
                    {
                        // 确保ProtoBufPrebuildHelper已经初始化（只尝试一次）
                        if (!_warmupAttempted)
                        {
                            _warmupAttempted = true;
                            try
                            {
                                ProtoBufPrebuildHelper.WarmupSerializers();
                            }
                            catch (Exception warmupEx)
                            {
                                // 静默失败，让后续的初始化处理
                                Debug.LogWarning($"[ProtoBufHelper] WarmupSerializers failed: {warmupEx.Message}");
                            }
                        }
                        
                        InitializeModel();
                        _modelInitialized = true;
                    }
                }
            }
            
            return _model;
        }
    }
    
    /// <summary>
    /// 初始化模型，确保所有类型都被注册并预编译
    /// 这是避免运行时反射的关键步骤
    /// </summary>
    private static void InitializeModel()
    {
        try
        {
            FileLogger.Log("[ProtoBufHelper] Initializing RuntimeTypeModel for IL2CPP compatibility...");
            Debug.Log("[ProtoBufHelper] Initializing RuntimeTypeModel for IL2CPP compatibility...");
            
            // 关键：启用AutoCompile和AutoAddMissingTypes
            // 注意：RuntimeTypeModel.Default 必须允许缺失类型，这是 protobuf-net 的限制
            // 但我们会预先添加所有类型，这样即使 AutoAddMissingTypes=true，也不会触发反射
            _model.AutoCompile = false;
            _model.AutoAddMissingTypes = true; // 必须为 true（protobuf-net 的限制）
            
            FileLogger.Log($"[ProtoBufHelper] RuntimeTypeModel config: AutoCompile={_model.AutoCompile}, AutoAddMissingTypes={_model.AutoAddMissingTypes}");
            
            // ProtoBufPrebuildHelper.WarmupSerializers() 已经在 Model getter 中调用了
            // 这里只需要确保模型配置正确
            
            FileLogger.Log("[ProtoBufHelper] RuntimeTypeModel initialized successfully (types should be pre-registered)");
            Debug.Log("[ProtoBufHelper] RuntimeTypeModel initialized successfully");
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "ProtoBufHelper.InitializeModel");
            FileLogger.LogError($"[ProtoBufHelper] Failed to initialize RuntimeTypeModel: {ex.Message}\n{ex.StackTrace}");
            Debug.LogError($"[ProtoBufHelper] Failed to initialize RuntimeTypeModel: {ex.Message}\n{ex.StackTrace}");
            // 即使失败也继续，让AutoCompile处理
        }
    }
    
    /// <summary>
    /// 序列化对象
    /// 始终使用RuntimeTypeModel，确保使用预编译的序列化器
    /// </summary>
    public static void Serialize<T>(Stream destination, T instance)
    {
        try
        {
            // 始终使用RuntimeTypeModel，确保使用预编译的序列化器
            Model.Serialize(destination, instance);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProtoBufHelper] Serialize failed for {typeof(T).Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }
    
    /// <summary>
    /// 反序列化对象
    /// 始终使用RuntimeTypeModel，确保使用预编译的序列化器
    /// </summary>
    public static T Deserialize<T>(Stream source)
    {
        try
        {
            FileLogger.Log($"[ProtoBufHelper] Deserializing {typeof(T).Name}, stream position: {source.Position}, length: {source.Length}");
            
            // 记录原始数据（用于调试）
            MemoryStream memoryStream = source as MemoryStream;
            if (memoryStream != null)
            {
                long originalPosition = memoryStream.Position;
                byte[] data = memoryStream.ToArray();
                FileLogger.Log($"[ProtoBufHelper] Stream data length: {data.Length}, position: {originalPosition}");
                // 只记录前100字节，避免日志过大
                int logLength = Math.Min(100, data.Length);
                string hexData = BitConverter.ToString(data, 0, logLength);
                FileLogger.Log($"[ProtoBufHelper] First {logLength} bytes (hex): {hexData}");
                memoryStream.Position = originalPosition; // 恢复位置

                // GamePlayThree uses an explicit wire parser on every runtime.
                // This keeps Editor and IL2CPP players on the same code path and
                // avoids protobuf-net's unsupported runtime reflection calls.
                if (ProtoBufManualParser.TryParseGameThree(data, out T manualResult))
                {
                    FileLogger.Log($"[ProtoBufHelper] Deserialized {typeof(T).Name} with the AOT-safe wire parser");
                    return manualResult;
                }
            }
            
            // 确保类型已添加到模型（特别是对于 EnterRoomResponse）
            if (typeof(T) == typeof(EnterRoomResponse))
            {
                try
                {
                    FileLogger.Log("[ProtoBufHelper] Ensuring EnterRoomResponse is in model before deserialization...");
                    var metaType = Model.Add(typeof(EnterRoomResponse), true);
                    if (metaType != null)
                    {
                        FileLogger.Log("[ProtoBufHelper] EnterRoomResponse is in model");
                        
                        // 检查字段配置
                        try
                        {
                            var fields = metaType.GetFields();
                            FileLogger.Log($"[ProtoBufHelper] EnterRoomResponse has {fields?.Length ?? 0} fields configured before deserialization");
                            if (fields == null || fields.Length == 0)
                            {
                                FileLogger.LogError("[ProtoBufHelper] ERROR: EnterRoomResponse has no fields configured! Deserialization will fail.");
                                FileLogger.LogError("[ProtoBufHelper] This is why Success=False, Message=, Players=0 in IL2CPP build.");
                            }
                        }
                        catch (Exception fieldEx)
                        {
                            FileLogger.LogWarning($"[ProtoBufHelper] Failed to check EnterRoomResponse fields: {fieldEx.Message}");
                        }
                    }
                    else
                    {
                        FileLogger.LogWarning("[ProtoBufHelper] EnterRoomResponse metaType is null, will rely on AutoAddMissingTypes");
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogWarning($"[ProtoBufHelper] Failed to ensure EnterRoomResponse in model: {ex.Message}, will rely on AutoAddMissingTypes");
                }
            }
            
            // 始终使用RuntimeTypeModel，确保使用预编译的序列化器
            var result = (T)Model.Deserialize(source, null, typeof(T));
            
            FileLogger.Log($"[ProtoBufHelper] Deserialized {typeof(T).Name} successfully");
            
            // 自动修复包含 List<T> 字段的类型（IL2CPP 下的常见问题）
            if (memoryStream != null)
            {
                FixListFields(result, memoryStream);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, $"ProtoBufHelper.Deserialize<{typeof(T).Name}>");
            FileLogger.LogError($"[ProtoBufHelper] Deserialize failed for {typeof(T).Name}: {ex.Message}\n{ex.StackTrace}");
            Debug.LogError($"[ProtoBufHelper] Deserialize failed for {typeof(T).Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }
    
    /// <summary>
    /// 反序列化对象（指定类型）
    /// 始终使用RuntimeTypeModel，确保使用预编译的序列化器
    /// </summary>
    public static object Deserialize(Stream source, Type type)
    {
        try
        {
            // 始终使用RuntimeTypeModel，确保使用预编译的序列化器
            return Model.Deserialize(source, null, type);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProtoBufHelper] Deserialize failed for {type.Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }
    
    /// <summary>
    /// 自动修复包含 List<T> 字段的类型（IL2CPP 下的常见问题）
    /// </summary>
    private static void FixListFields<T>(T result, MemoryStream memoryStream)
    {
        try
        {
            memoryStream.Position = 0;
            byte[] data = memoryStream.ToArray();
            
            // EnterRoomResponse
            if (result is EnterRoomResponse enterRoomResp)
            {
                if (enterRoomResp.Players == null || enterRoomResp.Players.Count == 0)
                {
                    FileLogger.LogWarning("[ProtoBufHelper] EnterRoomResponse Players is empty, attempting manual parsing...");
                    var players = ProtoBufManualParser.ParseEnterRoomResponsePlayers(data);
                    if (players != null && players.Count > 0)
                    {
                        enterRoomResp.Players = players;
                        FileLogger.Log($"[ProtoBufHelper] Fixed EnterRoomResponse: {players.Count} players parsed");
                    }
                }
            }
            // RoomStateUpdate
            else if (result is RoomStateUpdate roomStateUpdate)
            {
                if (roomStateUpdate.Players == null || roomStateUpdate.Players.Count == 0)
                {
                    FileLogger.LogWarning("[ProtoBufHelper] RoomStateUpdate Players is empty, attempting manual parsing...");
                    var players = ProtoBufManualParser.ParseRoomStateUpdatePlayers(data);
                    if (players != null && players.Count > 0)
                    {
                        roomStateUpdate.Players = players;
                        FileLogger.Log($"[ProtoBufHelper] Fixed RoomStateUpdate: {players.Count} players parsed");
                    }
                }
            }
            // GameThreeStateSync
            else if (result is GameThreeStateSync gameThreeState)
            {
                bool needsFix = gameThreeState.Players == null || gameThreeState.Players.Count == 0
                    || gameThreeState.TerritoryGrid == null || gameThreeState.TerritoryGrid.Count == 0
                    || gameThreeState.Obstacles == null || gameThreeState.Obstacles.Count == 0;
                if (needsFix)
                {
                    FileLogger.LogWarning("[ProtoBufHelper] GameThreeStateSync has empty authoritative lists, attempting manual parsing...");
                    ProtoBufManualParser.ParseGameThreeStateSync(data, gameThreeState);
                    FileLogger.Log($"[ProtoBufHelper] Fixed GameThreeStateSync: Players={gameThreeState.Players?.Count ?? 0}, Bombs={gameThreeState.Bombs?.Count ?? 0}, Territory={gameThreeState.TerritoryGrid?.Count ?? 0}, Pickups={gameThreeState.Pickups?.Count ?? 0}, Obstacles={gameThreeState.Obstacles?.Count ?? 0}");
                }
            }
            // GameThreeRoundStart
            else if (result is GameThreeRoundStart gameThreeRoundStart)
            {
                if (gameThreeRoundStart.Players == null || gameThreeRoundStart.Players.Count == 0
                    || gameThreeRoundStart.Obstacles == null || gameThreeRoundStart.Obstacles.Count == 0)
                {
                    FileLogger.LogWarning("[ProtoBufHelper] GameThreeRoundStart has empty authoritative lists, attempting manual parsing...");
                    ProtoBufManualParser.ParseGameThreeRoundStart(data, gameThreeRoundStart);
                }
            }
            // GameThreeBombExploded
            else if (result is GameThreeBombExploded gameThreeBombExploded)
            {
                if (gameThreeBombExploded.AffectedCells == null || gameThreeBombExploded.AffectedCells.Count == 0)
                {
                    ProtoBufManualParser.ParseGameThreeBombExploded(data, gameThreeBombExploded);
                }
            }
            // GameThreeGameOver
            else if (result is GameThreeGameOver gameThreeGameOver)
            {
                if (gameThreeGameOver.TerritoryScores == null || gameThreeGameOver.TerritoryScores.Count == 0)
                {
                    ProtoBufManualParser.ParseGameThreeGameOver(data, gameThreeGameOver);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogWarning($"[ProtoBufHelper] Failed to fix List fields: {ex.Message}");
        }
    }
}

