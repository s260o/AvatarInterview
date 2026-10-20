using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

[Serializable]
public class ApiMessage
{
    public string role;
    public string content;
}

[Serializable]
public class ApiRequest
{
    public ApiMessage[] messages;
}

[Serializable]
public class ApiResponse
{
    public string model;
    public string expression;
    public string text;
    public string raw;
}

public class InterviewClient : MonoBehaviour
{
    [Header("FastAPI endpoint")]
    public string apiUrl = "http://127.0.0.1:8000/api/interview";

    [Header("Auth (optional)")]
    public bool useApiKey = false;
    public string apiKey = "your-secret";

    [Header("VRM Expression")]
    public VrmExpressionApplier expressionApplier;

    [Header("Log")]
    public bool showRawInLog = false;
    public int maxLogLines = 200;

    [Header("Canvas UI")]
    public TMP_Text logText;
    public ScrollRect logScrollRect;
    public TMP_InputField inputField;
    public Button sendButton;

    private readonly List<ApiMessage> _history = new List<ApiMessage>();
    private readonly List<string> _log = new List<string>();
    private bool _sending = false;

    void Awake()
    {
        AddLog("System: Ready.");
        if (sendButton != null)
            sendButton.onClick.AddListener(SendFromUI);
    }

    void AddLog(string line)
    {
        _log.Add(line);
        if (_log.Count > maxLogLines)
            _log.RemoveAt(0);

        if (logText != null)
            logText.text = string.Join("\n", _log);

        // スクロールを一番下へ
        if (logScrollRect != null)
        {
            Canvas.ForceUpdateCanvases();
            logScrollRect.verticalNormalizedPosition = 0f;
        }
    }

    // Canvas の Send ボタンから呼ぶ
    public void SendFromUI()
    {
        if (_sending) return;
        if (inputField == null) return;
        var text = inputField.text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        inputField.text = "";
        SendUserText(text);
    }

    public void SendUserText(string userText)
    {
        if (_sending) return;
        if (string.IsNullOrWhiteSpace(userText)) return;
        StartCoroutine(CoSend(userText));
    }

    private IEnumerator CoSend(string userText)
    {
        _sending = true;
        if (sendButton != null) sendButton.interactable = false;

        AddLog($"You: {userText}");
        _history.Add(new ApiMessage { role = "user", content = userText });

        var reqObj = new ApiRequest { messages = _history.ToArray() };
        var json = JsonUtility.ToJson(reqObj);

        using var req = new UnityWebRequest(apiUrl, "POST");
        byte[] body = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        if (useApiKey)
            req.SetRequestHeader("X-API-Key", apiKey);

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            AddLog($"[Error] {req.error}");
            Debug.LogError($"API Error: {req.error}\n{req.downloadHandler.text}");
            _sending = false;
            if (sendButton != null) sendButton.interactable = true;
            yield break;
        }

        var res = JsonUtility.FromJson<ApiResponse>(req.downloadHandler.text);

        if (expressionApplier != null)
            expressionApplier.ApplyLabel(res.expression, "ai");

        AddLog($"Bot: {(showRawInLog ? res.raw : res.text)}");
        _history.Add(new ApiMessage { role = "assistant", content = res.text });

        _sending = false;
        if (sendButton != null) sendButton.interactable = true;
    }
}
