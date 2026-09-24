using UnityEditor;
using UnityEngine;

public static class NetworkTestTools
{
    [MenuItem("Tools/Network/Simulate Client Transport Drop")]
    public static void SimulateClientTransportDrop()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[ReconnectTest] Enter Play mode before simulating a transport drop");
            return;
        }

        var networkManager = Object.FindObjectOfType<NetworkManager>();
        if (networkManager == null)
        {
            Debug.LogWarning("[ReconnectTest] NetworkManager was not found");
            return;
        }

        networkManager.SimulateTransportDropForTest();
    }
}
