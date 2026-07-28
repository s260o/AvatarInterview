using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// テーブル面にデプス書き込み専用オクルーダーを生成する。
/// TABLE アンカーが見つからない場合（L字デスクなど）は MRUK GlobalMesh 全体をオクルーダーとして使う。
/// </summary>
public class TableOccluder : MonoBehaviour
{
    [Header("References")]
    [Tooltip("DepthOccluder シェーダーを使ったマテリアルを割り当てる")]
    public Material occluderMaterial;

    [Header("Quad Occluder (TABLE アンカーが見つかった場合)")]
    [Tooltip("テーブルサイズより少し大きくするスケール倍率")]
    [Range(1.0f, 1.5f)]
    public float tableScale = 1.08f;

    [Tooltip("テーブル面より少し上にオフセット（z-fighting 防止）")]
    public float surfaceOffset = 0.002f;

    [Header("Global Mesh Occluder (TABLE が見つからない場合)")]
    [Tooltip("TABLE アンカー未検出時に MRUK GlobalMesh 全体をオクルーダーにする")]
    public bool useGlobalMeshAsFallback = true;

    private GameObject _occluder;

    public void SpawnOccluder()
    {
        if (occluderMaterial == null)
        {
            Debug.LogError("[TableOccluder] occluderMaterial が未設定です。");
            return;
        }

        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[TableOccluder] MRUK の部屋データが取得できません。");
            return;
        }

        // TABLE → OTHER/STORAGE の順で検索
        MRUKAnchor tableAnchor = null;
        foreach (var anchor in room.Anchors)
        {
            if (anchor == null) continue;
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) { tableAnchor = anchor; break; }
        }

        if (tableAnchor != null)
        {
            SpawnQuadOccluder(tableAnchor);
        }
        else if (useGlobalMeshAsFallback)
        {
            SpawnGlobalMeshOccluder(room);
        }
        else
        {
            Debug.LogWarning("[TableOccluder] TABLE アンカーが見つかりません。useGlobalMeshAsFallback を有効にしてください。");
        }
    }

    // TABLE アンカーの位置・サイズに合わせた Quad オクルーダー
    private void SpawnQuadOccluder(MRUKAnchor tableAnchor)
    {
        Vector2 tableSize;
        if (tableAnchor.PlaneRect.HasValue)
            tableSize = tableAnchor.PlaneRect.Value.size * tableScale;
        else if (tableAnchor.VolumeBounds.HasValue)
            tableSize = new Vector2(tableAnchor.VolumeBounds.Value.size.x, tableAnchor.VolumeBounds.Value.size.z) * tableScale;
        else
            tableSize = new Vector2(1.2f, 0.8f) * tableScale;

        if (_occluder != null) Destroy(_occluder);

        _occluder = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _occluder.name = "[TableOccluder_Quad]";
        Destroy(_occluder.GetComponent<Collider>());

        Vector3 center = tableAnchor.GetAnchorCenter() + Vector3.up * surfaceOffset;
        float yaw = tableAnchor.transform.eulerAngles.y;
        _occluder.transform.SetPositionAndRotation(center, Quaternion.Euler(-90f, yaw, 0f));
        _occluder.transform.localScale = new Vector3(tableSize.x, tableSize.y, 1f);

        var rend = _occluder.GetComponent<Renderer>();
        rend.material = occluderMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        Debug.Log($"[TableOccluder] Quad オクルーダー生成: 中心={center}, サイズ={tableSize}");
    }

    // GlobalMesh アンカーからメッシュを取得してオクルーダーマテリアルを適用
    private void SpawnGlobalMeshOccluder(MRUKRoom room)
    {
        MRUKAnchor globalMeshAnchor = null;
        foreach (var anchor in room.Anchors)
        {
            if (anchor != null && anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
            {
                globalMeshAnchor = anchor;
                break;
            }
        }

        if (globalMeshAnchor == null)
        {
            Debug.LogWarning("[TableOccluder] GlobalMesh アンカーが見つかりません。MRUK コンポーネントの Enable Global Mesh を ON にしてください。");
            return;
        }

        // GlobalMesh アンカーの GameObject にある全 Renderer にオクルーダーマテリアルを適用
        var renderers = globalMeshAnchor.GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers.Length == 0)
        {
            Debug.LogWarning("[TableOccluder] GlobalMesh アンカーに Renderer が見つかりません。");
            return;
        }

        foreach (var r in renderers)
        {
            // 全サブメッシュにオクルーダーマテリアルを適用
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = occluderMaterial;
            r.materials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        Debug.Log($"[TableOccluder] GlobalMesh オクルーダー適用: {renderers.Length} Renderer");
    }

    void OnDestroy()
    {
        if (_occluder != null) Destroy(_occluder);
    }
}
