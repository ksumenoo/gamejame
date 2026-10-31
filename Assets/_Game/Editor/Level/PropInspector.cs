using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jam.Gameplay;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Jam.EditorTools
{
    /// <summary>
    /// Проверка сборки без беготни по сцене: снимает каждый проп (P_*, BEFORE_/AFTER_*) двумя ракурсами
    /// и пишет отчёт — какие материалы и текстуры у каждого меша, какие Dupli-копии видны, какие двери открыты.
    /// Всё складывается в Temp/Inspect (в проект и в git не попадает).
    /// </summary>
    public static class PropInspector
    {
        const string OutDir = "Temp/Inspect"; // Temp не попадает в git

        [MenuItem("Tools/Jam/Debug/Inspect Props")]
        public static void Run()
        {
            var root = GameObject.Find("LEVEL_Generated");
            if (!root) { Debug.LogWarning("[Jam] Inspect: нет LEVEL_Generated в открытой сцене."); return; }
            Directory.CreateDirectory(OutDir);
            foreach (var f in Directory.GetFiles(OutDir)) File.Delete(f);

            var report = new StringBuilder();
            var rx = new Regex(@"^(P_|BEFORE_S\d+_|AFTER_S\d+_)");
            var targets = root.GetComponentsInChildren<Transform>(true).Where(t => rx.IsMatch(t.name)).ToList();

            // временный свет и камера; туман и тёмный эмбиент дома на время съёмки выключаем
            bool fog = RenderSettings.fog;
            var amb = RenderSettings.ambientLight;
            var ambMode = RenderSettings.ambientMode;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);

            var camGo = new GameObject("_InspectCam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.2f, 0.25f, 0.3f);
            cam.nearClipPlane = 0.02f;
            cam.fieldOfView = 45f;
            int ghost = LayerMask.NameToLayer("Ghost"), past = LayerMask.NameToLayer("Past"), eyes = LayerMask.NameToLayer("PlayerOnly");
            int mask = ~0;
            foreach (var l in new[] { ghost, past, eyes }) if (l >= 0) mask &= ~(1 << l);
            cam.cullingMask = mask;
            var lightGo = new GameObject("_InspectLight") { hideFlags = HideFlags.HideAndDontSave };
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 6f;
            light.range = 12f;
            light.color = Color.white;

            var rt = new RenderTexture(480, 480, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(480, 480, TextureFormat.RGB24, false);
            int idx = 0;
            try
            {
                foreach (var t in targets)
                {
                    idx++;
                    var rends = t.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !r.name.Contains("Dupli")).ToList();
                    report.AppendLine($"## {idx:00} {Path(t, root.transform)}  active={t.gameObject.activeInHierarchy}  pos={t.position:F2} rotY={t.eulerAngles.y:F0}");
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = string.Join(", ", r.sharedMaterials.Select(m => m ? $"{m.name}[{(m.shader ? m.shader.name : "-")}; tex={(m.mainTexture ? m.mainTexture.name : "НЕТ")}]" : "NULL"));
                        report.AppendLine($"   {(r.gameObject.activeInHierarchy && r.enabled ? "+" : "-")} {r.name}: {mats}");
                    }
                    if (rends.Count == 0) { report.AppendLine("   (видимых мешей нет)"); continue; }
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    float size = Mathf.Max(b.extents.magnitude, 0.25f);
                    var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
                    if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                    var right = Vector3.Cross(Vector3.up, fwd);
                    int shot = 0;
                    foreach (var dir in new[] { fwd, -fwd, right, -right })
                    {
                        var d = (dir + Vector3.up * 0.45f).normalized;
                        cam.transform.position = b.center + d * size * 2.6f;
                        cam.transform.LookAt(b.center);
                        light.transform.position = cam.transform.position + Vector3.up * 0.3f;
                        Render(cam, rt);
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                        tex.Apply();
                        RenderTexture.active = null;
                        File.WriteAllBytes($"{OutDir}/{idx:00}_{Safe(t.name)}_{shot++}.png", tex.EncodeToPNG());
                    }
                }

                report.AppendLine();
                report.AppendLine("## Dupli-копии, которые ВИДНЫ (должно быть пусто):");
                foreach (var r in root.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && r.name.Contains("Dupli")))
                    report.AppendLine("   " + Path(r.transform, root.transform));
                report.AppendLine();
                report.AppendLine("## Двери:");
                foreach (var d in root.GetComponentsInChildren<Door>(true))
                    report.AppendLine($"   {d.name} startOpen={d.startOpen} pos={d.transform.position:F1}");
                report.AppendLine();
                report.AppendLine("## Меши без текстуры (материал не T_*):");
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(false).Where(r => r.enabled && r.sharedMaterials.Any(m => m && !m.mainTexture)))
                    report.AppendLine($"   {Path(r.transform, root.transform)}: {string.Join(", ", r.sharedMaterials.Where(m => m).Select(m => m.name))}");
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambMode;
                RenderSettings.ambientLight = amb;
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
            File.WriteAllText($"{OutDir}/report.txt", report.ToString(), Encoding.UTF8);
            Debug.Log($"[Jam] Inspect: {targets.Count} объектов, снимки и отчёт в {System.IO.Path.GetFullPath(OutDir)}");
        }

        static void Render(Camera cam, RenderTexture rt)
        {
            var req = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
            else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
        }

        static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string Safe(string s) => Regex.Replace(s, @"[^\w\-]", "_");
    }
}
