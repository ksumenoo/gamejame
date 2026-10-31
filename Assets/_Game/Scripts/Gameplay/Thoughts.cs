using System.Collections.Generic;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Мысли героя: субтитр в общей строке подсказок + голос, если он записан.
    /// Голос кладётся в Assets/Resources/VO/vo_&lt;id&gt;.wav (id — из документа «текст для озвучки»).
    /// Нет файла — просто субтитр, так что озвучку можно добавлять по частям.
    /// Зачем: все реплики игры идут через одну точку — подключение озвучки не требует правок в коде.
    /// </summary>
    public static class Thoughts
    {
        /// <summary>
        /// Показывать ли мысли субтитрами. Выключено: игра объясняет себя без текста (свет, звук, сценки),
        /// а мысли звучат, только если для них записан голос. Кнопки (keys) показываются всегда.
        /// </summary>
        public static bool ShowText = false;

        static readonly HashSet<string> s_said = new();
        static AudioSource s_voice;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_said.Clear();
            s_voice = null;
        }

        public static bool WasSaid(string id) => s_said.Contains(id);

        /// <summary>Сказать один раз за игру.</summary>
        public static bool Once(string id, string text, string keys = null)
        {
            if (!s_said.Add(id)) return false;
            Say(id, text, keys);
            return true;
        }

        /// <summary>
        /// Сказать (без проверки «уже говорил»). keys — подсказка по кнопкам: серой строкой под субтитром,
        /// голосом не читается.
        /// </summary>
        public static void Say(string id, string text, string keys = null)
        {
            s_said.Add(id);
            string line = ShowText ? text ?? "" : "";
            if (!string.IsNullOrEmpty(keys)) line += (line.Length > 0 ? "\n" : "") + "<color=#9a9488>" + keys + "</color>";
            if (line.Length > 0 && PlateCounterUI.Instance) PlateCounterUI.Instance.ShowHint(line);

            var clip = Resources.Load<AudioClip>("VO/vo_" + id);
            if (!clip) return;
            if (!s_voice)
            {
                var go = new GameObject("[Thoughts VO]");
                s_voice = go.AddComponent<AudioSource>();
                s_voice.spatialBlend = 0f;
                s_voice.playOnAwake = false;
            }
            s_voice.Stop();
            s_voice.clip = clip;
            s_voice.Play();
        }
    }
}
