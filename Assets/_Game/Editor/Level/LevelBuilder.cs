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
    /// дом, штативы (CAM_xx), сцены прошлого (SCENE_xx или заглушки перед штативами), игрока с ручной обскурой
    /// (SPAWN_Player), призрака (GHOST_xx, STOP_GHOST_xx), свет, NavMesh, интерфейс.
    /// Повторный запуск пересобирает только LEVEL_Generated — всё, что вне него, остаётся.
    /// </summary>
    public static class LevelBuilder
    {
        const string RootName = "LEVEL_Generated";
        const string MatFolder = "Assets/_Game/Level";
        const string PlateMatPath = "Assets/_Game/Exposure/Debug/M_PlateDisplay.mat";
        const string CameraModelPath = "Assets/Art/3D/Models/Props/camera.fbx";
        const float LensHeight = 1.4f;

        // слои: Ghost — Маргарет, Past — фигуры прошлого, PlayerOnly — то, что видит только глаз игрока
        static int s_ghost, s_past, s_playerOnly;
        static int MaskEyes => ~((1 << s_ghost) | (1 << s_past));
        static int MaskObscura => ~(1 << s_playerOnly);
        static int MaskSolid => ~((1 << s_ghost) | (1 << s_past) | (1 << s_playerOnly));

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

            s_ghost = EnsureLayer("Ghost");
            s_past = EnsureLayer("Past");
            s_playerOnly = EnsureLayer("PlayerOnly");
            if (s_ghost < 0 || s_past < 0 || s_playerOnly < 0)
            {
                EditorUtility.DisplayDialog("Build Level", "Нет свободных слоёв для Ghost / Past / PlayerOnly.", "OK");
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
            var matGhost = SaveMat("M_Level_GhostPlaceholder", new Color(0.7f, 0.12f, 0.1f)); // красный — проверка акцента на пластине
            var matPastAlive = SaveBillboardMat("M_Past_Alive_Placeholder", new Color(0.75f, 0.72f, 0.68f));
            var matPastDead = SaveBillboardMat("M_Past_Dead_Placeholder", new Color(1f, 1f, 1f));
            var matPlate = LoadOrCreatePlateMat();

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

            // ---------- штативы ----------
            var stationsRoot = new GameObject("Stations").transform;
            stationsRoot.SetParent(root.transform, false);
            var camNames = markers.Keys.Where(n => Regex.IsMatch(n, @"^CAM_\d+$")).OrderBy(n => n).ToList();
            var stations = new List<(CameraStation station, Vector3 dir, int index)>();
            foreach (var camName in camNames)
            {
                var m = markers[camName];
                Vector3 dir = markers.TryGetValue(camName + "_LOOK", out var look) ? look.position - m.position : m.forward;
                var st = CreateStation(camName, m.position, dir, stationsRoot, matProp, matDark);
                stations.Add((st, dir, int.Parse(camName.Substring(4))));
            }

            // ---------- сцены прошлого ----------
            var scenesRoot = new GameObject("PastScenes").transform;
            scenesRoot.SetParent(root.transform, false);
            foreach (var (station, dir, index) in stations)
            {
                string sceneMarker = $"SCENE_{index:00}";
                Vector3 anchor = markers.TryGetValue(sceneMarker, out var sm)
                    ? sm.position
                    : station.transform.position + Flat(dir) * 3f;
                CreatePastScene(index, anchor, station, scenesRoot, matPastAlive, matPastDead, isPlaceholder: sm == null);
            }

            // ---------- игрок ----------
            Vector3 spawnPos = Vector3.zero, spawnDir = Vector3.forward;
            if (markers.TryGetValue("SPAWN_Player", out var spawn))
            {
                spawnPos = spawn.position;
                if (markers.TryGetValue("SPAWN_Player_LOOK", out var spawnLook)) spawnDir = spawnLook.position - spawn.position;
            }
            else Debug.LogWarning("[Jam] Нет метки SPAWN_Player, игрок в начале координат.");
            var (player, fpc, eyes, interactor, inventory) = CreatePlayer(spawnPos, spawnDir, root.transform);

            // ---------- призрак ----------
            var routeMarkers = markers.Values
                .Select(t => (t, m: Regex.Match(t.name, @"^(STOP_)?GHOST_(\d+)$")))
                .Where(x => x.m.Success)
                .OrderBy(x => int.Parse(x.m.Groups[2].Value))
                .ToList();
            if (routeMarkers.Count > 0)
                CreateGhost(routeMarkers.Select(x => (x.t, x.m.Groups[1].Success)).ToList(), root.transform, matGhost);
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
            surface.layerMask = MaskSolid;
            root.AddComponent<RuntimeNavMeshBake>();

            // ---------- интерфейс и ручная обскура ----------
            var hud = CreateHud(root.transform, interactor, inventory, fpc, matPlate);
            var hand = player.AddComponent<HandObscura>();
            hand.eyes = eyes;
            hand.frame = hud.frame;
            hand.frameGroup = hud.frameGroup;
            hand.view = hud.glass;
            hand.viewMaterial = matPlate;
            hand.cullingMask = MaskObscura;

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
            Debug.Log($"[Jam] Уровень собран: {camNames.Count} штативов, {stations.Count} сцен прошлого, {routeMarkers.Count} точек маршрута. Сохрани сцену (Ctrl+S).");
        }

        // ================= штатив =================

        /// <summary>
        /// Корень на полу (CameraStation), модель внутри, дочерний Lens с Camera + ObscuraCamera на уровне отверстия.
        /// Если есть camera.fbx — ставит её, иначе кубики-заглушки.
        /// </summary>
        static CameraStation CreateStation(string name, Vector3 markerPos, Vector3 dir, Transform parent, Material matProp, Material matDark)
        {
            dir = Flat(dir);
            float floorY = FloorY(markerPos, markerPos.y - LensHeight);

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
                    lensWorld = b.center + dir * (Mathf.Max(b.extents.x, b.extents.z) + 0.03f);
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

            var lens = new GameObject("Lens");
            lens.transform.SetParent(root.transform, false);
            lens.transform.SetPositionAndRotation(lensWorld, root.transform.rotation);

            var cam = lens.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 25f;
            cam.cullingMask = MaskObscura;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.enabled = false;

            var obscura = lens.AddComponent<ObscuraCamera>();
            obscura.accumulateShader = Shader.Find("Hidden/Jam/ExposureAccumulate");
            obscura.occluders = MaskSolid;

            var station = root.AddComponent<CameraStation>();
            station.obscura = obscura;
            station.body = body;
            station.shutter = shutter;
            station.exposureSeconds = 6f;
            if (shutter)
                station.shutterOpenOffset = shutter.parent ? shutter.parent.InverseTransformVector(slideWorld) : slideWorld;
            return station;
        }

        // ================= сцена прошлого =================

        /// <summary>
        /// Заглушка сцены: три фигуры в ряд, две живые (качаются → смазаны), средняя мёртвая (неподвижна → резкая).
        /// Когда художники нарисуют силуэты, меняем текстуры у материалов фигур.
        /// </summary>
        static void CreatePastScene(int index, Vector3 anchor, CameraStation station, Transform parent, Material alive, Material dead, bool isPlaceholder)
        {
            float floorY = FloorY(anchor + Vector3.up * 1.5f, anchor.y);
            var go = new GameObject($"Scene_{index:00}" + (isPlaceholder ? "_Placeholder" : ""));
            go.transform.SetParent(parent, false);
            Vector3 toStation = Flat(station.transform.position - anchor);
            go.transform.SetPositionAndRotation(new Vector3(anchor.x, floorY, anchor.z), Quaternion.LookRotation(-toStation));
            go.layer = s_past;

            var scene = go.AddComponent<PastScene>();
            scene.index = index;
            scene.station = station;
            scene.startState = PastScene.State.Visible;

            float[] offsets = { -0.75f, 0f, 0.75f };
            for (int i = 0; i < offsets.Length; i++)
            {
                bool isDead = i == 1;
                var fig = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(fig.GetComponent<Collider>());
                fig.name = isDead ? "Figure_Dead" : "Figure_Alive";
                fig.layer = s_past;
                fig.transform.SetParent(go.transform, false);
                fig.transform.localPosition = new Vector3(offsets[i], 0.85f, 0f);
                fig.transform.localScale = new Vector3(0.6f, 1.7f, 1f);
                var mr = fig.GetComponent<MeshRenderer>();
                mr.sharedMaterial = isDead ? dead : alive;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                var pf = fig.AddComponent<PastFigure>();
                pf.alive = !isDead;
            }
        }

        // ================= игрок =================

        static (GameObject, FirstPersonController, Camera, Interactor, PlateInventory) CreatePlayer(Vector3 pos, Vector3 dir, Transform parent)
        {
            dir = Flat(dir);
            var player = new GameObject("Player");
            player.transform.SetParent(parent, false);
            player.transform.SetPositionAndRotation(pos + Vector3.up * 0.05f, Quaternion.LookRotation(dir));

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
            cam.cullingMask = MaskEyes;
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
            interactor.mask = MaskSolid;
            var inv = player.AddComponent<PlateInventory>();
            player.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // капсула игрока не должна дырявить NavMesh
            return (player, fpc, cam, interactor, inv);
        }

        // ================= призрак =================

        static void CreateGhost(List<(Transform t, bool stop)> route, Transform parent, Material mat)
        {
            var ghost = new GameObject("Ghost") { layer = s_ghost };
            ghost.transform.SetParent(parent, false);
            ghost.transform.position = route[0].t.position;

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Visual_Placeholder";
            visual.layer = s_ghost;
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

        // ================= свет =================

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

        // ================= интерфейс =================

        struct HudRefs
        {
            public RectTransform frame;
            public CanvasGroup frameGroup;
            public RawImage glass;
        }

        static HudRefs CreateHud(Transform parent, Interactor interactor, PlateInventory inventory, FirstPersonController fpc, Material plateMat)
        {
            var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(parent, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;

            // --- ручная обскура: рамка с матовым стеклом ---
            var frameGo = new GameObject("ObscuraFrame", typeof(RectTransform), typeof(CanvasGroup));
            frameGo.transform.SetParent(canvasGo.transform, false);
            var frame = (RectTransform)frameGo.transform;
            frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(0.5f, 0.5f);
            frame.sizeDelta = new Vector2(1000f, 780f);
            var frameGroup = frameGo.GetComponent<CanvasGroup>();
            frameGroup.blocksRaycasts = false;
            frameGroup.interactable = false;
            var border = UiChild<Image>("Border", frame, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 780f));
            border.color = new Color(0.09f, 0.065f, 0.05f, 1f);
            border.raycastTarget = false;
            var glass = UiChild<RawImage>("Glass", frame, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(940f, 705f));
            glass.raycastTarget = false;

            // --- прицел и подсказка ---
            var dot = UiChild<Image>("Crosshair", canvasGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6, 6));
            dot.color = new Color(0.9f, 0.85f, 0.75f, 0.7f);
            dot.raycastTarget = false;

            var prompt = UiChild<Text>("InteractionPrompt", canvasGo.transform, new Vector2(0.5f, 0.5f), new Vector2(0, -60), new Vector2(800, 60));
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/Fonts/SpecialElite-Regular.ttf");
            prompt.font = font ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            prompt.fontSize = 30;
            prompt.alignment = TextAnchor.MiddleCenter;
            prompt.raycastTarget = false;
            var promptUi = canvasGo.AddComponent<InteractionPromptUI>();
            promptUi.interactor = interactor;
            promptUi.label = prompt;

            // --- пластина на весь экран ---
            var viewerGo = new GameObject("PlateViewer", typeof(RectTransform), typeof(CanvasGroup));
            viewerGo.transform.SetParent(canvasGo.transform, false);
            Stretch((RectTransform)viewerGo.transform);
            var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
            backdrop.transform.SetParent(viewerGo.transform, false);
            Stretch((RectTransform)backdrop.transform);
            var bd = backdrop.GetComponent<Image>();
            bd.color = new Color(0f, 0f, 0f, 0.92f);
            bd.raycastTarget = false;
            var plateImg = UiChild<RawImage>("Plate", viewerGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1024f, 768f));
            plateImg.raycastTarget = false;

            var viewer = viewerGo.AddComponent<PlateViewer>();
            viewer.group = viewerGo.GetComponent<CanvasGroup>();
            viewer.image = plateImg;
            viewer.plateMaterial = plateMat;
            viewer.player = fpc;
            viewer.interactor = interactor;

            var hud = canvasGo.AddComponent<PlateHUD>();
            hud.inventory = inventory;
            hud.viewer = viewer;

            return new HudRefs { frame = frame, frameGroup = frameGroup, glass = glass };
        }

        // ================= helpers =================

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized;
        }

        static float FloorY(Vector3 from, float fallback)
        {
            Physics.SyncTransforms();
            return Physics.Raycast(from, Vector3.down, out var hit, 4f, MaskSolid, QueryTriggerInteraction.Ignore) ? hit.point.y : fallback;
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

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

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
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (!shader) shader = Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material SaveBillboardMat(string name, Color color)
        {
            string path = $"{MatFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                var shader = Shader.Find("Jam/Billboard");
                if (!shader)
                {
                    Debug.LogError("[Jam] Не найден шейдер Jam/Billboard.");
                    shader = Shader.Find("Universal Render Pipeline/Unlit");
                }
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material LoadOrCreatePlateMat()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(PlateMatPath);
            if (mat) return mat;
            string path = MatFolder + "/M_PlateDisplay.mat";
            mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat) return mat;
            var sh = Shader.Find("Jam/PlateDisplay");
            if (!sh) return null;
            mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, path);
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
