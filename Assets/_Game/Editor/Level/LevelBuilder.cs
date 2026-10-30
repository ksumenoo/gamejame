using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jam.Exposure;
using Jam.Gameplay;
using Jam.Interaction;
using Jam.Player;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Jam.EditorTools
{
    /// <summary>
    /// Tools → Jam → Build Level From House.
    /// Берёт House.fbx с метками из Blender и собирает в открытой сцене объект LEVEL_Generated:
    /// дом, камеры-станции (CAM_xx), игрока (SPAWN_Player), призрака с маршрутом (GHOST_xx, STOP_GHOST_xx),
    /// свет по комнатам, NavMesh, интерфейс. Повторный запуск пересобирает только LEVEL_Generated —
    /// всё, что ты добавил в сцену вне него, остаётся.
    /// </summary>
    public static class LevelBuilder
    {
        const string RootName = "LEVEL_Generated";
        const string MatFolder = "Assets/_Game/Level";
        const string GhostLayerName = "Ghost";
        const string PlateMatPath = "Assets/_Game/Exposure/Debug/M_PlateDisplay.mat";

        [MenuItem("Tools/Jam/Build Level From House")]
        public static void Build()
        {
            AssetDatabase.ImportAsset(HouseModelPostprocessor.HousePath, ImportAssetOptions.ForceUpdate);
            var housePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HouseModelPostprocessor.HousePath);
            if (!housePrefab)
            {
                EditorUtility.DisplayDialog("Build Level", "Не найден " + HouseModelPostprocessor.HousePath + ". Нажми Export House to Unity в Blender.", "OK");
                return;
            }

            int ghostLayer = EnsureLayer(GhostLayerName);
            if (ghostLayer < 0)
            {
                EditorUtility.DisplayDialog("Build Level", "Нет свободного слоя для Ghost.", "OK");
                return;
            }

            var scene = EditorSceneManager.GetActiveScene();
            var old = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
            if (old) Object.DestroyImmediate(old);

            DisableForeignMainCameras(scene);
            EnsureFolder(MatFolder);
            var matWall = SaveMat("M_Level_Wall", new Color(0.32f, 0.29f, 0.27f));
            var matFloor = SaveMat("M_Level_Floor", new Color(0.36f, 0.27f, 0.19f));
            var matProp = SaveMat("M_Level_Prop", new Color(0.45f, 0.35f, 0.25f));
            var matDark = SaveMat("M_Level_Dark", new Color(0.12f, 0.11f, 0.1f));
            var matGhost = SaveMat("M_Level_GhostPlaceholder", new Color(0.95f, 0.95f, 0.9f));

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Level");

            // ---------- дом ----------
            var house = (GameObject)PrefabUtility.InstantiatePrefab(housePrefab, root.transform);
            house.name = "House";
            foreach (var r in house.GetComponentsInChildren<MeshRenderer>())
            {
                r.sharedMaterial = r.name.StartsWith("Plane") || r.name.StartsWith("Floor") ? matFloor : matWall;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ContributeGI);
            }

            var markers = house.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => t.name)
                .ToDictionary(g => g.Key, g => g.First());

            // ---------- камеры-станции ----------
            var stationsRoot = new GameObject("Stations").transform;
            stationsRoot.SetParent(root.transform, false);
            var camNames = markers.Keys.Where(n => Regex.IsMatch(n, @"^CAM_\d+$")).OrderBy(n => n).ToList();
            foreach (var camName in camNames)
            {
                var m = markers[camName];
                Vector3 dir = markers.TryGetValue(camName + "_LOOK", out var look) ? look.position - m.position : m.forward;
                CreateStation(camName, m.position, dir, stationsRoot, ghostLayer, matProp, matDark);
            }

            // ---------- игрок ----------
            Vector3 spawnPos = Vector3.zero, spawnDir = Vector3.forward;
            if (markers.TryGetValue("SPAWN_Player", out var spawn))
            {
                spawnPos = spawn.position;
                if (markers.TryGetValue("SPAWN_Player_LOOK", out var spawnLook)) spawnDir = spawnLook.position - spawn.position;
            }
            else Debug.LogWarning("[Jam] Нет метки SPAWN_Player, игрок в начале координат.");
            var (player, interactor, inventory) = CreatePlayer(spawnPos, spawnDir, root.transform, ghostLayer);

            // ---------- призрак ----------
            var routeMarkers = markers.Values
                .Select(t => (t, m: Regex.Match(t.name, @"^(STOP_)?GHOST_(\d+)$")))
                .Where(x => x.m.Success)
                .OrderBy(x => int.Parse(x.m.Groups[2].Value))
                .ToList();
            if (routeMarkers.Count > 0)
                CreateGhost(routeMarkers.Select(x => (x.t, x.m.Groups[1].Success)).ToList(), root.transform, ghostLayer, matGhost);
            else Debug.LogWarning("[Jam] Нет меток GHOST_xx, призрак не создан.");

            // ---------- свет и атмосфера ----------
            CreateRoomLights(house, root.transform);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.07f, 0.065f, 0.06f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.07f;
            RenderSettings.fogColor = new Color(0.02f, 0.02f, 0.025f);
            RenderSettings.skybox = null;

            // ---------- NavMesh (строится при старте) ----------
            var surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~(1 << ghostLayer);
            root.AddComponent<RuntimeNavMeshBake>();

            // ---------- интерфейс ----------
            CreateHud(root.transform, interactor, inventory);

            // ---------- Graphy: FPS и память, чтобы следить за WebGL ----------
            var graphyPath = AssetDatabase.FindAssets("Graphy t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "[Graphy]");
            if (graphyPath != null)
            {
                var graphy = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(graphyPath), root.transform);
                graphy.name = "[Graphy] (debug, удалить перед релизом)";
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root;
            Debug.Log($"[Jam] Уровень собран: {camNames.Count} камер, {routeMarkers.Count} точек маршрута. Сохрани сцену (Ctrl+S).");
        }

        // ================= части уровня =================

        const string CameraModelPath = "Assets/Art/3D/Models/Props/camera.fbx";
        const float LensHeight = 1.4f;

        /// <summary>
        /// Станция: корень на полу (CameraStation + коллайдер для E), модель внутри,
        /// дочерний Lens с Camera + ObscuraCamera на уровне отверстия.
        /// Если есть camera.fbx — ставит её, иначе кубики-заглушки.
        /// </summary>
        static void CreateStation(string name, Vector3 markerPos, Vector3 dir, Transform parent, int ghostLayer, Material matProp, Material matDark)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.forward;
            dir.Normalize();

            float floorY = markerPos.y - LensHeight;
            Physics.SyncTransforms();
            if (Physics.Raycast(markerPos, Vector3.down, out var hit, 4f, ~(1 << ghostLayer), QueryTriggerInteraction.Ignore))
                floorY = hit.point.y;

            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(new Vector3(markerPos.x, floorY, markerPos.z), Quaternion.LookRotation(dir, Vector3.up));

            Transform shutter, body;
            Vector3 lensWorld;
            Vector3 slideWorld = Vector3.zero;

            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraModelPath);
            if (modelPrefab)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab, root.transform);
                model.name = "Model";
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                body = model.transform;
                shutter = FindDeep(model.transform, "Shutter");
                if (!shutter) Debug.LogWarning("[Jam] В camera.fbx нет объекта 'Shutter' — заслонка не будет двигаться.");

                // разворачиваем модель так, чтобы заслонка (перед ящика) смотрела по направлению метки
                var b = RenderBounds(model);
                var shutterR = shutter ? shutter.GetComponent<Renderer>() : null;
                Renderer boxR = null;
                if (shutterR)
                {
                    // ящик = самая крупная деталь на высоте заслонки (штатив ниже — его не берём)
                    float sy = shutterR.bounds.center.y;
                    boxR = model.GetComponentsInChildren<Renderer>()
                        .Where(r => r != shutterR && r.bounds.min.y <= sy && r.bounds.max.y >= sy)
                        .OrderByDescending(r => r.bounds.size.x * r.bounds.size.y * r.bounds.size.z)
                        .FirstOrDefault();
                    Vector3 boxC = boxR ? boxR.bounds.center : b.center;

                    // перед ящика = нормаль тонкой заслонки, знак — от центра ящика к заслонке
                    var sb = shutterR.bounds;
                    Vector3 front = sb.size.x < sb.size.z
                        ? Vector3.right * Mathf.Sign(sb.center.x - boxC.x)
                        : Vector3.forward * Mathf.Sign(sb.center.z - boxC.z);
                    model.transform.Rotate(0f, Vector3.SignedAngle(front, dir, Vector3.up), 0f, Space.World);
                    b = RenderBounds(model);
                    boxC = boxR ? boxR.bounds.center : b.center;
                    sb = shutterR.bounds;
                    lensWorld = sb.center + dir * 0.03f; // origin заслонки может быть где угодно, берём центр геометрии

                    // заслонка уезжает вбок по плоскости передней стенки — к центру ящика, на свою ширину
                    Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
                    float side = Vector3.Dot(sb.center - boxC, right);
                    float width = Mathf.Abs(sb.size.x * right.x) + Mathf.Abs(sb.size.z * right.z);
                    slideWorld = right * (side > 0f ? -1f : 1f) * width * 1.05f;
                }
                else
                {
                    lensWorld = new Vector3(b.center.x, b.center.y, b.center.z) + dir * (Mathf.Max(b.extents.x, b.extents.z) + 0.03f);
                }

                float lensH = lensWorld.y - floorY;
                if (lensH < 0.8f || lensH > 1.7f)
                    Debug.LogWarning($"[Jam] Отверстие камеры на высоте {lensH:0.00} м — проверь, что низ ножек штатива в Blender на z = 0.");

                // коллайдер только на ящике — точно по мешу и поворачивается вместе с ним
                if (boxR) boxR.gameObject.AddComponent<BoxCollider>();
                else
                {
                    var col = root.AddComponent<BoxCollider>();
                    var local = new Bounds(root.transform.InverseTransformPoint(b.center), Vector3.zero);
                    foreach (var c in Corners(b)) local.Encapsulate(root.transform.InverseTransformPoint(c));
                    col.center = local.center;
                    col.size = local.size;
                }
            }
            else
            {
                var model = new GameObject("Model_Placeholder").transform;
                model.SetParent(root.transform, false);
                body = model;
                Box("Box", model, new Vector3(0, LensHeight, -0.25f), new Vector3(0.3f, 0.3f, 0.4f), matProp, true);
                Box("Tripod", model, new Vector3(0, LensHeight * 0.5f, -0.25f), new Vector3(0.08f, LensHeight, 0.08f), matProp, false);
                shutter = Box("Shutter", model, new Vector3(0.04f, LensHeight, -0.04f), new Vector3(0.12f, 0.12f, 0.02f), matDark, false).transform;
                lensWorld = root.transform.position + Vector3.up * LensHeight;
                slideWorld = -root.transform.right * 0.13f;
            }

            // ---- объектив ----
            var lens = new GameObject("Lens");
            lens.transform.SetParent(root.transform, false);
            lens.transform.SetPositionAndRotation(lensWorld, root.transform.rotation);

            var cam = lens.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 25f;
            cam.cullingMask = ~0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.enabled = false;

            var obscura = lens.AddComponent<ObscuraCamera>();
            obscura.accumulateShader = Shader.Find("Hidden/Jam/ExposureAccumulate");
            obscura.occluders = ~(1 << ghostLayer);

            // ---- станция ----
            var station = root.AddComponent<CameraStation>();
            station.obscura = obscura;
            station.body = body;
            station.shutter = shutter;
            station.exposureSeconds = 6f;
            if (shutter)
                station.shutterOpenOffset = shutter.parent ? shutter.parent.InverseTransformVector(slideWorld) : slideWorld;
        }

        static Transform FindDeep(Transform t, string name)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
                if (c.name == name) return c;
            return null;
        }

        static Bounds RenderBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        static (GameObject, Interactor, PlateInventory) CreatePlayer(Vector3 pos, Vector3 dir, Transform parent, int ghostLayer)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.forward;

            var player = new GameObject("Player");
            player.transform.SetParent(parent, false);
            player.transform.SetPositionAndRotation(pos + Vector3.up * 0.05f, Quaternion.LookRotation(dir.normalized));

            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.9f, 0f);

            var pivot = new GameObject("CameraPivot").transform;
            pivot.SetParent(player.transform, false);
            pivot.localPosition = new Vector3(0f, 1.65f, 0f);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(pivot, false);
            var cam = camGo.AddComponent<Camera>();
            cam.cullingMask = ~(1 << ghostLayer);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 40f;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            camGo.AddComponent<AudioListener>();

            var fpc = player.AddComponent<FirstPersonController>();
            fpc.cameraPivot = pivot;
            var interactor = player.AddComponent<Interactor>();
            interactor.viewCamera = cam;
            interactor.mask = ~(1 << ghostLayer);
            var inv = player.AddComponent<PlateInventory>();
            player.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // капсула игрока не должна дырявить NavMesh
            return (player, interactor, inv);
        }

        static void CreateGhost(List<(Transform t, bool stop)> route, Transform parent, int ghostLayer, Material mat)
        {
            var ghost = new GameObject("Ghost") { layer = ghostLayer };
            ghost.transform.SetParent(parent, false);
            ghost.transform.position = route[0].t.position;

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Visual_Placeholder";
            visual.layer = ghostLayer;
            visual.transform.SetParent(ghost.transform, false);
            visual.transform.localPosition = new Vector3(0f, 1f, 0f);
            visual.GetComponent<Renderer>().sharedMaterial = mat;
            Object.DestroyImmediate(visual.GetComponent<Collider>());

            var subject = ghost.AddComponent<ExposureSubject>();
            subject.probe = visual.transform;

            var agent = ghost.AddComponent<NavMeshAgent>();
            agent.radius = 0.3f;
            agent.height = 1.9f;
            agent.speed = 1.2f;
            agent.angularSpeed = 300f;
            agent.acceleration = 6f;
            agent.stoppingDistance = 0.1f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
            agent.enabled = false; // GhostAI включит после постройки NavMesh

            var ai = ghost.AddComponent<GhostAI>();
            foreach (var (t, stop) in route)
                ai.route.Add(new GhostAI.Waypoint { point = t, canStop = stop });
        }

        static void CreateRoomLights(GameObject house, Transform parent)
        {
            var lightsRoot = new GameObject("RoomLights").transform;
            lightsRoot.SetParent(parent, false);
            foreach (var r in house.GetComponentsInChildren<MeshRenderer>())
            {
                if (!(r.name.StartsWith("Plane") || r.name.StartsWith("Floor"))) continue;
                var b = r.bounds;
                var l = new GameObject("Light_" + r.name).AddComponent<Light>();
                l.transform.SetParent(lightsRoot, false);
                l.transform.position = new Vector3(b.center.x, b.max.y + 2.4f, b.center.z);
                l.type = LightType.Point;
                l.color = new Color(1f, 0.75f, 0.48f);
                l.range = Mathf.Max(b.size.x, b.size.z) * 0.9f + 1f;
                l.intensity = 1.8f;
                l.shadows = LightShadows.None;
            }
        }

        static void CreateHud(Transform parent, Interactor interactor, PlateInventory inventory)
        {
            var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(parent, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var dot = UiChild<Image>("Crosshair", canvasGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6, 6));
            dot.color = new Color(0.9f, 0.85f, 0.75f, 0.7f);
            dot.raycastTarget = false;

            var prompt = UiChild<Text>("InteractionPrompt", canvasGo.transform, new Vector2(0.5f, 0.5f), new Vector2(0, -60), new Vector2(800, 60));
            prompt.font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/Fonts/SpecialElite-Regular.ttf") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            prompt.fontSize = 30;
            prompt.alignment = TextAnchor.MiddleCenter;
            prompt.raycastTarget = false;
            var promptUi = canvasGo.AddComponent<InteractionPromptUI>();
            promptUi.interactor = interactor;
            promptUi.label = prompt;

            var plateView = UiChild<RawImage>("PlateView", canvasGo.transform, new Vector2(1f, 0f), new Vector2(-40, 40), new Vector2(480, 360));
            plateView.rectTransform.pivot = new Vector2(1f, 0f);
            plateView.raycastTarget = false;
            var hud = canvasGo.AddComponent<PlateHUD>();
            hud.inventory = inventory;
            hud.view = plateView;
            hud.plateMaterial = AssetDatabase.LoadAssetAtPath<Material>(PlateMatPath);
            if (!hud.plateMaterial)
            {
                var sh = Shader.Find("Jam/PlateDisplay");
                if (sh)
                {
                    var m = new Material(sh);
                    AssetDatabase.CreateAsset(m, MatFolder + "/M_PlateDisplay.mat");
                    hud.plateMaterial = m;
                }
            }
        }

        // ================= helpers =================

        static T UiChild<T>(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size) where T : Graphic
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(T));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return go.GetComponent<T>();
        }

        static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static void DisableForeignMainCameras(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var g in scene.GetRootGameObjects())
            {
                foreach (var cam in g.GetComponentsInChildren<Camera>(true))
                {
                    if (!cam.CompareTag("MainCamera") || !cam.gameObject.activeSelf) continue;
                    cam.gameObject.SetActive(false);
                    Debug.Log($"[Jam] Выключена лишняя камера '{cam.name}' — игрок принесёт свою.");
                }
            }
        }

        static Material SaveMat(string name, Color color)
        {
            string path = $"{MatFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            mat.color = color;
            EditorUtility.SetDirty(mat);
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
