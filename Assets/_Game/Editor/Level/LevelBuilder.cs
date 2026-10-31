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
            var matHungPlate = SaveUnlitMat("M_HungPlate");

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Level");

            // ---------- дом ----------
            var house = (GameObject)PrefabUtility.InstantiatePrefab(housePrefab, root.transform);
            house.name = "House";
            foreach (var r in house.GetComponentsInChildren<MeshRenderer>())
            {
                ApplyPs1Materials(r, IsFloor(r) ? matFloor : matWall);
                // шкафы, стулья, посуду в акте 2 роняет призрак (Poltergeist) — их нельзя склеивать статик-батчингом
                bool movable = Poltergeist.GroupKey(r.name) != null;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, movable
                    ? StaticEditorFlags.OccludeeStatic
                    : StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ContributeGI);
            }

            var markers = house.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => t.name)
                .ToDictionary(g => g.Key, g => g.First());

            // ---------- точки обзора и сцены прошлого ----------
            // CAM_xx (+ CAM_xx_LOOK) — откуда сцена складывается целиком: туда надо поставить камеру.
            // SCENE_xx — где стоят фигуры (иначе 3 м перед точкой обзора).
            var scenesRoot = new GameObject("PastScenes").transform;
            scenesRoot.SetParent(root.transform, false);
            var viewRoot = new GameObject("ViewPoints").transform;
            viewRoot.SetParent(root.transform, false);
            var camNames = markers.Keys.Where(n => Regex.IsMatch(n, @"^CAM_\d+$")).OrderBy(n => n).ToList();
            var pastScenes = new List<PastScene>();
            foreach (var camName in camNames)
            {
                var m = markers[camName];
                int index = int.Parse(camName.Substring(4));
                Vector3 dir = Flat(markers.TryGetValue(camName + "_LOOK", out var look) ? look.position - m.position : m.forward);
                var vp = new GameObject($"ViewPoint_{index:00}").transform;
                vp.SetParent(viewRoot, false);
                vp.SetPositionAndRotation(m.position, Quaternion.LookRotation(dir, Vector3.up));
                PlaceTripodMarks(vp, viewRoot, matDark);

                string sceneMarker = $"SCENE_{index:00}";
                Vector3 anchor = markers.TryGetValue(sceneMarker, out var sm) ? sm.position : m.position + dir * 3f;
                pastScenes.Add(CreatePastScene(index, anchor, vp, scenesRoot, matPastAlive, matPastDead, isPlaceholder: sm == null));
            }
            WireSceneSwitches(house, pastScenes);

            // ---------- игрок ----------
            Vector3 spawnPos = Vector3.zero, spawnDir = Vector3.forward;
            if (markers.TryGetValue("SPAWN_Player", out var spawn))
            {
                spawnPos = spawn.position;
                if (markers.TryGetValue("SPAWN_Player_LOOK", out var spawnLook)) spawnDir = spawnLook.position - spawn.position;
            }
            else Debug.LogWarning("[Jam] Нет метки SPAWN_Player, игрок в начале координат.");
            var (player, fpc, eyes, interactor, inventory) = CreatePlayer(spawnPos, spawnDir, root.transform);

            // ---------- переносная камера: одна, в руках с самого начала ----------
            var station = CreateStation("PortableCamera", spawnPos + Flat(spawnDir) * 1f + Vector3.up * LensHeight, spawnDir, root.transform, matProp, matDark);
            station.gameObject.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // носится по дому — дыру в NavMesh не делает
            var portable = station.gameObject.AddComponent<PortableCamera>();
            portable.player = fpc;
            portable.interactor = interactor;
            portable.inventory = inventory;
            portable.groundMask = MaskSolid;

            // ---------- призрак ----------
            var routeMarkers = markers.Values
                .Select(t => (t, m: Regex.Match(t.name, @"^(STOP_)?GHOST_(\d+)$")))
                .Where(x => x.m.Success)
                .OrderBy(x => int.Parse(x.m.Groups[2].Value))
                .ToList();
            MargaretAI margaret = null;
            if (routeMarkers.Count > 0)
                margaret = CreateMargaret(routeMarkers.Select(x => x.t).ToList(), root.transform, matGhost, fpc, eyes.transform);
            else Debug.LogWarning("[Jam] Нет меток GHOST_xx, Маргарет не создана.");

            // ---------- сюжет ----------
            // сцена 5 (коридор): на пластине должна резко отпечататься сама Маргарет — её надо подманить и снять
            var gateScene = pastScenes.FirstOrDefault(ps => ps.index == 5);
            if (gateScene && margaret)
            {
                gateScene.requireMargaretSharp = true;
                gateScene.margaret = margaret.GetComponent<ExposureSubject>();
                // стойка для посмертного фото: метка POSE_STAND, иначе центр сцены
                gateScene.posePoint = markers.TryGetValue("POSE_STAND", out var poseMarker) ? poseMarker : gateScene.transform;
            }
            root.AddComponent<StoryDirector>();

            // ---------- свет и атмосфера ----------
            var roomLights = CreateRoomLights(house, root.transform);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.07f, 0.065f, 0.06f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.07f;
            RenderSettings.fogColor = new Color(0.02f, 0.02f, 0.025f);
            RenderSettings.skybox = null;

            // ---------- NavMesh (запекается в конце сборки, когда все коллайдеры на месте) ----------
            var surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = MaskSolid;

            // ---------- интерфейс и ручная обскура ----------
            var hud = CreateHud(root.transform, interactor, inventory, fpc, matPlate);
            var hand = player.AddComponent<HandObscura>();
            hand.eyes = eyes;
            hand.frame = hud.frame;
            hand.frameGroup = hud.frameGroup;
            hand.view = hud.glass;
            hand.viewMaterial = matPlate;
            hand.cullingMask = MaskObscura;
            // ручная обскура от 3D-шника: снизу, поднимается вместе с рамкой (линзой вперёд)
            hand.handModel = CreateViewModel("hand_obscura", eyes.transform, new Vector3(0f, -0.3f, 0.5f), 0.3f, "T_Wood_Dark", "T_Brass", PickMode.MultiMaterialOnly, matProp);

            // ---------- смерть и чекпойнт ----------
            var death = root.AddComponent<DeathAndCheckpoint>();
            death.margaret = margaret;
            death.player = fpc;
            death.interactor = interactor;
            death.hand = hand;
            death.inventory = inventory;
            death.viewer = hud.viewer;
            death.blackout = hud.blackout;

            // ---------- проявочная ----------
            CreateDarkroom(markers, house, roomLights, root.transform, matProp, matDark, matHungPlate,
                inventory, hud.viewer, fpc, interactor, hand, death);

            // ---------- вспышка, свеча, канделябры, двери ----------
            var flash = player.AddComponent<MagnesiumFlash>();
            flash.eyes = eyes.transform;
            flash.margaret = margaret;
            flash.occluders = MaskSolid;
            // магниевая вспышка в левой руке: лоток с порошком — вперёд
            flash.viewModel = CreateViewModel("magnesium_flash_lamp", eyes.transform, new Vector3(-0.3f, -0.36f, 0.5f), 0.36f, "T_Brass", "T_Iron", PickMode.Largest, matProp);
            CreateFlashCounter(hud.frame.parent, flash);
            CreatePlateCounter(hud.frame.parent, inventory);
            CreateExposureHud(hud.frame.parent, portable);

            var candle = player.AddComponent<HandCandle>();
            candle.anchor = eyes.transform;
            candle.margaret = margaret;
            candle.localOffset = new Vector3(0.24f, -0.18f, 0.45f); // огонёк в кадре, справа снизу
            // подсвечник в правой руке, свеча — под огоньком HandCandle
            var stick = CreateViewModel("chamberstick", eyes.transform, candle.localOffset + new Vector3(0f, -0.09f, 0f), 0.15f, null, null, PickMode.Largest, matProp);
            if (stick)
            {
                var top = CandleTops(stick.gameObject).OrderByDescending(t => t.y).FirstOrDefault();
                if (top != default)
                {
                    candle.flameVisual = CreateFlameDot(stick, top, s_playerOnly);
                    candle.localOffset = eyes.transform.InverseTransformPoint(top + Vector3.up * 0.05f); // свет — от огонька
                }
            }

            CreateCandelabras(markers, house, root.transform, matDark);
            CreateSeanceCandles(markers, house, root.transform, matDark);
            SetupDoors(house);
            PlaceProps(house, matProp);

            // ---------- Graphy: FPS и память, чтобы следить за WebGL ----------
            // ---------- PS1-вид: low-res вывод и дрожание вершин ----------
            var settings = root.AddComponent<LevelSettings>();
            settings.mainCamera = eyes;
            settings.outputMaterial = SaveShaderMat("M_LowResOutput", "Jam/LowResOutput");

            // ---------- звук ----------
            var audio = new GameObject("JamAudio").AddComponent<Jam.Audio.JamAudio>();
            audio.transform.SetParent(root.transform, false);
            audio.bank = LoadOrCreateSoundBank();

            // ---------- письмо-приглашение и титры ----------
            var credits = CreateLetterAndCredits(hud.frame.parent, fpc, interactor);
            var tutorial = root.AddComponent<TutorialHints>();
            tutorial.letter = Object.FindAnyObjectByType<IntroLetter>(FindObjectsInactive.Include);
            tutorial.hand = hand;
            tutorial.portable = portable;
            tutorial.darkroom = Object.FindAnyObjectByType<Darkroom>(FindObjectsInactive.Include);

            // ---------- перелом ----------
            var twist = root.AddComponent<TwistSequence>();
            twist.player = fpc;
            twist.interactor = interactor;
            twist.hand = hand;
            twist.viewer = hud.viewer;
            twist.margaret = margaret;
            twist.credits = credits;
            twist.finalPlate = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/2D/UI/P_Final.png");
            twist.whiteFlash = CreateWhiteFlash(hud.frame.parent);
            twist.ceilingMode = true;      // акт 2: игрок на потолке
            twist.continueToAct2 = true;

            // ---------- акт 2: призрак на потолке, живой фотограф ----------
            CreateAct2(root.transform, markers, viewRoot, fpc, interactor, twist, hud, credits, station, matProp);

            var graphyPath = AssetDatabase.FindAssets("Graphy t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "[Graphy]");
            if (graphyPath != null)
            {
                // В корне сцены, а не внутри уровня: Graphy делает DontDestroyOnLoad своему корневому объекту
                // и иначе утащил бы туда весь LEVEL_Generated.
                const string graphyName = "[Graphy] (debug, удалить перед релизом)";
                var oldGraphy = scene.GetRootGameObjects().FirstOrDefault(g => g.name == graphyName);
                if (oldGraphy) Object.DestroyImmediate(oldGraphy);
                var graphy = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(graphyPath), scene);
                graphy.name = graphyName;
                graphy.SetActive(false); // отладка на английском — включать вручную, когда меряем FPS
            }

            BakeNavMesh(surface, scene);
            if (margaret) CheckPatrolReachability(margaret);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root;
            Debug.Log($"[Jam] Уровень собран: {pastScenes.Count} сцен прошлого (точки обзора CAM_xx), {pastScenes.Sum(ps => ps.switches.Count)} переключателей, {routeMarkers.Count} точек маршрута. Сохрани сцену (Ctrl+S).");
        }

        // ================= NavMesh =================

        /// <summary>
        /// Запекаем в редакторе и сохраняем ассетом рядом со сценой: в билде не нужен доступ на чтение к мешам,
        /// и в WebGL ничего не строится при старте.
        /// </summary>
        /// <summary>
        /// Агент по умолчанию (Humanoid) толстый: радиус 0.5 м — в дверной проём 0.9 м NavMesh не пролезает,
        /// и комната остаётся отрезанным островом. Делаем агента под Маргарет: радиус 0.25 м.
        /// </summary>
        static void SetupAgentType(NavMeshSurface surface)
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset").FirstOrDefault();
            if (asset)
            {
                var so = new SerializedObject(asset);
                var list = so.FindProperty("m_Settings");
                if (list != null && list.arraySize > 0)
                {
                    var a = list.GetArrayElementAtIndex(0);
                    a.FindPropertyRelative("agentRadius").floatValue = 0.25f;
                    a.FindPropertyRelative("agentHeight").floatValue = 1.8f;
                    a.FindPropertyRelative("agentClimb").floatValue = 0.35f;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            surface.agentTypeID = NavMesh.GetSettingsByIndex(0).agentTypeID;
            surface.overrideVoxelSize = true;
            surface.voxelSize = 0.06f; // мельче — точнее проходит в двери
        }

        /// <summary>Все точки маршрута должны быть достижимы из первой — иначе Маргарет застрянет в отрезанной комнате.</summary>
        static void CheckPatrolReachability(MargaretAI ai)
        {
            var pts = ai.patrolPoints.Where(p => p).ToList();
            if (pts.Count < 2) { Debug.Log($"[Jam] NavMesh-проверка: точек маршрута {pts.Count}, проверять нечего."); return; }
            var path = new NavMeshPath();
            // берём самую «связанную» точку за начало: из неё доходим до максимума остальных
            var off = pts.Where(p => !NavMesh.SamplePosition(p.position, out _, 2f, NavMesh.AllAreas)).Select(p => p.name).ToList();
            var on = pts.Where(p => NavMesh.SamplePosition(p.position, out _, 2f, NavMesh.AllAreas)).ToList();
            List<string> best = null;
            foreach (var from in on)
            {
                NavMesh.SamplePosition(from.position, out var f, 2f, NavMesh.AllAreas);
                var unreachable = new List<string>();
                foreach (var to in on)
                {
                    if (to == from) continue;
                    NavMesh.SamplePosition(to.position, out var t, 2f, NavMesh.AllAreas);
                    if (!NavMesh.CalculatePath(f.position, t.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                        unreachable.Add(to.name);
                }
                if (best == null || unreachable.Count < best.Count) best = unreachable;
            }
            best ??= new List<string>();
            if (off.Count == 0 && best.Count == 0)
                Debug.Log($"[Jam] NavMesh: все {pts.Count} точек маршрута связаны.");
            else
                Debug.Log($"[Jam] NavMesh: ПРОБЛЕМА. Не на сетке: {(off.Count > 0 ? string.Join(", ", off) : "—")}. " +
                          $"Отрезаны от остальных: {(best.Count > 0 ? string.Join(", ", best) : "—")}. Проём уже 0.5 м или порог выше 0.35 м?");
        }

        static void BakeNavMesh(NavMeshSurface surface, UnityEngine.SceneManagement.Scene scene)
        {
            SetupAgentType(surface);
            surface.minRegionArea = 1.5f; // столы, сиденья стульев, кровать — не «островки», куда она может встать
            Physics.SyncTransforms();
            surface.BuildNavMesh();
            if (!surface.navMeshData)
            {
                Debug.LogWarning("[Jam] NavMesh не построился — проверь, что у пола есть коллайдеры.");
                return;
            }
            string dir = string.IsNullOrEmpty(scene.path) ? MatFolder : Path.GetDirectoryName(scene.path).Replace('\\', '/');
            string path = $"{dir}/NavMesh_{(string.IsNullOrEmpty(scene.name) ? "Level" : scene.name)}.asset";
            var old = AssetDatabase.LoadAssetAtPath<NavMeshData>(path);
            if (old) AssetDatabase.DeleteAsset(path);
            var data = surface.navMeshData;
            surface.RemoveData();
            AssetDatabase.CreateAsset(data, path);
            surface.navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(path);
            surface.AddData();
            Debug.Log($"[Jam] NavMesh запечён: {path}");
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
            Transform shutterOpen = null;

            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraModelPath);
            if (modelPrefab)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab, root.transform);
                model.name = "Model";
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                foreach (var mr in model.GetComponentsInChildren<Renderer>()) ApplyPs1Materials(mr, matProp);
                body = model.transform;
                shutter = FindDeep(model.transform, "Shutter");
                shutterOpen = FindDeep(model.transform, "Shutter_Open");
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
            station.shutterOpenTarget = shutterOpen; // открытое положение задано в Blender копией Shutter_Open
            if (shutter && !shutterOpen)
                station.shutterOpenOffset = shutter.parent ? shutter.parent.InverseTransformVector(slideWorld) : slideWorld;
            return station;
        }

        // ================= сцена прошлого =================

        /// <summary>
        /// Заглушка сцены: три фигуры в ряд, две живые (качаются → смазаны), средняя мёртвая (неподвижна → резкая).
        /// Когда художники нарисуют силуэты, меняем текстуры у материалов фигур.
        /// </summary>
        static PastScene CreatePastScene(int index, Vector3 anchor, Transform viewPoint, Transform parent, Material alive, Material dead, bool isPlaceholder)
        {
            float floorY = FloorY(anchor + Vector3.up * 0.3f, anchor.y); // низко: с 1,5 м луч ловил верх стойки/стола, и фигуры висели в воздухе
            var go = new GameObject($"Scene_{index:00}" + (isPlaceholder ? "_Placeholder" : ""));
            go.transform.SetParent(parent, false);
            Vector3 toStation = Flat(viewPoint.position - anchor);
            go.transform.SetPositionAndRotation(new Vector3(anchor.x, floorY, anchor.z), Quaternion.LookRotation(-toStation));
            go.layer = s_past;

            var scene = go.AddComponent<PastScene>();
            scene.index = index;
            scene.viewPoint = viewPoint;
            scene.startState = PastScene.State.Visible;

            // спрайты художников (Assets/Art/2D/Characters/C_*.png) — если есть, иначе серые заглушки
            var specs = SceneFigures.TryGetValue(index, out var list) ? list : null;
            var found = specs?.Select(f => (f, tex: AssetDatabase.LoadAssetAtPath<Texture2D>($"{CharactersFolder}/{f.file}.png")))
                .Where(x => x.tex).ToList();
            if (index == 5)
            {
                // посмертный портрет: в обскуре у стойки — её собственная тень из прошлого (тот же спрайт Маргарет).
                // Откроешь заслонку — настоящая Маргарет встанет ровно в неё. Новых спрайтов не нужно.
                var mtex = AssetDatabase.LoadAssetAtPath<Texture2D>(MargaretSpritePath);
                if (mtex)
                {
                    var (w, h) = FigureSize(mtex, MargaretHeight);
                    // тень, а не сама Маргарет: тёмная, холодная, наполовину прозрачная и мерцает
                    var mat = SaveBillboardMat("M_Past_Margaret_Portrait", new Color(0.5f, 0.58f, 0.75f, 1f));
                    mat.SetTexture("_BaseMap", mtex);
                    mat.SetFloat("_Opacity", 0.5f);
                    AddFigure(go.transform, "Margaret_Portrait", new Vector3(0f, h * 0.5f, -0.35f), new Vector3(w, h, 1f), mat, false);
                }
                else AddFigure(go.transform, "Figure_Dead", new Vector3(0f, 0.85f, -0.35f), new Vector3(0.6f, 1.7f, 1f), dead, false);
                return scene;
            }
            if (found != null && found.Count > 0)
            {
                go.name = $"Scene_{index:00}";
                float step = 0.75f, x0 = -(found.Count - 1) * step * 0.5f;
                for (int i = 0; i < found.Count; i++)
                {
                    var (f, tex) = found[i];
                    var (w, h) = FigureSize(tex, 0f);
                    var mat = SaveBillboardMat("M_Past_" + f.file, Color.white);
                    mat.SetTexture("_BaseMap", tex);
                    AddFigure(go.transform, f.file, new Vector3(x0 + i * step, f.lift + h * 0.5f, 0f), new Vector3(w, h, 1f), mat, !f.dead);
                }
                return scene;
            }

            float[] offsets = { -0.75f, 0f, 0.75f };
            for (int i = 0; i < offsets.Length; i++)
            {
                bool isDead = i == 1;
                AddFigure(go.transform, isDead ? "Figure_Dead" : "Figure_Alive", new Vector3(offsets[i], 0.85f, 0f),
                    new Vector3(0.6f, 1.7f, 1f), isDead ? dead : alive, !isDead);
            }
            return scene;
        }

        /// <summary>Шрифт с кириллицей: в браузере нет системных шрифтов, Special Elite без кириллицы там пустой.</summary>
        const string FontPath = "Assets/Art/Fonts/OldStandard-Regular.ttf";
        const string CharactersFolder = "Assets/Art/2D/Characters";
        const string MargaretSpritePath = CharactersFolder + "/C_Margaret_Stand.png";
        /// <summary>256 px = 1,7 м — стоящая фигура в полный рост (ТЗ художникам).</summary>
        const float PixelsPerMeter = 150f;
        /// <summary>Рост Маргарет в игре, м — не зависит от размера картинки.</summary>
        const float MargaretHeight = 1.75f;

        /// <summary>
        /// Размер билборда фигуры: height &gt; 0 — ровно такой рост (ширина по пропорциям картинки);
        /// иначе по 150 px/м, но картинку в высоком разрешении (выше 2,2 м) приводим к росту человека.
        /// </summary>
        static (float w, float h) FigureSize(Texture2D tex, float height)
        {
            float aspect = tex.width / (float)Mathf.Max(1, tex.height);
            float h = height > 0f ? height : tex.height / PixelsPerMeter;
            if (h > 2.2f) h = MargaretHeight;
            return (h * aspect, h);
        }

        /// <summary>Кто стоит в какой сцене (по ТЗ художникам). lift — подъём над полом, м (лицо в камине).</summary>
        static readonly Dictionary<int, (string file, bool dead, float lift)[]> SceneFigures = new()
        {
            [1] = new[] { ("C_Father_Sit", false, 0f), ("C_Clara_Sit", true, 0f), ("C_Mother_Sit", false, 0f) },
            [2] = new[] { ("C_Mother_Comb", false, 0f), ("C_Clara_SitBed", true, 0f) },
            [3] = new[] { ("C_Guest_1", false, 0f), ("C_Clara_Chair", true, 0f), ("C_Guest_2", false, 0f) },
            [4] = new[] { ("C_Father_Burn", false, 0f), ("C_Clara_FaceFire", true, 0.35f) },
            [6] = new[] { ("C_Photographer_Fall", true, 0f) },
        };

        static void AddFigure(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat, bool alive)
        {
            var fig = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(fig.GetComponent<Collider>());
            fig.name = name;
            fig.layer = s_past;
            fig.transform.SetParent(parent, false);
            fig.transform.localPosition = localPos;
            fig.transform.localScale = size;
            var mr = fig.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            fig.AddComponent<PastFigure>().alive = alive;
        }

        /// <summary>
        /// SWITCH_S{n}_… в доме — условия сцены n: свечи на столе, зеркало, камин.
        /// Дочерние ON / OFF показывают состояние, свет внутри ON зажигается сам.
        /// </summary>
        static void WireSceneSwitches(GameObject house, List<PastScene> scenes)
        {
            var rx = new Regex(@"^SWITCH_S(\d+)_(.*)$", RegexOptions.IgnoreCase);
            foreach (var t in house.GetComponentsInChildren<Transform>(true))
            {
                var m = rx.Match(t.name);
                if (!m.Success) continue;
                int n = int.Parse(m.Groups[1].Value);
                var scene = scenes.FirstOrDefault(s => s.index == n);
                if (!scene) { Debug.LogWarning($"[Jam] {t.name}: нет сцены {n}."); continue; }

                var sw = t.gameObject.AddComponent<SceneSwitch>();
                sw.sceneIndex = n;
                string what = m.Groups[2].Value.ToLowerInvariant();
                if (what.Contains("mirror")) { sw.promptOff = "Завесить зеркало"; sw.promptOn = "Открыть зеркало"; }
                else if (what.Contains("fire")) { sw.promptOff = "Разжечь камин"; sw.promptOn = "Погасить камин"; }
                else if (what.Contains("clock")) { sw.promptOff = "Остановить часы"; sw.promptOn = "Завести часы"; }
                else if (what.Contains("curtain")) { sw.promptOff = "Задёрнуть шторы"; sw.promptOn = "Раздёрнуть шторы"; }
                foreach (Transform c in t)
                {
                    if (c.name.StartsWith("ON", System.StringComparison.OrdinalIgnoreCase)) sw.onVisual = c.gameObject;
                    else if (c.name.StartsWith("OFF", System.StringComparison.OrdinalIgnoreCase)) sw.offVisual = c.gameObject;
                }
                // огонь — с тёплым светом
                if (what.Contains("candle") || what.Contains("fire"))
                {
                    var lgo = new GameObject("SwitchLight");
                    lgo.transform.SetParent(t, false);
                    lgo.transform.localPosition = Vector3.up * 0.3f;
                    var l = lgo.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.color = new Color(1f, 0.62f, 0.3f);
                    l.range = 4f;
                    l.intensity = 0f;
                    l.shadows = LightShadows.None;
                    sw.onLight = l;
                }
                if (!t.GetComponentInChildren<Collider>())
                {
                    var r = t.GetComponentInChildren<Renderer>();
                    var col = t.gameObject.AddComponent<BoxCollider>();
                    if (r)
                    {
                        col.center = t.InverseTransformPoint(r.bounds.center);
                        col.size = t.InverseTransformVector(r.bounds.size);
                        col.size = new Vector3(Mathf.Abs(col.size.x), Mathf.Abs(col.size.y), Mathf.Abs(col.size.z));
                    }
                    else col.size = Vector3.one * 0.4f;
                }
                scene.switches.Add(sw);
            }
        }

        const string TripodMarksPath = "Assets/Art/2D/Textures/D_TripodMarks.png";
        const string TripodMarksMatPath = MatFolder + "/M_TripodMarks.mat";

        /// <summary>
        /// Следы штатива — декаль на полу под точкой обзора: плоский квад 1 × 1 м с прозрачной текстурой D_TripodMarks.png.
        /// Верх картинки = куда смотрел объектив. Нет текстуры — следов нет.
        /// </summary>
        static void PlaceTripodMarks(Transform viewPoint, Transform parent, Material fallback)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TripodMarksPath);
            if (!tex) tex = PlaceholderTripodMarks(); // пока художники не нарисовали — простые вмятины от трёх ножек
            if (!tex) return;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(TripodMarksMatPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, TripodMarksMatPath);
            }
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_Smoothness", 0f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            EditorUtility.SetDirty(mat);

            float y = FloorY(viewPoint.position, viewPoint.position.y - LensHeight);
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = "TripodMarks_" + viewPoint.name.Replace("ViewPoint_", "");
            go.transform.SetParent(parent, false);
            // квад лёг на пол лицом вверх, верх текстуры — по взгляду объектива
            go.transform.SetPositionAndRotation(new Vector3(viewPoint.position.x, y + 0.004f, viewPoint.position.z),
                Quaternion.LookRotation(Flat(viewPoint.forward), Vector3.up) * Quaternion.Euler(90f, 0f, 0f));
            go.transform.localScale = Vector3.one;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>Заглушка D_TripodMarks: три тёмные вмятины с царапинами, две ножки впереди (верх картинки — куда смотрел объектив).</summary>
        static Texture2D PlaceholderTripodMarks()
        {
            string path = MatFolder + "/D_TripodMarks_Placeholder.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing) return existing;
            const int N = 128;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color32[N * N];
            var mark = new Color32(28, 20, 16, 255);
            void Dot(float cx, float cy, float rx, float ry)
            {
                for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    if (dx * dx + dy * dy <= 1f) px[y * N + x] = mark;
                }
            }
            float c = N * 0.5f, rad = N * 0.3f;
            foreach (float deg in new[] { 60f, 120f, 270f })
            {
                float a = deg * Mathf.Deg2Rad;
                float cx = c + Mathf.Cos(a) * rad, cy = c + Mathf.Sin(a) * rad;
                Dot(cx, cy, 4.5f, 4.5f);
                // штатив двигали: короткие царапины от каждой ножки
                for (int k = 1; k <= 7; k++) Dot(cx + Mathf.Cos(a) * k * 1.6f, cy + Mathf.Sin(a) * k * 1.6f, 1.2f, 1.2f);
            }
            t.SetPixels32(px);
            t.Apply();
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.alphaIsTransparency = true;
                ti.filterMode = FilterMode.Point;
                ti.mipmapEnabled = false;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// Альбом семьи: рамки по числу сцен. Метка ALBUM (+ ALBUM_LOOK — куда смотрит лицевая сторона),
        /// иначе — на стене над верстаком проявочной. Рамки — заглушки, пока нет модели.
        /// </summary>
        static void CreateAlbum(Dictionary<string, Transform> markers, Vector3 benchPos, Vector3 faceDir, Transform parent,
            Material matDark, Material matPicture, PlateViewer viewer)
        {
            var indices = markers.Keys.Where(n => Regex.IsMatch(n, @"^CAM_\d+$")).Select(n => int.Parse(n.Substring(4))).OrderBy(i => i).ToList();
            if (indices.Count == 0) return;

            var root = new GameObject("FamilyAlbum").transform;
            root.SetParent(parent, false);
            if (markers.TryGetValue("ALBUM", out var am))
            {
                Vector3 face = markers.TryGetValue("ALBUM_LOOK", out var al) ? Flat(al.position - am.position) : Flat(am.forward);
                root.SetPositionAndRotation(am.position, Quaternion.LookRotation(-face));
            }
            else
                root.SetPositionAndRotation(benchPos - faceDir * 0.42f + Vector3.up * 1.5f, Quaternion.LookRotation(-faceDir));

            var album = root.gameObject.AddComponent<FamilyAlbum>();
            album.viewer = viewer;
            album.frameMaterial = matPicture;
            album.hintMask = MaskSolid;

            // рамки 3D-шника (ALBUM_FRAME_1…N в house.blend): снимки вставляются прямо в них
            var userFrames = Enumerable.Range(1, indices.Count)
                .Select(k => markers.TryGetValue($"ALBUM_FRAME_{k}", out var t) ? t.GetComponent<Renderer>() : null).ToList();
            if (userFrames.All(r => r))
            {
                Vector3 face = -root.forward, right = root.right;
                for (int k = 0; k < indices.Count; k++)
                {
                    var b = userFrames[k].bounds;
                    float h = b.size.y * 0.72f;
                    float w = (Mathf.Abs(b.size.x * right.x) + Mathf.Abs(b.size.z * right.z)) * 0.72f;
                    var f = new GameObject($"Frame_{indices[k]:00}").transform;
                    f.SetParent(root, false);
                    float depth = Mathf.Abs(b.extents.x * face.x) + Mathf.Abs(b.extents.z * face.z);
                    f.SetPositionAndRotation(b.center + face * Mathf.Min(depth * 0.4f + 0.004f, 0.03f), root.rotation);
                    var col = f.gameObject.AddComponent<BoxCollider>();
                    col.size = new Vector3(w / 0.72f, h / 0.72f, 0.05f);
                    var pq = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.DestroyImmediate(pq.GetComponent<Collider>());
                    pq.name = "Picture";
                    pq.transform.SetParent(f, false);
                    pq.transform.localScale = new Vector3(w, h, 1f);
                    var pr = pq.GetComponent<MeshRenderer>();
                    pr.sharedMaterial = matPicture;
                    pr.shadowCastingMode = ShadowCastingMode.Off;
                    var uf = f.gameObject.AddComponent<AlbumFrame>();
                    uf.sceneIndex = indices[k];
                    uf.picture = pr;
                    float aspect = w / Mathf.Max(0.01f, h), photo = 4f / 3f;
                    uf.crop = new Vector2(Mathf.Min(1f, aspect / photo), Mathf.Min(1f, photo / aspect));
                    album.frames.Add(uf);
                }
                return;
            }

            const float step = 0.4f;
            float x0 = -(indices.Count - 1) * step * 0.5f;
            for (int k = 0; k < indices.Count; k++)
            {
                var f = new GameObject($"Frame_{indices[k]:00}").transform;
                f.SetParent(root, false);
                f.localPosition = new Vector3(x0 + k * step, (k % 2 == 0 ? 0.04f : -0.04f), 0f); // чуть вразнобой, как на стене у живых людей
                var col = f.gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(0.36f, 0.29f, 0.04f);
                Box("Backing", f, new Vector3(0f, 0f, 0.012f), new Vector3(0.36f, 0.29f, 0.02f), matDark, false);
                var pic = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(pic.GetComponent<Collider>());
                pic.name = "Picture";
                pic.transform.SetParent(f, false);
                pic.transform.localPosition = new Vector3(0f, 0f, -0.002f);
                pic.transform.localScale = new Vector3(0.31f, 0.233f, 1f);
                var mr = pic.GetComponent<MeshRenderer>();
                mr.sharedMaterial = matPicture;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                var af = f.gameObject.AddComponent<AlbumFrame>();
                af.sceneIndex = indices[k];
                af.picture = mr;
                album.frames.Add(af);
            }
        }

        static AudioClip LoadAudio(string name)
        {
            if (!AssetDatabase.IsValidFolder(AudioFolder)) return null;
            foreach (var g in AssetDatabase.FindAssets(name + " t:AudioClip", new[] { AudioFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(path).ToLowerInvariant() == name) return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            return null;
        }

        const string PropsFolder = "Assets/Art/3D/Models/Props";

        enum PickMode { All, MultiMaterialOnly, Largest }

        /// <summary>
        /// Модель «в руках» у глаз игрока: Props/&lt;file&gt;.fbx, слой PlayerOnly (обскуры и альбом её не видят), без коллайдеров и теней.
        /// Поворачивается так, чтобы направление от материала frontFrom к frontTo смотрело вперёд, и подгоняется под размер size (м).
        /// </summary>
        static Transform CreateViewModel(string file, Transform eyes, Vector3 localPos, float size, string frontFrom, string frontTo, PickMode toMode, Material fallback)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/{file}.fbx");
            if (!prefab) { Debug.Log($"[Jam] Нет {file}.fbx — в руках пусто."); return null; }
            var pivot = new GameObject(file + "_InHand").transform;
            pivot.SetParent(eyes, false);
            pivot.localPosition = localPos;
            var m = (GameObject)PrefabUtility.InstantiatePrefab(prefab, pivot);
            m.transform.localPosition = Vector3.zero;

            if (frontFrom != null && frontTo != null)
            {
                Vector3 a = MatCenter(m, frontFrom, PickMode.All), b = MatCenter(m, frontTo, toMode);
                Vector3 dir = pivot.InverseTransformDirection(b - a);
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-6f)
                    m.transform.RotateAround(pivot.position, pivot.up, Vector3.SignedAngle(dir, Vector3.forward, Vector3.up));
            }
            var bb = RenderBounds(m);
            float ext = Mathf.Max(bb.size.x, bb.size.y, bb.size.z);
            if (ext > 1e-4f) m.transform.localScale *= size / ext;
            bb = RenderBounds(m);
            m.transform.position += pivot.position - bb.center;

            foreach (var t in m.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = s_playerOnly;
                foreach (var c in t.GetComponents<Collider>()) Object.DestroyImmediate(c);
            }
            foreach (var r in m.GetComponentsInChildren<Renderer>())
            {
                ApplyPs1Materials(r, fallback);
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            return pivot;
        }

        /// <summary>Средняя точка частей модели с материалом matPrefix (по подмешам).</summary>
        static Vector3 MatCenter(GameObject model, string matPrefix, PickMode mode)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            float best = -1f;
            Vector3 bestC = Vector3.zero;
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var mats = r.sharedMaterials;
                if (mode == PickMode.MultiMaterialOnly && mats.Length < 2) continue;
                for (int i = 0; i < mats.Length && i < mf.sharedMesh.subMeshCount; i++)
                {
                    if (!mats[i] || !mats[i].name.StartsWith(matPrefix)) continue;
                    var sb = mf.sharedMesh.GetSubMesh(i).bounds;
                    var c = r.transform.TransformPoint(sb.center);
                    sum += c; n++;
                    float vol = r.transform.TransformVector(sb.size).magnitude;
                    if (vol > best) { best = vol; bestC = c; }
                }
            }
            if (mode == PickMode.Largest && best > 0f) return bestC;
            return n > 0 ? sum / n : RenderBounds(model).center;
        }

        /// <summary>
        /// Мебель из отдельных .blend: в house.blend ставится пустышка (или instance коллекции) с именем
        /// P_&lt;имя файла&gt; — P_chair, P_chair.001, P_bed… Сюда подставляется Props/&lt;имя&gt;.fbx в её позиции, повороте и масштабе,
        /// с PS1-материалами и коробочным коллайдером на каждую деталь (NavMesh их обходит).
        /// </summary>
        static void PlaceProps(GameObject house, Material fallback)
        {
            var rx = new Regex(@"^P_(.+?)(\.\d+)?$");
            int placed = 0;
            var missing = new HashSet<string>();
            foreach (var t in house.GetComponentsInChildren<Transform>(true).ToList())
            {
                var m = rx.Match(t.name);
                if (!m.Success || t.GetComponent<Renderer>()) continue;
                string name = m.Groups[1].Value;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/{name}.fbx");
                if (!prefab) { missing.Add(name); continue; }
                // если это instance коллекции, FBX мог притащить копию мебели внутрь пустышки — прячем её
                foreach (Transform child in t) child.gameObject.SetActive(false);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, t);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                {
                    ApplyPs1Materials(r, fallback);
                    if (!r.GetComponent<Collider>()) r.gameObject.AddComponent<BoxCollider>();
                    GameObjectUtility.SetStaticEditorFlags(r.gameObject, StaticEditorFlags.BatchingStatic);
                }
                placed++;
            }
            if (placed > 0 || missing.Count > 0)
                Debug.Log($"[Jam] Мебель: расставлено {placed}." + (missing.Count > 0 ? $" Нет FBX для: {string.Join(", ", missing)} (Export Props в Blender)." : ""));
        }

        static void CreateExposureHud(Transform canvas, PortableCamera portable)
        {

            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (!font) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var label = UiChild<Text>("ExposureLine", canvas, new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(1000f, 44f));
            label.font = font;
            label.fontSize = 26;
            label.alignment = TextAnchor.LowerCenter;
            label.color = new Color(0.85f, 0.8f, 0.7f, 0.85f);
            label.raycastTarget = false;
            label.transform.SetSiblingIndex(1);
            var ui = label.gameObject.AddComponent<ExposureHUD>();
            ui.portable = portable;
            ui.station = portable.GetComponent<CameraStation>();
            ui.label = label;
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

        static MargaretAI CreateMargaret(List<Transform> patrol, Transform parent, Material mat, FirstPersonController player, Transform eyes)
        {
            var route = patrol;
            var ghost = new GameObject("Margaret") { layer = s_ghost };
            ghost.transform.SetParent(parent, false);
            ghost.transform.position = route[route.Count / 2].position; // подальше от старта игрока

            // спрайт художников C_Margaret_Stand.png (билборд лицом к камере), пока его нет — красная капсула
            var sprite = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CharactersFolder}/C_Margaret_Stand.png");
            GameObject visual;
            if (sprite)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
                visual.name = "Visual_Sprite";
                var (w, h) = FigureSize(sprite, MargaretHeight);
                visual.transform.SetParent(ghost.transform, false);
                visual.transform.localPosition = new Vector3(0f, h * 0.5f, 0f);
                visual.transform.localScale = new Vector3(w, h, 1f);
                var sm = SaveBillboardMat("M_Margaret_Sprite", Color.white);
                sm.SetTexture("_BaseMap", sprite);
                visual.GetComponent<Renderer>().sharedMaterial = sm;
            }
            else
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.name = "Visual_Placeholder";
                visual.transform.SetParent(ghost.transform, false);
                visual.transform.localPosition = new Vector3(0f, 1f, 0f);
                visual.GetComponent<Renderer>().sharedMaterial = mat;
            }
            visual.layer = s_ghost;
            visual.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
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
            agent.enabled = false; // MargaretAI включит после постройки NavMesh

            var ai = ghost.AddComponent<MargaretAI>();
            ai.patrolPoints.AddRange(route);
            ai.player = player;
            ai.playerEyes = eyes;
            ai.occluders = MaskSolid;
            return ai;
        }

        // ================= свет =================

        static Dictionary<Renderer, Light> CreateRoomLights(GameObject house, Transform parent)
        {
            var map = new Dictionary<Renderer, Light>();
            var lightsRoot = new GameObject("RoomLights").transform;
            lightsRoot.SetParent(parent, false);
            foreach (var r in house.GetComponentsInChildren<MeshRenderer>())
            {
                if (!IsFloor(r)) continue;
                var b = r.bounds;
                var l = new GameObject("Light_" + r.name).AddComponent<Light>();
                l.transform.SetParent(lightsRoot, false);
                l.transform.position = new Vector3(b.center.x, b.max.y + 2.4f, b.center.z);
                l.type = LightType.Point;
                // свет в доме не горит: только холодная луна из окон — тёплый свет дают свеча и канделябры
                l.color = new Color(0.55f, 0.62f, 0.8f);
                l.range = Mathf.Max(b.size.x, b.size.z) * 0.9f + 1f;
                l.intensity = 0.45f;
                l.shadows = LightShadows.None;
                map[r] = l;
            }
            return map;
        }

        static bool IsFloor(Renderer r) => r.name.StartsWith("Plane") || r.name.StartsWith("Floor");

        // ================= свет у игрока, канделябры, двери =================

        static void CreateFlashCounter(Transform canvas, MagnesiumFlash flash)
        {
            var label = UiChild<Text>("FlashCounter", canvas, new Vector2(0f, 0f), new Vector2(40f, 40f), new Vector2(320f, 50f));
            label.rectTransform.pivot = Vector2.zero;
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            label.font = font ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 28;
            label.alignment = TextAnchor.LowerLeft;
            label.raycastTarget = false;
            label.transform.SetSiblingIndex(1); // под пластиной и затемнением
            var ui = label.gameObject.AddComponent<FlashCounterUI>();
            ui.flash = flash;
            ui.label = label;
        }

        static void CreatePlateCounter(Transform canvas, PlateInventory inventory)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (!font) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var counter = UiChild<Text>("PlateCounter", canvas, new Vector2(0f, 0f), new Vector2(40f, 90f), new Vector2(360f, 50f));
            counter.rectTransform.pivot = Vector2.zero;
            counter.font = font;
            counter.fontSize = 28;
            counter.alignment = TextAnchor.LowerLeft;
            counter.color = new Color(0.85f, 0.8f, 0.7f, 0.85f);
            counter.raycastTarget = false;
            counter.transform.SetSiblingIndex(1);

            var hint = UiChild<Text>("PlateHint", canvas, new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(1800f, 90f));
            hint.font = font;
            hint.fontSize = 30;
            hint.alignment = TextAnchor.MiddleCenter;
            hint.color = new Color(0.9f, 0.85f, 0.75f, 1f);
            hint.raycastTarget = false;
            hint.transform.SetSiblingIndex(1);

            var ui = counter.gameObject.AddComponent<PlateCounterUI>();
            ui.inventory = inventory;
            ui.counter = counter;
            ui.hint = hint;
        }

        /// <summary>Канделябры по меткам CANDLE_xx; если меток нет — по одному в центре каждой комнаты.</summary>
        static void CreateCandelabras(Dictionary<string, Transform> markers, GameObject house, Transform parent, Material matDark)
        {
            var root = new GameObject("Candelabras").transform;
            root.SetParent(parent, false);

            var points = markers.Where(kv => Regex.IsMatch(kv.Key, @"^CANDLE_\d+$")).Select(kv => kv.Value.position).ToList();
            bool fromMarkers = points.Count > 0;
            if (!fromMarkers)
            {
                foreach (var f in house.GetComponentsInChildren<MeshRenderer>().Where(IsFloor))
                {
                    var b = f.bounds;
                    if (b.size.x < 2.6f || b.size.z < 2.6f) continue; // в узких местах (коридор, холл) — нет
                    if (b.size.x * b.size.z < 10f) continue;        // и в проявочной — там свой красный свет
                    points.Add(new Vector3(b.center.x, b.max.y, b.center.z));
                }
            }

            int i = 1;
            foreach (var p in points)
            {
                float floorY = FloorY(p + Vector3.up * (fromMarkers ? 0.4f : 1.5f), p.y); // метка на столе/камине — ставим на него
                var go = new GameObject($"Candelabra_{i++:00}");
                go.transform.SetParent(root, false);
                go.transform.position = new Vector3(p.x, floorY, p.z);
                // модель от 3D-шника (Props/candelabra.fbx), пока её нет — чёрный столбик-заглушка
                float flameY = 1.25f;
                var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/candelabra.fbx");
                if (model)
                {
                    var m = (GameObject)PrefabUtility.InstantiatePrefab(model, go.transform);
                    m.transform.localPosition = Vector3.zero;
                    foreach (var top in CandleTops(m)) CreateFlameDot(go.transform, top, 0);
                    foreach (var r in m.GetComponentsInChildren<MeshRenderer>())
                    {
                        ApplyPs1Materials(r, matDark);
                        if (!r.GetComponent<Collider>()) r.gameObject.AddComponent<BoxCollider>();
                    }
                    flameY = RenderBounds(m).max.y - go.transform.position.y + 0.05f;
                }
                else
                {
                    Box("Stand", go.transform, new Vector3(0f, 0.55f, 0f), new Vector3(0.12f, 1.1f, 0.12f), matDark, true);
                    Box("Base", go.transform, new Vector3(0f, 0.03f, 0f), new Vector3(0.35f, 0.06f, 0.35f), matDark, false);
                }

                var flame = new GameObject("Flame").AddComponent<Light>();
                flame.transform.SetParent(go.transform, false);
                flame.transform.localPosition = new Vector3(0f, flameY, 0f);
                flame.type = LightType.Point;
                flame.color = new Color(1f, 0.7f, 0.4f);
                flame.range = 4.5f;
                flame.intensity = 1.3f;
                flame.shadows = LightShadows.None;

                var c = go.AddComponent<Candelabra>();
                c.flame = flame;
                c.baseIntensity = flame.intensity;
            }
            Debug.Log($"[Jam] Канделябров: {points.Count} ({(fromMarkers ? "по меткам CANDLE_xx" : "по центрам комнат")}).");
        }

        /// <summary>
        /// Сцена 3 (сеанс): после проявки вокруг стола сеанса сами собой горят свечи — объект AFTER_S3_candles,
        /// его включает StoryDirector. Модель Props/candles.fbx, огоньки и тёплый свет — кодом.
        /// Это не канделябры: в акте 2 их гасить не нужно.
        /// </summary>
        static void CreateSeanceCandles(Dictionary<string, Transform> markers, GameObject house, Transform parent, Material fallback)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/candles.fbx");
            if (!prefab) return;
            var table = house.GetComponentsInChildren<Renderer>(true).Where(r => r.name.StartsWith("F_seance_table")).ToList();
            Vector3 center;
            if (table.Count > 0)
            {
                var b = table[0].bounds;
                foreach (var r in table) b.Encapsulate(r.bounds);
                center = b.center;
            }
            else if (markers.TryGetValue("SCENE_03", out var sc)) center = sc.position;
            else return;

            var root = new GameObject("AFTER_S3_candles").transform;
            root.SetParent(parent, false);
            for (int i = 0; i < 4; i++)
            {
                float a = (45f + 90f * i) * Mathf.Deg2Rad;
                Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 1.25f;
                p.y = FloorY(new Vector3(p.x, center.y + 0.4f, p.z), 0f);
                var g = new GameObject($"Candles_{i + 1}").transform;
                g.SetParent(root, false);
                g.position = p;
                var m = (GameObject)PrefabUtility.InstantiatePrefab(prefab, g);
                m.transform.localPosition = Vector3.zero;
                m.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * m.transform.localRotation;
                foreach (var c in m.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                foreach (var top in CandleTops(m)) CreateFlameDot(g, top, 0);
                foreach (var r in m.GetComponentsInChildren<Renderer>()) { ApplyPs1Materials(r, fallback); r.shadowCastingMode = ShadowCastingMode.Off; }
                var l = new GameObject("Light").AddComponent<Light>();
                l.transform.SetParent(g, false);
                l.transform.localPosition = Vector3.up * 0.5f;
                l.type = LightType.Point;
                l.color = new Color(1f, 0.68f, 0.38f);
                l.range = 3f;
                l.intensity = 0.8f;
                l.shadows = LightShadows.None;
            }
            Debug.Log("[Jam] Сцена 3: свечи вокруг стола сеанса (появятся после проявки).");
        }

        static Material s_flameMat;

        /// <summary>Верхушки свечей: самые высокие вершины частей с материалом T_Plaster, сгруппированные по свечам.</summary>
        static List<Vector3> CandleTops(GameObject model)
        {
            var tops = new List<Vector3>();
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.sharedMaterials.Any(mt => mt && mt.name.Contains("T_Plaster"))) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var vs = mf.sharedMesh.vertices.Select(v => r.transform.TransformPoint(v)).ToList();
                float maxY = vs.Max(v => v.y);
                foreach (var v in vs.Where(v => v.y > maxY - 0.015f))
                {
                    int i = tops.FindIndex(t => new Vector2(t.x - v.x, t.z - v.z).sqrMagnitude < 0.04f * 0.04f);
                    if (i < 0) tops.Add(v);
                    else tops[i] = new Vector3((tops[i].x + v.x) * 0.5f, Mathf.Max(tops[i].y, v.y), (tops[i].z + v.z) * 0.5f);
                }
            }
            return tops;
        }

        /// <summary>Яркая капля пламени — видно канделябр издалека, даже в тумане.</summary>
        static GameObject CreateFlameDot(Transform parent, Vector3 candleTop, int layer)
        {
            if (!s_flameMat)
            {
                s_flameMat = SaveUnlitMat("M_Flame");
                if (s_flameMat && s_flameMat.HasProperty("_BaseColor")) s_flameMat.SetColor("_BaseColor", new Color(1f, 0.8f, 0.45f));
            }
            var f = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(f.GetComponent<Collider>());
            f.name = "FlameDot";
            f.layer = layer;
            f.transform.SetParent(parent, true);
            f.transform.position = candleTop + Vector3.up * 0.022f;
            f.transform.localScale = Vector3.one;
            var ls = f.transform.lossyScale;
            f.transform.localScale = new Vector3(0.018f / ls.x, 0.034f / ls.y, 0.018f / ls.z);
            var r = f.GetComponent<MeshRenderer>();
            r.sharedMaterial = s_flameMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            return f;
        }

        /// <summary>Двери — объекты дома с именем DOOR_xx. Origin должен быть на петлях. Нет таких — пропускаем.</summary>
        static void SetupDoors(GameObject house)
        {
            int n = 0;
            var all = house.GetComponentsInChildren<Renderer>();
            Vector3 center = all.Length > 0 ? all.Aggregate(all[0].bounds, (acc, r) => { acc.Encapsulate(r.bounds); return acc; }).center : Vector3.zero;

            // входная дверь заперта: невидимая стена во весь проём, чтобы не выйти из дома
            var front = house.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "DoorFrame_Front");
            if (front && front.GetComponent<Renderer>())
            {
                var fb = front.GetComponent<Renderer>().bounds;
                var block = new GameObject("FrontDoorBlocker");
                block.transform.SetParent(house.transform, false);
                block.transform.position = new Vector3(fb.center.x, fb.min.y + 1.6f, fb.center.z);
                var bc = block.AddComponent<BoxCollider>();
                bc.size = new Vector3(fb.size.x + 0.4f, 3.2f, fb.size.z + 0.4f);
            }

            foreach (var t in house.GetComponentsInChildren<Transform>(true))
            {
                if (!Regex.IsMatch(t.name, @"^DOOR_\d+")) continue;
                var r = t.GetComponent<Renderer>();
                if (!r) continue;
                foreach (var c in t.GetComponentsInChildren<Transform>(true))
                {
                    GameObjectUtility.SetStaticEditorFlags(c.gameObject, 0); // дверь и ручка двигаются — не статик
                    if (c != t) foreach (var cc in c.GetComponents<Collider>()) Object.DestroyImmediate(cc); // ручке коллайдер не нужен
                }
                if (!t.GetComponent<Collider>())
                {
                    var mc = t.gameObject.AddComponent<MeshCollider>();
                    mc.convex = true;
                }
                if (!t.GetComponent<NavMeshModifier>()) t.gameObject.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
                var door = t.gameObject.AddComponent<Door>();
                door.startOpen = true; // дом «приоткрыт»: игрок сразу видит проходы, закрыть можно E
                door.openAwayFrom = center;
                n++;
            }
            if (n > 0) Debug.Log($"[Jam] Дверей: {n}.");
        }

        // ================= звук и обёртка =================

        const string SoundBankPath = "Assets/_Game/Audio/SoundBank.asset";
        const string AudioFolder = "Assets/Audio";

        /// <summary>
        /// SoundBank с записями на все ключи игры. Клипы подхватываются из Assets/Audio по имени файла:
        /// footstep.wav, footstep_01.wav, footstep-2.ogg → ключ footstep. Уже заданные вручную клипы не трогаем.
        /// </summary>
        static Jam.Audio.SoundBank LoadOrCreateSoundBank()
        {
            EnsureFolder("Assets/_Game/Audio");
            var bank = AssetDatabase.LoadAssetAtPath<Jam.Audio.SoundBank>(SoundBankPath);
            if (!bank)
            {
                bank = ScriptableObject.CreateInstance<Jam.Audio.SoundBank>();
                AssetDatabase.CreateAsset(bank, SoundBankPath);
            }

            var clips = AssetDatabase.IsValidFolder(AudioFolder)
                ? AssetDatabase.FindAssets("t:AudioClip", new[] { AudioFolder }).Select(g => AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(g))).Where(c => c).ToList()
                : new List<AudioClip>();

            int found = 0;
            foreach (var key in Jam.Audio.SoundBank.Keys)
            {
                var e = bank.entries.FirstOrDefault(x => x.key == key);
                if (e == null)
                {
                    e = new Jam.Audio.SoundBank.Entry { key = key };
                    if (key == "ambient" || key == "sting" || key == "caught" || key == "death_gasp" || key == "margaret_chase" || key == "obscura_raise" || key == "candle_out" || key == "candle_light" || key == "flash" || key == "flash_empty")
                        e.spatial = false;
                    if (key == "ambient") e.volume = 0.5f;
                    bank.entries.Add(e);
                }
                if (e.clips.Count(c => c) > 0) { found++; continue; }
                var rx = new Regex("^" + Regex.Escape(key) + @"([_\- ]?\d+)?$", RegexOptions.IgnoreCase);
                e.clips = clips.Where(c => rx.IsMatch(c.name)).OrderBy(c => c.name).ToList();
                if (e.clips.Count > 0) found++;
            }
            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Jam] Звук: клипы есть для {found} из {Jam.Audio.SoundBank.Keys.Length} ключей (кладите файлы в {AudioFolder}, имя = ключ).");
            return bank;
        }

        // ================= акт 2 =================

        /// <summary>
        /// Акт 2: Act2Director, живой фотограф (спрайты C_Photographer_Stand / C_Photographer_Walk_1…4, иначе капсула-заглушка),
        /// два его штатива (копия модели нашей камеры + холодный конус света, пока заслонка открыта),
        /// белая виньетка «проявленности».
        /// </summary>
        static void CreateAct2(Transform root, Dictionary<string, Transform> markers, Transform viewRoot, FirstPersonController fpc,
            Interactor interactor, TwistSequence twist, HudRefs hud, EndCredits credits, CameraStation station, Material matProp)
        {
            var director = root.gameObject.AddComponent<Act2Director>();
            director.player = fpc;
            director.interactor = interactor;
            director.twist = twist;
            director.viewer = hud.viewer;
            director.credits = credits;
            director.whiteFlash = twist.whiteFlash;
            director.finalPlate = (Texture)AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/2D/UI/P_Final_Act2.png")
                                  ?? AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/2D/UI/P_Final.png");

            // --- виньетка «проявленности» ---
            var canvas = hud.frame.parent;
            var meterGo = new GameObject("GhostMeter", typeof(RectTransform), typeof(CanvasGroup), typeof(RawImage));
            meterGo.transform.SetParent(canvas, false);
            Stretch((RectTransform)meterGo.transform);
            var edges = meterGo.GetComponent<RawImage>();
            edges.texture = GhostEdgesTexture();
            edges.color = new Color(1f, 0.97f, 0.9f);
            edges.raycastTarget = false;
            var meterGroup = meterGo.GetComponent<CanvasGroup>();
            meterGroup.alpha = 0f;
            meterGroup.blocksRaycasts = false;
            meterGo.transform.SetSiblingIndex(1);
            var meter = meterGo.AddComponent<GhostMeterUI>();
            meter.group = meterGroup;
            director.meter = meter;

            // --- живой фотограф ---
            markers.TryGetValue("SPAWN_Player", out var spawn);
            var ph = new GameObject("LivingPhotographer");
            ph.transform.SetParent(root, false);
            ph.transform.position = spawn ? spawn.position : Vector3.zero;
            var agent = ph.AddComponent<NavMeshAgent>();
            agent.radius = 0.3f;
            agent.height = 1.8f;
            agent.speed = 1.3f;
            agent.angularSpeed = 240f;
            agent.acceleration = 6f;
            agent.stoppingDistance = 0.2f;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
            agent.enabled = false; // включит LivingPhotographer.Begin()

            var stand = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CharactersFolder}/C_Photographer_Stand.png");
            var walk = Enumerable.Range(1, 4)
                .Select(i => AssetDatabase.LoadAssetAtPath<Texture2D>($"{CharactersFolder}/C_Photographer_Walk_{i}.png"))
                .Where(t => t).ToArray();
            GameObject visual;
            if (stand)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
                var (w, h) = FigureSize(stand, MargaretHeight);
                visual.transform.SetParent(ph.transform, false);
                visual.transform.localPosition = new Vector3(0f, h * 0.5f, 0f);
                visual.transform.localScale = new Vector3(w, h, 1f);
                var pm = SaveBillboardMat("M_Photographer_Sprite", Color.white);
                pm.SetTexture("_BaseMap", stand);
                visual.GetComponent<Renderer>().sharedMaterial = pm;
            }
            else
            {
                // заглушка, пока художники не нарисовали C_Photographer_Stand
                visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.transform.SetParent(ph.transform, false);
                visual.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                visual.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
                visual.GetComponent<Renderer>().sharedMaterial = SaveMat("M_Photographer_Placeholder", new Color(0.32f, 0.3f, 0.28f));
            }
            visual.name = "Visual";
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;

            var photographer = ph.AddComponent<LivingPhotographer>();
            photographer.visual = visual.GetComponent<Renderer>();
            photographer.standTexture = stand;
            photographer.walkFrames = walk;
            photographer.tripodBlockers = MaskSolid;
            photographer.spawn = spawn;
            photographer.exit = spawn;
            foreach (Transform vp in viewRoot)
                if (vp.name.StartsWith("ViewPoint_")) photographer.points.Add(vp);

            // --- его штативы: копия модели нашей камеры ---
            var model = station ? station.transform.Find("Model") : null;
            for (int i = 0; i < 2; i++)
            {
                var t = new GameObject($"PhotographerTripod_{i + 1}");
                t.transform.SetParent(root, false);
                if (model)
                {
                    var m = Object.Instantiate(model.gameObject, t.transform, false);
                    m.name = "Model";
                    m.transform.localPosition = model.localPosition;
                    m.transform.localRotation = model.localRotation;
                    foreach (var c in m.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                    // метка «открытая заслонка» — только для нашей камеры, у копии её прячем
                    var openMark = FindDeep(m.transform, "Shutter_Open");
                    if (openMark && openMark.GetComponent<Renderer>()) openMark.GetComponent<Renderer>().enabled = false;
                }
                else
                {
                    Box("Box", t.transform, new Vector3(0f, LensHeight, 0f), new Vector3(0.3f, 0.3f, 0.4f), matProp, false);
                    Box("Legs", t.transform, new Vector3(0f, LensHeight * 0.5f, 0f), new Vector3(0.06f, LensHeight, 0.06f), matProp, false);
                }
                var pc = t.AddComponent<PhotographerCamera>();
                pc.occluders = MaskSolid;
                pc.lensHeight = LensHeight;
                // холодный конус света, пока заслонка открыта: видно, куда смотрит камера
                var beam = new GameObject("Beam").AddComponent<Light>();
                beam.transform.SetParent(t.transform, false);
                beam.transform.localPosition = new Vector3(0f, LensHeight, 0.2f);
                beam.transform.localRotation = Quaternion.identity;
                beam.type = LightType.Spot;
                beam.spotAngle = pc.halfAngle * 2f;
                beam.range = pc.range;
                beam.intensity = 2f;
                beam.color = new Color(0.75f, 0.85f, 1f);
                beam.shadows = LightShadows.None;
                beam.enabled = false;
                pc.beam = beam;
                t.SetActive(false);
                photographer.tripods.Add(pc);
            }
            director.photographer = photographer;
            Debug.Log($"[Jam] Акт 2: фотограф {(stand ? "со спрайтом" : "— заглушка")}, точек съёмки {photographer.points.Count}.");
        }

        /// <summary>Белые края экрана с прозрачной серединой — призрак «проявляется».</summary>
        static Texture2D GhostEdgesTexture()
        {
            string path = MatFolder + "/UI_GhostEdges.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing) return existing;
            const int N = 128;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.414f;
                byte a = (byte)(Mathf.Clamp01(Mathf.InverseLerp(0.35f, 1f, r)) * 255f);
                px[y * N + x] = new Color32(255, 255, 255, a);
            }
            t.SetPixels32(px);
            t.Apply();
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.alphaIsTransparency = true;
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static CanvasGroup CreateWhiteFlash(Transform canvas)
        {
            var go = new GameObject("WhiteFlash", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            go.transform.SetParent(canvas, false);
            Stretch((RectTransform)go.transform);
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 0.98f, 0.94f);
            img.raycastTarget = false;
            var g = go.GetComponent<CanvasGroup>();
            g.alpha = 0f;
            g.blocksRaycasts = false;
            // под пластиной: пластина проявляется поверх белого
            int viewerIndex = canvas.Find("PlateViewer") ? canvas.Find("PlateViewer").GetSiblingIndex() : go.transform.GetSiblingIndex();
            go.transform.SetSiblingIndex(viewerIndex);
            return g;
        }

        static EndCredits CreateLetterAndCredits(Transform canvas, FirstPersonController fpc, Interactor interactor)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (!font) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // --- письмо ---
            var letterGo = new GameObject("IntroLetter", typeof(RectTransform), typeof(CanvasGroup));
            letterGo.transform.SetParent(canvas, false);
            Stretch((RectTransform)letterGo.transform);
            var shade = new GameObject("Shade", typeof(RectTransform), typeof(Image));
            shade.transform.SetParent(letterGo.transform, false);
            Stretch((RectTransform)shade.transform);
            shade.GetComponent<Image>().color = Color.black; // заставка начинается в полной темноте
            shade.GetComponent<Image>().raycastTarget = false;

            var paper = UiChild<RawImage>("Paper", letterGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900f, 980f));
            paper.color = new Color(0.86f, 0.8f, 0.68f);
            paper.raycastTarget = false;
            var paperTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/2D/UI/UI_Letter.png");
            if (paperTex)
            {
                paper.texture = paperTex; // RawImage — без отдельного спрайта-ассета
                paper.color = Color.white;
            }

            var body = UiChild<Text>("Body", paper.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(760f, 860f));
            body.font = font;
            body.fontSize = 28;
            body.lineSpacing = 1.1f;
            body.color = new Color(0.16f, 0.12f, 0.1f);
            body.alignment = TextAnchor.UpperLeft;
            body.raycastTarget = false;

            var hint = UiChild<Text>("Hint", letterGo.transform, new Vector2(0.5f, 0f), new Vector2(0f, 50f), new Vector2(600f, 50f));
            hint.font = font;
            hint.fontSize = 24;
            hint.alignment = TextAnchor.MiddleCenter;
            hint.color = new Color(0.8f, 0.75f, 0.65f, 0.7f);
            hint.raycastTarget = false;

            var letter = letterGo.AddComponent<IntroLetter>();
            letter.group = letterGo.GetComponent<CanvasGroup>();
            letter.body = body;
            letter.hint = hint;
            letter.textEn = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Game/Text/letter_en.txt");
            letter.textRu = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Game/Text/intro_ru.txt")
                            ?? AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/_Game/Text/letter_ru.txt");
            // заставка: голос, экипаж, фасад дома (всё необязательно — без файлов идёт по таймеру)
            letter.paper = paper.rectTransform;
            letter.audioSource = letterGo.AddComponent<AudioSource>();
            letter.audioSource.playOnAwake = false;
            letter.audioSource.spatialBlend = 0f;
            letter.voiceOver = LoadAudio("intro_vo");
            letter.carriageLoop = LoadAudio("carriage");
            letter.arrival = LoadAudio("carriage_stop");
            letter.plateMaterial = LoadOrCreatePlateMat();
            var house = UiChild<RawImage>("House", letterGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1440f, 1080f));
            house.texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/2D/UI/UI_House.png");
            house.raycastTarget = false;
            house.transform.SetSiblingIndex(1);
            if (house.texture) house.rectTransform.sizeDelta = new Vector2(1080f * house.texture.width / house.texture.height, 1080f);
            letter.houseImage = house;
            letter.player = fpc;
            letter.interactor = interactor;

            // --- титры ---
            var credGo = new GameObject("EndCredits", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            credGo.transform.SetParent(canvas, false);
            Stretch((RectTransform)credGo.transform);
            credGo.GetComponent<Image>().color = Color.black;
            credGo.GetComponent<Image>().raycastTarget = false;
            var credText = UiChild<Text>("Text", credGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200f, 800f));
            credText.font = font;
            credText.fontSize = 34;
            credText.alignment = TextAnchor.MiddleCenter;
            credText.color = new Color(0.86f, 0.8f, 0.68f);
            credText.raycastTarget = false;
            var credits = credGo.AddComponent<EndCredits>();
            credits.group = credGo.GetComponent<CanvasGroup>();
            credits.text = credText;
            return credits;
        }

        // ================= проявочная =================

        /// <summary>
        /// Проявочная: по меткам DARKROOM_BENCH (+ _LOOK — куда смотрит игрок у верстака) и DARKROOM_ROPE,
        /// иначе — в комнате с самым маленьким полом. Верстак-заглушка, красная лампа, верёвка, зона комнаты.
        /// </summary>
        static void CreateDarkroom(Dictionary<string, Transform> markers, GameObject house, Dictionary<Renderer, Light> roomLights,
            Transform parent, Material matProp, Material matDark, Material matHung,
            PlateInventory inventory, PlateViewer viewer, FirstPersonController fpc, Interactor interactor, HandObscura hand, DeathAndCheckpoint death)
        {
            Vector3 houseCenter = RenderBounds(house).center;

            // комната: пол под меткой верстака или самый маленький пол
            markers.TryGetValue("DARKROOM_BENCH", out var benchMarker);
            Renderer floor = null;
            var floors = house.GetComponentsInChildren<MeshRenderer>().Where(IsFloor).Cast<Renderer>().ToList();
            if (benchMarker)
                floor = floors.FirstOrDefault(f => f.bounds.min.x <= benchMarker.position.x && f.bounds.max.x >= benchMarker.position.x
                                                && f.bounds.min.z <= benchMarker.position.z && f.bounds.max.z >= benchMarker.position.z);
            if (!floor)
                floor = floors.OrderBy(f => f.bounds.size.x * f.bounds.size.z).FirstOrDefault();
            if (!floor)
            {
                Debug.LogWarning("[Jam] Не нашёл пол для проявочной — проявочная не создана, включён автопоказ пластин.");
                var ph = Object.FindAnyObjectByType<PlateHUD>();
                if (ph) ph.autoDevelopOnPickup = true;
                return;
            }

            var fb = floor.bounds;
            var go = new GameObject("Darkroom");
            go.transform.SetParent(parent, false);

            // верстак: у дальней от центра дома стены, лицом к центру
            Vector3 toCenter = Flat(houseCenter - fb.center);
            Vector3 benchPos, faceDir;
            if (benchMarker)
            {
                benchPos = new Vector3(benchMarker.position.x, fb.max.y, benchMarker.position.z);
                faceDir = markers.TryGetValue("DARKROOM_BENCH_LOOK", out var bl) ? Flat(bl.position - benchMarker.position) : toCenter;
            }
            else
            {
                float depth = Mathf.Abs(Vector3.Dot(fb.extents, new Vector3(Mathf.Abs(toCenter.x), 0f, Mathf.Abs(toCenter.z))));
                benchPos = new Vector3(fb.center.x, fb.max.y, fb.center.z) - toCenter * Mathf.Max(0f, depth - 0.45f);
                faceDir = toCenter;
            }

            var bench = new GameObject("Bench").transform;
            bench.SetParent(go.transform, false);
            bench.SetPositionAndRotation(benchPos, Quaternion.LookRotation(faceDir));
            var top = Box("Bench_Top", bench, new Vector3(0f, 0.85f, 0f), new Vector3(1.6f, 0.08f, 0.6f), matProp, true);
            Box("Bench_Base", bench, new Vector3(0f, 0.42f, 0f), new Vector3(1.5f, 0.84f, 0.5f), matProp, true);
            bool userProps = house.GetComponentsInChildren<Transform>(true).Any(t => t.name.StartsWith("DR_")); // кюветы от 3D-шника уже на верстаке
            for (int i = -1; i <= 1 && !userProps; i++)
                Box($"Tray_{i + 2}", bench, new Vector3(i * 0.45f, 0.91f, 0.02f), new Vector3(0.36f, 0.04f, 0.28f), matDark, false);

            // точка, где стоит игрок, — перед верстаком
            var stand = new GameObject("StandPoint").transform;
            stand.SetParent(go.transform, false);
            stand.SetPositionAndRotation(benchPos + faceDir * 0.85f + Vector3.up * 0.05f, Quaternion.LookRotation(-faceDir));

            // красная лампа над верстаком
            var red = new GameObject("RedLamp").AddComponent<Light>();
            red.transform.SetParent(go.transform, false);
            red.transform.position = benchPos + Vector3.up * 2.4f + faceDir * 0.3f;
            red.type = LightType.Point;
            red.color = new Color(1f, 0.08f, 0.04f);
            red.range = 4.5f;
            red.intensity = 0.5f;
            red.shadows = LightShadows.None;

            // верёвка поперёк комнаты, над головой, чуть перед верстаком
            var rope = new GameObject("Rope").transform;
            rope.SetParent(go.transform, false);
            if (markers.TryGetValue("DARKROOM_ROPE", out var ropeMarker))
                rope.SetPositionAndRotation(ropeMarker.position, Quaternion.LookRotation(-faceDir));
            else
                rope.SetPositionAndRotation(benchPos + faceDir * 1.4f + Vector3.up * 2.45f, Quaternion.LookRotation(-faceDir)); // пластины висят выше головы — не мешают пройти
            var cord = Box("Cord", rope, Vector3.zero, new Vector3(2.4f, 0.012f, 0.012f), matDark, false);
            cord.name = "Cord";

            var dr = bench.gameObject.AddComponent<Darkroom>(); // на корне верстака — E работает по любой его части
            dr.inventory = inventory;
            dr.viewer = viewer;
            dr.player = fpc;
            dr.interactor = interactor;
            dr.hand = hand;
            dr.checkpoint = death;
            dr.standPoint = stand;
            dr.redLight = red;
            // фонарь на верстаке (DR_lantern от 3D-шника): от него тоже можно зажечь свечу
            var lantern = house.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith("DR_lantern") && t.GetComponent<Collider>());
            if (lantern) lantern.gameObject.AddComponent<Candelabra>();
            dr.roomLight = roomLights.TryGetValue(floor, out var rl) ? rl : null;
            dr.roomBounds = new Bounds(new Vector3(fb.center.x, fb.max.y + 1.5f, fb.center.z), new Vector3(fb.size.x, 3f, fb.size.z));
            dr.rope = rope;
            dr.ropeStart = new Vector3(-1.1f, 0f, 0f);
            dr.ropeEnd = new Vector3(1.1f, 0f, 0f);
            dr.hungPlateMaterial = matHung;
            // прищепка от 3D-шника: шаблон (выключен), Darkroom копирует его на каждую пластину на верёвке
            var pinPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PropsFolder}/prishepka.fbx");
            if (pinPrefab)
            {
                var pin = new GameObject("ClothespinTemplate");
                pin.transform.SetParent(go.transform, false);
                var pm = (GameObject)PrefabUtility.InstantiatePrefab(pinPrefab, pin.transform);
                pm.transform.localPosition = Vector3.zero;
                foreach (var c in pm.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
                foreach (var r in pm.GetComponentsInChildren<Renderer>()) { ApplyPs1Materials(r, matProp); r.shadowCastingMode = ShadowCastingMode.Off; }
                pin.SetActive(false);
                dr.clothespin = pin;
            }

            CreateAlbum(markers, benchPos, faceDir, parent, matDark, matHung, viewer);

            Debug.Log($"[Jam] Проявочная: {(benchMarker ? "по метке DARKROOM_BENCH" : $"в комнате с самым маленьким полом ({floor.name})")}.");
        }

        // ================= интерфейс =================

        struct HudRefs
        {
            public RectTransform frame;
            public CanvasGroup frameGroup;
            public RawImage glass;
            public PlateViewer viewer;
            public CanvasGroup blackout;
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
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
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
            // с латунной рамкой (T_Plate_Frame: 512×420, окно 320×240) пластина меньше, чтобы рамка влезла в экран
            var frameTex = LoadArt2D("T_Plate_Frame");
            const float k = 2.4f;
            var plateImg = UiChild<RawImage>("Plate", viewerGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero,
                frameTex ? new Vector2(320f * k, 240f * k) : new Vector2(1024f, 768f));
            plateImg.raycastTarget = false;
            if (frameTex)
            {
                var frameImg = UiChild<RawImage>("PlateFrame", plateImg.transform, new Vector2(0.5f, 0.5f), Vector2.zero,
                    new Vector2(frameTex.width * k, frameTex.height * k));
                frameImg.texture = frameTex;
                frameImg.raycastTarget = false;
            }

            // подпись от руки под снимком из альбома
            var capFont = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            var cap = UiChild<Text>("Caption", viewerGo.transform, new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(1400f, 70f));
            cap.font = capFont ? capFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            cap.fontSize = 40;
            cap.fontStyle = FontStyle.Italic;
            cap.alignment = TextAnchor.MiddleCenter;
            cap.color = new Color(0.86f, 0.78f, 0.62f, 1f);
            cap.raycastTarget = false;

            var viewer = viewerGo.AddComponent<PlateViewer>();
            viewer.group = viewerGo.GetComponent<CanvasGroup>();
            viewer.image = plateImg;
            viewer.caption = cap;
            viewer.plateMaterial = plateMat;
            viewer.player = fpc;
            viewer.interactor = interactor;

            var hud = canvasGo.AddComponent<PlateHUD>();
            hud.inventory = inventory;
            hud.viewer = viewer;

            // --- затемнение при смерти: поверх всего ---
            var blackGo = new GameObject("Blackout", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            blackGo.transform.SetParent(canvasGo.transform, false);
            Stretch((RectTransform)blackGo.transform);
            var blackImg = blackGo.GetComponent<Image>();
            blackImg.color = Color.black;
            blackImg.raycastTarget = false;
            var blackout = blackGo.GetComponent<CanvasGroup>();
            blackout.alpha = 0f;
            blackout.blocksRaycasts = false;

            return new HudRefs { frame = frame, frameGroup = frameGroup, glass = glass, viewer = viewer, blackout = blackout };
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

        const string TexturesFolder = "Assets/Art/2D/Textures";

        static Shader Ps1Shader()
        {
            var sh = Shader.Find("Jam/PS1Lit");
            if (!sh)
            {
                Debug.LogError("[Jam] Не найден шейдер Jam/PS1Lit — материалы будут на URP/Lit.");
                sh = Shader.Find("Universal Render Pipeline/Lit");
            }
            return sh;
        }

        /// <summary>Однотонный PS1-материал (заглушки, стены и полы без своего тайла).</summary>
        static Material SaveMat(string name, Color color)
        {
            string path = $"{MatFolder}/{name}.mat";
            var shader = Ps1Shader();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader) mat.shader = shader;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// PS1-материал для слота с именем тайла (T_Wood_Dark и т.п.): M_PS1_T_Wood_Dark с текстурой
        /// Assets/Art/2D/Textures/T_Wood_Dark.png. Нет текстуры — серый, появится при следующей сборке.
        /// </summary>
        static Material TileMat(string tileName)
        {
            string path = $"{MatFolder}/M_PS1_{tileName}.mat";
            var shader = Ps1Shader();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader) mat.shader = shader;

            var tex = FindTexture(tileName);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tex ? Color.white : new Color(0.5f, 0.48f, 0.45f));
            // 64 px на метр: тайл 128 px = 2 м
            if (mat.HasProperty("_TileSize")) mat.SetFloat("_TileSize", tex ? Mathf.Max(0.5f, tex.width / 64f) : 1f);
            // трим-лист и портрет кладутся по UV модели, остальное — по осям мира
            bool meshUV = tileName.StartsWith("T_Trim") || tileName.StartsWith("T_Portrait");
            if (mat.HasProperty("_UseMeshUV")) mat.SetFloat("_UseMeshUV", meshUV ? 1f : 0f);
            if (meshUV) mat.EnableKeyword("_USEMESHUV_ON"); else mat.DisableKeyword("_USEMESHUV_ON");
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Texture2D FindTexture(string name)
        {
            if (!AssetDatabase.IsValidFolder(TexturesFolder)) return null;
            foreach (var guid in AssetDatabase.FindAssets(name + " t:Texture2D", new[] { TexturesFolder }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) == name) return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            }
            return null;
        }

        /// <summary>Слоты с именами T_… получают тайл, остальные — запасной материал. Цвета вершин включаются, если они есть.</summary>
        static void ApplyPs1Materials(Renderer r, Material fallback)
        {
            var src = r.sharedMaterials;
            var dst = new Material[src.Length];
            bool hasColors = false;
            var mf = r.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh) hasColors = mf.sharedMesh.colors32 != null && mf.sharedMesh.colors32.Length > 0;

            for (int i = 0; i < src.Length; i++)
            {
                string n = src[i] ? src[i].name.Replace(" (Instance)", "") : "";
                n = Regex.Replace(n, @"\.\d+$", ""); // T_Wood_Dark.001 из FBX — та же текстура T_Wood_Dark
                var m = n.StartsWith("T_") ? TileMat(n) : fallback;
                if (hasColors && m && m.HasProperty("_VertexColorAmount"))
                {
                    // отдельная копия с цветами вершин, чтобы не красить заглушки без цветов
                    var vcPath = AssetDatabase.GetAssetPath(m).Replace(".mat", "_VC.mat");
                    var vc = AssetDatabase.LoadAssetAtPath<Material>(vcPath);
                    if (!vc) { vc = new Material(m); AssetDatabase.CreateAsset(vc, vcPath); }
                    else vc.CopyPropertiesFromMaterial(m);
                    vc.SetFloat("_VertexColorAmount", 1f);
                    EditorUtility.SetDirty(vc);
                    m = vc;
                }
                dst[i] = m;
            }
            if (dst.Length == 0) dst = new[] { fallback };
            r.sharedMaterials = dst;
        }

        static Material SaveShaderMat(string name, string shaderName)
        {
            string path = $"{MatFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat) return mat;
            var sh = Shader.Find(shaderName);
            if (!sh) { Debug.LogError($"[Jam] Не найден шейдер {shaderName}."); return null; }
            mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, path);
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

        static Material SaveUnlitMat(string name)
        {
            string path = $"{MatFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (!shader) return null;
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static Material LoadOrCreatePlateMat()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(PlateMatPath);
            if (!mat)
            {
                string path = MatFolder + "/M_PlateDisplay.mat";
                mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat)
                {
                    var sh = Shader.Find("Jam/PlateDisplay");
                    if (!sh) return null;
                    mat = new Material(sh);
                    AssetDatabase.CreateAsset(mat, path);
                }
            }
            // царапины и патина от художников (T_Plate_Overlay.png в Art/2D/UI или Art/2D/Textures)
            var overlay = LoadArt2D("T_Plate_Overlay");
            if (overlay && mat.HasProperty("_Overlay"))
            {
                mat.SetTexture("_Overlay", overlay);
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        static Texture2D LoadArt2D(string name) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/Art/2D/UI/{name}.png")
            ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/Art/2D/Textures/{name}.png");

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
