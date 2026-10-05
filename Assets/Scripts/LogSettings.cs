using UnityEngine;

/// <summary>
/// 実行時のログ量を抑える設定。
///
/// Meta XR SDK (OVRPlugin / MetaXRFeature / MRUK) は Debug.Log を大量に出すが、
/// Unity は既定で 1 行のログごとに 20〜30 行のスタックトレースを付ける。
/// このため Editor.log が実際の情報量の数十倍に膨れ上がり、クラッシュ解析の妨げになる。
///
/// 通常ログ (LogType.Log) だけトレースを外す。
/// Warning / Error / Exception は原因追跡に必要なので既定のまま残す。
/// </summary>
public static class LogSettings
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Apply()
    {
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
    }
}
