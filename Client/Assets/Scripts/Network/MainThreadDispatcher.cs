using UnityEngine;
using System;
using System.Collections;
using System.Collections.Concurrent;

public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    public static MainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<MainThreadDispatcher>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("MainThreadDispatcher");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
            }
            return _instance;
        }
    }

    private readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

    void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (_instance != this)
        {
            Destroy(gameObject);
        }
    }

    void Update()
    {
        // 执行主线程队列中的所有动作
        while (_actionQueue.TryDequeue(out Action action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"主线程动作执行异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 将动作添加到主线程执行队列，可从任何线程调用，
    /// </summary>
    public void Enqueue(Action action)
    {
        if (action == null) return;
        _actionQueue.Enqueue(action);
    }

    /// <summary>
    /// 主线程启动协程
    /// </summary>
    public void StartCoroutineOnMainThread(IEnumerator coroutine)
    {
        Enqueue(() => {
            if (this != null)
            {
                StartCoroutine(coroutine);
            }
        });
    }
}