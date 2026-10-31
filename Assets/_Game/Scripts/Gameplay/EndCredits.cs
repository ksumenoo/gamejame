using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>Титры: чёрный экран, текст проявляется. Заглушка — вызывается в конце игры.</summary>
    public class EndCredits : MonoBehaviour
    {
        public CanvasGroup group;
        public Text text;
        [Tooltip("Музыка титров (первый трек из Assets/Audio/Music — ставит сборщик уровня).")]
        public AudioClip music;
        [Range(0f, 1f)] public float musicVolume = 0.7f;
        [TextArea(4, 12)]
        public string credits = "ВЫДЕРЖКА\n\n3D, Unity — ...\n2D — ...\n2D — ...\n\nГеймджем 2026";

        void Awake()
        {
            if (group) group.alpha = 0f;
        }

        public async UniTask ShowAsync()
        {
            // один текст на меню «Авторы» и финальные титры: Resources/Text/credits.txt
            var file = Resources.Load<TextAsset>("Text/credits");
            if (file) credits = file.text.Trim();
            if (text) text.text = credits;
            if (music)
            {
                var src = gameObject.GetComponent<AudioSource>();
                if (!src) src = gameObject.AddComponent<AudioSource>();
                src.clip = music;
                src.loop = true;
                src.spatialBlend = 0f;
                src.ignoreListenerPause = true;
                src.volume = 0f;
                src.Play();
                src.DOFade(musicVolume, 3f).SetLink(gameObject);
            }
            if (!group) return;
            group.blocksRaycasts = true;
            var t = group.DOFade(1f, 2.5f).SetLink(gameObject);
            while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);
        }
    }
}
