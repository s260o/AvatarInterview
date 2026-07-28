using Meta.XR.MRUtilityKit;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// MRUK が認識したアンカーを色付き球で可視化するデバッグスクリプト。
/// 右コントローラーの A ボタンで表示/非表示を切り替える。
/// 色の凡例: 緑=TABLE 灰=FLOOR 青=WALL 黄=COUCH 紫=STORAGE 水=OTHER 橙=GlobalMesh
/// </summary>
public class MRUKDebugVisualizer : MonoBehaviour
{
    [Header("設定")]
    [Tooltip("MRUK ロード完了時に自動で可視化する")]
    public bool autoShowOnLoad = true;

    [Tooltip("アンカー中心を示す球のサイズ (m)")]
    [Range(0.05f, 0.3f)]
    public float markerSize = 0.12f;

    private readonly List<GameObject> _markers = new List<GameObject>();
    private readonly List<string> _log = new List<string>();
    private bool _visible;

    void Start()
    {
        if (autoShowOnLoad && MRUK.Instance != null)
            MRUK.Instance.RegisterSceneLoadedCallback(Visualize);
    }

    void Update()
    {
        // 右コントローラー A ボタンで表示/非表示トグル
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            Toggle();

        // 右コントローラー A 長押し (1 秒) で再スキャン
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            _holdTime += Time.deltaTime;
            if (_holdTime >= 1.0f)
            {
                _holdTime = 0f;
                Visualize();
            }
        }
        else
        {
            _holdTime = 0f;
        }
    }

    private float _holdTime;

    void OnGUI()
    {
        if (!_visible || _log.Count == 0) return;

        GUIStyle style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 22,
            alignment = TextAnchor.UpperLeft
        };
        style.normal.textColor = Color.white;

        string header = $"MRUK Anchors ({_log.Count} 件)\n";
        string body = string.Join("\n", _log);
        float height = 36 + _log.Count * 28f;
        GUI.Box(new Rect(10, 10, 560, height), header + body, style);

        // 凡例
        GUIStyle legend = new GUIStyle(GUI.skin.label) { fontSize = 20 };
        GUI.Label(new Rect(10, height + 14, 560, 28),
            "凡例: 緑=TABLE  灰=FLOOR  青=WALL  黄=COUCH  紫=STORAGE  水=OTHER", legend);
    }

    public void Visualize()
    {
        Clear();

        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[MRUKDebugVisualizer] room is null.");
            return;
        }

        foreach (var anchor in room.Anchors)
        {
            if (anchor == null) continue;

            var label = anchor.GetLabelsAsEnum();

            // GlobalMesh はメッシュが大きいので球マーカーを省略
            if ((label & MRUKAnchor.SceneLabels.GLOBAL_MESH) != 0) continue;

            Vector3 pos = anchor.GetAnchorCenter();
            Color col = LabelToColor(label);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"[Debug] {label}";
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * markerSize;
            Destroy(go.GetComponent<Collider>());

            var mat = new Material(Shader.Find("Unlit/Color")) { color = col };
            go.GetComponent<Renderer>().material = mat;
            _markers.Add(go);

            _log.Add($"{label}  高さ={pos.y:F2}m  ({pos.x:F2}, {pos.z:F2})");
            Debug.Log($"[MRUK] {label} @ {pos}");
        }

        _visible = true;
        Debug.Log($"[MRUKDebugVisualizer] {_markers.Count} アンカーを可視化しました。");
    }

    private Color LabelToColor(MRUKAnchor.SceneLabels l)
    {
        if ((l & MRUKAnchor.SceneLabels.TABLE)     != 0) return Color.green;
        if ((l & MRUKAnchor.SceneLabels.FLOOR)     != 0) return Color.gray;
        if ((l & MRUKAnchor.SceneLabels.WALL_FACE) != 0) return Color.blue;
        if ((l & MRUKAnchor.SceneLabels.COUCH)     != 0) return Color.yellow;
        if ((l & MRUKAnchor.SceneLabels.STORAGE)   != 0) return Color.magenta;
        if ((l & MRUKAnchor.SceneLabels.OTHER)     != 0) return Color.cyan;
        return Color.white;
    }

    private void Toggle()
    {
        _visible = !_visible;
        foreach (var m in _markers)
            if (m != null) m.SetActive(_visible);
    }

    private void Clear()
    {
        foreach (var m in _markers)
            if (m != null) Destroy(m);
        _markers.Clear();
        _log.Clear();
        _visible = false;
    }

    void OnDestroy() => Clear();
}
