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
        [Tooltip("Без озвучки: секунд на символ.")]
        public float secondsPerChar = 0.06f;
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
            if (hint) hint.text = "[Пробел] пропустить";

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
                paper.gameObject.SetActive(true);
                var pg = paper.GetComponent<CanvasGroup>();
                if (!pg) pg = paper.gameObject.AddComponent<CanvasGroup>();
                pg.alpha = 0f;
                pg.DOFade(1f, 1.5f).SetLink(gameObject);
            }

            // длительность каждой фразы: по длине голоса, пропорционально числу букв
            int chars = 0;
            foreach (var l in lines) chars += l.Length;
            float total = voiceOver ? Mathf.Max(1f, voiceOver.length) : chars * secondsPerChar + lines.Count * 0.6f;
            var durs = new float[lines.Count];
            for (int i = 0; i < lines.Count; i++) durs[i] = Mathf.Max(1.2f, total * lines[i].Length / Mathf.Max(1, chars));

            // камера ползёт по листу: чуть крупнее, медленно вниз по тексту
            Vector2 start = paper ? paper.anchoredPosition : Vector2.zero;
            if (paper)
            {
                paper.localScale = Vector3.one * 1.15f;
                paper.DOScale(1.3f, total + voiceDelay).SetEase(Ease.Linear).SetLink(gameObject);
                paper.DOAnchorPosY(start.y + 260f, total + voiceDelay).SetEase(Ease.InOutSine).SetLink(gameObject);
            }

            float t0 = Time.time + voiceDelay;
            await UniTask.WaitUntil(() => Time.time >= t0 || _skipped, cancellationToken: ct);
            if (_skipped) return;
            if (audioSource && voiceOver) audioSource.PlayOneShot(voiceOver);

            var sb = new StringBuilder();
            float lineStart = Time.time;
            int cur = 0;
            while (cur < lines.Count && !_skipped)
            {
                float k = Mathf.Clamp01((Time.time - lineStart) / 0.8f); // строка проступает за 0,8 с
                sb.Clear();
                for (int i = 0; i < lines.Count; i++)
                {
                    float a = i < cur ? 0.55f : i == cur ? k : 0f; // прочитанное чуть бледнеет
                    sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(ink))
                      .Append(((int)(a * 255)).ToString("X2")).Append('>')
                      .Append(lines[i]).Append("</color>\n\n");
                }
                body.text = sb.ToString();
                if (Time.time - lineStart >= durs[cur]) { cur++; lineStart = Time.time; }
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
            if (_skipped) return;
            await UniTask.Delay(900, cancellationToken: ct);
            if (paper)
            {
                var pg = paper.GetComponent<CanvasGroup>();
                var t = pg.DOFade(0f, 1.2f).SetLink(gameObject);
                while (t.IsActive() && !t.IsComplete() && !_skipped) await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

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
