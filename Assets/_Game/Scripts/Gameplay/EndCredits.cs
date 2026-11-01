using System.Text;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Финальные титры как в кино: чёрный экран, музыка, текст медленно едет снизу вверх,
    /// в конце последняя строка («Спасибо за игру») останавливается посередине. Пробел/E/ЛКМ — промотать быстрее.
    /// Текст — Resources/Text/credits.txt. Формат:
    ///   первая строка — название (крупно);
    ///   «# РАЗДЕЛ» — заголовок раздела;
    ///   «Роль — Имя» — роль мелко, имя под ней крупно;
    ///   любая другая строка — как есть; пустые строки — отступы.
    /// </summary>
    public class EndCredits : MonoBehaviour
    {
        public CanvasGroup group;
        public Text text;
        [Tooltip("Музыка титров (первый трек из Assets/Audio/Music — ставит сборщик уровня).")]
        public AudioClip music;
        [Range(0f, 1f)] public float musicVolume = 0.7f;
        [Tooltip("Скорость прокрутки, пикселей в секунду (в масштабе 1080p).")]
        public float scrollSpeed = 70f;
        [TextArea(4, 12)]
        public string credits = "ВЫДЕРЖКА\n\nГеймджем 2026";

        const string Dim = "#9c907c";
        const string Red = "#a8382c";

        void Awake()
        {
            if (group) group.alpha = 0f;
        }

        /// <summary>Разметка титров в rich text для Text.</summary>
        public static string Format(string raw)
        {
            var sb = new StringBuilder();
            var lines = raw.Replace("\r", "").Trim().Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (i == 0) { sb.Append("<size=96>").Append(l).Append("</size>\n"); continue; }
                if (l.Length == 0) { sb.Append('\n'); continue; }
                if (l.StartsWith("#"))
                {
                    sb.Append("\n<size=26><color=").Append(Red).Append('>').Append(l.TrimStart('#', ' ')).Append("</color></size>\n");
                    continue;
                }
                int dash = l.IndexOf(" — ", System.StringComparison.Ordinal);
                if (dash > 0)
                {
                    sb.Append("<size=24><color=").Append(Dim).Append('>').Append(l.Substring(0, dash).Trim()).Append("</color></size>\n");
                    sb.Append("<size=40>").Append(l.Substring(dash + 3).Trim()).Append("</size>\n\n");
                    continue;
                }
                sb.Append(l).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        public async UniTask ShowAsync()
        {
            var file = Resources.Load<TextAsset>("Text/credits");
            if (file) credits = file.text;
            Jam.Audio.GameMusic.FadeOut(); // у титров своя музыка
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
                src.DOFade(GameSettings.MusicVolume(musicVolume), 3f).SetLink(gameObject);
            }
            if (!group) return;

            // текст — колонка во всю высоту, верхний край пока под нижним краем экрана
            RectTransform rt = null;
            float parentH = 1080f, h = 0f;
            if (text)
            {
                text.supportRichText = true;
                text.alignment = TextAnchor.UpperCenter;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.lineSpacing = 1.1f;
                text.text = Format(credits);
                rt = text.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 1f);
                var parent = rt.parent as RectTransform;
                if (parent) parentH = parent.rect.height;
                Canvas.ForceUpdateCanvases();
                h = text.preferredHeight;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
                rt.anchoredPosition = new Vector2(0f, -parentH * 0.5f);
            }

            group.blocksRaycasts = true;
            var t = group.DOFade(1f, 2.5f).SetLink(gameObject);
            while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);
            if (!rt) return;

            // едем, пока низ текста (последняя строка) не встанет в середину экрана
            float endY = h - 30f;
            while (rt && rt.anchoredPosition.y < endY)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                var mouse = UnityEngine.InputSystem.Mouse.current;
                bool fast = (kb != null && (kb.spaceKey.isPressed || kb.eKey.isPressed)) || (mouse != null && mouse.leftButton.isPressed);
                float y = rt.anchoredPosition.y + scrollSpeed * (fast ? 6f : 1f) * Time.unscaledDeltaTime;
                rt.anchoredPosition = new Vector2(0f, Mathf.Min(y, endY));
                await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);
            }
        }
    }
}
