using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Jam.Exposure;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Gameplay
{
    /// <summary>
    /// Сохранение — автоматическое, в момент проявки (это и есть чекпойнт игры) и при начале акта 2.
    /// Хранится: какие сцены проявлены (значит, как изменился дом), снимки этих сцен (JPG — для альбома и верёвки),
    /// сколько магния, начат ли акт 2. Всё в PlayerPrefs: в браузере они живут в IndexedDB и переживают перезапуск.
    /// «Продолжить» в меню: GameSession.ContinueGame() → сцена игры без письма → игрок у верстака, дом как был.
    /// </summary>
    public static class SaveGame
    {
        const string KeyData = "jam.save.v1";
        const string KeyPlate = "jam.save.plate.";

        [Serializable]
        class Data
        {
            public List<int> developed = new();
            public bool act2;
            public int flash = -1;
        }

        public static bool HasSave => PlayerPrefs.HasKey(KeyData);

        /// <summary>Следующая загрузка сцены игры — восстановить сохранение, а не начинать с письма.</summary>
        public static bool PendingRestore { get; set; }

        static Data Load()
        {
            try { return JsonUtility.FromJson<Data>(PlayerPrefs.GetString(KeyData, "{}")) ?? new Data(); }
            catch { return new Data(); }
        }

        static void Store(Data d)
        {
            PlayerPrefs.SetString(KeyData, JsonUtility.ToJson(d));
            PlayerPrefs.Save();
        }

        public static void Clear()
        {
            for (int i = 0; i <= 9; i++) PlayerPrefs.DeleteKey(KeyPlate + i);
            PlayerPrefs.DeleteKey(KeyData);
            PlayerPrefs.Save();
        }

        // ---------------- запись ----------------

        static void OnPlateDeveloped(PastScene scene, Plate plate)
        {
            if (!scene) return;
            var d = HasSave ? Load() : new Data();
            if (!d.developed.Contains(scene.index)) d.developed.Add(scene.index);
            if (plate != null && plate.Image)
            {
                var jpg = ToJpg(plate.Image);
                if (jpg != null) PlayerPrefs.SetString(KeyPlate + scene.index, Convert.ToBase64String(jpg));
            }
            var flash = UnityEngine.Object.FindAnyObjectByType<MagnesiumFlash>();
            if (flash) d.flash = flash.Charges;
            Store(d);
            Debug.Log($"[Save] Сохранено: проявлены {string.Join(", ", d.developed)}.");
        }

        static void OnAct2Started()
        {
            var d = HasSave ? Load() : new Data();
            d.act2 = true;
            Store(d);
            Debug.Log("[Save] Сохранено: акт 2.");
        }

        static byte[] ToJpg(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                var t = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                t.Apply();
                var bytes = t.EncodeToJPG(85);
                UnityEngine.Object.Destroy(t);
                return bytes;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] Не удалось сохранить снимок: " + e.Message);
                return null;
            }
            finally { RenderTexture.active = prev; }
        }

        static Texture2D LoadPlate(int index)
        {
            var s = PlayerPrefs.GetString(KeyPlate + index, null);
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var t = new Texture2D(2, 2, TextureFormat.RGB24, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "SavedPlate_" + index };
                return t.LoadImage(Convert.FromBase64String(s)) ? t : null;
            }
            catch { return null; }
        }

        // ---------------- подключение к сцене игры ----------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        // sceneLoaded приходит после Awake объектов сцены, но до их Start — письмо ещё не началось, его можно отменить
        static void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (!UnityEngine.Object.FindAnyObjectByType<StoryDirector>() || UnityEngine.Object.FindAnyObjectByType<Hook>()) return;
            bool restore = PendingRestore && HasSave;
            PendingRestore = false;
            if (restore)
            {
                var intro = UnityEngine.Object.FindAnyObjectByType<IntroLetter>(FindObjectsInactive.Include);
                if (intro)
                {
                    intro.showOnStart = false;
                    if (intro.group) { intro.group.alpha = 0f; intro.group.blocksRaycasts = false; }
                }
            }
            new GameObject("SaveGame").AddComponent<Hook>().restore = restore;
        }

        /// <summary>Живёт в сцене игры: подписывается на проявку и, если надо, восстанавливает сохранение.</summary>
        class Hook : MonoBehaviour
        {
            public bool restore;
            StoryDirector _story;
            Act2Director _act2;

            IEnumerator Start()
            {
                _story = StoryDirector.Instance;
                _act2 = FindAnyObjectByType<Act2Director>();
                if (_story) _story.PlateDeveloped += OnPlateDeveloped;
                if (_act2) _act2.Started += OnAct2Started;
                if (!restore) yield break;
                // альбом рисует подсказки кадром позже, обскура на верстаке ставится при загрузке — даём всему встать
                for (int i = 0; i < 3; i++) yield return null;
                Restore();
            }

            void OnDestroy()
            {
                if (_story) _story.PlateDeveloped -= OnPlateDeveloped;
                if (_act2) _act2.Started -= OnAct2Started;
            }

            void Restore()
            {
                var d = Load();
                var developed = d.developed.Distinct().OrderBy(i => i).ToList();
                var player = FindAnyObjectByType<FirstPersonController>();
                var room = FindAnyObjectByType<Darkroom>();

                // обучение пройдено: обскура в руках, дом проснулся; нулевая сцена снята — камера своя
                var gear = FindAnyObjectByType<GearPickup>();
                if (gear) gear.SkipTutorial(developed.Count > 0);

                if (_story) _story.Restore(developed);

                var album = FindAnyObjectByType<FamilyAlbum>();
                foreach (var i in developed)
                {
                    var tex = LoadPlate(i);
                    if (!tex) continue;
                    if (album) album.RestoreDeveloped(i, tex);
                    if (room) room.HangRestored(tex);
                }

                var flash = FindAnyObjectByType<MagnesiumFlash>();
                if (flash && d.flash >= 0) flash.AddCharges(d.flash - flash.Charges);

                if (player && room && room.standPoint)
                {
                    player.Teleport(room.standPoint.position, room.standPoint.rotation);
                    if (room.checkpoint) room.checkpoint.SetCheckpoint(room.standPoint.position, room.standPoint.rotation);
                }
                var margaret = FindAnyObjectByType<MargaretAI>();
                if (margaret && player && margaret.isActiveAndEnabled) margaret.Calm(8f, player.transform.position);

                if (d.act2 && _act2) _act2.DebugJump(); // тот же вход в акт 2, что после перелома: потолок, Маргарет рядом
                Debug.Log($"[Save] Продолжение: проявлены {string.Join(", ", developed)}{(d.act2 ? ", акт 2" : "")}.");
            }
        }
    }
}
