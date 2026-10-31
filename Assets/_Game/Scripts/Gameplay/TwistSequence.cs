using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Audio;
using Jam.Interaction;
using Jam.Player;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Перелом: проявлена последняя сцена → игрок закрывает пластину → белая вспышка → финальная пластина
    /// (картинка художников P_Final, если её нет — последняя проявленная) → мир переворачивается.
    ///
    /// Вариант Б (сейчас): переворот только картинки — главная камера крутится на 180° по Z,
    /// мышь и стрейф инвертируются, чтобы управление оставалось естественным.
    /// Вариант А (TODO, флаг ceilingMode): дом разворачивается, игрок ходит по потолку.
    ///
    /// Дальше: continueToAct2 = false — пауза и титры (игра заканчивается на твисте),
    /// true — событие Act2Started для второго акта.
    /// </summary>
    public class TwistSequence : MonoBehaviour
    {
        [Header("Ссылки")]
        public FirstPersonController player;
        public Interactor interactor;
        public HandObscura hand;
        public PlateViewer viewer;
        public MargaretAI margaret;
        public EndCredits credits;
        [Tooltip("Белый экран вспышки (CanvasGroup с белым Image).")]
        public CanvasGroup whiteFlash;
        [Tooltip("Финальная пластина от художников (Assets/Art/2D/UI/P_Final.png).")]
        public Texture finalPlate;

        [Header("Сценарий")]
        [Tooltip("Акт 2 на потолке: во время белой вспышки Act2Director переносит игрока на потолок (тяжесть вверх).")]
        public bool ceilingMode;
        public bool continueToAct2;
        [Tooltip("Сколько секунд игрок бродит по перевёрнутому дому до титров.")]
        public float upsideDownSeconds = 20f;
        public float flipTime = 2.5f;

        [ShowNativeProperty] public bool Played { get; private set; }

        public event Action Act2Started;

        void Start()
        {
            if (StoryDirector.Instance) StoryDirector.Instance.TwistReady += OnTwistReady;
            if (whiteFlash) whiteFlash.alpha = 0f;
        }

        void OnDestroy()
        {
            if (StoryDirector.Instance) StoryDirector.Instance.TwistReady -= OnTwistReady;
        }

        void OnTwistReady()
        {
            if (!Played) RunAsync(destroyCancellationToken).Forget();
        }

        [Button("Запустить перелом (тест)")]
        void DebugRun()
        {
            if (Application.isPlaying && !Played) RunAsync(destroyCancellationToken).Forget();
        }

        async UniTaskVoid RunAsync(CancellationToken ct)
        {
            Played = true;
            try
            {
                // 1. Дождаться, пока игрок закроет проявленную пластину
                await UniTask.Delay(300, cancellationToken: ct);
                if (viewer) await UniTask.WaitUntil(() => !viewer.IsOpen, cancellationToken: ct);

                // 2. Маргарет больше не охотится. В акте 2 она остаётся — теперь мы по одну сторону стекла
                if (margaret)
                {
                    if (ceilingMode) margaret.BecomeCompanion();
                    else margaret.gameObject.SetActive(false);
                }
                Lock(true);
                if (hand) hand.SetHeld(false);

                // 3. Белая вспышка магния
                JamAudio.Play("flash");
                if (whiteFlash)
                {
                    whiteFlash.alpha = 1f;
                    await UniTask.Delay(900, cancellationToken: ct);
                }
                // пока экран белый — игрок оказывается на потолке (акт 2)
                if (ceilingMode && Act2Director.Instance) Act2Director.Instance.EnterCeiling();

                // 4. Финальная пластина
                Lock(false); // PlateViewer сам блокирует игрока на время показа
                if (viewer && finalPlate)
                {
                    if (whiteFlash) whiteFlash.DOFade(0f, 0.4f).SetLink(gameObject);
                    await viewer.ShowTextureAsync(finalPlate, sting: true, flip: true);
                }
                else if (whiteFlash)
                {
                    await Fade(whiteFlash, 0f, 0.6f, ct);
                }

                // 5. Переворот
                Lock(true);
                await FlipViewAsync(ct);
                Lock(false);

                // 6. Дальше
                if (continueToAct2)
                {
                    Act2Started?.Invoke();
                    return;
                }
                await UniTask.Delay(TimeSpan.FromSeconds(upsideDownSeconds), cancellationToken: ct);
                Lock(true);
                if (credits) await credits.ShowAsync();
            }
            catch (OperationCanceledException) { }
        }

        async UniTask FlipViewAsync(CancellationToken ct)
        {
            if (!player) return;
            var cam = player.GetComponentInChildren<Camera>();
            if (!cam) return;

            JamAudio.Play("door_slam");
            var t = cam.transform.DOLocalRotate(new Vector3(0f, 0f, 180f), flipTime, RotateMode.LocalAxisAdd)
                .SetEase(Ease.InOutCubic).SetLink(gameObject);
            if (player.cameraPivot) player.cameraPivot.DOShakePosition(flipTime, 0.05f, 12).SetLink(gameObject);
            while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, ct);

            player.LookInvert = new Vector2(-1f, -1f);
            player.StrafeInvert = -1f;
        }

        void Lock(bool locked)
        {
            if (player) player.InputLocked = locked;
            if (interactor) interactor.Locked = locked;
        }

        static async UniTask Fade(CanvasGroup g, float to, float time, CancellationToken ct)
        {
            var t = g.DOFade(to, time);
            while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, ct);
        }
    }
}
