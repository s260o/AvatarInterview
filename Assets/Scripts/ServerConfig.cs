using System;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// サーバーの接続先と API トークンを、Git 管理外の設定ファイルから読み込む。
///
/// 設定ファイル: Assets/Resources/ServerSettings.json（.gitignore 済み。ビルドには含まれる）
///   { "apiBaseUrl": "https://api.s260o.com", "apiKey": "（Server/.env の API_TOKEN と同じ値）" }
/// ・apiBaseUrl が空なら VoiceInterviewClient のインスペクタの値を使う
/// ・ファイルが無ければ認証なしで接続する（このPC上の localhost サーバー向け）
/// トークンをシーンやスクリプトに直接書かないこと（リポジトリに残るため）。
/// </summary>
public static class ServerConfig
{
    [Serializable]
    private class Data
    {
        public string apiBaseUrl = "";
        public string apiKey = "";
    }

    private static Data _data;

    private static Data Settings
    {
        get
        {
            if (_data != null) return _data;
            _data = new Data();
            var asset = Resources.Load<TextAsset>("ServerSettings");
            if (asset == null)
            {
                Debug.Log("[ServerConfig] Resources/ServerSettings.json が無いため、認証なし・インスペクタの URL で接続します。");
                return _data;
            }
            try
            {
                JsonUtility.FromJsonOverwrite(asset.text, _data);
                Debug.Log($"[ServerConfig] 設定を読み込みました（URL={(string.IsNullOrEmpty(_data.apiBaseUrl) ? "(インスペクタの値)" : _data.apiBaseUrl)}, " +
                          $"APIキー={(string.IsNullOrEmpty(_data.apiKey) ? "なし" : "あり")}）");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ServerConfig] ServerSettings.json を読めません: {e.Message}");
            }
            return _data;
        }
    }

    /// <summary>設定ファイルで指定された接続先（無ければ空）。</summary>
    public static string ApiBaseUrl => Settings.apiBaseUrl?.Trim().TrimEnd('/') ?? "";

    /// <summary>API トークン（無ければ空）。</summary>
    public static string ApiKey => Settings.apiKey?.Trim() ?? "";

    /// <summary>"Authorization: Bearer ..." ヘッダーの値（トークンが無ければ null）。</summary>
    public static string AuthorizationHeader => string.IsNullOrEmpty(ApiKey) ? null : "Bearer " + ApiKey;

    /// <summary>リクエストに認証ヘッダーを付ける。</summary>
    public static void Authorize(UnityWebRequest req)
    {
        var header = AuthorizationHeader;
        if (header != null) req.SetRequestHeader("Authorization", header);
    }
}
