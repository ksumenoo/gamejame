using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Акт 2: «проявленность» призрака — без шкалы и цифр. Края экрана белеют, как засвеченная пластина;
    /// пока камера смотрит на тебя, белизна дрожит.
    /// </summary>
    public class GhostMeterUI : MonoBehaviour
    {
        public CanvasGroup group;
        [Tooltip("Белая рамка-виньетка по краям экрана.")]
        public Image edges;
        [Range(0f, 1f)] public float maxAlpha = 0.85f;

        float _shown;

        void Awake()
        {
            if (group) group.alpha = 0f;
        }

        public void Set(float value01, bool seen)
        {
            if (!group) return;
            _shown = Mathf.MoveTowards(_shown, value01, Time.deltaTime * 2f);
            float flicker = seen ? Random.Range(0.85f, 1.1f) : 1f;
            group.alpha = Mathf.Clamp01(_shown * maxAlpha * flicker);
        }

        public void Clear()
        {
            _shown = 0f;
            if (group) group.alpha = 0f;
        }
    }
}
