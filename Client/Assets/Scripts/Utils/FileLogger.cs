using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 文件日志工具类，用于在构建后的exe中记录日志
/// </summary>
public static class FileLogger
{
    private static string _logFilePath;
    private static bool _initialized = false;
    private static readonly object _lockObject = new object();
    
    /// <summary>
    /// 初始化日志文件路径
    /// </summary>
    private static void Initialize()
    {
        if (_initialized) return;
        
        lock (_lockObject)
        {
            if (_initialized) return;
            
            try
            {
                // 在Windows上，日志文件放在exe同目录的Logs文件夹，或者Application.persistentDataPath
                string logDir = null;
                
                #if UNITY_EDITOR
                    logDir = Path.Combine(Application.dataPath, "..", "Logs");
                #else
                    // 构建后的exe，优先使用exe同目录的Logs文件夹
                    // Application.dataPath 在构建后指向 exe 同目录的 _Data 文件夹
                    string exeDir = Path.GetDirectoryName(Application.dataPath);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        logDir = Path.Combine(exeDir, "Logs");
                    }
                    
                    // 如果无法创建Logs目录，使用Application.persistentDataPath
                    if (string.IsNullOrEmpty(logDir) || !TryCreateDirectory(logDir))
                    {
                        logDir = Application.persistentDataPath;
                    }
                #endif
                
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(logDir, $"protobuf_log_{timestamp}.txt");
                
                // 写入初始信息（使用直接写入，避免递归调用）
                string initInfo = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FileLogger] Log file initialized at: {_logFilePath}\n" +
                                 $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FileLogger] Unity Version: {Application.unityVersion}\n" +
                                 $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FileLogger] Platform: {Application.platform}\n" +
                                 $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FileLogger] Application.dataPath: {Application.dataPath}\n" +
                                 $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FileLogger] Application.persistentDataPath: {Application.persistentDataPath}\n" +
                                 $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FileLogger] Log directory: {logDir}\n";
                
                File.WriteAllText(_logFilePath, initInfo, Encoding.UTF8);
                
                _initialized = true;
            }
            catch (Exception ex)
            {
                // 如果初始化失败，至少尝试输出到Unity Console
                Debug.LogError($"[FileLogger] Failed to initialize: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
    
    /// <summary>
    /// 尝试创建目录
    /// </summary>
    private static bool TryCreateDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// 写入日志到文件
    /// </summary>
    private static void WriteToFile(string message)
    {
        try
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;
            
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
            
            lock (_lockObject)
            {
                File.AppendAllText(_logFilePath, logMessage, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // 静默失败，避免影响主程序
            Debug.LogError($"[FileLogger] Write failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 记录信息日志
    /// </summary>
    public static void Log(string message)
    {
        Initialize();
        WriteToFile($"[INFO] {message}");
        Debug.Log(message); // 同时输出到Unity Console（如果可用）
    }
    
    /// <summary>
    /// 记录警告日志
    /// </summary>
    public static void LogWarning(string message)
    {
        Initialize();
        WriteToFile($"[WARNING] {message}");
        Debug.LogWarning(message); // 同时输出到Unity Console（如果可用）
    }
    
    /// <summary>
    /// 记录错误日志
    /// </summary>
    public static void LogError(string message)
    {
        Initialize();
        WriteToFile($"[ERROR] {message}");
        Debug.LogError(message); // 同时输出到Unity Console（如果可用）
    }
    
    /// <summary>
    /// 记录异常日志
    /// </summary>
    public static void LogException(Exception ex, string context = "")
    {
        Initialize();
        string message = string.IsNullOrEmpty(context) 
            ? $"Exception: {ex.GetType().Name}\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}"
            : $"[{context}] Exception: {ex.GetType().Name}\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}";
        
        WriteToFile($"[EXCEPTION] {message}");
        Debug.LogError(message); // 同时输出到Unity Console（如果可用）
    }
    
    /// <summary>
    /// 获取日志文件路径
    /// </summary>
    public static string GetLogFilePath()
    {
        Initialize();
        return _logFilePath;
    }
}

