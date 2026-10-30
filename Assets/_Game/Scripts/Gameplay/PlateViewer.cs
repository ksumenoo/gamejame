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
            if (plate == null || !plate.Image || IsOpen) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            var ct = _cts.Token;

            IsOpen = true;
            SetPlayerLocked(true);
            try
            {
                image.texture = plate.Image;
                image.rectTransform.localScale = Vector3.one;
                if (_mat) _mat.SetFloat(DevelopId, 0f);

                await WaitTween(group.DOFade(1f, 0.25f).SetLink(gameObject), ct);
                if (audioSource && developClip) audioSource.PlayOneShot(developClip);

                float t = 0f;
                await WaitTween(DOTween.To(() => t, v => { t = v; if (_mat) _mat.SetFloat(DevelopId, v); }, 1f, developSeconds)
                    .SetEase(Ease.InQuad).SetLink(gameObject), ct);

                if (sting)
                {
                    if (audioSource && stingClip) audioSource.PlayOneShot(stingClip);
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
            Closed?.Invoke(plate);
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
