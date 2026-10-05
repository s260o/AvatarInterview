using TMPro;
using UnityEngine;

public class ChatUI : MonoBehaviour
{
    public TMP_InputField input;
    public InterviewClient client;

    public void OnClickSend()
    {
        if (client == null || input == null) return;

        var text = input.text;
        if (string.IsNullOrWhiteSpace(text)) return;

        client.SendUserText(text);   // ← これが送信
        input.text = "";
        input.ActivateInputField();
    }
}
