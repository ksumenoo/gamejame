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

        public string carriedText = "F — поставить камеру";
        public string exposingFormat = "Выдержка {0}% — держите E";
        public string readyText = "Пластина готова — E забрать, F сложить камеру";
        public string ruinedText = "Заслонка закрылась раньше времени. Пластина засвечена.";

        void OnEnable()
        {
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
            if (station && station.Current == CameraStation.State.Exposing)
                text = string.Format(exposingFormat, Mathf.RoundToInt(station.Progress * 100f));
            else if (portable && portable.IsCarried && portable.PlaceCount < 2)
                text = carriedText;
            else if (station && station.Current == CameraStation.State.PlateReady)
                text = readyText;
            if (label.text != text) label.text = text;
        }

        void OnRuined(CameraStation s)
        {
            if (PlateCounterUI.Instance) PlateCounterUI.Instance.ShowHint(ruinedText);
        }
    }
}
