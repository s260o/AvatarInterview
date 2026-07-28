using Meta.XR.EnvironmentDepth;
using UnityEngine;

/// <summary>
/// Quest 3 の Environment Depth（IR 深度センサー）を有効化するセットアップスクリプト。
/// OVRCameraRig と同じ GameObject または新規 GO にアタッチする。
/// </summary>
[RequireComponent(typeof(EnvironmentDepthManager))]
public class EnvironmentDepthSetup : MonoBehaviour
{
    [Tooltip("起動時に自動で Environment Depth を有効化する")]
    public bool enableOnStart = true;

    [Tooltip("手をデプスオクルーダーとして扱わない（手でアバターが隠れるのを防ぐ）")]
    public bool removeHands = true;

    [Tooltip("HardOcclusion: ピクセル単位でカット / SoftOcclusion: エッジをぼかす（やや重い）")]
    public OcclusionShadersMode occlusionMode = OcclusionShadersMode.HardOcclusion;

    private EnvironmentDepthManager _depthManager;

    void Awake()
    {
        _depthManager = GetComponent<EnvironmentDepthManager>();
    }

    void Start()
    {
        if (!enableOnStart) return;
        EnableDepth();
    }

    public void EnableDepth()
    {
        _depthManager.RemoveHands = removeHands;
        _depthManager.OcclusionShadersMode = occlusionMode;
        _depthManager.enabled = true;
        Debug.Log("[EnvironmentDepthSetup] EnvironmentDepthManager を有効化しました。");
    }

    public void DisableDepth()
    {
        _depthManager.enabled = false;
    }
}
