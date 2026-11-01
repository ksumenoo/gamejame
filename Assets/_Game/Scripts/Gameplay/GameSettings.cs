using Jam.Audio;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Gameplay
{
    /// <summary>
    /// Настройки игрока: общая громкость, музыка, звуки, чувствительность мыши, яркость.
    /// Меню вызывает Set…(), значения сохраняются в PlayerPrefs и сразу применяются,
    /// а при загрузке сцены игры применяются сами (Apply в AfterSceneLoad) — меню может быть отдельной сценой.
    /// </summary>
    public static class GameSettings
    {
        const string KeyMaster = "jam.volume.master";
        const string KeySfx = "jam.volume.sfx";
        const string KeyMouse = "jam.mouse";
        const string KeyMusic = "jam.volume.music";
        const string KeyBright = "jam.brightness";
        const string KeySmooth = "jam.smooth";
        static readonly int BrightnessId = Shader.PropertyToID("_JamBrightness");

        /// <summary>Вся игра, включая голос письма и музыку меню: 0…1.</summary>
        public static float Master => PlayerPrefs.GetFloat(KeyMaster, 1f);
        /// <summary>Звуковые эффекты игры (JamAudio): 0…1.</summary>
        public static float Sfx => PlayerPrefs.GetFloat(KeySfx, 1f);
        /// <summary>Множитель чувствительности мыши: 0,25…3, по умолчанию 1.</summary>
        public static float Mouse => PlayerPrefs.GetFloat(KeyMouse, 1f);
        /// <summary>Музыка меню и титров: 0…1.</summary>
        public static float Music => PlayerPrefs.GetFloat(KeyMusic, 1f);
        /// <summary>Яркость картинки: 0,6…1,8, по умолчанию 1 (множитель в шейдере вывода кадра).</summary>
        public static float Brightness => PlayerPrefs.GetFloat(KeyBright, 1f);

        /// <summary>Сглаженная картинка: без дрожания вершин и без ряби дизеринга — для тех, кого укачивает.</summary>
        public static bool Smooth => PlayerPrefs.GetInt(KeySmooth, 0) == 1;
        public static void SetSmooth(bool on) { PlayerPrefs.SetInt(KeySmooth, on ? 1 : 0); Apply(); }

        public static void SetMaster(float v) { PlayerPrefs.SetFloat(KeyMaster, Mathf.Clamp01(v)); Apply(); }
        public static void SetSfx(float v) { PlayerPrefs.SetFloat(KeySfx, Mathf.Clamp01(v)); Apply(); }
        public static void SetMouse(float v) { PlayerPrefs.SetFloat(KeyMouse, Mathf.Clamp(v, 0.25f, 3f)); Apply(); }
        public static void SetMusic(float v) { PlayerPrefs.SetFloat(KeyMusic, Mathf.Clamp01(v)); Apply(); }
        public static void SetBrightness(float v) { PlayerPrefs.SetFloat(KeyBright, Mathf.Clamp(v, 0.6f, 1.8f)); Apply(); }

        /// <summary>Громкость музыки с учётом настройки — для тех, кто сам ставит громкость своему источнику (титры).</summary>
        public static float MusicVolume(float baseVolume) => baseVolume * Music;

        const float MenuMusicBase = 0.6f;

        static float s_baseLook = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_baseLook = -1f;

        /// <summary>AfterSceneLoad срабатывает только для первой сцены — дальше применяем на каждую загрузку (меню → игра).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Apply();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) => Apply();

        public static void Apply()
        {
            AudioListener.volume = Master;
            Shader.SetGlobalFloat(BrightnessId, Brightness);
            var level = Object.FindAnyObjectByType<LevelSettings>();
            if (level) level.ApplySnap();
            var menuMusic = GameObject.Find("MenuMusic");
            if (menuMusic && menuMusic.TryGetComponent<AudioSource>(out var ms)) ms.volume = MenuMusicBase * Music;
            if (JamAudio.Instance) JamAudio.Instance.masterVolume = Sfx;
            GameMusic.ApplyVolume();
            var fpc = Object.FindAnyObjectByType<FirstPersonController>();
            if (fpc)
            {
                if (s_baseLook < 0f) s_baseLook = fpc.lookSensitivity;
                fpc.lookSensitivity = s_baseLook * Mouse;
            }
        }

        /// <summary>Пауза: стоп времени, игрок не двигается, курсор свободен. Снять паузу — Pause(false).</summary>
        public static void Pause(bool on)
        {
            Time.timeScale = on ? 0f : 1f;
            AudioListener.pause = on;
            var fpc = Object.FindAnyObjectByType<FirstPersonController>();
            if (fpc) fpc.InputLocked = on;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (on) FirstPersonController.LockCursor(false); // в браузере курсор захватит следующий клик игрока
#else
            FirstPersonController.LockCursor(!on);
#endif
        }
    }
}
