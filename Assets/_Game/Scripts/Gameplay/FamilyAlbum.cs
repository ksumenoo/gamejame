using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Exposure;
using Jam.Interaction;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Jam.Gameplay
{
    /// <summary>
    /// Шесть рамок семьи Эшдаун на стене. В каждой — выцветший снимок пустой комнаты ровно с точки обзора сцены:
    /// по нему игрок узнаёт, где и куда смотреть. Проявленная пластина встаёт в свою рамку.
    /// Снимки-подсказки рендерятся при старте камерой без слоёв Ghost/Past.
    /// </summary>
    public class FamilyAlbum : MonoBehaviour
    {
        public PlateViewer viewer;
        [Tooltip("Рамки по порядку сцен (1…6). Пусто — возьмёт дочерние AlbumFrame.")]
        public List<AlbumFrame> frames = new();
        public LayerMask hintMask = ~0;
        public Vector2Int hintResolution = new(256, 192);
        [Range(20f, 100f)] public float hintFov = 70f;
        public Material frameMaterial;
        [Tooltip("Подписи от руки под снимками, по порядку сцен 1…6: какая комната — точное место игрок ищет по снимку.")]
        public string[] captions =
        {
            "Столовая. Обед всей семьёй.",
            "Спальня. Мама причёсывает её перед сном.",
            "Гостиная. Сеанс.",
            "Кабинет. У камина.",
            "Коридор. Портрет у стойки.",
            "У дверей проявочной.",
        };

        readonly List<RenderTexture> _rts = new();
        static readonly int Ps1UnlitId = Shader.PropertyToID("_PS1Unlit");

        System.Collections.IEnumerator Start()
        {
            if (frames.Count == 0) frames = GetComponentsInChildren<AlbumFrame>(true).OrderBy(f => f.sceneIndex).ToList();
            // кадр подождать: двери открываются в своём Start — иначе на снимке с точки у двери будет закрытая створка
            yield return null;
            RenderHints();
            if (StoryDirector.Instance) StoryDirector.Instance.PlateDeveloped += OnDeveloped;
        }

        void OnDestroy()
        {
            if (StoryDirector.Instance) StoryDirector.Instance.PlateDeveloped -= OnDeveloped;
            foreach (var rt in _rts) if (rt) { rt.Release(); Destroy(rt); }
        }

        void RenderHints()
        {
            var go = new GameObject("AlbumHintCamera");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = hintFov;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 25f;
            cam.cullingMask = hintMask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            // снимки сделаны «днём»: ровный свет со стороны объектива + светлый рассеянный.
            // Точечная вспышка не годится — свет падает с расстоянием, дальняя стена чёрная.
            var flash = go.AddComponent<Light>();
            flash.type = LightType.Directional;
            flash.intensity = 1.1f;
            flash.color = new Color(1f, 0.95f, 0.85f);
            flash.shadows = LightShadows.None;
            var ambient = RenderSettings.ambientLight;
            var ambientMode = RenderSettings.ambientMode;
            var probe = RenderSettings.ambientProbe;
            bool fog = RenderSettings.fog;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.52f, 0.47f);
            // шейдеры URP берут рассеянный свет из ambientProbe — сам ambientLight во время игры его не обновляет
            var sh = new UnityEngine.Rendering.SphericalHarmonicsL2();
            sh.AddAmbientLight(RenderSettings.ambientLight);
            RenderSettings.ambientProbe = sh;
            // главное: шейдер дома рисует «днём», без зависимости от света сцены (иначе тёмные полосы и чёрный пол)
            Shader.SetGlobalFloat(Ps1UnlitId, 1f);
            RenderSettings.fog = false;

            foreach (var f in frames)
            {
                var scene = PastScene.All.FirstOrDefault(s => s && s.index == f.sceneIndex);
                if (!scene || !scene.viewPoint) continue;
                var rt = new RenderTexture(hintResolution.x, hintResolution.y, 16, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Point,
                    name = $"AlbumHint_{f.sceneIndex}",
                };
                rt.Create();
                _rts.Add(rt);
                go.transform.SetPositionAndRotation(scene.viewPoint.position, scene.viewPoint.rotation);
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
                if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(cam, request))
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);
                else
                {
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = null;
                }
                f.Init(this, rt, frameMaterial);
            }
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambient;
            RenderSettings.ambientProbe = probe;
            Shader.SetGlobalFloat(Ps1UnlitId, 0f);
            RenderSettings.fog = fog;
            Destroy(go);
        }

        void OnDeveloped(PastScene scene, Plate plate)
        {
            var f = frames.FirstOrDefault(x => x && x.sceneIndex == scene.index);
            if (!f || plate == null || !plate.Image) return;
            // своя копия: пластину на верёвке потом могут снять и освободить
            var rt = new RenderTexture(plate.Image.width, plate.Image.height, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                name = $"AlbumPlate_{scene.index}",
            };
            rt.Create();
            Graphics.Blit(plate.Image, rt);
            _rts.Add(rt);
            f.SetDeveloped(rt);
        }

        public void Show(Texture tex, bool developed, int sceneIndex = 0)
        {
            string caption = sceneIndex >= 1 && sceneIndex <= captions.Length ? captions[sceneIndex - 1] : null;
            if (viewer && tex && !viewer.IsOpen)
                viewer.ShowTextureAsync(tex, false, false, developed ? -1f : 1.15f, clean: !developed, caption: caption).Forget(); // отпечаток — прямой
        }
    }
}
