using UnityEngine;
using UnityEngine.UI;

namespace Jam.Interaction
{
    /// <summary>Подсказка "[E] ..." в центре экрана. Работает с обычным Text или TextMeshPro.</summary>
    public class InteractionPromptUI : MonoBehaviour
    {
        public Interactor interactor;
        public Text label;
        public TMPro.TMP_Text tmpLabel;
        public string keyHint = "[E] ";
        public Color activeColor = new(0.92f, 0.88f, 0.78f, 1f);
        public Color disabledColor = new(0.55f, 0.53f, 0.5f, 0.8f);

        void Update()
        {
            var current = interactor ? interactor.Current : null;
            bool show = current != null && !string.IsNullOrEmpty(current.Prompt);

            string text = show ? (current.CanInteract(interactor) ? keyHint : "") + current.Prompt : "";
            Color color = show && current.CanInteract(interactor) ? activeColor : disabledColor;

            if (label)
            {
                label.enabled = show;
                label.text = text;
                label.color = color;
            }
            if (tmpLabel)
            {
                tmpLabel.enabled = show;
                tmpLabel.text = text;
                tmpLabel.color = color;
            }
        }
    }
}
