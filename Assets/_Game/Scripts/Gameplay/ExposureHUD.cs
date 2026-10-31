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

        static string Bar(float t)
        {
            int n = Mathf.Clamp(Mathf.RoundToInt(t * 10f), 0, 10);
            return new string('|', n) + new string('.', 10 - n); // ASCII: пиксельный шрифт может не знать других символов
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
            if (station && station.Current == CameraStation.State.Exposing)
                text = "E  " + Bar(station.Progress);
            else if (portable && portable.IsCarried && portable.PlaceCount < 3 && hand && hand.IsRaised && hand.Alignment >= hand.alignLock)
                text = "F";
            if (label.text != text) label.text = text;
        }

        void OnRuined(CameraStation s)
        {
            // без текста: заслонка хлопает, полоска пропадает — этого достаточно
        }
    }
}
