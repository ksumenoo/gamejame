using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Jam.Exposure.Debugging
{
    /// <summary>
    /// Тестовый стенд выдержки. Не для финальной игры.
    /// T — выдержка, Y — выдержка со вспышкой. Пластина показывается на RawImage и проявляется.
    /// В консоль пишет, кто на пластине и резкий ли он.
    /// </summary>
    public class ExposureTester : MonoBehaviour
    {
        public ObscuraCamera obscura;
        public RawImage plateView;
        [Tooltip("Материал с шейдером Jam/PlateDisplay")]
        public Material plateMaterial;
        [Min(0.1f)] public float exposureSeconds = 5f;
        [Min(0.1f)] public float developSeconds = 3f;
        [Tooltip("Удалять предыдущую пластину. Выключи, если пластины забирает игрок (CameraStation).")]
        public bool releasePrevious = true;

        Plate _current;
        Material _viewMat;
        float _developT = -1f;

        static readonly int DevelopId = Shader.PropertyToID("_Develop");

        void Awake()
        {
            if (plateMaterial) _viewMat = new Material(plateMaterial);
            if (plateView)
            {
                plateView.material = _viewMat;
                plateView.enabled = false;
            }
        }

        void OnEnable()
        {
            if (obscura) obscura.ExposureFinished += OnFinished;
        }

        void OnDisable()
        {
            if (obscura) obscura.ExposureFinished -= OnFinished;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && obscura && !obscura.IsExposing)
            {
                if (kb.tKey.wasPressedThisFrame) obscura.BeginExposure(exposureSeconds, false);
                if (kb.yKey.wasPressedThisFrame) obscura.BeginExposure(exposureSeconds, true);
            }

            if (_developT >= 0f && _viewMat)
            {
                _developT += Time.deltaTime / developSeconds;
                _viewMat.SetFloat(DevelopId, Mathf.Clamp01(_developT));
                if (_developT >= 1f) _developT = -1f;
            }
        }

        void OnFinished(ObscuraCamera cam, Plate plate)
        {
            if (releasePrevious) _current?.Release();
            _current = plate;

            if (plateView)
            {
                plateView.texture = plate.Image;
                plateView.enabled = true;
                _developT = 0f;
                if (_viewMat) _viewMat.SetFloat(DevelopId, 0f);
            }

            if (plate.Readings.Count == 0)
            {
                Debug.Log($"[Plate] {cam.name}: пусто ({plate.Duration:0.0}s{(plate.Flash ? ", вспышка" : "")})");
                return;
            }

            foreach (var r in plate.Readings)
            {
                string kind = r.CaughtByFlash ? "ПОЙМАН ВСПЫШКОЙ"
                            : r.IsSharp ? "РЕЗКИЙ (стоял и смотрел)"
                            : r.VisibleFraction > 0f ? "шлейф" : "нет";
                Debug.Log($"[Plate] {cam.name}: {r.Subject.name} — {kind} | в кадре {r.VisibleFraction:P0}, резкость {r.Sharpness:0.00}, точка {r.SharpestPosition}");
            }
        }

        void OnDestroy()
        {
            if (releasePrevious) _current?.Release();
            if (_viewMat) Destroy(_viewMat);
        }
    }
}
