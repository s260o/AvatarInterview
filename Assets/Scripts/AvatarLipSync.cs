using UnityEngine;
using VRM;

/// <summary>
/// AudioSource の再生音量に応じてアバターの口（BlendShapePreset.A）を開閉する簡易リップシンク。
///
/// 音素解析ではなく音量（RMS）ベースの「開き具合」制御なので、
/// TTS 音声でも、人間評価者の音声（WoZ）でも、同じ AudioSource を監視するだけで口が動く。
/// 表情系（Fun/Joy 等）とは別チャンネル（A のみ）を触るため、EmotionManager と共存できる。
/// （EmotionManager.keepLipSync = true なら A はリセットされない）
/// </summary>
[DisallowMultipleComponent]
public class AvatarLipSync : MonoBehaviour
{
    [Header("参照")]
    [Tooltip("監視する音声出力（TTS／人間音声を再生している AudioSource）")]
    public AudioSource source;
    [Tooltip("口を動かす VRMBlendShapeProxy。未設定なら子から自動取得")]
    public VRMBlendShapeProxy proxy;

    [Header("調整")]
    [Tooltip("これ未満の音量は無音とみなす（環境ノイズ・小音対策）")]
    public float noiseFloor = 0.01f;
    [Tooltip("音量 → 口の開き の倍率")]
    public float gain = 12f;
    [Tooltip("口の最大開き（0〜1）")]
    [Range(0f, 1f)] public float maxOpen = 0.9f;
    [Tooltip("追従の速さ（大きいほど機敏に開閉）")]
    public float responsiveness = 18f;

    private float _value;
    private readonly float[] _samples = new float[256];

    void Reset()
    {
        proxy = GetComponentInChildren<VRMBlendShapeProxy>();
        source = GetComponent<AudioSource>();
    }

    void Start()
    {
        if (proxy == null) proxy = GetComponentInChildren<VRMBlendShapeProxy>();
        if (proxy == null)
            Debug.LogWarning("[AvatarLipSync] VRMBlendShapeProxy が見つかりません。口が動きません。");
    }

    void LateUpdate()
    {
        if (proxy == null) return;

        float target = 0f;
        if (source != null && source.isPlaying)
        {
            source.GetOutputData(_samples, 0);
            float sum = 0f;
            for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
            float rms = Mathf.Sqrt(sum / _samples.Length);
            target = Mathf.Clamp01((rms - noiseFloor) * gain) * maxOpen;
        }

        _value = Mathf.Lerp(_value, target, Time.deltaTime * responsiveness);
        if (_value < 0.001f) _value = 0f;

        // 口（A）のみ更新。表情は EmotionManager 側が別途 SetValue/Apply する
        proxy.SetValue(BlendShapePreset.A, _value);
        proxy.Apply();
    }
}
