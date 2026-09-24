using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class SettingsPanelManager : MonoBehaviour
{
    [Header("Buttons")]
    public GameObject backButton;
    public GameObject closeButton;
    
    // Start is called before the first frame update
    void Start()
    {
        // 添加点击事件监听
        if (backButton != null)
        {
            backButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                this.gameObject.SetActive(false);
            });
        }
        
        if (closeButton != null)
        {
            closeButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                this.gameObject.SetActive(false);
            });
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
