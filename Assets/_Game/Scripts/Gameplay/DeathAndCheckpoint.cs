using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Поимка игрока: резкое затемнение, звук, пластины в руках теряются, игрок возвращается на чекпойнт,
    /// Маргарет уходит в дальний конец дома. Чекпойнт ставится после каждой проявленной сцены.
    /// </summary>
    public class DeathAndCheckpoint : MonoBehaviour
    {
        public MargaretAI margaret;
        public FirstPersonController player;
        public Interactor interactor;
        public HandObscura hand;
        public PlateInventory inventory;
        public PlateViewer viewer;
        [Tooltip("Чёрный экран поверх всего (CanvasGroup с Image).")]
        public CanvasGroup blackout;
        [Tooltip("После возрождения Маргарет столько секунд никого не замечает.")]
        public float graceAfterRespawn = 20f; // после смерти — время оглядеться и подумать

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip caughtClip;

        [Header("Тайминг")]
        public float holdBlack = 1.6f;
        public float fadeIn = 1.2f;

        Vector3 _cpPos;
        Quaternion _cpRot;
        bool _running;

        void Start()
        {
            if (player) SetCheckpoint(player.transform.position, player.transform.rotation);
            if (blackout) blackout.alpha = 0f;
            if (margaret) margaret.CaughtPlayer += OnCaught;
            if (StoryDirector.Instance) StoryDirector.Instance.SceneDeveloped += OnSceneDeveloped;
        }

        void OnDestroy()
        {
            if (margaret) margaret.CaughtPlayer -= OnCaught;
            if (StoryDirector.Instance) StoryDirector.Instance.SceneDeveloped -= OnSceneDeveloped;
        }

        public void SetCheckpoint(Vector3 pos, Quaternion rot)
        {
            _cpPos = pos;
            _cpRot = rot;
        }

        void OnSceneDeveloped(PastScene scene)
        {
            if (player) SetCheckpoint(player.transform.position, player.transform.rotation);
        }

        void OnCaught(MargaretAI m)
        {
            if (!_running) RunAsync(destroyCancellationToken).Forget();
        }

        async UniTaskVoid RunAsync(CancellationToken ct)
        {
            _running = true;
            try
            {
                if (player) player.InputLocked = true;
                if (interactor) interactor.Locked = true;
                if (hand) hand.SetHeld(false);
                if (viewer) viewer.ForceClose();
                if (audioSource && caughtClip) audioSource.PlayOneShot(caughtClip);

                if (blackout)
                {
                    blackout.DOKill();
                    blackout.alpha = 1f; // резко, без плавности — это удар
                }
                if (player && player.cameraPivot)
                    player.cameraPivot.DOShakeRotation(0.4f, 12f, 30, 90f, true).SetLink(player.gameObject);

                await UniTask.Delay((int)(holdBlack * 1000), cancellationToken: ct);

                if (inventory) inventory.DropAll();
                Teleport(_cpPos, _cpRot);
                if (margaret && player) margaret.Calm(graceAfterRespawn, player.transform.position);
                var portable = FindAnyObjectByType<PortableCamera>();
                if (portable) portable.ReturnToPlayer();

                await UniTask.Delay(400, cancellationToken: ct);
                if (blackout)
                {
                    var t = blackout.DOFade(0f, fadeIn).SetLink(gameObject);
                    while (t.IsActive() && !t.IsComplete())
                        await UniTask.Yield(PlayerLoopTiming.Update, ct);
                }
            }
            finally
            {
                if (player) player.InputLocked = false;
                if (interactor) interactor.Locked = false;
                _running = false;
            }
        }

        void Teleport(Vector3 pos, Quaternion rot)
        {
            if (!player) return;
            var cc = player.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
            player.transform.SetPositionAndRotation(pos, rot);
            if (cc) cc.enabled = true;
        }
    }
}
