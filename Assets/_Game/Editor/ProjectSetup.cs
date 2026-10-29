using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Одноразовая настройка структуры проекта для джема.
// Запуск: Tools → Jam → Setup Project Structure
public static class ProjectSetup
{
    static readonly string[] Folders =
    {
        "Assets/_Game/Scripts",
        "Assets/_Game/Editor",
        "Assets/Art/2D/Textures",
        "Assets/Art/2D/UI",
        "Assets/Art/3D/Models",
        "Assets/Art/Fonts",
        "Assets/Materials",
        "Assets/Shaders",
        "Assets/Prefabs",
        "Assets/Scenes",
        "Assets/Audio/Music",
        "Assets/Audio/SFX",
        "Assets/Audio/Ambient",
    };

    const string ScenesDir = "Assets/Scenes";

    [MenuItem("Tools/Jam/Setup Project Structure")]
    public static void Run()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        // 1. Папки (+ .gitkeep, чтобы git не терял пустые папки у остальных)
        foreach (var f in Folders)
        {
            Directory.CreateDirectory(f);
            var keep = Path.Combine(f, ".gitkeep");
            if (!File.Exists(keep)) File.WriteAllText(keep, "");
        }
        AssetDatabase.Refresh();

        // 2. Шрифты → Art/Fonts
        foreach (var guid in AssetDatabase.FindAssets("t:Font", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetDirectoryName(path)?.Replace('\\', '/') == "Assets")
                Log(AssetDatabase.MoveAsset(path, "Assets/Art/Fonts/" + Path.GetFileName(path)), "Move " + path);
        }

        // 3. Сцены: SampleScene → Game (GUID сохраняется), + Boot и MainMenu
        string game = $"{ScenesDir}/Game.unity";
        if (File.Exists($"{ScenesDir}/SampleScene.unity") && !File.Exists(game))
            Log(AssetDatabase.RenameAsset($"{ScenesDir}/SampleScene.unity", "Game"), "Rename SampleScene → Game");

        CreateSceneIfMissing($"{ScenesDir}/Boot.unity", NewSceneSetup.EmptyScene);
        CreateSceneIfMissing($"{ScenesDir}/MainMenu.unity", NewSceneSetup.DefaultGameObjects);
        CreateSceneIfMissing(game, NewSceneSetup.DefaultGameObjects);

        EditorBuildSettings.scenes = new[] { "Boot", "MainMenu", "Game" }
            .Select(n => new EditorBuildSettingsScene($"{ScenesDir}/{n}.unity", true))
            .ToArray();

        // 4. Мусор из шаблона
        if (AssetDatabase.IsValidFolder("Assets/TutorialInfo"))
            Log(AssetDatabase.DeleteAsset("Assets/TutorialInfo"), "Delete TutorialInfo");
        if (File.Exists("Assets/Readme.asset"))
            Log(AssetDatabase.DeleteAsset("Assets/Readme.asset"), "Delete Readme.asset");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorSceneManager.OpenScene(game);

        Debug.Log("[ProjectSetup] Готово: папки, сцены Boot/MainMenu/Game в Build Settings, шаблон удалён.");
    }

    static void CreateSceneIfMissing(string path, NewSceneSetup setup)
    {
        if (File.Exists(path)) return;
        var scene = EditorSceneManager.NewScene(setup, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, path);
    }

    static void Log(string error, string action)
    {
        if (string.IsNullOrEmpty(error)) Debug.Log($"[ProjectSetup] {action}: ok");
        else Debug.LogWarning($"[ProjectSetup] {action}: {error}");
    }

    static void Log(bool ok, string action)
    {
        if (ok) Debug.Log($"[ProjectSetup] {action}: ok");
        else Debug.LogWarning($"[ProjectSetup] {action}: failed");
    }
}
