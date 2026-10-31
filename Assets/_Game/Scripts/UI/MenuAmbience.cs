using UnityEngine;

namespace Jam.UI
{
    /// <summary>
    /// Жизнь натюрморта в меню: камера едва заметно «дышит» (снимают с рук, а не со штатива),
    /// свечи дрожат, красная лампа проявочной медленно тлеет. Ставит Tools → Jam → Build Menu Scene.
    /// </summary>
    public class MenuAmbience : MonoBehaviour
    {
        public Transform cameraRig;
        [Tooltip("Насколько гуляет взгляд камеры, градусы.")]
        public float breathAngle = 0.6f;
        public float breathSpeed = 0.18f;

        public Light[] candles;
        public Light redLamp;

        float[] _candleBase;
        float _redBase;
        Quaternion _camBase;

        void Start()
        {
            if (cameraRig) _camBase = cameraRig.localRotation;
            _candleBase = new float[candles != null ? candles.Length : 0];
            for (int i = 0; i < _candleBase.Length; i++) _candleBase[i] = candles[i] ? candles[i].intensity : 0f;
            if (redLamp) _redBase = redLamp.intensity;
        }

        void Update()
        {
            float t = Time.unscaledTime;
            if (cameraRig)
            {
                float yaw = (Mathf.PerlinNoise(t * breathSpeed, 0.3f) - 0.5f) * 2f * breathAngle;
                float pitch = (Mathf.PerlinNoise(0.7f, t * breathSpeed) - 0.5f) * 2f * breathAngle * 0.6f;
                cameraRig.localRotation = _camBase * Quaternion.Euler(pitch, yaw, 0f);
            }
            for (int i = 0; i < _candleBase.Length; i++)
            {
                if (!candles[i]) continue;
                float n = Mathf.PerlinNoise(t * 7f + i * 13.1f, i * 3.7f);
                candles[i].intensity = _candleBase[i] * (0.75f + n * 0.45f);
            }
            if (redLamp) redLamp.intensity = _redBase * (0.85f + 0.15f * Mathf.Sin(t * 0.6f));
        }
    }
}
