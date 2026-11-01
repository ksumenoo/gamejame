using System.Collections.Generic;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Gameplay
{
    /// <summary>
    /// В браузере URP рисует ограниченное число точечных ламп на весь кадр. В доме десятки свечей и ламп —
    /// лишние просто выбрасывались, и целые стены оставались без света (чёрные). Здесь горят только ближайшие
    /// к камере лампы (в пределах бюджета), дальние временно гасятся — их всё равно не видно за стенами.
    /// Лампы, которые погасила сама игра, не трогаем: включаем обратно только то, что гасили сами.
    /// </summary>
    public class LightBudget : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        public int budget = 12;
#else
        public int budget = 24;
#endif
        static LightBudget s_inst;
        readonly HashSet<Light> _offByUs = new();
        readonly List<Light> _all = new();
        readonly List<(Light l, float d)> _on = new();
        Transform _cam;
        float _next, _rescan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
            OnLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnLoaded(Scene s, LoadSceneMode m)
        {
            if (!FindAnyObjectByType<FirstPersonController>()) return;
            // страховка: запечённых карт света в игре нет — если в сцене остались старые, объекты с ними чернеют. Отключаем.
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include))
                if (r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE) r.lightmapIndex = -1;
            LightmapSettings.lightmaps = new LightmapData[0];
            if (s_inst) return;
            s_inst = new GameObject("LightBudget").AddComponent<LightBudget>();
        }

        /// <summary>Игра гасит лампу насовсем (свечи в акте 2): бюджет её больше не включит.</summary>
        public static void TurnOff(Light l)
        {
            if (!l) return;
            if (s_inst) s_inst._offByUs.Remove(l);
            l.enabled = false;
        }

        void OnDestroy()
        {
            Shader.SetGlobalFloat(CountId, 0f);
            foreach (var l in _offByUs) if (l) l.enabled = true;
            if (s_inst == this) s_inst = null;
        }

        /// <summary>Отладка (F6): что за объект в центре экрана и чем он освещён — пишет в консоль.</summary>
        void Probe()
        {
            if (!Physics.Raycast(_cam.position, _cam.forward, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore)) { Debug.Log("[Probe] ничего"); return; }
            var r = hit.collider.GetComponentInChildren<Renderer>();
            var sb = new System.Text.StringBuilder($"[Probe] {hit.collider.name} точка {hit.point} нормаль {hit.normal}\n");
            foreach (var rr in hit.collider.GetComponentsInChildren<Renderer>())
            {
                if (!rr.enabled || !rr.bounds.Contains(hit.point) && rr.bounds.SqrDistance(hit.point) > 0.05f) continue;
                sb.Append($"  рендерер {rr.name} слой {rr.gameObject.layer} lightmap {rr.lightmapIndex}\n");
                foreach (var m in rr.sharedMaterials)
                {
                    if (!m) continue;
                    sb.Append($"    материал {m.name} шейдер {m.shader.name}");
                    foreach (var prop in new[] { "_PS1Unlit", "_VertexColorAmount", "_ObjectUV", "_UseMeshUV" })
                        if (m.HasProperty(prop)) sb.Append($" {prop}={m.GetFloat(prop)}");
                    if (m.HasProperty("_BaseColor")) sb.Append($" _BaseColor={m.GetColor("_BaseColor")}");
                    sb.Append("\n");
                }
            }
            sb.Append($"  глобально _PS1Unlit={Shader.GetGlobalFloat("_PS1Unlit")} ламп в списке={Shader.GetGlobalFloat(CountId)} луна={RenderSettings.sun?.name} {RenderSettings.sun?.intensity} ambient={RenderSettings.ambientLight}\n");
            foreach (var (l, d) in _on)
            {
                float dist = Vector3.Distance(l.transform.position, hit.point);
                if (dist < l.range) sb.Append($"  лампа {l.name} ({l.transform.parent?.name}) {dist:0.0} м, радиус {l.range:0.0}, яркость {l.intensity:0.00}\n");
            }
            Debug.Log(sb.ToString());
        }

        static readonly int PosId = Shader.PropertyToID("_JamLightPos");
        static readonly int ColId = Shader.PropertyToID("_JamLightCol");
        static readonly int CountId = Shader.PropertyToID("_JamLightCount");
        [Tooltip("Общая яркость ламп в доме: меньше — темнее игра.")]
        public float lightScale = 0.4f;
        [Tooltip("Лунный (главный) свет — ровный, без направления: меньше — темнее.")]
        public float moonScale = 0.12f;
        static readonly int MainId = Shader.PropertyToID("_JamMainScale");
        const int Max = 48; // столько же, сколько JAM_MAX_LIGHTS в PS1Lit
        readonly Vector4[] _pos = new Vector4[Max];
        readonly Vector4[] _col = new Vector4[Max];

        /// <summary>
        /// Каждый кадр — список ближайших включённых ламп в шейдер стен (PS1Lit). Свет считается одинаково для каждого
        /// пикселя всех объектов, без выбора ламп «на объект», — нет скачков яркости между соседними кусками стены.
        /// </summary>
        void Update()
        {
            if (!_cam) { var c = Camera.main; if (c) _cam = c.transform; else return; }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f6Key.wasPressedThisFrame) Probe();
            if (Time.unscaledTime >= _rescan || _all.Count == 0)
            {
                _rescan = Time.unscaledTime + 2f; // новые лампы (вспышка, свечение пластины) подхватываем раз в 2 с
                _all.Clear();
                foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                    if (l.type != LightType.Directional) _all.Add(l);
            }
            Vector3 p = _cam.position;
            _on.Clear();
            foreach (var l in _all)
            {
                if (!l || !l.enabled || !l.gameObject.activeInHierarchy || l.intensity <= 0f) continue;
                _on.Add((l, Vector3.Distance(p, l.transform.position) - l.range));
            }
            _on.Sort((a, b) => a.d.CompareTo(b.d));
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            int n = Mathf.Min(Max, _on.Count);
            for (int i = 0; i < n; i++)
            {
                var l = _on[i].l;
                Vector3 lp = l.transform.position;
                float r = Mathf.Max(0.01f, l.range);
                _pos[i] = new Vector4(lp.x, lp.y, lp.z, 1f / (r * r));
                Color c = (linear ? l.color.linear : l.color) * (l.intensity * lightScale);
                _col[i] = new Vector4(c.r, c.g, c.b, 0f);
            }
            Shader.SetGlobalVectorArray(PosId, _pos);
            Shader.SetGlobalVectorArray(ColId, _col);
            Shader.SetGlobalFloat(CountId, n);
            Shader.SetGlobalFloat(MainId, moonScale);
        }
    }
}
