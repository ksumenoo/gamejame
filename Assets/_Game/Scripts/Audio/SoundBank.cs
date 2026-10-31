using System;
using System.Collections.Generic;
using UnityEngine;

namespace Jam.Audio
{
    /// <summary>
    /// Библиотека звуков: ключ → клипы (случайный из списка), громкость, разброс высоты, 3D или 2D.
    /// Клипы подхватываются сборщиком уровня по имени файла из Assets/Audio: footstep_01.wav → ключ footstep.
    /// </summary>
    [CreateAssetMenu(menuName = "Jam/Sound Bank", fileName = "SoundBank")]
    public class SoundBank : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string key;
            public List<AudioClip> clips = new();
            [Range(0f, 1f)] public float volume = 1f;
            [Range(0f, 0.5f)] public float pitchJitter = 0.08f;
            [Tooltip("3D — звук из точки в мире, 2D — в голове игрока.")]
            public bool spatial = true;
            [Min(1f)] public float maxDistance = 15f;
        }

        public List<Entry> entries = new();

        /// <summary>Ключи, которые использует игра. Сборщик уровня создаёт для них пустые записи.</summary>
        public static readonly string[] Keys =
        {
            "footstep", "shutter_open", "shutter_close", "exposure_tick", "develop", "sting",
            "flash", "flash_empty", "candle_out", "candle_light", "door_open", "door_close", "door_slam",
            "margaret_whisper", "margaret_chase", "caught", "ambient", "obscura_raise", "scene_align",
            "prop_heavy", "prop_fall", "dish_break", "prop_creak", "death_gasp",
        };

        Dictionary<string, Entry> _map;

        public Entry Get(string key)
        {
            if (_map == null || _map.Count != entries.Count)
            {
                _map = new Dictionary<string, Entry>();
                foreach (var e in entries)
                    if (e != null && !string.IsNullOrEmpty(e.key)) _map[e.key] = e;
            }
            return _map.TryGetValue(key, out var x) ? x : null;
        }
    }
}
