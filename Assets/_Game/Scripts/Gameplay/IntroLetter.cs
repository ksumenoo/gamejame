using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Заставка: герой едет к дому и перечитывает письмо.
    /// Темнота, звук экипажа, камера медленно ползёт по листу, строки проступают чернилами в темпе голоса
    /// (это и есть субтитры). В конце — фасад дома проявляется, как пластина, и мы в прихожей.
    /// Без озвучки идёт по таймеру; без картинки дома — сразу в игру. Пробел / Enter / Esc / E — пропустить.
    /// </summary>
    public class IntroLetter : MonoBehaviour
    {
        public CanvasGroup group;
        public Text body;
        public Text hint;
        [Tooltip("Текст заставки: одна строка файла = одна фраза голоса.")]
        public TextAsset textRu;
        public TextAsset textEn; // не используется, оставлен для совместимости
        public FirstPersonController player;
        public Interactor interactor;
        public bool showOnStart = true;

        [Header("Заставка")]
        public RectTransform paper;
        public AudioSource audioSource;
        [Tooltip("Голос: письмо целиком (Assets/Audio/intro_vo).")]
        public AudioClip voiceOver;
        [Tooltip("Фон: стук колёс, лошадь (Assets/Audio/carriage), по кругу.")]
        public AudioClip carriageLoop;
        [Tooltip("Финал: остановка, дверца, шаги, дверь дома (Assets/Audio/carriage_stop).")]
        public AudioClip arrival;
        [Tooltip("Фасад дома ночью (Assets/Art/2D/UI/UI_House.png). Проявляется, как пластина.")]
        public RawImage houseImage;
        public Material plateMaterial;
        [Min(0f)] public float voiceDelay = 1.5f;
        [Tooltip("Без озвучки: секунд на букву (на точках и запятых — паузы).")]
        public float secondsPerChar = 0.045f;

        [Header("Лист")]
        public Vector2 paperSize = new(620f, 760f);
        public float paperMargin = 56f;
        [Tooltip("Наклон листа, градусы.")]
        public float paperTilt = -1.5f;
        public int fontSize = 27;
        [Tooltip("Рукописный шрифт письма. Пусто — берётся Resources/Fonts/MarckScript-Regular.")]
        public Font letterFont;
        [Tooltip("Сколько секунд письмо висит дописанным, если игрок не нажал Пробел.")]
        public float readSeconds = 6f;
        public string continueHint = "[Пробел]";
        [Tooltip("Звук пера — короткий, по нему стучит печать (необязательно).")]
        public AudioClip typeClip;
        public Color ink = new(0.16f, 0.12f, 0.1f, 1f);

        public event Action Closed;

        InputAction _skip;
        bool _skipped;
        static readonly int DevelopId = Shader.PropertyToID("_Develop");
        static readonly int FlipId = Shader.PropertyToID("_Flip");

        void Awake()
        {
            _skip = new InputAction("SkipIntro", InputActionType.Button);
            _skip.AddBinding("<Keyboard>/space");
            _skip.AddBinding("<Keyboard>/enter");
            _skip.AddBinding("<Keyboard>/escape");
            _skip.AddBinding("<Keyboard>/e");
            if (group) group.alpha = 0f;
            if (houseImage) houseImage.gameObject.SetActive(false);
        }

        void OnEnable() => _skip.Enable();
        void OnDisable() => _skip.Disable();
        void OnDestroy() => _skip.Dispose();

        void Start()
        {
            if (showOnStart) ShowAsync().Forget();
        }

        void Update()
        {
            if (!_skipped && group && group.alpha > 0.5f && _skip.WasPressedThisFrame()) _skipped = true;
        }

        public async UniTask ShowAsync()
        {
            var ct = destroyCancellationToken;
            if (player) player.InputLocked = true;
            if (interactor) interactor.Locked = true;
            if (group) group.alpha = 1f; // начинаем в полной темноте
            if (hint) hint.text = "";

            var lines = new List<string>();
            var src = textRu ? textRu : textEn;
            if (src)
                foreach (var l in src.text.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(l)) lines.Add(l.Trim());

            try
            {
                if (audioSource && carriageLoop)
                {
                    audioSource.clip = carriageLoop;
                    audioSource.loop = true;
                    audioSource.volume = 0f;
                    audioSource.Play();
                    audioSource.DOFade(0.7f, 2f).SetLink(gameObject);
                }

                await PlayLetterAsync(lines, ct);
                if (!_skipped) await PlayHouseAsync(ct);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (audioSource) { audioSource.DOKill(); audioSource.Stop(); }
                if (houseImage) houseImage.gameObject.SetActive(false);
                if (paper) paper.gameObject.SetActive(false);
                if (group)
                {
                    group.DOKill();
                    var t = group.DOFade(0f, 1.2f).SetLink(gameObject);
                    try { while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, ct); }
                    catch (OperationCanceledException) { }
                }
                if (player) player.InputLocked = false;
                if (interactor) interactor.Locked = false;
            }
            Closed?.Invoke();
        }

        async UniTask PlayLetterAsync(List<string> lines, CancellationToken ct)
        {
            if (!body || lines.Count == 0) return;
            if (paper)
            {
                // лист — небольшой, по центру, чуть наискось: письмо в руках, а не стена текста
                paper.sizeDelta = paperSize;
                paper.anchoredPosition = Vector2.zero;
                paper.localScale = Vector3.one;
                paper.localRotation = Quaternion.Euler(0f, 0f, paperTilt);
                body.rectTransform.sizeDelta = paperSize - new Vector2(paperMargin * 2f, paperMargin * 2f);
                body.rectTransform.anchoredPosition = Vector2.zero;
                body.fontSize = fontSize;
                if (!letterFont) letterFont = Resources.Load<Font>("Fonts/MarckScript-Regular");
                if (letterFont) body.font = letterFont; // почерк, а не книжный набор
                body.lineSpacing = 1.05f;
                paper.gameObject.SetActive(true);
                var pg = paper.GetComponent<CanvasGroup>();
                if (!pg) pg = paper.gameObject.AddComponent<CanvasGroup>();
                pg.alpha = 0f;
                pg.DOFade(1f, 1.2f).SetLink(gameObject);
                // едва заметно «дышит» — как лист в руке в покачивающемся экипаже
                paper.DOLocalRotate(new Vector3(0f, 0f, paperTilt + 0.6f), 3.5f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetLink(gameObject);
            }

            string full = string.Join("\n\n", lines);
            // голос есть — печатаем в его темпе; нет — свой темп с паузами на знаках препинания
            float total = 0f;
            foreach (char ch in full) total += CharDelay(ch);
            float scale = voiceOver ? Mathf.Max(1f, voiceOver.length) / Mathf.Max(0.1f, total) : 1f;

            body.text = "";
            float t0 = Time.time + voiceDelay;
            await UniTask.WaitUntil(() => Time.time >= t0 || _skipped, cancellationToken: ct);
            if (audioSource && voiceOver && !_skipped) audioSource.PlayOneShot(voiceOver);
            if (hint) hint.text = "";

            // буква за буквой; Пробел — дописать сразу
            int shown = 0;
            float next = Time.time;
            while (shown < full.Length && !_skipped)
            {
                while (shown < full.Length && Time.time >= next)
                {
                    next += CharDelay(full[shown]) * scale;
                    shown++;
                    if (audioSource && typeClip && !char.IsWhiteSpace(full[shown - 1]) && shown % 2 == 0)
                        audioSource.PlayOneShot(typeClip, 0.35f);
                }
                body.text = Visible(full, shown);
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
            body.text = full;

            // дописано: ждём Пробел (или несколько секунд), второе нажатие — дальше
            _skipped = false;
            if (hint) hint.text = continueHint;
            float until = Time.time + readSeconds;
            await UniTask.WaitUntil(() => Time.time >= until || _skipped, cancellationToken: ct);
            _skipped = false;
            if (hint) hint.text = "";
            if (paper)
            {
                var pg = paper.GetComponent<CanvasGroup>();
                var t = pg.DOFade(0f, 1.2f).SetLink(gameObject);
                while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>Напечатанная часть + ещё не напечатанная прозрачной: строки не прыгают, пока текст растёт.</summary>
        string Visible(string full, int shown)
        {
            if (shown >= full.Length) return full;
            return full.Substring(0, shown) + "<color=#00000000>" + full.Substring(shown) + "</color>";
        }

        float CharDelay(char c) => c switch
        {
            '.' or '!' or '?' or '…' => secondsPerChar * 7f,
            ',' or ';' or ':' => secondsPerChar * 3f,
            '—' => secondsPerChar * 4f,
            '\n' => secondsPerChar * 5f,
            _ => secondsPerChar,
        };

        async UniTask PlayHouseAsync(CancellationToken ct)
        {
            if (audioSource && arrival) audioSource.PlayOneShot(arrival);
            if (!houseImage || !houseImage.texture)
            {
                await UniTask.Delay(1500, cancellationToken: ct);
                return;
            }
            houseImage.gameObject.SetActive(true);
            Material mat = null;
            if (plateMaterial)
            {
                mat = new Material(plateMaterial);
                mat.SetFloat(FlipId, 0f);
                mat.SetFloat(DevelopId, 0f);
                houseImage.material = mat;
            }
            houseImage.rectTransform.localScale = Vector3.one;
            houseImage.rectTransform.DOScale(1.08f, 6f).SetEase(Ease.Linear).SetLink(gameObject);
            float d = 0f;
            var tw = DOTween.To(() => d, v => { d = v; if (mat) mat.SetFloat(DevelopId, v); }, 1f, 3f).SetEase(Ease.InQuad).SetLink(gameObject);
            while (tw.IsActive() && !tw.IsComplete() && !_skipped) await UniTask.Yield(PlayerLoopTiming.Update, ct);
            float until = Time.time + 2.5f;
            await UniTask.WaitUntil(() => Time.time >= until || _skipped, cancellationToken: ct);
            if (audioSource) audioSource.DOFade(0f, 1f).SetLink(gameObject);
            if (mat) Destroy(mat, 3f);
        }
    }
}
