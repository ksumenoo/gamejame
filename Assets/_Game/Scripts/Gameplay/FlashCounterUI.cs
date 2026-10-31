using DG.Tweening;
using Jam.Player;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>Счётчик вспышек магния в углу экрана.</summary>
    public class FlashCounterUI : MonoBehaviour
    {
        public MagnesiumFlash flash;
        public Text label;
        public string format = "Магний: {0}";

        void OnEnable()
        {
            if (flash) flash.ChargesChanged += OnChanged;
            if (flash) OnChanged(flash.Charges);
        }

        void OnDisable()
        {
            if (flash) flash.ChargesChanged -= OnChanged;
        }

        void OnChanged(int n)
        {
            if (!label) return;
            label.text = string.Format(format, n);
            label.color = n > 0 ? new Color(0.85f, 0.8f, 0.7f, 0.8f) : new Color(0.6f, 0.2f, 0.15f, 0.8f);
            label.rectTransform.DOKill();
            label.rectTransform.localScale = Vector3.one;
            label.rectTransform.DOPunchScale(Vector3.one * 0.15f, 0.25f).SetLink(gameObject);
        }
    }
}
