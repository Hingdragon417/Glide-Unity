using UnityEngine;
using UnityEngine.Rendering;

public static class ClientRuntimeSettings
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Apply()
    {
        if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return;
        }

        QualitySettings.vSyncCount = 1;
        Application.targetFrameRate = -1;
    }
}
