using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Exposure;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Пластина на весь экран: проступает изображение, на резком — стингер, закрыть на E / Esc / ЛКМ.
    /// Пользуются проявочная и временный HUD. Пока открыт — игрок не двигается.
    /// </summary>
    public class PlateViewer : MonoBehaviour
    {
        public CanvasGroup group;
        public RawImage image;
        [Tooltip("Подпись под снимком (альбом). Пусто — не показывается.")]
        public Text caption;
        [Tooltip("Материал на шейдере Jam/PlateDisplay")]
        public Material plateMaterial;
        [Min(0.1f)] public float developSeconds = 3f;

        [Header("Игрок (блокируется, пока смотрим)")]
        public FirstPersonController player;
        public Interactor interactor;

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip developClip;
        public AudioClip stingClip;

        public bool IsOpen { get; private set; }
        public event Action<Plate> Closed;
        /// <summary>На пластине что-то резкое — момент «ой».</summary>
        public event Action Stung;

        Material _mat;
        InputAction _close;
        CancellationTokenSource _cts;

        static readonly int DevelopId = Shader.PropertyToID("_Develop");

        void Awake()
        {
            if (plateMaterial) _mat = new Material(plateMaterial);
            if (image) image.material = _mat;
            if (group)
            {
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
            }
            _close = new InputAction("ClosePlate", InputActionType.Button);
            _close.AddBinding("<Keyboard>/e");
            _close.AddBinding("<Keyboard>/escape");
            _close.AddBinding("<Mouse>/leftButton");
        }

        void OnEnable() => _close.Enable();
        void OnDisable() => _close.Disable();

        void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _close.Dispose();
            if (_mat) Destroy(_mat);
        }

        /// <summary>Показать пластину и дождаться, пока игрок её закроет.</summary>
        public async UniTask ShowAsync(Plate plate, bool sting)
        {
            if (plate == null || !plate.Image) return;
            await ShowTextureAsync(plate.Image, sting, true, plate.Blank ? 1.9f : -1f);
            Closed?.Invoke(plate);
        }

        static readonly int FlipId = Shader.PropertyToID("_Flip");
        static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        float _baseBrightness = -1f;
        static readonly int GrainId = Shader.PropertyToID("_Grain");
        static readonly int RedKeepId = Shader.PropertyToID("_RedKeep");
        static readonly int ContrastId = Shader.PropertyToID("_Contrast");
        static readonly int OverlayId = Shader.PropertyToID("_OverlayStrength");
        float[] _baseLook;

        /// <summary>Показать любую картинку как пластину (финальная пластина от художников). flip — перевернуть как обскура.</summary>
        /// <param name="clean">Снимок-подсказка: меньше зерна и царапин, без красного акцента — чтобы читалась комната.</param>
        public async UniTask ShowTextureAsync(Texture texture, bool sting, bool flip, float brightness = -1f, bool clean = false, string caption = null)
        {
            if (!texture || IsOpen) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            var ct = _cts.Token;

            IsOpen = true;
            SetPlayerLocked(true);
            try
            {
                image.texture = texture;
                if (this.caption) this.caption.text = caption ?? "";
                image.rectTransform.localScale = Vector3.one;
                // снимок из альбома — обычный отпечаток, без «пластинного» шейдера: тот съедает тёмные места, и комната не читается
                image.material = clean ? null : _mat;
                image.color = clean ? new Color(1f, 0.93f, 0.8f) : Color.white;
                if (_mat)
                {
                    _mat.SetFloat(DevelopId, 0f);
                    _mat.SetFloat(FlipId, flip ? 1f : 0f);
                    // пустая (засвеченная) пластина — белёсая
                    if (_baseBrightness < 0f) _baseBrightness = _mat.GetFloat(BrightnessId);
                    _mat.SetFloat(BrightnessId, brightness >= 0f ? brightness : _baseBrightness);
                    _baseLook ??= new[] { _mat.GetFloat(GrainId), _mat.GetFloat(RedKeepId), _mat.GetFloat(ContrastId), _mat.GetFloat(OverlayId) };
                    _mat.SetFloat(GrainId, clean ? 0.02f : _baseLook[0]);
                    _mat.SetFloat(RedKeepId, clean ? 0f : _baseLook[1]);
                    _mat.SetFloat(ContrastId, clean ? 1.15f : _baseLook[2]);
                    _mat.SetFloat(OverlayId, clean ? 0.25f : _baseLook[3]);
                }

                await WaitTween(group.DOFade(1f, 0.25f).SetLink(gameObject), ct);
                if (audioSource && developClip) audioSource.PlayOneShot(developClip);

                float t = 0f;
                await WaitTween(DOTween.To(() => t, v => { t = v; if (_mat) _mat.SetFloat(DevelopId, v); }, 1f, developSeconds)
                    .SetEase(Ease.InQuad).SetLink(gameObject), ct);

                if (sting)
                {
                    if (audioSource && stingClip) audioSource.PlayOneShot(stingClip);
                    Stung?.Invoke();
                    image.rectTransform.DOPunchScale(Vector3.one * 0.06f, 0.35f, 8, 0.6f).SetLink(gameObject);
                }

                await UniTask.WaitUntil(() => _close.WasPressedThisFrame(), cancellationToken: ct);
                await WaitTween(group.DOFade(0f, 0.2f).SetLink(gameObject), ct);
            }
            catch (OperationCanceledException)
            {
                // объект уничтожен или показ прерван — просто выходим
            }
            finally
            {
                IsOpen = false;
                SetPlayerLocked(false);
            }
        }

        /// <summary>Прервать показ (смерть, катсцена).</summary>
        public void ForceClose()
        {
            _cts?.Cancel();
            if (group) group.alpha = 0f;
        }

        void SetPlayerLocked(bool locked)
        {
            if (player) player.InputLocked = locked;
            if (interactor) interactor.Locked = locked;
        }

        static async UniTask WaitTween(Tween tween, CancellationToken ct)
        {
            while (tween != null && tween.IsActive() && !tween.IsComplete())
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
        }
    }
}
