using DG.Tweening;
using Jam.Exposure;
using Jam.Interaction;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Камера-обскура как предмет в мире: E — открыть заслонку, ждать выдержку, E — забрать пластину.
    /// Вешается на корень камеры (рядом с ObscuraCamera или выше). Нужен коллайдер на модели ящика.
    /// </summary>
    public class CameraStation : MonoBehaviour, IInteractable
    {
        public enum State { Idle, Exposing, PlateReady }

        public ObscuraCamera obscura;
        [Min(0.5f)] public float exposureSeconds = 6f;

        [Header("Заслонка (необязательно)")]
        public Transform shutter;
        public Vector3 shutterOpenOffset = new(0f, 0.06f, 0f);
        public float shutterTime = 0.25f;

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip shutterOpenClip;
        public AudioClip shutterCloseClip;
        public AudioClip tickLoop;

        public State Current { get; private set; } = State.Idle;

        Plate _plate;
        Vector3 _shutterClosedPos;

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
                    Current = State.Exposing;
                    MoveShutter(open: true);
                    Play(shutterOpenClip);
                    if (audioSource && tickLoop) { audioSource.clip = tickLoop; audioSource.loop = true; audioSource.Play(); }
                    obscura.BeginExposure(exposureSeconds, false);
                    break;

                case State.PlateReady:
                    if (TryGetInventory(who, out var inv) && inv.Add(_plate))
                    {
                        _plate = null;
                        Current = State.Idle;
                    }
                    break;
            }
        }

        void OnExposureFinished(ObscuraCamera cam, Plate plate)
        {
            if (Current != State.Exposing) return; // выдержку запустил не игрок (например, отладочный тестер)
            _plate = plate;
            Current = State.PlateReady;
            MoveShutter(open: false);
            if (audioSource && audioSource.loop) { audioSource.Stop(); audioSource.loop = false; }
            Play(shutterCloseClip);
        }

        void MoveShutter(bool open)
        {
            if (!shutter) return;
            shutter.DOKill();
            shutter.DOLocalMove(open ? _shutterClosedPos + shutterOpenOffset : _shutterClosedPos, shutterTime)
                   .SetEase(Ease.OutQuad);
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
            if (shutter) shutter.DOKill();
        }
    }
}
