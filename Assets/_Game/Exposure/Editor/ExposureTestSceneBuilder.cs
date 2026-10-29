#if UNITY_EDITOR
using System.IO;
using Jam.Exposure;
using Jam.Exposure.Debugging;
using Jam.Gameplay;
using Jam.Interaction;
using Jam.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Exposure.EditorTools
{
    /// <summary>
    /// Tools → Jam → Build Exposure Test Scene.
    /// Собирает тестовый стенд из примитивов: комната, призрак с маршрутом, камера-обскура,
    /// пластина на экране. Только для проверки механики — в билд не идёт.
    /// </summary>
    public static class ExposureTestSceneBuilder
    {
        const string Root = "Assets/_Game/Exposure/Debug";
        const string ScenePath = Root + "/ExposureTest.unity";
        const string GhostLayerName = "Ghost";

        [MenuItem("Tools/Jam/Build Exposure Test Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EnsureFolder(Root);
            int ghostLayer = EnsureLayer(GhostLayerName);
            if (ghostLayer < 0)
            {
                EditorUtility.DisplayDialog("Exposure Test", "Нет свободного слоя для Ghost (8–31).", "OK");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- материалы ----
            var matFloor = SaveMat("M_TestFloor", new Color(0.25f, 0.2f, 0.16f));
            var matWall = SaveMat("M_TestWall", new Color(0.18f, 0.17f, 0.2f));
            var matProp = SaveMat("M_TestProp", new Color(0.45f, 0.35f, 0.25f));
            var matGhost = SaveMat("M_TestGhost", new Color(0.95f, 0.95f, 0.9f));
            var matPlate = SavePlateMat();

            // ---- свет ----
            var sun = new GameObject("Directional Light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.6f;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            AddPointLight("Candle_A", new Vector3(-3f, 2.2f, 1.5f));
            AddPointLight("Candle_B", new Vector3(3f, 2.2f, 1.5f));

            // ---- комната 12×10, потолка нет ----
            var room = new GameObject("Room").transform;
            Box("Floor", room, new Vector3(0, -0.05f, 0), new Vector3(12, 0.1f, 10), matFloor);
            Box("Wall_Back", room, new Vector3(0, 1.5f, 5), new Vector3(12, 3, 0.2f), matWall);
            Box("Wall_Left", room, new Vector3(-6, 1.5f, 0), new Vector3(0.2f, 3, 10), matWall);
            Box("Wall_Right", room, new Vector3(6, 1.5f, 0), new Vector3(0.2f, 3, 10), matWall);
            // мебель-заглушки: фон, который должен остаться резким
            Box("Prop_Wardrobe", room, new Vector3(-4.5f, 1.05f, 4.4f), new Vector3(1.2f, 2.1f, 0.6f), matProp);
            Box("Prop_Table", room, new Vector3(2.5f, 0.375f, 3.5f), new Vector3(2f, 0.75f, 0.9f), matProp);
            Box("Prop_Clock", room, new Vector3(5.4f, 1f, 4.6f), new Vector3(0.5f, 2f, 0.3f), matProp);
            // колонна: призрак за ней пропадает из логики (проверка перекрытия)
            Box("Prop_Pillar", room, new Vector3(-1.5f, 1.5f, 1f), new Vector3(0.5f, 3f, 0.5f), matWall);

            // ---- камера-обскура ----
            var obscuraGo = new GameObject("CAM_Obscura");
            obscuraGo.transform.SetPositionAndRotation(new Vector3(0, 1.4f, -4.2f), Quaternion.Euler(5f, 0f, 0f));
            var obscuraCam = obscuraGo.AddComponent<Camera>();
            obscuraCam.fieldOfView = 65f;
            obscuraCam.nearClipPlane = 0.1f;
            obscuraCam.farClipPlane = 30f;
            obscuraCam.cullingMask = ~0; // видит всё, включая Ghost
            obscuraCam.clearFlags = CameraClearFlags.SolidColor;
            obscuraCam.backgroundColor = Color.black;
            obscuraCam.enabled = false;
            var obscura = obscuraGo.AddComponent<ObscuraCamera>();
            obscura.accumulateShader = Shader.Find("Hidden/Jam/ExposureAccumulate");
            obscura.occluders = ~(1 << ghostLayer);
            // ящик с коллайдером — по нему игрок жмёт E
            Box("CameraBox_Placeholder", obscuraGo.transform, new Vector3(0, 0, -0.25f), new Vector3(0.3f, 0.3f, 0.4f), matProp);
            Box("Tripod_Placeholder", obscuraGo.transform, new Vector3(0, -0.7f, -0.25f), new Vector3(0.08f, 1.4f, 0.08f), matProp, collider: false);
            var shutter = Box("Shutter_Placeholder", obscuraGo.transform, new Vector3(0, 0, -0.04f), new Vector3(0.12f, 0.12f, 0.02f), matWall, collider: false);
            var station = obscuraGo.AddComponent<CameraStation>();
            station.obscura = obscura;
            station.shutter = shutter.transform;
            station.shutterOpenOffset = new Vector3(0f, 0.12f, 0f);
            station.exposureSeconds = 6f;

            // ---- призрак ----
            var ghost = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            ghost.name = "Ghost";
            ghost.layer = ghostLayer;
            ghost.transform.position = new Vector3(-4f, 1f, 0f);
            ghost.GetComponent<Renderer>().sharedMaterial = matGhost;
            Object.DestroyImmediate(ghost.GetComponent<Collider>()); // через призрака видно, он не перекрывает сам себя
            ghost.AddComponent<ExposureSubject>();
            var walker = ghost.AddComponent<WaypointWalker>();
            walker.speed = 1.4f;

            var path = new GameObject("GhostPath").transform;
            var p1 = Point("GP_01", path, new Vector3(-4f, 1f, 0f));
            var p2 = Point("GP_02", path, new Vector3(4f, 1f, 0f));
            var p3 = Point("GP_03_Stop", path, new Vector3(1f, 1f, 2.5f));
            var p4 = Point("GP_04", path, new Vector3(-3f, 1f, 3f));
            walker.route.Add(new WaypointWalker.Stop { point = p1, wait = 0f });
            walker.route.Add(new WaypointWalker.Stop { point = p2, wait = 0f });
            walker.route.Add(new WaypointWalker.Stop { point = p3, wait = 6f, lookAt = obscuraGo.transform }); // стоит и смотрит в камеру
            walker.route.Add(new WaypointWalker.Stop { point = p4, wait = 1f });

            // ---- игрок: призрака глазом не видит ----
            var player = new GameObject("Player");
            player.transform.SetPositionAndRotation(new Vector3(2f, 0f, -3.5f), Quaternion.Euler(0f, -20f, 0f));
            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.9f, 0f);

            var pivot = new GameObject("CameraPivot").transform;
            pivot.SetParent(player.transform, false);
            pivot.localPosition = new Vector3(0f, 1.65f, 0f);

            var mainGo = new GameObject("Main Camera") { tag = "MainCamera" };
            mainGo.transform.SetParent(pivot, false);
            var mainCam = mainGo.AddComponent<Camera>();
            mainCam.cullingMask = ~(1 << ghostLayer);
            mainCam.nearClipPlane = 0.05f;
            mainCam.fieldOfView = 70f;
            mainGo.AddComponent<AudioListener>();

            var fpc = player.AddComponent<FirstPersonController>();
            fpc.cameraPivot = pivot;
            var interactor = player.AddComponent<Interactor>();
            interactor.viewCamera = mainCam;
            interactor.mask = ~(1 << ghostLayer);
            player.AddComponent<PlateInventory>();

            // ---- UI: пластина в углу ----
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var plateGo = new GameObject("PlateView", typeof(RectTransform), typeof(RawImage));
            plateGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)plateGo.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-40f, 40f);
            rt.sizeDelta = new Vector2(640f, 480f);
            var raw = plateGo.GetComponent<RawImage>();
            raw.material = matPlate;

            // прицел-точка
            var dotGo = new GameObject("Crosshair", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(canvasGo.transform, false);
            var dotRt = (RectTransform)dotGo.transform;
            dotRt.anchorMin = dotRt.anchorMax = dotRt.pivot = new Vector2(0.5f, 0.5f);
            dotRt.sizeDelta = new Vector2(6f, 6f);
            var dot = dotGo.GetComponent<Image>();
            dot.color = new Color(0.9f, 0.85f, 0.75f, 0.7f);
            dot.raycastTarget = false;

            // подсказка взаимодействия
            var promptGo = new GameObject("InteractionPrompt", typeof(RectTransform), typeof(Text));
            promptGo.transform.SetParent(canvasGo.transform, false);
            var promptRt = (RectTransform)promptGo.transform;
            promptRt.anchorMin = promptRt.anchorMax = promptRt.pivot = new Vector2(0.5f, 0.5f);
            promptRt.anchoredPosition = new Vector2(0f, -60f);
            promptRt.sizeDelta = new Vector2(800f, 60f);
            var promptText = promptGo.GetComponent<Text>();
            promptText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            promptText.fontSize = 32;
            promptText.alignment = TextAnchor.MiddleCenter;
            promptText.raycastTarget = false;
            var promptUi = canvasGo.AddComponent<InteractionPromptUI>();
            promptUi.interactor = interactor;
            promptUi.label = promptText;

            // ---- тестер ----
            var tester = new GameObject("ExposureTester").AddComponent<ExposureTester>();
            tester.obscura = obscura;
            tester.plateView = raw;
            tester.plateMaterial = matPlate;
            tester.exposureSeconds = 5f;
            tester.developSeconds = 2.5f;
            tester.releasePrevious = false; // пластины забирает игрок

            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = obscuraGo;
            Debug.Log("[Jam] Тестовая сцена выдержки собрана: " + ScenePath + ". Play → подойди к камере и жми E (или T/Y — выдержка без подхода).");
        }

        // ---------- helpers ----------

        static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material mat, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static Transform Point(string name, Transform parent, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.position = pos;
            return t;
        }

        static void AddPointLight(string name, Vector3 pos)
        {
            var l = new GameObject(name).AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.78f, 0.5f);
            l.range = 7f;
            l.intensity = 2.5f;
            l.transform.position = pos;
        }

        static Material SaveMat(string name, Color color)
        {
            string path = $"{Root}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material SavePlateMat()
        {
            string path = $"{Root}/M_PlateDisplay.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Jam/PlateDisplay");
                if (shader == null)
                {
                    Debug.LogError("[Jam] Не найден шейдер Jam/PlateDisplay — проверь, что он скомпилировался.");
                    return null;
                }
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            Directory.CreateDirectory(path);
            AssetDatabase.Refresh();
        }

        static int EnsureLayer(string name)
        {
            int existing = LayerMask.NameToLayer(name);
            if (existing >= 0) return existing;

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            for (int i = 8; i < 32; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(p.stringValue)) continue;
                p.stringValue = name;
                tagManager.ApplyModifiedProperties();
                return i;
            }
            return -1;
        }
    }
}
#endif
