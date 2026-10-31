using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Акт 2: шкала испуга живого фотографа — вверху экрана, без слов: его фигурка и полоска.
    /// Полоска растёт, когда его пугают (свеча гаснет у него на глазах, хлопает дверь, Маргарет шепчет),
    /// вздрагивает на сильный испуг и краснеет к концу. Дошла до края — он бежит из дома.
    /// Создаётся из кода (Act2Director), в сцене ничего настраивать не надо.
    /// </summary>
    public class FearMeterUI : MonoBehaviour
    {
        const float Width = 360f, Height = 10f;

        LivingPhotographer _who;
        RectTransform _fill, _root;
        Image _fillImg;
        float _shown;

        public static FearMeterUI Create(Canvas canvas, LivingPhotographer who)
        {
            if (!canvas || !who) return null;
            var root = new GameObject("FearMeter", typeof(RectTransform), typeof(CanvasGroup)).GetComponent<RectTransform>();
            root.SetParent(canvas.transform, false);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = new Vector2(0f, -28f);
            root.sizeDelta = new Vector2(Width + 44f, 40f);

            // его фигурка слева — чтобы было понятно, чья это шкала
            if (who.standTexture)
            {
                var icon = new GameObject("Icon", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                icon.transform.SetParent(root, false);
                icon.texture = who.standTexture;
                icon.raycastTarget = false;
                var ir = icon.rectTransform;
                ir.anchorMin = ir.anchorMax = new Vector2(0f, 0.5f);
                ir.pivot = new Vector2(0f, 0.5f);
                float aspect = who.standTexture.width / (float)Mathf.Max(1, who.standTexture.height);
                ir.sizeDelta = new Vector2(36f * aspect, 36f);
                ir.anchoredPosition = Vector2.zero;
            }

            var back = MakeImage("Back", root, new Color(0f, 0f, 0f, 0.55f));
            var br = back.rectTransform;
            br.anchorMin = br.anchorMax = new Vector2(1f, 0.5f);
            br.pivot = new Vector2(1f, 0.5f);
            br.sizeDelta = new Vector2(Width, Height);
            br.anchoredPosition = Vector2.zero;

            var fill = MakeImage("Fill", br, new Color(0.85f, 0.82f, 0.75f, 0.9f));
            var fr = fill.rectTransform;
            fr.anchorMin = new Vector2(0f, 0f);
            fr.anchorMax = new Vector2(0f, 1f);
            fr.pivot = new Vector2(0f, 0.5f);
            fr.sizeDelta = new Vector2(0f, 0f);
            fr.anchoredPosition = Vector2.zero;

            var ui = root.gameObject.AddComponent<FearMeterUI>();
            ui._who = who;
            ui._root = root;
            ui._fill = fr;
            ui._fillImg = fill;
            who.FearChanged += ui.OnFear;
            var cg = root.GetComponent<CanvasGroup>();
            cg.alpha = 0f;
            cg.DOFade(1f, 1.5f).SetLink(root.gameObject);
            return ui;
        }

        static Image MakeImage(string name, Transform parent, Color c)
        {
            var img = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            img.transform.SetParent(parent, false);
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        void OnDestroy()
        {
            if (_who) _who.FearChanged -= OnFear;
        }

        void OnFear(float f)
        {
            // сильный испуг — шкала вздрагивает
            if (f - _shown > 0.06f && _root)
            {
                _root.DOKill(true);
                _root.DOShakeAnchorPos(0.35f, 6f, 30).SetLink(gameObject);
            }
        }

        void Update()
        {
            if (!_who || !_fill) return;
            _shown = Mathf.MoveTowards(_shown, _who.Fear, Time.deltaTime * 0.8f);
            _fill.sizeDelta = new Vector2(Width * _shown, 0f);
            _fillImg.color = Color.Lerp(new Color(0.85f, 0.82f, 0.75f, 0.9f), new Color(0.75f, 0.12f, 0.08f, 1f), _shown * _shown);
        }

        public void Hide()
        {
            var cg = GetComponent<CanvasGroup>();
            if (cg) cg.DOFade(0f, 1f).SetLink(gameObject);
        }
    }
}
