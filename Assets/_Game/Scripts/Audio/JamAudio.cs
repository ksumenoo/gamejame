using System.Collections.Generic;
using Jam.Exposure;
using Jam.Gameplay;
using Jam.Player;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Jam.Audio
{
    /// <summary>
    /// Звук игры. Пул источников + SoundBank. Сам подписывается на события систем (шаги, заслонки, проявка,
    /// вспышка, свеча, двери, Маргарет). Нет клипа — тишина без ошибок.
    /// </summary>
    [DefaultExecutionOrder(-80)]
    public class JamAudio : MonoBehaviour
    {
        public static JamAudio Instance { get; private set; }

        public SoundBank bank;
        [Min(4)] public int poolSize = 16;
        [Range(0f, 1f)] public float masterVolume = 1f;

        [Header("Маргарет")]
        [Tooltip("Ближе этого шёпот на полной громкости.")]
        public float whisperNear = 2f;
        [Tooltip("Дальше этого шёпота не слышно.")]
        public float whisperFar = 10f;

        readonly List<AudioSource> _pool = new();
        int _next;
        AudioSource _ambient, _whisper;
        MargaretAI _margaret;
        FirstPersonController _player;

        void Awake()
        {
            Instance = this;
            for (int i = 0; i < poolSize; i++)
            {
                var go = new GameObject("SFX_" + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.rolloffMode = AudioRolloffMode.Linear;
                _pool.Add(src);
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            Hook();
            _ambient = StartLoop("ambient", null);
        }

        // ---------------- API ----------------

        /// <summary>Проиграть звук. position == null — 2D.</summary>
        public static void Play(string key, Vector3? position = null)
        {
            if (Instance) Instance.PlayInternal(key, position);
        }

        void PlayInternal(string key, Vector3? position)
        {
            var e = bank ? bank.Get(key) : null;
            if (e == null || e.clips.Count == 0) return;
            var clip = e.clips[Random.Range(0, e.clips.Count)];
            if (!clip) return;

            var src = _pool[_next];
            _next = (_next + 1) % _pool.Count;
            Setup(src, e, position);
            src.pitch = 1f + Random.Range(-e.pitchJitter, e.pitchJitter);
            src.PlayOneShot(clip, e.volume * masterVolume);
        }

        /// <summary>Зацикленный звук. parent == null — 2D. Возвращает источник (или null, если клипа нет).</summary>
        public AudioSource StartLoop(string key, Transform parent)
        {
            var e = bank ? bank.Get(key) : null;
            if (e == null || e.clips.Count == 0 || !e.clips[0]) return null;
            var go = new GameObject("Loop_" + key);
            go.transform.SetParent(parent ? parent : transform, false);
            var src = go.AddComponent<AudioSource>();
            Setup(src, e, parent ? parent.position : null);
            src.clip = e.clips[0];
            src.loop = true;
            src.volume = e.volume * masterVolume;
            src.Play();
            return src;
        }

        void Setup(AudioSource src, SoundBank.Entry e, Vector3? position)
        {
            bool spatial = e.spatial && position.HasValue;
            src.spatialBlend = spatial ? 1f : 0f;
            src.maxDistance = e.maxDistance;
            src.minDistance = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            if (spatial && src.transform.parent == transform) src.transform.position = position.Value;
        }

        // ---------------- подписки ----------------

        void Hook()
        {
            _player = FindAnyObjectByType<FirstPersonController>();
            if (_player) _player.Footstep += p => Play("footstep", p.transform.position);

            foreach (var cam in FindObjectsByType<ObscuraCamera>())
            {
                cam.ExposureStarted += c => Play("shutter_open", c.transform.position);
                cam.ExposureFinished += (c, _) => Play("shutter_close", c.transform.position);
            }

            foreach (var dr in FindObjectsByType<Darkroom>())
                dr.DevelopStarted += d => Play("develop", d.transform.position);

            foreach (var v in FindObjectsByType<PlateViewer>())
                v.Stung += () => Play("sting");

            foreach (var f in FindObjectsByType<MagnesiumFlash>())
            {
                f.Fired += x => Play("flash");
                f.Empty += x => Play("flash_empty");
            }

            foreach (var c in FindObjectsByType<HandCandle>())
            {
                c.Extinguished += x => Play("candle_out");
                c.Lit += x => Play("candle_light");
            }

            foreach (var h in FindObjectsByType<HandObscura>())
            {
                h.Raised += x => Play("obscura_raise");
                h.SceneAligned += (x, s) => Play("scene_align");
            }

            Door.AnyDoor += (d, kind) => Play(kind, d.transform.position);

            _margaret = FindAnyObjectByType<MargaretAI>();
            if (_margaret)
            {
                _margaret.CaughtPlayer += m => Play("caught");
                _margaret.StateChanged += (m, s) => { if (s == MargaretAI.State.Chase) Play("margaret_chase"); };
                _whisper = StartLoop("margaret_whisper", _margaret.transform);
                if (_whisper) _whisper.spatialBlend = 0.6f; // слышно «в голове», но с направлением
            }
        }

        void OnDisable() => Door.ClearAnyDoor();

        void Update()
        {
            if (_whisper && _margaret)
            {
                float d = _margaret.DistanceToPlayer;
                float k = 1f - Mathf.InverseLerp(whisperNear, whisperFar, d);
                var e = bank ? bank.Get("margaret_whisper") : null;
                _whisper.volume = k * k * (e != null ? e.volume : 1f) * masterVolume;
            }
        }
    }
}
