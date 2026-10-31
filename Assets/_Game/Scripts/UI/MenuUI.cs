using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Jam.UI
{
    /// <summary>
    /// Общие детали главного меню и паузы, собираются кодом: шрифт Old Standard, цвет старой бумаги,
    /// выделение — тёмно-красный, как свет проявочной. Без картинок: всё рисуется текстом и плашками.
    /// </summary>
    public static class MenuUI
    {
        public static readonly Color Paper = new(0.859f, 0.8f, 0.678f, 1f);      // #DBCCAD
        public static readonly Color PaperDim = new(0.859f, 0.8f, 0.678f, 0.55f);
        public static readonly Color Red = new(0.78f, 0.22f, 0.17f, 1f);
        public static readonly Color RedDark = new(0.45f, 0.1f, 0.08f, 1f);

        static Font s_font, s_bold;

        /// <summary>ColorTint умножает цвет текста на цвет состояния: чтобы бумажный текст стал красным, делим.</summary>
        static Color Tint(Color want) => new(want.r / Paper.r, want.g / Paper.g, want.b / Paper.b, 1f);

        public static Font Font
        {
            get
            {
                if (!s_font) s_font = Resources.Load<Font>("Fonts/OldStandard-Regular");
                if (!s_font) s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return s_font;
            }
        }

        public static Font Bold
        {
            get
            {
                if (!s_bold) s_bold = Resources.Load<Font>("Fonts/OldStandard-Bold");
                return s_bold ? s_bold : Font;
            }
        }

        public static Canvas CreateCanvas(string name, int order, Transform parent = null)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            if (parent) go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        /// <summary>Кнопкам нужен EventSystem. В проекте новый Input System — модуль под него.</summary>
        public static void EnsureEventSystem()
        {
            if (EventSystem.current || UnityEngine.Object.FindAnyObjectByType<EventSystem>()) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = go.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        public static Image Fill(string name, Transform parent, Color color)
        {
            var rt = Stretch(Rect(name, parent));
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Затемнение слева направо: под колонкой меню темно, к центру экрана сходит на нет.</summary>
        public static RawImage LeftShade(Transform parent, float width01, float alpha)
        {
            var tex = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int x = 0; x < 64; x++)
            {
                float t = x / 63f;
                float a = alpha * (1f - Mathf.SmoothStep(0.45f, 1f, t));
                tex.SetPixel(x, 0, new Color(0f, 0f, 0f, a));
            }
            tex.Apply();
            var rt = Rect("Shade", parent);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(width01, 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var img = rt.gameObject.AddComponent<RawImage>();
            img.texture = tex;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Колонка слева: заголовок и пункты сверху вниз.</summary>
        public static RectTransform Column(Transform parent, string name, float x, float topFromCenter, float width = 640f)
        {
            var rt = Rect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, topFromCenter);
            rt.sizeDelta = new Vector2(width, 900f);
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childAlignment = TextAnchor.UpperLeft;
            v.spacing = 14f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            return rt;
        }

        public static Text Label(Transform parent, string text, int size, Color color, Font font = null, float height = 0f)
        {
            var rt = Rect("Label", parent);
            rt.pivot = new Vector2(0f, 0.5f); // наведение увеличивает пункт от левого края
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font ? font : Font;
            t.fontSize = size;
            t.color = color;
            t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height > 0f ? height : size * 1.35f;
            return t;
        }

        public static void Spacer(Transform parent, float height)
        {
            var le = Rect("Space", parent).gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = height;
        }

        /// <summary>Пункт меню — просто строка текста. Наведение: красный и шаг вправо, щелчок затвора.</summary>
        public static Button Button(Transform parent, string text, int size, Action onClick)
        {
            var t = Label(parent, text, size, Paper);
            t.raycastTarget = true;
            var b = t.gameObject.AddComponent<Button>();
            b.targetGraphic = t;
            b.transition = Selectable.Transition.ColorTint;
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = Color.white;
            cb.highlightedColor = Tint(Red);          // Text.color * tint → красный
            cb.pressedColor = Tint(RedDark);
            cb.selectedColor = Color.white;              // не залипать красным после щелчка
            cb.disabledColor = new Color(1f, 1f, 1f, 0.35f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            var nav = b.navigation;
            nav.mode = Navigation.Mode.None;
            b.navigation = nav;
            b.onClick.AddListener(() => onClick?.Invoke());
            t.gameObject.AddComponent<MenuButtonFX>();
            return b;
        }

        /// <summary>Строка настройки: подпись слева, полоска справа. Без цифр.</summary>
        public static Slider Slider(Transform parent, string label, float min, float max, float value, Action<float> onChange)
        {
            var row = Rect("Row_" + label, parent);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 64f;

            var lt = row.gameObject.AddComponent<Text>();
            lt.font = Font;
            lt.fontSize = 34;
            lt.color = Paper;
            lt.text = label;
            lt.alignment = TextAnchor.MiddleLeft;
            lt.raycastTarget = false;

            var sr = Rect("Slider", row);
            sr.anchorMin = new Vector2(0.48f, 0.5f);
            sr.anchorMax = new Vector2(1f, 0.5f);
            sr.sizeDelta = new Vector2(0f, 26f);
            sr.anchoredPosition = Vector2.zero;
            var slider = sr.gameObject.AddComponent<Slider>();

            var bg = Fill("Back", sr, new Color(0f, 0f, 0f, 0.6f));
            bg.rectTransform.anchorMin = new Vector2(0f, 0.38f);
            bg.rectTransform.anchorMax = new Vector2(1f, 0.62f);
            bg.raycastTarget = true; // клик по всей полоске

            var fillArea = Rect("FillArea", sr);
            fillArea.anchorMin = new Vector2(0f, 0.38f);
            fillArea.anchorMax = new Vector2(1f, 0.62f);
            fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;
            var fill = Fill("Fill", fillArea, PaperDim);

            var handleArea = Rect("HandleArea", sr);
            Stretch(handleArea);
            var handle = Rect("Handle", handleArea);
            handle.sizeDelta = new Vector2(12f, 0f);
            handle.anchorMin = new Vector2(0f, 0f);
            handle.anchorMax = new Vector2(0f, 1f);
            var hImg = handle.gameObject.AddComponent<Image>();
            hImg.color = Paper;

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle;
            slider.targetGraphic = hImg;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.SetValueWithoutNotify(value);
            var cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = Tint(Red);
            cb.pressedColor = Tint(RedDark);
            cb.selectedColor = Color.white;
            slider.colors = cb;
            var nav = slider.navigation;
            nav.mode = Navigation.Mode.None;
            slider.navigation = nav;
            slider.onValueChanged.AddListener(v => onChange?.Invoke(v));
            return slider;
        }

        /// <summary>Панель настроек — одна и та же в меню и в паузе.</summary>
        public static RectTransform Settings(Transform parent, float x, float top, Action onBack)
        {
            var col = Column(parent, "Settings", x, top, 820f);
            Label(col, "Настройки", 64, Paper, Bold, 110f);
            Slider(col, "Громкость", 0f, 1f, Jam.Gameplay.GameSettings.Master, Jam.Gameplay.GameSettings.SetMaster);
            Slider(col, "Звуки", 0f, 1f, Jam.Gameplay.GameSettings.Sfx, Jam.Gameplay.GameSettings.SetSfx);
            Slider(col, "Мышь", 0.25f, 3f, Jam.Gameplay.GameSettings.Mouse, Jam.Gameplay.GameSettings.SetMouse);
            Spacer(col, 30f);
            Button(col, "Назад", 44, onBack);
            return col;
        }

        public static void Show(Component panel, bool on)
        {
            if (panel) panel.gameObject.SetActive(on);
        }
    }
}
