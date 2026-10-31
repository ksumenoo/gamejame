using Jam.Audio;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Gameplay
{
    /// <summary>
    /// Настройки игрока: общая громкость, громкость эффектов, чувствительность мыши.
    /// Меню вызывает Set…(), значения сохраняются в PlayerPrefs и сразу применяются,
    /// а при загрузке сцены игры применяются сами (Apply в AfterSceneLoad) — меню может быть отдельной сценой.
    /// </summary>
    public static class GameSettings
    {
        const string KeyMaster = "jam.volume.master";
        const string KeySfx = "jam.volume.sfx";
        const string KeyMouse = "jam.mouse";

        /// <summary>Вся игра, включая голос письма и музыку меню: 0…1.</summary>
        public static float Master => PlayerPrefs.GetFloat(KeyMaster, 1f);
        /// <summary>Звуковые эффекты игры (JamAudio): 0…1.</summary>
        public static float Sfx => PlayerPrefs.GetFloat(KeySfx, 1f);
        /// <summary>Множитель чувствительности мыши: 0,25…3, по умолчанию 1.</summary>
        public static float Mouse => PlayerPrefs.GetFloat(KeyMouse, 1f);

        public static void SetMaster(float v) { PlayerPrefs.SetFloat(KeyMaster, Mathf.Clamp01(v)); Apply(); }
        public static void SetSfx(float v) { PlayerPrefs.SetFloat(KeySfx, Mathf.Clamp01(v)); Apply(); }
        public static void SetMouse(float v) { PlayerPrefs.SetFloat(KeyMouse, Mathf.Clamp(v, 0.25f, 3f)); Apply(); }

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
            if (JamAudio.Instance) JamAudio.Instance.masterVolume = Sfx;
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
