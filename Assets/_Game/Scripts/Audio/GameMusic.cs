using DG.Tweening;
using Jam.Gameplay;
using UnityEngine;

namespace Jam.Audio
{
    /// <summary>
    /// Музыка в игре: трек первого акта с начала, на втором акте — плавный переход на трек второго акта,
    /// перед титрами — затухание (у титров своя музыка). Громкость — настройка «Музыка».
    /// Ставит сборщик уровня (Build Level From House), треки — из Assets/Audio/Music (номер в конце имени файла).
    /// </summary>
    public class GameMusic : MonoBehaviour
    {
        public AudioClip act1;
        public AudioClip act2;
        [Range(0f, 1f)] public float volume = 0.35f;
        public float crossfade = 4f;

        static GameMusic s_inst;
        AudioSource _a, _b; // текущий и следующий — для перехода внахлёст
        Act2Director _director;

        /// <summary>Громкость с учётом настройки «Музыка» — GameSettings.Apply зовёт при каждом изменении.</summary>
        public static void ApplyVolume()
        {
            if (s_inst && s_inst._a && !DOTween.IsTweening(s_inst._a)) s_inst._a.volume = s_inst.Target;
        }

        float Target => GameSettings.MusicVolume(volume);

        /// <summary>Тишина перед титрами.</summary>
        public static void FadeOut(float seconds = 2.5f)
        {
            if (!s_inst) return;
            foreach (var s in new[] { s_inst._a, s_inst._b })
                if (s) { s.DOKill(); s.DOFade(0f, seconds).SetUpdate(true).SetLink(s.gameObject).OnComplete(s.Stop); }
        }

        void Awake()
        {
            s_inst = this;
            _a = MakeSource();
            _b = MakeSource();
        }

        AudioSource MakeSource()
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.loop = true;
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.volume = 0f;
            return s;
        }

        void Start()
        {
            _director = FindAnyObjectByType<Act2Director>();
            if (_director)
            {
                _director.Started += OnAct2;
                if (_director.Active) { OnAct2(); return; } // уже во втором акте (продолжение сохранения)
            }
            PlayClip(act1);
        }

        void OnDestroy()
        {
            if (_director) _director.Started -= OnAct2;
            if (s_inst == this) s_inst = null;
        }

        void OnAct2() => PlayClip(act2);

        void PlayClip(AudioClip clip)
        {
            if (!clip || (_a.isPlaying && _a.clip == clip)) return;
            // старый трек уходит, новый приходит внахлёст
            var old = _a;
            _a = _b;
            _b = old;
            _b.DOKill();
            if (_b.isPlaying) _b.DOFade(0f, crossfade).SetLink(gameObject).OnComplete(_b.Stop);
            _a.DOKill();
            _a.clip = clip;
            _a.volume = 0f;
            _a.Play();
            _a.DOFade(Target, crossfade).SetLink(gameObject);
        }
    }
}
