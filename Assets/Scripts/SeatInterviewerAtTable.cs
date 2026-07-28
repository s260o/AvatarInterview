using Meta.XR.MRUtilityKit;
using UnityEngine;

public class SeatInterviewerAtTable : MonoBehaviour
{
    [Header("References")]
    public Transform avatar;
    public Transform headset;

    [Header("Seat Adjustment")]
    public float behindTable = 0.35f;
    public float seatDrop = 0.45f;

    [Header("Visibility")]
    [Tooltip("ON にすると空間認識完了まで Avatar を非表示にする")]
    public bool hideUntilPlaced = true;

    [Header("Fallback (TABLE アンカーが見つからない場合)")]
    [Tooltip("TABLE が未検出のとき、ヘッドセット正面の固定位置に配置する")]
    public bool useFallbackPosition = true;
    [Tooltip("ヘッドセットから机の中心までの距離 (m)")]
    public float fallbackDistance = 0.8f;
    [Tooltip("フォールバック時の机面の高さ (m) — 標準的な机は 0.72m")]
    public float fallbackDeskHeight = 0.72f;

    [Header("実行時コントローラー操作")]
    [Tooltip("左スティック X/Z・右スティック Y でアバターを微調整する速度 (m/s)")]
    public float nudgeSpeed = 0.3f;

    void Start()
    {
        // 空間認識完了まで Avatar を非表示
        if (hideUntilPlaced && avatar != null)
            avatar.gameObject.SetActive(false);

        if (MRUK.Instance == null)
        {
            Debug.LogWarning("SeatInterviewerAtTable: MRUK instance is not available in Start().");
            return;
        }

        // RegisterSceneLoadedCallback は「既にロード済みなら即時実行・未ロードなら完了後に実行」
        MRUK.Instance.RegisterSceneLoadedCallback(PlaceAvatar);
    }

    void Update()
    {
        // 右 B ボタン: アバターを現在のヘッドセット位置基準で再配置
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
            PlaceAvatar();

        if (avatar == null || !avatar.gameObject.activeSelf) return;

        // 左スティック: アバターを前後左右に微調整
        Vector2 ls = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        // 右スティック Y: アバターの高さを微調整
        float rsY = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick, OVRInput.Controller.RTouch).y;

        if (ls.sqrMagnitude > 0.01f || Mathf.Abs(rsY) > 0.01f)
        {
            float dt = nudgeSpeed * Time.deltaTime;
            avatar.position += new Vector3(ls.x * dt, rsY * dt, ls.y * dt);
        }
    }

    void OnDestroy()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.RemoveListener(PlaceAvatar);
    }

    public void PlaceAvatar()
    {
        if (avatar == null)
        {
            Debug.LogWarning("SeatInterviewerAtTable: Avatar reference is not assigned.");
            return;
        }

        if (headset == null)
        {
            Debug.LogWarning("SeatInterviewerAtTable: Headset reference is not assigned.");
            return;
        }

        if (MRUK.Instance == null)
        {
            Debug.LogWarning("SeatInterviewerAtTable: MRUK instance is not available.");
            return;
        }

        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("SeatInterviewerAtTable: Current room data is not available.");
            return;
        }

        // TABLE を優先検索し、なければ OTHER / STORAGE も試す（L字デスクは OTHER になりやすい）
        MRUKAnchor table = null;
        var fallbackLabels = MRUKAnchor.SceneLabels.TABLE
                           | MRUKAnchor.SceneLabels.OTHER
                           | MRUKAnchor.SceneLabels.STORAGE;

        foreach (var anchor in room.Anchors)
        {
            if (anchor == null) continue;

            // TABLE を最優先
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE))
            {
                table = anchor;
                break;
            }

            // 机の高さ範囲 (0.5m〜1.1m) にある OTHER / STORAGE を机代わりに使う
            if (table == null && anchor.HasAnyLabel(MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.STORAGE))
            {
                float h = anchor.GetAnchorCenter().y;
                if (h >= 0.5f && h <= 1.1f)
                    table = anchor;
            }
        }

        Vector3 tablePos;
        Vector3 toTable;

        if (table != null)
        {
            Debug.Log($"[SeatInterviewerAtTable] Anchor found: {table.GetLabelsAsEnum()} at {table.GetAnchorCenter()}");
            tablePos = table.GetAnchorCenter();
            toTable = tablePos - headset.position;
            toTable.y = 0f;
        }
        else if (useFallbackPosition)
        {
            // TABLE も OTHER も見つからない場合 → ヘッドセット正面の固定位置（L字デスク対応）
            Debug.LogWarning("[SeatInterviewerAtTable] TABLE anchor not found. Using fallback position in front of headset.");
            Vector3 forward = Vector3.ProjectOnPlane(headset.forward, Vector3.up).normalized;
            tablePos = headset.position + forward * fallbackDistance;
            tablePos.y = fallbackDeskHeight;
            toTable = forward;
        }
        else
        {
            Debug.LogWarning("[SeatInterviewerAtTable: TABLE anchor was not found. Enable useFallbackPosition to use fixed position.");
            return;
        }

        toTable.Normalize();

        Vector3 seatPos = tablePos + toTable * behindTable;
        seatPos.y = tablePos.y - seatDrop;
        avatar.position = seatPos;

        Vector3 lookDir = headset.position - avatar.position;
        lookDir.y = 0f;

        if (lookDir.sqrMagnitude < 0.0001f)
        {
            Debug.LogWarning("SeatInterviewerAtTable: Headset is too close to the avatar to calculate look direction.");
            return;
        }

        avatar.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);

        // 配置完了後に Avatar を表示
        avatar.gameObject.SetActive(true);

        // テーブルオクルーダーが同じ GO にある場合は自動で生成する
        var occluder = GetComponent<TableOccluder>();
        if (occluder != null)
            occluder.SpawnOccluder();

        Debug.Log("SeatInterviewerAtTable: Avatar placed successfully.");
    }
}
