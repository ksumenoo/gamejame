using DG.Tweening;
using Jam.Exposure;
using Jam.Interaction;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Камера-обскура как предмет в мире: E — открыть заслонку, ждать выдержку, E — забрать пластину.
    /// Корень стоит на полу, ObscuraCamera — в дочернем объекте на уровне отверстия.
    /// Анимации заслонки и корпуса — DOTween.
    /// </summary>
    public class CameraStation : MonoBehaviour, IInteractable
    {
        public enum State { Idle, Exposing, PlateReady }

        public ObscuraCamera obscura;
        [Min(0.5f)] public float exposureSeconds = 6f;

        [Header("Заслонка")]
        public Transform shutter;
        [Tooltip("Смещение заслонки при открытии, в осях родителя заслонки.")]
        public Vector3 shutterOpenOffset = new(0f, 0.12f, 0f);
        [Min(0.01f)] public float shutterOpenTime = 0.22f;
        [Min(0.01f)] public float shutterCloseTime = 0.12f;

        [Header("Корпус (необязательно)")]
        [Tooltip("Модель камеры. Вздрагивает при щелчке заслонки.")]
        public Transform body;
        public float clickShakeStrength = 1.2f;

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip shutterOpenClip;
        public AudioClip shutterCloseClip;
        public AudioClip tickLoop;

        [ShowNativeProperty] public State Current { get; private set; } = State.Idle;

        Plate _plate;
        Vector3 _shutterClosedPos;
        Sequence _shutterSeq;

        public string Prompt => Current switch
        {
            State.Idle => "Открыть заслонку",
            State.Exposing => $"Выдержка… {Mathf.RoundToInt(obscura.Progress * 100f)}%",
            State.PlateReady => "Забрать пластину",
            _ => "",
        };

        void Awake()
        {
            if (!obscura) obscura = GetComponentInChildren<ObscuraCamera>();
            if (shutter) _shutterClosedPos = shutter.localPosition;
        }

        void OnEnable() { if (obscura) obscura.ExposureFinished += OnExposureFinished; }
        void OnDisable() { if (obscura) obscura.ExposureFinished -= OnExposureFinished; }

        public bool CanInteract(Interactor who) => Current switch
        {
            State.Idle => obscura && !obscura.IsExposing,
            State.PlateReady => TryGetInventory(who, out var inv) && inv.HasRoom,
            _ => false,
        };

        public void Interact(Interactor who)
        {
            switch (Current)
            {
                case State.Idle:
                    OpenShutter();
                    break;
                case State.PlateReady:
                    if (TryGetInventory(who, out var inv) && inv.Add(_plate))
                    {
                        _plate = null;
                        Current = State.Idle;
                        if (body) body.DOPunchScale(Vector3.one * 0.03f, 0.2f, 6, 0.5f);
                    }
                    break;
            }
        }

        [Button("Открыть заслонку (тест)")]
        void OpenShutter()
        {
            if (Current != State.Idle || !obscura) return;
            Current = State.Exposing;
            AnimateShutter(open: true);
            Play(shutterOpenClip);
            if (audioSource && tickLoop) { audioSource.clip = tickLoop; audioSource.loop = true; audioSource.Play(); }
            obscura.BeginExposure(exposureSeconds, false);
        }

        void OnExposureFinished(ObscuraCamera cam, Plate plate)
        {
            if (Current != State.Exposing) return; // выдержку запустил не игрок (например, отладочный тестер)
            _plate = plate;
            Current = State.PlateReady;
            AnimateShutter(open: false);
            if (audioSource && audioSource.loop) { audioSource.Stop(); audioSource.loop = false; }
            Play(shutterCloseClip);
        }

        void AnimateShutter(bool open)
        {
            _shutterSeq?.Kill();
            _shutterSeq = DOTween.Sequence().SetLink(gameObject);

            if (shutter)
            {
                var target = open ? _shutterClosedPos + shutterOpenOffset : _shutterClosedPos;
                _shutterSeq.Append(shutter.DOLocalMove(target, open ? shutterOpenTime : shutterCloseTime)
                                          .SetEase(open ? Ease.OutCubic : Ease.InQuad));
            }
            if (body)
            {
                // щелчок: корпус коротко вздрагивает, когда заслонка доходит до упора
                _shutterSeq.Append(body.DOShakeRotation(0.15f, clickShakeStrength, 18, 90f, true));
            }
        }

        void Play(AudioClip clip)
        {
            if (audioSource && clip) audioSource.PlayOneShot(clip);
        }

        static bool TryGetInventory(Interactor who, out PlateInventory inv)
        {
            inv = who ? who.GetComponentInParent<PlateInventory>() : null;
            return inv != null;
        }

        void OnDestroy()
        {
            _plate?.Release();
            _shutterSeq?.Kill();
        }
    }
}
