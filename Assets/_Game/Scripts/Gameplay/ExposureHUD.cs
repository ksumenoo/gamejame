using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Строка внизу экрана про переносную камеру: «F — поставить», «Выдержка 40% — держите E»,
    /// и короткие сообщения: пластина засвечена / готова.
    /// </summary>
    public class ExposureHUD : MonoBehaviour
    {
        public PortableCamera portable;
        public CameraStation station;
        public Text label;
        [Tooltip("Обскура в руке: подсказка «F» появляется, только когда картинка в ней сложилась.")]
        public Jam.Player.HandObscura hand;

        public string carriedText = "F — поставить камеру";
        public string exposingFormat = "Выдержка {0}% — держите E";
        public string readyText = "Пластина готова — E забрать, F сложить камеру";
        public string ruinedText = "Заслонка закрылась раньше времени. Пластина засвечена.";

        // полоска выдержки под прицелом: крупная, заметная, без слов — «E» слева и заполняющаяся шкала
        const float BarW = 260f, BarH = 12f;
        RectTransform _bar, _fill;
        Image _fillImg;
        Text _barKey;

        void BuildBar()
        {
            if (_bar || !label) return;
            var canvas = label.canvas ? label.canvas.rootCanvas : null;
            if (!canvas) return;
            _bar = new GameObject("ExposureBar", typeof(RectTransform)).GetComponent<RectTransform>();
            _bar.SetParent(canvas.transform, false);
            _bar.anchorMin = _bar.anchorMax = new Vector2(0.5f, 0.5f);
            _bar.anchoredPosition = new Vector2(0f, -90f);
            _bar.sizeDelta = new Vector2(BarW, BarH);

            var frame = Img("Frame", _bar, new Color(0.9f, 0.85f, 0.75f, 0.7f));
            Stretch(frame.rectTransform, -2f);
            var back = Img("Back", _bar, new Color(0.05f, 0.04f, 0.03f, 0.85f));
            Stretch(back.rectTransform, 0f);
            _fillImg = Img("Fill", _bar, new Color(0.95f, 0.9f, 0.78f, 1f));
            _fill = _fillImg.rectTransform;
            _fill.anchorMin = new Vector2(0f, 0f);
            _fill.anchorMax = new Vector2(0f, 1f);
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.anchoredPosition = Vector2.zero;
            _fill.sizeDelta = Vector2.zero;

            _barKey = new GameObject("Key", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            _barKey.transform.SetParent(_bar, false);
            _barKey.font = label.font;
            _barKey.fontSize = 30;
            _barKey.alignment = TextAnchor.MiddleRight;
            _barKey.color = new Color(0.95f, 0.9f, 0.78f, 1f);
            _barKey.text = "E";
            _barKey.raycastTarget = false;
            var kr = _barKey.rectTransform;
            kr.anchorMin = kr.anchorMax = new Vector2(0f, 0.5f);
            kr.pivot = new Vector2(1f, 0.5f);
            kr.anchoredPosition = new Vector2(-12f, 0f);
            kr.sizeDelta = new Vector2(60f, 40f);
            _bar.gameObject.SetActive(false);
        }

        static Image Img(string name, Transform parent, Color c)
        {
            var i = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            i.transform.SetParent(parent, false);
            i.color = c;
            i.raycastTarget = false;
            return i;
        }

        static void Stretch(RectTransform r, float grow)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(-grow, -grow); r.offsetMax = new Vector2(grow, grow);
        }

        void OnEnable()
        {
            if (!hand) hand = FindAnyObjectByType<Jam.Player.HandObscura>();
            if (!station && portable) station = portable.GetComponent<CameraStation>();
            if (station)
            {
                station.ExposureRuined += OnRuined;
            }
        }

        void OnDisable()
        {
            if (station) station.ExposureRuined -= OnRuined;
        }

        void Update()
        {
            if (!label) return;
            string text = "";
            // без слов: кнопка и полоска выдержки. «F» — только когда стоишь в точке съёмки
            BuildBar();
            bool exposing = station && station.Current == CameraStation.State.Exposing;
            if (_bar)
            {
                bool ruinFlash = !exposing && Time.time < _ruinUntil;
                if (_bar.gameObject.activeSelf != (exposing || ruinFlash)) _bar.gameObject.SetActive(exposing || ruinFlash);
                if (exposing)
                {
                    _fill.sizeDelta = new Vector2(BarW * Mathf.Clamp01(station.Progress), 0f);
                    // в темноте полоска тусклее и ползёт медленнее — видно, что свету не хватает
                    var c = station.InDark ? new Color(0.6f, 0.55f, 0.48f, 1f) : new Color(0.95f, 0.9f, 0.78f, 1f);
                    // отходишь от камеры — полоска краснеет: ещё шаг, и пластина засветится
                    float leave = Mathf.InverseLerp(0.55f, 1f, station.LeaveFactor);
                    _fillImg.color = Color.Lerp(c, new Color(0.85f, 0.15f, 0.1f, 1f), leave);
                    _barKey.text = station.holdToExpose ? "E" : "";
                }
                else if (ruinFlash)
                {
                    // сорвалась: полоска вспыхивает красным и гаснет — видно, что снимок пропал
                    float k = (_ruinUntil - Time.time) / 0.9f;
                    _fillImg.color = new Color(0.85f, 0.15f, 0.1f, Mathf.Clamp01(k));
                }
            }
            if (exposing) text = "";
            else if (portable && portable.IsCarried && portable.PlaceCount < 3 && hand && hand.IsRaised && hand.Alignment >= hand.alignLock)
                text = "F";
            if (label.text != text) label.text = text;
        }

        float _ruinUntil;

        void OnRuined(CameraStation s)
        {
            // без текста: заслонка хлопает, полоска вспыхивает красным
            _ruinUntil = Time.time + 0.9f;
        }
    }
}
