using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class UdpReceiver : MonoBehaviour
{
    public Transform targetObj; // 動かす対象（FaceTarget）
    public int port = 5005; // 通信ポート番号

    private UdpClient udpClient;
    private Thread thread;
    private Vector2 receivedPos = Vector2.zero; // 受け取った座標（0.0 〜 1.0）
    private bool isDataReceived = false;

    // 動きの倍率（顔を動かした時にターゲットがどれくらい動くか）
    public float movementScale = 5.0f;

    [Header("Non-verbal Behavior Presets (High / Low)")]
    public float movementScaleHigh = 5.0f;
    // Low条件は報告書の定義に従い正面固定（顔追従スケール 0）
    public float movementScaleLow = 0.0f;

    public void ApplyPreset(bool isHigh)
    {
        movementScale = isHigh ? movementScaleHigh : movementScaleLow;
    }

    void Start()
    {
        udpClient = new UdpClient(port);
        thread = new Thread(new ThreadStart(ThreadMethod));
        thread.Start();
        Debug.Log("UDP受信開始: Port " + port);

        ApplyPreset(true);
    }

    void Update()
    {
        if (isDataReceived && targetObj != null)
        {
            // 受信したデータ(0~1)を、Unityの座標系に変換
            // 中心(0.5, 0.5)を基準に、左右上下に広げる
            float x = (receivedPos.x - 0.5f) * -movementScale; // 左右反転
            float y = (receivedPos.y - 0.5f) * movementScale;

            // カメラの位置を基準に、ターゲットを移動させる
            if (Camera.main != null)
            {
                Vector3 camPos = Camera.main.transform.position;
                // カメラと同じ奥行き、少し手前などに配置
                // ここでは「カメラのZ位置」を基準にX,Yだけ動かす
                targetObj.position = new Vector3(camPos.x + x, camPos.y + y, camPos.z);
            }
        }
    }

    private void ThreadMethod()
    {
        while (true)
        {
            try
            {
                IPEndPoint remoteEP = null;
                byte[] data = udpClient.Receive(ref remoteEP);
                string text = Encoding.UTF8.GetString(data);

                // "x,y" という文字列が来る想定
                string[] split = text.Split(',');
                if (split.Length == 2)
                {
                    float x = float.Parse(split[0]);
                    float y = float.Parse(split[1]);
                    receivedPos = new Vector2(x, y);
                    isDataReceived = true;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(e.ToString());
                break;
            }
        }
    }

    void OnApplicationQuit()
    {
        if (thread != null) thread.Abort();
        if (udpClient != null) udpClient.Close();
    }
}