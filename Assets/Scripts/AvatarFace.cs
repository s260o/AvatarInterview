using UnityEngine;
using VRM; // VRMBlendShapeProxy.SetValue(BlendShapePreset, float) は VRM 名前空間の拡張メソッド

/// <summary>
/// アバターの表情・口の形を指定するためのキー。
/// 名前は VRM 0.x の BlendShapePreset に合わせてある（VRM 1.0 では AvatarFace が対応する表情に読み替える）。
/// </summary>
public enum FaceKey
{
    Neutral,
    Joy,      // VRM 1.0: happy
    Fun,      // VRM 1.0: relaxed
    Angry,    // VRM 1.0: angry
    Sorrow,   // VRM 1.0: sad
    Blink,
    Blink_L,  // VRM 1.0: blinkLeft
    Blink_R,  // VRM 1.0: blinkRight
    A,        // VRM 1.0: aa
    I,        // VRM 1.0: ih
    U,        // VRM 1.0: ou
    E,        // VRM 1.0: ee
    O,        // VRM 1.0: oh
    Surprised, // VRM 1.0: surprised（VRM 0.x には対応するプリセットが無いため無視する）
}

/// <summary>
/// VRM 0.x（VRMBlendShapeProxy）と VRM 1.0（Vrm10Instance）の表情操作を共通化する。
/// 両方が指定されている場合は VRM 1.0 を優先する。
///
///   VRM 0.x: SetValue で値を積み、Apply() で反映する。
///   VRM 1.0: SetWeight で値を設定すると、Vrm10Instance が毎フレーム（LateUpdate）自動で反映する。
/// </summary>
public class AvatarFace
{
    private readonly global::VRM.VRMBlendShapeProxy _proxy;
    private readonly UniVRM10.Vrm10Instance _vrm10;

    public AvatarFace(global::VRM.VRMBlendShapeProxy proxy, UniVRM10.Vrm10Instance vrm10)
    {
        _proxy = proxy;
        _vrm10 = vrm10;
    }

    /// <summary>操作できるアバターが見つかっているか。</summary>
    public bool IsValid => _vrm10 != null || _proxy != null;

    public bool IsVrm10 => _vrm10 != null;

    /// <summary>ログ・デバッグ表示用の説明。</summary>
    public string Describe =>
        _vrm10 != null ? $"VRM 1.0 ({_vrm10.name})" :
        _proxy != null ? $"VRM 0.x ({_proxy.name})" :
        "(未設定)";

    public void Set(FaceKey key, float value)
    {
        if (_vrm10 == null && key == FaceKey.Surprised) return; // VRM 0.x には無い
        if (_vrm10 != null)
            _vrm10.Runtime.Expression.SetWeight(ToVrm10(key), value);
        else if (_proxy != null)
            _proxy.SetValue(ToVrm0(key), value);
    }

    /// <summary>VRM 0.x では必須。VRM 1.0 は自動で反映されるため何もしない。</summary>
    public void Apply()
    {
        if (_vrm10 == null && _proxy != null)
            _proxy.Apply();
    }

    private static UniVRM10.ExpressionKey ToVrm10(FaceKey key)
    {
        switch (key)
        {
            case FaceKey.Joy:     return UniVRM10.ExpressionKey.Happy;
            case FaceKey.Fun:     return UniVRM10.ExpressionKey.Relaxed;
            case FaceKey.Angry:   return UniVRM10.ExpressionKey.Angry;
            case FaceKey.Sorrow:  return UniVRM10.ExpressionKey.Sad;
            case FaceKey.Blink:   return UniVRM10.ExpressionKey.Blink;
            case FaceKey.Blink_L: return UniVRM10.ExpressionKey.BlinkLeft;
            case FaceKey.Blink_R: return UniVRM10.ExpressionKey.BlinkRight;
            case FaceKey.A:       return UniVRM10.ExpressionKey.Aa;
            case FaceKey.I:       return UniVRM10.ExpressionKey.Ih;
            case FaceKey.U:       return UniVRM10.ExpressionKey.Ou;
            case FaceKey.E:       return UniVRM10.ExpressionKey.Ee;
            case FaceKey.O:       return UniVRM10.ExpressionKey.Oh;
            case FaceKey.Surprised: return UniVRM10.ExpressionKey.Surprised;
            default:              return UniVRM10.ExpressionKey.Neutral;
        }
    }

    private static global::VRM.BlendShapePreset ToVrm0(FaceKey key)
    {
        switch (key)
        {
            case FaceKey.Joy:     return global::VRM.BlendShapePreset.Joy;
            case FaceKey.Fun:     return global::VRM.BlendShapePreset.Fun;
            case FaceKey.Angry:   return global::VRM.BlendShapePreset.Angry;
            case FaceKey.Sorrow:  return global::VRM.BlendShapePreset.Sorrow;
            case FaceKey.Blink:   return global::VRM.BlendShapePreset.Blink;
            case FaceKey.Blink_L: return global::VRM.BlendShapePreset.Blink_L;
            case FaceKey.Blink_R: return global::VRM.BlendShapePreset.Blink_R;
            case FaceKey.A:       return global::VRM.BlendShapePreset.A;
            case FaceKey.I:       return global::VRM.BlendShapePreset.I;
            case FaceKey.U:       return global::VRM.BlendShapePreset.U;
            case FaceKey.E:       return global::VRM.BlendShapePreset.E;
            case FaceKey.O:       return global::VRM.BlendShapePreset.O;
            default:              return global::VRM.BlendShapePreset.Neutral;
        }
    }
}
