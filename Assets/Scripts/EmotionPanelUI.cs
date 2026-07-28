using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// EmotionManager (VrmExpressionApplier) と Canvas UI を接続するブリッジ。
/// このスクリプトを EmotionCanvas の GameObject にアタッチし、
/// 各 Button の onClick から OnXxx() メソッドを呼ぶ。
/// </summary>
public class EmotionPanelUI : MonoBehaviour
{
    [Header("Target Script")]
    public VrmExpressionApplier emotionManager;

    [Header("Status Texts (毎フレーム更新)")]
    public TMP_Text micLevelText;
    public TMP_Text lastAppliedText;
    public TMP_Text selectedLabelText;

    [Header("Toggles (毎フレーム同期)")]
    public Toggle leaningToggle;
    public Toggle simulateListeningToggle;

    void Update()
    {
        if (emotionManager == null) return;

        if (micLevelText != null && emotionManager.interviewer != null)
        {
            var iv = emotionManager.interviewer;
            micLevelText.text = $"Mic: {iv.GetLastVolume():0.000}  Talking: {iv.GetIsTalking()}";
        }

        if (lastAppliedText != null)
            lastAppliedText.text = $"Last: {emotionManager.GetLastApplied()}";

        if (selectedLabelText != null)
            selectedLabelText.text = emotionManager.GetSelectedLabel();

        if (leaningToggle != null && emotionManager.interviewer != null)
            leaningToggle.SetIsOnWithoutNotify(emotionManager.interviewer.enableLeaning);

        if (simulateListeningToggle != null && emotionManager.interviewer != null)
            simulateListeningToggle.SetIsOnWithoutNotify(emotionManager.interviewer.simulateListening);
    }

    // ===== Button / Toggle コールバック =====
    // Inspector の Button.onClick や Toggle.onValueChanged からこれらを呼ぶ

    public void OnHighClicked()               => emotionManager?.SetHighPreset();
    public void OnLowClicked()                => emotionManager?.SetLowPreset();
    public void OnTriggerNodClicked()         => emotionManager?.TriggerNod();
    public void OnBlinkClicked()              => emotionManager?.TriggerBlinkOnce();
    public void OnResetClicked()              => emotionManager?.ResetFacialOnly();
    public void OnPrevLabel()                 => emotionManager?.SelectPrevLabel();
    public void OnNextLabel()                 => emotionManager?.SelectNextLabel();
    public void OnApplyLabel()                => emotionManager?.ApplySelectedLabel();
    public void OnStartDemo()                 => emotionManager?.StartAutoDemo();
    public void OnStopDemo()                  => emotionManager?.StopAutoDemo();
    public void OnLeaningToggled(bool v)      => emotionManager?.SetLeaningEnabled(v);
    public void OnSimulateListeningToggled(bool v) => emotionManager?.SetSimulateListening(v);
}
