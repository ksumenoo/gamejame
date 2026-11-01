using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Полоска «держите E» под прицелом — та же, что у выдержки камеры, но для всего остального:
    /// опрокинуть вещь, погасить свечу, напугать фотографа. Кто держит — каждый кадр зовёт HoldBar.Report(0..1).
    /// Не позвали в этом кадре — полоска прячется. Своя канва, поэтому работает и когда HUD камеры выключен (акт 2).
    /// </summary>
    public class HoldBar : MonoBehaviour
    {
        const float BarW = 260f, BarH = 12f;
        static HoldBar s_inst;
        static int s_frame = -10;
        static float s_value;
        static Color s_color;

        RectTransform _bar, _fill;
        Image _fillImg;

        public static void Report(float progress) => Report(progress, new Color(0.95f, 0.9f, 0.78f, 1f));

        public static void Report(float progress, Color fill)
        {
            if (!s_inst) s_inst = new GameObject("HoldBar").AddComponent<HoldBar>();
            s_frame = Time.frameCount;
            s_value = Mathf.Clamp01(progress);
            s_color = fill;
        }

        void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _bar = new GameObject("Bar", typeof(RectTransform)).GetComponent<RectTransform>();
            _bar.SetParent(transform, false);
            _bar.anchorMin = _bar.anchorMax = new Vector2(0.5f, 0.5f);
            _bar.anchoredPosition = new Vector2(0f, -90f);
            _bar.sizeDelta = new Vector2(BarW, BarH);

            Stretch(Img("Frame", new Color(0.9f, 0.85f, 0.75f, 0.7f)).rectTransform, -2f);
            Stretch(Img("Back", new Color(0.05f, 0.04f, 0.03f, 0.85f)).rectTransform, 0f);
            _fillImg = Img("Fill", Color.white);
            _fill = _fillImg.rectTransform;
            _fill.anchorMin = new Vector2(0f, 0f);
            _fill.anchorMax = new Vector2(0f, 1f);
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.anchoredPosition = Vector2.zero;
            _fill.sizeDelta = Vector2.zero;

            var key = new GameObject("Key", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            key.transform.SetParent(_bar, false);
            var any = FindAnyObjectByType<Text>();
            key.font = any && any != key && any.font ? any.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            key.fontSize = 30;
            key.alignment = TextAnchor.MiddleRight;
            key.color = new Color(0.95f, 0.9f, 0.78f, 1f);
            key.text = "E";
            key.raycastTarget = false;
            var kr = key.rectTransform;
            kr.anchorMin = kr.anchorMax = new Vector2(0f, 0.5f);
            kr.pivot = new Vector2(1f, 0.5f);
            kr.anchoredPosition = new Vector2(-12f, 0f);
            kr.sizeDelta = new Vector2(60f, 40f);
            _bar.gameObject.SetActive(false);
        }

        Image Img(string name, Color c)
        {
            var i = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            i.transform.SetParent(_bar, false);
            i.color = c;
            i.raycastTarget = false;
            return i;
        }

        static void Stretch(RectTransform r, float grow)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(-grow, -grow); r.offsetMax = new Vector2(grow, grow);
        }

        void LateUpdate()
        {
            // Report мог прийти раньше или позже нашего LateUpdate в том же кадре — допускаем кадр запаса
            bool show = Time.frameCount - s_frame <= 1 && Time.timeScale > 0f;
            if (_bar.gameObject.activeSelf != show) _bar.gameObject.SetActive(show);
            if (!show) return;
            _fill.sizeDelta = new Vector2(BarW * s_value, 0f);
            _fillImg.color = s_color;
        }
    }
}
