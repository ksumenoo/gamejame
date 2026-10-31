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
    /// Tools → Jam → Build WebGL: веб-билд для страницы джема.
    /// Настройки под любой хостинг: Gzip + распаковка в браузере (Brotli без заголовков сервера не запустится).
    /// Результат — Builds/WebGL и рядом zip с датой для заливки. Отчёт о весе — Builds/webgl_report.txt.
    /// </summary>
    public static class JamBuild
    {
        const string OutDir = "Builds/WebGL";
        public const string ProductName = "Выдержка";

        [MenuItem("Tools/Jam/Build WebGL")]
        public static void BuildWebGL()
        {
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                EditorUtility.DisplayDialog("Build WebGL", "Не удалось переключиться на WebGL. Установлен ли модуль WebGL Build Support?", "OK");
                return;
            }
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            PlayerSettings.productName = ProductName;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.runInBackground = false;

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0 || !scenes[0].EndsWith("MainMenu.unity"))
                Debug.LogWarning("[Build] Первая сцена не MainMenu — проверь Tools → Jam → Build Menu Scene.");

            if (Directory.Exists(OutDir)) Directory.Delete(OutDir, true);
            var t0 = DateTime.Now;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            });

            var sb = new StringBuilder();
            sb.AppendLine($"Результат: {report.summary.result}, за {(DateTime.Now - t0).TotalMinutes:F1} мин, ошибок {report.summary.totalErrors}, предупреждений {report.summary.totalWarnings}");
            sb.AppendLine("Сцены: " + string.Join(", ", scenes.Select(Path.GetFileNameWithoutExtension)));
            if (report.summary.result == BuildResult.Succeeded)
            {
                long total = 0;
                sb.AppendLine("\nФайлы:");
                foreach (var f in Directory.GetFiles(OutDir, "*", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length))
                {
                    long len = new FileInfo(f).Length;
                    total += len;
                    sb.AppendLine($"   {len / 1048576.0,7:F2} МБ  {f.Substring(OutDir.Length + 1)}");
                }
                sb.AppendLine($"Всего: {total / 1048576.0:F1} МБ");

                // что тяжелее всего внутри (по ассетам)
                sb.AppendLine("\nСамые тяжёлые ассеты (до сжатия):");
                foreach (var a in report.packedAssets.SelectMany(p => p.contents)
                             .GroupBy(c => c.sourceAssetPath).Select(g => (path: g.Key, size: g.Sum(c => (long)c.packedSize)))
                             .OrderByDescending(x => x.size).Take(25))
                    sb.AppendLine($"   {a.size / 1048576.0,7:F2} МБ  {a.path}");

                string zip = $"Builds/Vyderzhka_WebGL_{DateTime.Now:MMdd_HHmm}.zip";
                if (File.Exists(zip)) File.Delete(zip);
                ZipFile.CreateFromDirectory(OutDir, zip, System.IO.Compression.CompressionLevel.Optimal, false);
                sb.AppendLine($"\nАрхив для заливки: {Path.GetFullPath(zip)} ({new FileInfo(zip).Length / 1048576.0:F1} МБ)");
            }
            else
            {
                sb.AppendLine("\nОшибки:");
                foreach (var m in report.steps.SelectMany(s => s.messages).Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                    sb.AppendLine("   " + m.content);
            }
            Directory.CreateDirectory("Builds");
            File.WriteAllText("Builds/webgl_report.txt", sb.ToString(), Encoding.UTF8);
            Debug.Log("[Build] " + sb.ToString().Split('\n')[0] + ". Отчёт: " + Path.GetFullPath("Builds/webgl_report.txt"));
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
