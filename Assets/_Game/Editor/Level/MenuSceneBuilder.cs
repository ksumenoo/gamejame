using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jam.Gameplay;
using Jam.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Jam.EditorTools
{
    /// <summary>
    /// Tools → Jam → Build Menu Scene. Собирает сцену MainMenu. Есть арт Resources/Menu/menu_bg.png — меню 2D:
    /// картинка, кнопки, звук, без 3D-сцены. Нет арта — запасной вариант: фон-натюрморт, камера, свет, PS1-вид,
    /// и ставит MainMenu первой в Build Settings (перед Game).
    /// Фон: Menu.fbx от 3D-шника (метки MENU_CAM, MENU_CAM_LOOK, MENU_LIGHT_RED, MENU_CANDLE…),
    /// а пока его нет — угол проявочной из готовых пропов: камера на штативе смотрит на игрока.
    /// Если художник положит арт Resources/Menu/menu_bg.png — меню покажет его поверх сцены.
    /// </summary>
    public static partial class LevelBuilder
    {
        const string MenuScenePath = "Assets/Scenes/MainMenu.unity";
        const string GameScenePath = "Assets/Scenes/Game.unity";
        const string MenuModelPath = "Assets/Art/3D/Models/Menu.fbx";
        const string MenuRootName = "MENU_Generated";
        const string MenuArtPath = "Assets/Resources/Menu/menu_bg.png";

        [MenuItem("Tools/Jam/Build Menu Scene")]
        public static void BuildMenuScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = File.Exists(MenuScenePath)
                ? EditorSceneManager.OpenScene(MenuScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // сцену целиком ведёт сборщик: старую сборку и камеры/свет из шаблона — убираем
            foreach (var g in scene.GetRootGameObjects())
                if (g.name == MenuRootName || g.GetComponentInChildren<Camera>(true) || g.GetComponentInChildren<Light>(true) || g.GetComponent<MainMenu>())
                    Object.DestroyImmediate(g);

            // ---------- 2D-меню: есть арт menu_bg — никакой 3D-сцены, только камера (чёрный фон), звук и меню ----------
            if (File.Exists(MenuArtPath))
            {
                var root2d = new GameObject(MenuRootName);
                var cam2d = new GameObject("MenuCamera") { tag = "MainCamera" }.AddComponent<Camera>();
                cam2d.transform.SetParent(root2d.transform, false);
                cam2d.clearFlags = CameraClearFlags.SolidColor;
                cam2d.backgroundColor = Color.black;
                cam2d.cullingMask = 0; // рисовать нечего: фон — картинка в интерфейсе
                cam2d.gameObject.AddComponent<AudioListener>();
                AddMenuAudio(root2d.transform);
                root2d.AddComponent<MainMenu>();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, MenuScenePath);
                EnsureMenuInBuild();
                Debug.Log("[Jam] Меню собрано (2D: фон menu_bg). Build Settings: MainMenu → Game.");
                return;
            }

            EnsureFolder(MatFolder);
            var matProp = SaveMat("M_Level_Prop", new Color(0.45f, 0.35f, 0.25f));
            var matDark = SaveMat("M_Level_Dark", new Color(0.12f, 0.11f, 0.1f));

            var root = new GameObject(MenuRootName);
            Vector3 camPos, lookAt;
            Vector3? redPos = null;
            var candleTops = new List<Vector3>();

            if (File.Exists(MenuModelPath)) AssetDatabase.ImportAsset(MenuModelPath, ImportAssetOptions.ForceUpdate);
            var menuModel = AssetDatabase.LoadAssetAtPath<GameObject>(MenuModelPath);
            if (menuModel)
            {
                var m = (GameObject)PrefabUtility.InstantiatePrefab(menuModel, root.transform);
                m.name = "Menu";
                foreach (var r in m.GetComponentsInChildren<MeshRenderer>()) ApplyPs1Materials(r, matProp);
                var markers = m.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
                camPos = markers.TryGetValue("MENU_CAM", out var c) ? c.position : new Vector3(0f, 1.4f, 0f);
                lookAt = markers.TryGetValue("MENU_CAM_LOOK", out var l) ? l.position : camPos + Vector3.forward;
                if (markers.TryGetValue("MENU_LIGHT_RED", out var red)) redPos = red.position;
                candleTops.AddRange(markers.Where(kv => kv.Key.StartsWith("MENU_CANDLE")).Select(kv => kv.Value.position));
                // канделябры внутри Menu.fbx — огоньки на верхушки свечей сами
                foreach (var cand in m.GetComponentsInChildren<Transform>(true).Where(t => t.name.ToLowerInvariant().Contains("candelabra") && t.GetComponent<MeshRenderer>()))
                    candleTops.AddRange(CandleTops(cand.gameObject));
                Debug.Log($"[Jam] Меню: фон из Menu.fbx, огоньков {candleTops.Count}{(redPos.HasValue ? ", красная лампа" : "")}.");
            }
            else
            {
                BuildMenuStillLife(root.transform, matProp, matDark, out camPos, out lookAt, out redPos, candleTops);
                Debug.Log("[Jam] Меню: Menu.fbx нет — собран натюрморт из пропов (камера, канделябр, верёвка с пластинами).");
            }

            // ---------- камера ----------
            var rig = new GameObject("MenuCameraRig").transform;
            rig.SetParent(root.transform, false);
            rig.SetPositionAndRotation(camPos, Quaternion.LookRotation(lookAt - camPos, Vector3.up));
            var camGo = new GameObject("MenuCamera") { tag = "MainCamera" };
            camGo.transform.SetParent(rig, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            camGo.AddComponent<AudioListener>();

            // ---------- свет ----------
            var candles = new List<Light>();
            int i = 0;
            foreach (var top in candleTops)
            {
                var l = MenuPointLight($"Candle_{++i:00}", root.transform, top + Vector3.up * 0.06f, new Color(1f, 0.7f, 0.4f), 3.5f, 1.2f);
                CreateFlameDot(root.transform, top, 0);
                candles.Add(l);
            }
            Light redLamp = redPos.HasValue
                ? MenuPointLight("RedLamp", root.transform, redPos.Value, new Color(0.95f, 0.12f, 0.08f), 6f, 1.4f)
                : null;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.04f, 0.035f, 0.035f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.09f;
            RenderSettings.fogColor = new Color(0.015f, 0.012f, 0.012f);
            RenderSettings.skybox = null;

            var amb = root.AddComponent<MenuAmbience>();
            amb.cameraRig = rig;
            amb.candles = candles.ToArray();
            amb.redLamp = redLamp;

            // ---------- PS1-вид, звук, меню ----------
            var settings = root.AddComponent<LevelSettings>();
            settings.mainCamera = cam;
            settings.outputMaterial = SaveShaderMat("M_LowResOutput", "Jam/LowResOutput");

            AddMenuAudio(root.transform);
            root.AddComponent<MainMenu>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MenuScenePath);
            EnsureMenuInBuild();
            Debug.Log("[Jam] Сцена меню собрана и сохранена. Build Settings: MainMenu → Game.");
        }

        /// <summary>Звуки интерфейса и музыка меню (трек 2 из Assets/Audio/Music).</summary>
        static void AddMenuAudio(Transform root)
        {
            var audio = new GameObject("JamAudio").AddComponent<Jam.Audio.JamAudio>();
            audio.transform.SetParent(root, false);
            audio.bank = LoadOrCreateSoundBank();

            var music = FindMusic(2);
            if (!music) return;
            var ms = new GameObject("MenuMusic").AddComponent<AudioSource>();
            ms.transform.SetParent(root, false);
            ms.clip = music;
            ms.loop = true;
            ms.playOnAwake = true;
            ms.spatialBlend = 0f;
            ms.volume = 0.6f;
        }

        /// <summary>MainMenu — первая сцена билда, Game — вторая. Остальное (Boot) — как было, после них.</summary>
        static void EnsureMenuInBuild()
        {
            var list = EditorBuildSettings.scenes.ToList();
            list.RemoveAll(s => s.path == MenuScenePath || s.path == GameScenePath);
            list.Insert(0, new EditorBuildSettingsScene(MenuScenePath, true));
            list.Insert(1, new EditorBuildSettingsScene(GameScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        static Light MenuPointLight(string name, Transform parent, Vector3 pos, Color color, float range, float intensity)
        {
            var l = new GameObject(name).AddComponent<Light>();
            l.transform.SetParent(parent, false);
            l.transform.position = pos;
            l.type = LightType.Point;
            l.color = color;
            l.range = range;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            return l;
        }

        /// <summary>
        /// Запасной фон: угол проявочной ночью. Камера на штативе смотрит прямо на игрока, рядом канделябр,
        /// на верёвке сохнут пластины, в глубине — красная лампа. Всё — из уже готовых пропов.
        /// Предметы в правой половине кадра, левая треть тёмная — под меню.
        /// </summary>
        static void BuildMenuStillLife(Transform root, Material matProp, Material matDark,
            out Vector3 camPos, out Vector3 lookAt, out Vector3? redPos, List<Vector3> candleTops)
        {
            camPos = new Vector3(0f, 1.4f, 0f);
            lookAt = new Vector3(0.35f, 1.1f, 3f);
            redPos = null;

            // пол и две стены
            MenuQuad("Floor", root, new Vector3(0f, 0f, 3f), Quaternion.Euler(90f, 0f, 0f), new Vector3(10f, 10f, 1f), TileMat("T_Floor_Boards"));
            MenuQuad("WallBack", root, new Vector3(0f, 2f, 4.6f), Quaternion.identity, new Vector3(10f, 4f, 1f), TileMat("T_Plaster"));
            MenuQuad("WallRight", root, new Vector3(3.3f, 2f, 2f), Quaternion.Euler(0f, 90f, 0f), new Vector3(10f, 4f, 1f), TileMat("T_Plaster"));

            // камера на штативе — объективом к игроку
            var camPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraModelPath);
            if (camPrefab)
            {
                var tripod = MenuProp(camPrefab, root, new Vector3(1.05f, 0f, 2.7f), matProp);
                var open = FindDeep(tripod.transform, "Shutter_Open");
                if (open) open.gameObject.SetActive(false);
                FaceToward(tripod, camPos - tripod.transform.position);
            }

            // канделябр
            var candelabra = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/candelabra.fbx");
            if (candelabra)
            {
                var c = MenuProp(candelabra, root, new Vector3(2.2f, 0f, 3.7f), matDark);
                candleTops.AddRange(CandleTops(c));
            }
            if (candleTops.Count == 0) candleTops.Add(new Vector3(2.2f, 1.25f, 3.7f));

            // стол с красной лампой в глубине
            var desk = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/desk.fbx");
            float deskTop = 0.8f;
            if (desk)
            {
                var d = MenuProp(desk, root, new Vector3(2.55f, 0f, 4.2f), matProp);
                d.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                deskTop = RenderBounds(d).max.y;
            }
            var lantern = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/lantern.fbx");
            if (lantern)
            {
                var l = MenuProp(lantern, root, new Vector3(2.6f, deskTop, 4.25f), matProp);
                redPos = RenderBounds(l).center;
            }
            else redPos = new Vector3(2.6f, deskTop + 0.25f, 4.25f);

            // верёвка с пластинами
            float ropeY = 1.95f, ropeZ = 4.45f;
            Box("Rope", root, new Vector3(1.6f, ropeY, ropeZ), new Vector3(2.6f, 0.012f, 0.012f), matDark, false);
            var plateMat = SaveMat("M_Menu_Plate", new Color(0.32f, 0.27f, 0.21f));
            var pin = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/prishepka.fbx");
            foreach (float x in new[] { 0.85f, 1.45f, 2.05f })
            {
                MenuQuad("Plate", root, new Vector3(x, ropeY - 0.12f, ropeZ - 0.01f), Quaternion.identity, new Vector3(0.24f, 0.18f, 1f), plateMat);
                if (pin) MenuProp(pin, root, new Vector3(x, ropeY - 0.04f, ropeZ - 0.02f), matProp);
            }
        }

        static GameObject MenuProp(GameObject prefab, Transform parent, Vector3 pos, Material fallback)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.position = pos;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>()) ApplyPs1Materials(r, fallback);
            return go;
        }

        static GameObject MenuQuad(string name, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.transform.SetParent(parent, false);
            q.transform.SetPositionAndRotation(pos, rot);
            q.transform.localScale = scale;
            q.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return q;
        }

        /// <summary>Повернуть камеру-проп объективом в нужную сторону: перед ящика — там, где заслонка (Shutter).</summary>
        static void FaceToward(GameObject model, Vector3 dir)
        {
            dir = Flat(dir);
            var shutter = FindDeep(model.transform, "Shutter");
            var sr = shutter ? shutter.GetComponent<Renderer>() : null;
            if (!sr)
            {
                model.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                return;
            }
            float sy = sr.bounds.center.y;
            var box = model.GetComponentsInChildren<Renderer>()
                .Where(r => r != sr && r.bounds.min.y <= sy && r.bounds.max.y >= sy)
                .OrderByDescending(r => r.bounds.size.x * r.bounds.size.y * r.bounds.size.z)
                .FirstOrDefault();
            Vector3 boxC = box ? box.bounds.center : RenderBounds(model).center;
            var sb = sr.bounds;
            Vector3 front = sb.size.x < sb.size.z
                ? Vector3.right * Mathf.Sign(sb.center.x - boxC.x)
                : Vector3.forward * Mathf.Sign(sb.center.z - boxC.z);
            model.transform.Rotate(0f, Vector3.SignedAngle(front, dir, Vector3.up), 0f, Space.World);
        }
    }
}
