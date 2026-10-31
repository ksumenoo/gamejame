using NaughtyAttributes;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Настройки вида уровня: low-res рендер с дизерингом и «дрожание» вершин PS1.
    /// Low-res: главная камера рисует в маленькую RT, её на весь экран выводит RawImage с шейдером Jam/LowResOutput.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class LevelSettings : MonoBehaviour
    {
        [Header("Low-res вывод")]
        public bool lowRes = true;
        [Tooltip("Ширина кадра в пикселях; высота считается по пропорциям экрана.")]
        [Min(160)] public int renderWidth = 640;
        public Camera mainCamera;
        [Tooltip("Материал на шейдере Jam/LowResOutput.")]
        public Material outputMaterial;
        [Range(4, 256)] public int colorLevels = 64;
        [Range(0f, 2f)] public float dither = 0.35f;

        [Header("Дрожание вершин")]
        public bool vertexSnap = true;
        [Tooltip("Сетка снапа в пикселях. Меньше — сильнее дрожит.")]
        public Vector2 snapResolution = new(320, 180);

        static readonly int SnapId = Shader.PropertyToID("_PS1SnapRes");
        static readonly int LevelsId = Shader.PropertyToID("_Levels");
        static readonly int DitherId = Shader.PropertyToID("_Dither");

        RenderTexture _rt;
        RawImage _view;
        Material _mat;
        int _lastW, _lastH;

        void Awake()
        {
            ApplySnap();
            if (lowRes) SetupLowRes();
        }

        void OnValidate()
        {
            if (Application.isPlaying) ApplySnap();
        }

        void ApplySnap() =>
            Shader.SetGlobalVector(SnapId, vertexSnap ? new Vector4(snapResolution.x, snapResolution.y, 0, 0) : Vector4.zero);

        void SetupLowRes()
        {
            if (!mainCamera) mainCamera = Camera.main;
            if (!mainCamera) return;

            // Отдельная экранная камера сама рисует low-res кадр (Canvas в режиме Screen Space - Camera, слой UI).
            // Так на экран честно рисует камера: нет надписи «No cameras rendering», и интерфейс не мерцает в браузере.
            const int uiLayer = 5;
            var screenGo = new GameObject("ScreenCamera");
            screenGo.transform.SetParent(transform, false);
            screenGo.transform.position = new Vector3(0f, -1000f, 0f); // подальше от уровня
            var screenCam = screenGo.AddComponent<Camera>();
            screenCam.cullingMask = 1 << uiLayer;
            screenCam.clearFlags = CameraClearFlags.SolidColor;
            screenCam.backgroundColor = Color.black;
            screenCam.orthographic = true;
            screenCam.nearClipPlane = 0.1f;
            screenCam.farClipPlane = 10f;
            screenCam.depth = -100;
            screenCam.useOcclusionCulling = false;
            screenCam.allowHDR = false;
            screenCam.allowMSAA = false;

            var canvasGo = new GameObject("LowResOutput", typeof(Canvas)) { layer = uiLayer };
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = screenCam;
            canvas.planeDistance = 1f;
            canvas.sortingOrder = -100; // под HUD и всем остальным

            var imgGo = new GameObject("Frame", typeof(RectTransform), typeof(RawImage)) { layer = uiLayer };
            imgGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)imgGo.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            _view = imgGo.GetComponent<RawImage>();
            _view.raycastTarget = false;
            if (outputMaterial)
            {
                _mat = new Material(outputMaterial);
                _view.material = _mat;
            }
            Rebuild();
        }

        void Update()
        {
            if (!lowRes || !mainCamera) return;
            if (Screen.width != _lastW || Screen.height != _lastH) Rebuild();
            if (_mat)
            {
                _mat.SetFloat(LevelsId, colorLevels);
                _mat.SetFloat(DitherId, dither);
            }
        }

        void Rebuild()
        {
            _lastW = Screen.width;
            _lastH = Screen.height;
            int w = renderWidth;
            int h = Mathf.Max(90, Mathf.RoundToInt(renderWidth * (float)_lastH / Mathf.Max(1, _lastW)));

            if (_rt)
            {
                mainCamera.targetTexture = null;
                _rt.Release();
                Destroy(_rt);
            }
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                name = "LowResFrame",
            };
            _rt.Create();
            mainCamera.targetTexture = _rt;
            if (_view) _view.texture = _rt;
        }

        [Button("Low-res вкл/выкл (в игре)")]
        void Toggle()
        {
            if (!Application.isPlaying || !mainCamera) return;
            lowRes = !lowRes;
            mainCamera.targetTexture = lowRes ? _rt : null;
            if (_view) _view.enabled = lowRes;
            if (lowRes && !_rt) SetupLowRes();
        }

        void OnDestroy()
        {
            if (mainCamera) mainCamera.targetTexture = null;
            if (_rt) { _rt.Release(); Destroy(_rt); }
            if (_mat) Destroy(_mat);
            Shader.SetGlobalVector(SnapId, Vector4.zero);
        }
    }
}
