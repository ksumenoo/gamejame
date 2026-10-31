using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Единственное место, где живёт сюжет акта 1.
    /// — какие сцены прошлого проявлены и какие открыты;
    /// — изменения дома: объекты BEFORE_S{n}_… исчезают, AFTER_S{n}_… появляются после проявки сцены n;
    /// — агрессия Маргарет = число проявленных сцен.
    /// Сцены 1–4 открыты сразу, 5 (коридор) — после первых четырёх, 6 (проявочная) — после пятой.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class StoryDirector : MonoBehaviour
    {
        public static StoryDirector Instance { get; private set; }

        [Tooltip("Пусто — найдёт все PastScene в сцене.")]
        public List<PastScene> scenes = new();
        [Tooltip("Сцены с этим номером и выше открываются по очереди, остальные — сразу.")]
        public int firstGatedScene = 5;

        [ShowNativeProperty] public int DevelopedCount { get; private set; }
        public int Aggression => DevelopedCount;
        public bool AllDeveloped => scenes.Count > 0 && scenes.All(s => s.Current == PastScene.State.Developed);

        public event Action<PastScene> SceneDeveloped;
        /// <summary>Сцена проявлена вот этой пластиной (для альбома).</summary>
        public event Action<PastScene, Exposure.Plate> PlateDeveloped;
        public event Action<int> AggressionChanged;
        /// <summary>Проявлена последняя сцена — пора переворачивать дом.</summary>
        public event Action TwistReady;

        readonly Dictionary<int, List<GameObject>> _before = new();
        readonly Dictionary<int, List<GameObject>> _after = new();
        static readonly Regex VariantName = new(@"^(BEFORE|AFTER)_S(\d+)_", RegexOptions.IgnoreCase);

        void Awake()
        {
            Instance = this;
            if (scenes.Count == 0)
                scenes = FindObjectsByType<PastScene>(FindObjectsInactive.Include).OrderBy(s => s.index).ToList();

            CollectVariants();
            foreach (var list in _after.Values)
                foreach (var go in list) go.SetActive(false);

            foreach (var s in scenes)
                s.SetState(s.index >= firstGatedScene ? PastScene.State.Hidden : PastScene.State.Visible);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            foreach (var cam in FindObjectsByType<Exposure.ObscuraCamera>(FindObjectsInactive.Include))
                cam.ExposureFinished += TagPlate;
        }

        /// <summary>В момент съёмки запоминаем, какие сцены сложились в кадре: условия могли потом измениться.</summary>
        void TagPlate(Exposure.ObscuraCamera cam, Exposure.Plate plate)
        {
            if (plate == null) return;
            foreach (var s in scenes)
                if (s && s.Matches(plate) && !plate.Scenes.Contains(s.index))
                    plate.Scenes.Add(s.index);
            if (plate.Scenes.Count > 0) Debug.Log($"[Story] На пластине сцена(ы): {string.Join(", ", plate.Scenes)}");
        }

        /// <summary>
        /// Проявить пластину: если в кадре сложилась открытая сцена — она проявлена.
        /// Возвращает проявленную сцену или null. Вызывает проявочная.
        /// </summary>
        public PastScene TryDevelop(Exposure.Plate plate)
        {
            if (plate == null) return null;
            var scene = scenes.FirstOrDefault(s =>
                s && plate.Scenes.Contains(s.index) && s.Current == PastScene.State.Visible);
            if (scene)
            {
                Develop(scene);
                PlateDeveloped?.Invoke(scene, plate);
            }
            return scene;
        }

        [Button("Проявить следующую открытую сцену (тест)")]
        void DebugDevelopNext()
        {
            var s = scenes.FirstOrDefault(x => x.Current == PastScene.State.Visible);
            if (s) Develop(s);
        }

        public void Develop(PastScene scene)
        {
            if (!scene || scene.Current == PastScene.State.Developed) return;
            scene.SetState(PastScene.State.Developed);
            ToggleVariants(scene.index);
            DevelopedCount++;
            Debug.Log($"[Story] Проявлена сцена {scene.index}. Проявлено {DevelopedCount}/{scenes.Count}.");

            SceneDeveloped?.Invoke(scene);
            AggressionChanged?.Invoke(Aggression);
            OpenGatedScenes();

            if (AllDeveloped) TwistReady?.Invoke();
        }

        void OpenGatedScenes()
        {
            bool earlierDone = scenes.Where(s => s.index < firstGatedScene).All(s => s.Current == PastScene.State.Developed);
            if (!earlierDone) return;
            // открываем ровно одну следующую закрытую сцену, если все предыдущие проявлены
            var next = scenes.Where(s => s.Current == PastScene.State.Hidden).OrderBy(s => s.index).FirstOrDefault();
            if (!next) return;
            bool prevDone = scenes.Where(s => s.index < next.index).All(s => s.Current == PastScene.State.Developed);
            if (prevDone)
            {
                next.SetState(PastScene.State.Visible);
                Debug.Log($"[Story] Открыта сцена {next.index}.");
            }
        }

        void CollectVariants()
        {
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                var m = VariantName.Match(t.name);
                if (!m.Success) continue;
                int n = int.Parse(m.Groups[2].Value);
                var dict = m.Groups[1].Value.ToUpperInvariant() == "BEFORE" ? _before : _after;
                if (!dict.TryGetValue(n, out var list)) dict[n] = list = new List<GameObject>();
                list.Add(t.gameObject);
            }
        }

        void ToggleVariants(int n)
        {
            if (_before.TryGetValue(n, out var b)) foreach (var go in b) go.SetActive(false);
            if (_after.TryGetValue(n, out var a)) foreach (var go in a) go.SetActive(true);
        }
    }
}
