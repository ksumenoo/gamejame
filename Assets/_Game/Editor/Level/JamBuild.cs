using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.EditorTools
{
    /// <summary>
    /// Билды для джема — один пункт меню на платформу и один на обе сразу:
    /// Tools → Jam → Build WebGL  → Builds/WebGL   (Gzip + распаковка в браузере: работает на любом хостинге)
    /// Tools → Jam → Build Windows → Builds/Windows/Vyderzhka.exe
    /// Рядом с каждым — zip с датой для заливки и отчёт Builds/{платформа}_report.txt: вес, ошибки, самые тяжёлые ассеты.
    /// </summary>
    public static class JamBuild
    {
        public const string ProductName = "Выдержка";

        [MenuItem("Tools/Jam/Build WebGL")]
        public static void BuildWebGL() => Build(BuildTarget.WebGL);

        [MenuItem("Tools/Jam/Build Windows")]
        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64);

        [MenuItem("Tools/Jam/Build Both (WebGL + Windows)")]
        public static void BuildBoth()
        {
            // сначала Windows: после него редактор останется на WebGL, как и был
            if (Build(BuildTarget.StandaloneWindows64)) Build(BuildTarget.WebGL);
        }

        static bool Build(BuildTarget target)
        {
            bool web = target == BuildTarget.WebGL;
            string label = web ? "WebGL" : "Windows";
            string dir = "Builds/" + label;
            string location = web ? dir : dir + "/Vyderzhka.exe";
            var group = BuildPipeline.GetBuildTargetGroup(target);

            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                EditorUtility.DisplayDialog("Build " + label, $"Модуль {label} Build Support не установлен (Unity Hub → Installs → Add modules).", "OK");
                return false;
            }
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            if (EditorUserBuildSettings.activeBuildTarget != target && !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target)) return false;

            PlayerSettings.productName = ProductName;
            PlayerSettings.runInBackground = false;
            if (web)
            {
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = true;
                PlayerSettings.WebGL.dataCaching = true;
                PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            }
            else
            {
                PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
                PlayerSettings.resizableWindow = true;
                PlayerSettings.visibleInBackground = false;
            }

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0 || !scenes[0].EndsWith("MainMenu.unity"))
                Debug.LogWarning("[Build] Первая сцена не MainMenu — проверь Tools → Jam → Build Menu Scene.");

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var t0 = DateTime.Now;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = location,
                target = target,
                targetGroup = group,
                options = BuildOptions.None,
            });

            var sb = new StringBuilder();
            sb.AppendLine($"{label}: {report.summary.result}, за {(DateTime.Now - t0).TotalMinutes:F1} мин, ошибок {report.summary.totalErrors}, предупреждений {report.summary.totalWarnings}");
            sb.AppendLine("Сцены: " + string.Join(", ", scenes.Select(Path.GetFileNameWithoutExtension)));
            bool ok = report.summary.result == BuildResult.Succeeded;
            if (ok)
            {
                // отладочные символы в архив не кладём
                foreach (var junk in Directory.GetDirectories(dir, "*DoNotShip*")) Directory.Delete(junk, true);
                long total = 0;
                sb.AppendLine("\nФайлы:");
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).Take(15))
                    sb.AppendLine($"   {new FileInfo(f).Length / 1048576.0,7:F2} МБ  {f.Substring(dir.Length + 1)}");
                total = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                sb.AppendLine($"Всего: {total / 1048576.0:F1} МБ");

                sb.AppendLine("\nСамые тяжёлые ассеты (до сжатия):");
                foreach (var a in report.packedAssets.SelectMany(p => p.contents)
                             .GroupBy(c => c.sourceAssetPath).Select(g => (path: g.Key, size: g.Sum(c => (long)c.packedSize)))
                             .OrderByDescending(x => x.size).Take(25))
                    sb.AppendLine($"   {a.size / 1048576.0,7:F2} МБ  {a.path}");

                string zip = $"Builds/Vyderzhka_{label}_{DateTime.Now:MMdd_HHmm}.zip";
                if (File.Exists(zip)) File.Delete(zip);
                ZipFile.CreateFromDirectory(dir, zip, System.IO.Compression.CompressionLevel.Optimal, false);
                sb.AppendLine($"\nАрхив для заливки: {Path.GetFullPath(zip)} ({new FileInfo(zip).Length / 1048576.0:F1} МБ)");
            }
            else
            {
                sb.AppendLine("\nОшибки:");
                foreach (var m in report.steps.SelectMany(s => s.messages).Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                    sb.AppendLine("   " + m.content);
            }
            Directory.CreateDirectory("Builds");
            string rep = $"Builds/{label.ToLowerInvariant()}_report.txt";
            File.WriteAllText(rep, sb.ToString(), Encoding.UTF8);
            Debug.Log("[Build] " + sb.ToString().Split('\n')[0] + ". Отчёт: " + Path.GetFullPath(rep));
            return ok;
        }
    }

    /// <summary>
    /// В билд не идут отладочные объекты: всё, что в корне сцены начинается с «[Graphy]» (счётчик FPS).
    /// В редакторе и в Play они остаются.
    /// </summary>
    public class StripDebugObjects : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return; // Play в редакторе — не трогаем
            foreach (var go in scene.GetRootGameObjects())
                if (go.name.StartsWith("[Graphy]"))
                {
                    string n = go.name;
                    UnityEngine.Object.DestroyImmediate(go);
                    Debug.Log($"[Build] {scene.name}: убран отладочный {n}");
                }
        }
    }
}
