using System;
using DG.Tweening;
using Jam.Exposure;
using Jam.Interaction;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Камера-обскура как предмет в мире: держать E — заслонка открыта, идёт выдержка; отпустил или отошёл — пластина засвечена.
    /// E — забрать готовую пластину.
    /// Корень стоит на полу, ObscuraCamera — в дочернем объекте на уровне отверстия.
    /// Анимации заслонки и корпуса — DOTween.
    /// </summary>
    public class CameraStation : MonoBehaviour, IInteractable
    {
        public enum State { Idle, Exposing, PlateReady }

        public ObscuraCamera obscura;
        [Min(0.5f)] public float exposureSeconds = 6f;
        [Tooltip("Выдержку надо держать E. Отпустил — пластина засвечена.")]
        public bool holdToExpose = true;
        [Tooltip("Дальше этого от камеры отойти нельзя — пластина засвечена, м.")]
        public float holdRadius = 1.8f;

        [Header("Свет")]
        [Tooltip("Горящий канделябр в той же комнате ближе этого — светло, м.")]
        public float lightRadius = 5.5f;
        [Tooltip("Скорость выдержки в темноте (канделябр в комнате погас): 0.5 — вдвое дольше.")]
        [Range(0.2f, 1f)] public float darkSpeed = 0.5f;

        [Header("Заслонка")]
        public Transform shutter;
        [Tooltip("Смещение заслонки при открытии, в осях родителя заслонки.")]
        public Vector3 shutterOpenOffset = new(0f, 0.12f, 0f);
        [Tooltip("Где заслонка в открытом положении. Если задано — offset не используется. " +
                 "В Blender: копия заслонки с именем Shutter_Open, поставленная как открытая.")]
        public Transform shutterOpenTarget;
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
        public float Progress => obscura ? obscura.Progress : 0f;
        public bool HasPlate => _plate != null;
        /// <summary>Идёт выдержка, а свет в комнате погас — пластине нужно больше времени.</summary>
        [ShowNativeProperty] public bool InDark { get; private set; }

        public event Action<CameraStation> ExposureBegan;
        /// <summary>Выдержку сорвали — пластина засвечена.</summary>
        public event Action<CameraStation> ExposureRuined;
        public event Action<CameraStation> PlateDone;

        Plate _plate;
        Interactor _holder;
        Vector3 _shutterClosedPos, _shutterOpenPos;
        Quaternion _shutterClosedRot, _shutterOpenRot;
        Sequence _shutterSeq;

        public string Prompt => Current switch
        {
            State.Idle => (holdToExpose ? "Открыть заслонку (держать E)" : "Открыть заслонку") + (GetComponent<PortableCamera>() ? "   ·   F — забрать" : ""),
            State.Exposing => "", // ход выдержки — полоской внизу (ExposureHUD), без слов
            State.PlateReady => "Забрать пластину",
            _ => "",
        };

        void Awake()
        {
            if (!obscura) obscura = GetComponentInChildren<ObscuraCamera>();
            if (shutter)
            {
                _shutterClosedPos = shutter.localPosition;
                _shutterClosedRot = shutter.localRotation;
                if (shutterOpenTarget)
                {
                    // открытое положение — в осях родителя заслонки
                    var p = shutter.parent;
                    _shutterOpenPos = p ? p.InverseTransformPoint(shutterOpenTarget.position) : shutterOpenTarget.position;
                    _shutterOpenRot = p ? Quaternion.Inverse(p.rotation) * shutterOpenTarget.rotation : shutterOpenTarget.rotation;
                    var r = shutterOpenTarget.GetComponent<Renderer>();
                    if (r) r.enabled = false; // это только метка
                }
                else
                {
                    _shutterOpenPos = _shutterClosedPos + shutterOpenOffset;
                    _shutterOpenRot = _shutterClosedRot;
                }
            }
        }

        void OnEnable()
        {
            if (!obscura) return;
            obscura.ExposureFinished += OnExposureFinished;
            obscura.ExposureCancelled += OnExposureCancelled;
        }

        void OnDisable()
        {
            if (!obscura) return;
            obscura.ExposureFinished -= OnExposureFinished;
            obscura.ExposureCancelled -= OnExposureCancelled;
        }

        void Update()
        {
            if (Current == State.Exposing && obscura)
            {
                // свет проверяем всю выдержку: Маргарет может погасить канделябр посреди кадра
                InDark = !Candelabra.IsLightAt(obscura.transform.position, lightRadius);
                obscura.Speed = InDark ? darkSpeed : 1f;
            }
            else InDark = false;

            if (Current != State.Exposing || !_holder || !holdToExpose) return;
            Vector3 d = _holder.transform.position - transform.position;
            d.y = 0f;
            if (!_holder.UseHeld || d.magnitude > holdRadius) Ruin();
        }

        /// <summary>Сорвать выдержку (отпустили E, отошли, Маргарет схватила).</summary>
        public void Ruin()
        {
            if (Current != State.Exposing || !obscura) return;
            obscura.CancelExposure(); // дальше — OnExposureCancelled
        }

        /// <summary>Забрать готовую пластину (переносная камера забирает её сама при подъёме).</summary>
        public Plate TakePlate()
        {
            if (Current != State.PlateReady) return null;
            var p = _plate;
            _plate = null;
            Current = State.Idle;
            return p;
        }

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
                    _holder = who;
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
            ExposureBegan?.Invoke(this);
        }

        void OnExposureCancelled(ObscuraCamera cam)
        {
            if (Current != State.Exposing) return;
            _holder = null;
            Current = State.Idle;
            AnimateShutter(open: false);
            if (audioSource && audioSource.loop) { audioSource.Stop(); audioSource.loop = false; }
            Play(shutterCloseClip);
            ExposureRuined?.Invoke(this);
        }

        void OnExposureFinished(ObscuraCamera cam, Plate plate)
        {
            if (Current != State.Exposing) return; // выдержку запустил не игрок (например, отладочный тестер)
            _plate = plate;
            _holder = null;
            Current = State.PlateReady;
            AnimateShutter(open: false);
            if (audioSource && audioSource.loop) { audioSource.Stop(); audioSource.loop = false; }
            Play(shutterCloseClip);
            PlateDone?.Invoke(this);
        }

        void AnimateShutter(bool open)
        {
            _shutterSeq?.Kill();
            _shutterSeq = DOTween.Sequence().SetLink(gameObject);

            if (shutter)
            {
                float t = open ? shutterOpenTime : shutterCloseTime;
                var ease = open ? Ease.OutCubic : Ease.InQuad;
                _shutterSeq.Append(shutter.DOLocalMove(open ? _shutterOpenPos : _shutterClosedPos, t).SetEase(ease));
                _shutterSeq.Join(shutter.DOLocalRotateQuaternion(open ? _shutterOpenRot : _shutterClosedRot, t).SetEase(ease));
            }
            // корпус больше не трясём: тряска поворачивала всю модель, и заслонка уезжала криво
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
