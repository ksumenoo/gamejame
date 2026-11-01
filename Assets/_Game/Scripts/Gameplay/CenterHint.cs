using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Подсказка клавиш крупно по центру экрана на пару секунд («Shift — бег», «ПКМ — обскура»):
    /// в такие моменты игрок смотрит в обскуру в руках и мелкую строку в углу не замечает.
    /// </summary>
    public static class CenterHint
    {
        static Text s_text;
        static CanvasGroup s_group;

        public static void Show(string keys, float seconds = 3f)
        {
            if (!s_text)
            {
                var go = new GameObject("CenterHint", typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
                Object.DontDestroyOnLoad(go);
                var canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 300;
                var sc = go.GetComponent<CanvasScaler>();
                sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                sc.referenceResolution = new Vector2(1920f, 1080f);
                sc.matchWidthOrHeight = 0.5f;
                s_group = go.GetComponent<CanvasGroup>();
                s_group.blocksRaycasts = false;
                s_group.interactable = false;
                var t = new GameObject("Text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
                t.transform.SetParent(go.transform, false);
                var rt = t.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, 160f);
                rt.sizeDelta = new Vector2(1400f, 120f);
                var f = Resources.Load<Font>("Fonts/OldStandard-Regular");
                t.font = f ? f : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                t.fontSize = 56;
                t.alignment = TextAnchor.MiddleCenter;
                t.color = new Color(0.95f, 0.9f, 0.78f, 1f);
                t.raycastTarget = false;
                var o = t.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(0f, 0f, 0f, 0.8f);
                o.effectDistance = new Vector2(2f, -2f);
                s_text = t;
            }
            s_text.text = keys;
            s_group.DOKill();
            s_group.alpha = 0f;
            DOTween.Sequence().SetUpdate(true).SetTarget(s_group)
                .Append(s_group.DOFade(1f, 0.25f))
                .AppendInterval(seconds)
                .Append(s_group.DOFade(0f, 0.6f));
        }
    }
}
