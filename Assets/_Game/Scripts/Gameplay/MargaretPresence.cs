using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Маргарет без обскуры. Видна она только в стекле, но игрок не должен умирать, не понимая отчего:
    /// 1) рядом с ней края экрана холодеют и темнеют — чем ближе, тем сильнее (всегда, даже когда она просто бродит);
    /// 2) когда она гонится, её силуэт мерцает и для глаза — рваный, полупрозрачный, только вблизи.
    ///    Двойник на слое PlayerOnly: его видит только глаз игрока, в обскуру и на пластины он не попадает.
    /// Без текста и без скримера. Ставится сам при загрузке сцены, если в ней есть Маргарет.
    /// В акте 2 (Companion) выключается: там она и так видна.
    /// </summary>
    public class MargaretPresence : MonoBehaviour
    {
        [Header("Холод по краям экрана")]
        [Tooltip("С этого расстояния края экрана начинают холодеть, м.")]
        public float dreadFar = 7f;
        [Tooltip("Ближе этого — холод на полную, м.")]
        public float dreadNear = 1.5f;
        [Range(0f, 1f)] public float dreadMax = 0.75f;
        [Tooltip("В погоне холод сильнее.")]
        [Range(0f, 1f)] public float chaseBoost = 0.25f;

        [Header("Силуэт в погоне")]
        [Tooltip("Силуэт проступает с этого расстояния, м.")]
        public float glimpseFar = 6f;
        [Tooltip("Ближе этого силуэт на максимуме, м.")]
        public float glimpseNear = 1.5f;
        [Range(0f, 1f)] public float glimpseMax = 0.45f;

        MargaretAI _m;
        FirstPersonController _player;
        RawImage _vignette;
        Renderer _twin;
        Material _twinMat;
        float _dread, _glimpse;
        static readonly int OpacityId = Shader.PropertyToID("_Opacity");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Install();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) => Install();

        static void Install()
        {
            var m = FindAnyObjectByType<MargaretAI>(FindObjectsInactive.Include);
            if (!m || m.GetComponent<MargaretPresence>()) return;
            m.gameObject.AddComponent<MargaretPresence>();
        }

        void Start()
        {
            _m = GetComponent<MargaretAI>();
            _player = _m && _m.player ? _m.player : FindAnyObjectByType<FirstPersonController>();
            BuildVignette();
            BuildTwin();
        }

        void OnDestroy()
        {
            if (_vignette) Destroy(_vignette.canvas.gameObject);
            if (_twinMat) Destroy(_twinMat);
        }

        void BuildVignette()
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx * 0.8f + dy * dy);                 // чуть шире по горизонтали
                float a = Mathf.SmoothStep(0.35f, 1.15f, r);
                px[y * n + x] = new Color(0.55f, 0.65f, 0.8f, a);              // холодный синеватый край
            }
            tex.SetPixels(px);
            tex.Apply();

            var go = new GameObject("MargaretDread", typeof(Canvas));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40; // под подсказками и меню паузы
            var img = new GameObject("Vignette", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            img.transform.SetParent(go.transform, false);
            var rt = img.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            img.texture = tex;
            img.raycastTarget = false;
            img.color = new Color(0.12f, 0.14f, 0.2f, 0f); // тёмно-холодный, прозрачность задаём по близости
            _vignette = img;
        }

        void BuildTwin()
        {
            int ghost = LayerMask.NameToLayer("Ghost");
            int eyesOnly = LayerMask.NameToLayer("PlayerOnly");
            if (eyesOnly < 0) return;
            Renderer src = null;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.gameObject.layer == ghost) { src = r; break; }
            if (!src) return;

            var twin = Instantiate(src.gameObject, src.transform.parent, false);
            twin.name = src.gameObject.name + "_EyeGlimpse";
            twin.layer = eyesOnly;
            foreach (var c in twin.GetComponents<Component>())
                if (!(c is Transform) && !(c is MeshFilter) && !(c is Renderer)) Destroy(c);
            _twin = twin.GetComponent<Renderer>();
            _twinMat = new Material(src.sharedMaterial);
            _twin.sharedMaterial = _twinMat;
            _twin.enabled = false;
        }

        void Update()
        {
            if (!_m || !_player) return;
            bool act2 = _m.Companion;
            float d = _m.isActiveAndEnabled ? _m.DistanceToPlayer : float.MaxValue;
            bool hunting = _m.Current == MargaretAI.State.Chase || _m.Current == MargaretAI.State.Caught;

            // холод по краям: где бы она ни была — если близко, это чувствуется
            float near01 = Mathf.InverseLerp(dreadFar, dreadNear, d);
            float dreadTarget = act2 ? 0f : Mathf.Clamp01(near01 * dreadMax + (hunting ? chaseBoost * near01 : 0f));
            _dread = Mathf.MoveTowards(_dread, dreadTarget, Time.deltaTime * (dreadTarget > _dread ? 1.5f : 0.6f));
            if (_vignette)
            {
                float pulse = hunting ? 0.08f * Mathf.Sin(Time.time * 7f) : 0f; // в погоне — как сердце
                var c = _vignette.color;
                c.a = Mathf.Clamp01(_dread + pulse * _dread);
                _vignette.color = c;
            }

            // силуэт для глаза — только в погоне и вблизи
            float g = act2 || !hunting ? 0f : Mathf.InverseLerp(glimpseFar, glimpseNear, d) * glimpseMax;
            _glimpse = Mathf.MoveTowards(_glimpse, g, Time.deltaTime * 1.2f);
            if (_twin)
            {
                bool show = _glimpse > 0.01f;
                if (_twin.enabled != show) _twin.enabled = show;
                if (show && _twinMat)
                {
                    // рвано: то проступает, то пропадает
                    float flick = Mathf.PerlinNoise(Time.time * 5f, 0.37f);
                    _twinMat.SetFloat(OpacityId, _glimpse * Mathf.Lerp(0.3f, 1.2f, flick));
                }
            }
        }
    }
}
