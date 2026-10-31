using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Сколько пластин в сумке + подсказка при первом подборе: пластину надо проявить в проявочной.
    /// </summary>
    public class PlateCounterUI : MonoBehaviour
    {
        public PlateInventory inventory;
        public Text counter;
        public Text hint;
        public string counterFormat = "Пластины: {0}/{1}";
        public string firstPickupHint = "Пластина в сумке. Проявить её можно в проявочной — у верстака с кюветами.";
        public string fullHint = "Сумка полна — проявите пластины в проявочной.";
        public float hintSeconds = 6.5f;

        /// <summary>Строка подсказок вверху экрана — ей пользуются и другие системы.</summary>
        public static PlateCounterUI Instance { get; private set; }

        int _last;
        bool _shownFirst;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void OnEnable()
        {
            if (inventory) inventory.Changed += OnChanged;
            if (hint) hint.canvasRenderer.SetAlpha(0f);
            Refresh();
        }

        void OnDisable()
        {
            if (inventory) inventory.Changed -= OnChanged;
        }

        void OnChanged(PlateInventory inv)
        {
            bool added = inv.Count > _last;
            Refresh();
            if (added)
            {
                if (!_shownFirst) { _shownFirst = true; ShowHint(firstPickupHint); }
                else if (!inv.HasRoom) ShowHint(fullHint);
            }
        }

        void Refresh()
        {
            if (!inventory || !counter) return;
            int n = inventory.Count;
            counter.text = string.Format(counterFormat, n, inventory.capacity);
            counter.enabled = n > 0;
            if (n != _last)
            {
                counter.rectTransform.DOKill();
                counter.rectTransform.localScale = Vector3.one;
                counter.rectTransform.DOPunchScale(Vector3.one * 0.2f, 0.3f).SetLink(gameObject);
            }
            _last = n;
        }

        public void ShowHint(string text)
        {
            if (!hint) return;
            hint.text = text;
            hint.canvasRenderer.SetAlpha(0f);
            hint.CrossFadeAlpha(1f, 0.4f, true);
            CancelInvoke(nameof(HideHint));
            Invoke(nameof(HideHint), hintSeconds);
        }

        void HideHint()
        {
            if (hint) hint.CrossFadeAlpha(0f, 1f, true);
        }
    }
}
