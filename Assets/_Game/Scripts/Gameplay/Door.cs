using DG.Tweening;
using Jam.Interaction;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Дверь: E — открыть/закрыть. Slam() — захлопнуть для событий сюжета.
    /// Origin объекта двери в Blender должен стоять на петлях — вокруг него она и вращается.
    /// Маргарет проходит сквозь двери (у неё нет коллайдера, а двери не участвуют в NavMesh).
    /// </summary>
    public class Door : MonoBehaviour, IInteractable
    {
        public float openAngle = 95f;
        public float openTime = 0.6f;
        public float slamTime = 0.12f;
        [Tooltip("Открыта с начала игры.")]
        public bool startOpen;
        [Tooltip("При старте открывается в сторону от этой точки (центр дома) — чтобы створка не торчала в коридор.")]
        public Vector3 openAwayFrom;

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip openClip;
        public AudioClip closeClip;
        public AudioClip slamClip;

        [ShowNativeProperty] public bool IsOpen { get; private set; }

        /// <summary>Любая дверь: "door_open", "door_close" или "door_slam" — для звука.</summary>
        public static event System.Action<Door, string> AnyDoor;
        public static void ClearAnyDoor() => AnyDoor = null;

        Quaternion _closed;
        Vector3 _tip; // от петель к середине створки (закрытой), в мире
        bool _moving;

        /// <summary>Акт 2: призрак не открывает двери руками — держит E, стоя на месте, и дверь хлопает (или со скрипом открывается).</summary>
        public static bool GhostMode;
        [Tooltip("Акт 2: сколько секунд держать E.")]
        public float ghostHoldSeconds = 1f;
        Interactor _ghost;
        Jam.Player.FirstPersonController _ghostBody;
        float _ghostHold;

        public string Prompt => GhostMode
            ? (_ghost ? $"…{Mathf.RoundToInt(_ghostHold / ghostHoldSeconds * 100f)}%" : (IsOpen ? "Хлопнуть дверью (держать E)" : "Толкнуть дверь (держать E)"))
            : (IsOpen ? "Закрыть" : "Открыть");
        public bool CanInteract(Interactor who) => !_moving && (!GhostMode || !_ghost);

        void Update()
        {
            if (!_ghost) return;
            bool still = !_ghostBody || _ghostBody.CurrentSpeed < 0.3f;
            if (!GhostMode || !_ghost.UseHeld || !still || !ReferenceEquals(_ghost.Current, this)) { _ghost = null; return; }
            _ghostHold += Time.deltaTime;
            if (_ghostHold < ghostHoldSeconds) return;
            var who = _ghost;
            _ghost = null;
            if (IsOpen) Slam();
            else SetOpen(true, SignAwayFrom(who.transform.position));
        }

        void Awake()
        {
            _closed = transform.rotation;
            var r = GetComponent<Renderer>();
            _tip = r ? r.bounds.center - transform.position : transform.right * 0.4f;
            _tip.y = 0f;
        }

        void Start()
        {
            if (!startOpen) return;
            IsOpen = true;
            transform.rotation = OpenRotation(SignAwayFrom(openAwayFrom));
        }

        // вращаем вокруг мировой вертикали: оси объекта из FBX могут быть повёрнуты
        Quaternion OpenRotation(float sign) => Quaternion.AngleAxis(openAngle * sign, Vector3.up) * _closed;

        /// <summary>В какую сторону открыть, чтобы створка ушла дальше от точки.</summary>
        float SignAwayFrom(Vector3 point)
        {
            Vector3 hinge = transform.position;
            Vector3 a = hinge + Quaternion.AngleAxis(openAngle, Vector3.up) * _tip;
            Vector3 b = hinge + Quaternion.AngleAxis(-openAngle, Vector3.up) * _tip;
            point.y = a.y = b.y = 0f;
            return (a - point).sqrMagnitude >= (b - point).sqrMagnitude ? 1f : -1f;
        }

        public void Interact(Interactor who)
        {
            if (GhostMode)
            {
                _ghost = who;
                _ghostBody = who ? who.GetComponentInParent<Jam.Player.FirstPersonController>() : null;
                _ghostHold = 0f;
                return;
            }
            // открываем от игрока, чтобы не толкать его дверью
            float sign = who ? SignAwayFrom(who.transform.position) : 1f;
            SetOpen(!IsOpen, sign);
        }

        [Button("Открыть/закрыть (тест)")]
        void DebugToggle() => SetOpen(!IsOpen, 1f);

        public void SetOpen(bool open, float sign = 1f)
        {
            IsOpen = open;
            _moving = true;
            transform.DOKill();
            var target = open ? OpenRotation(sign) : _closed;
            transform.DORotateQuaternion(target, openTime).SetEase(Ease.InOutSine).SetLink(gameObject)
                     .OnComplete(() => _moving = false);
            Play(open ? openClip : closeClip);
            AnyDoor?.Invoke(this, open ? "door_open" : "door_close");
        }

        [Button("Захлопнуть (тест)")]
        public void Slam()
        {
            IsOpen = false;
            transform.DOKill();
            transform.DORotateQuaternion(_closed, slamTime).SetEase(Ease.InQuad).SetLink(gameObject)
                     .OnComplete(() =>
                     {
                         _moving = false;
                         transform.DOShakeRotation(0.25f, new Vector3(0f, 3f, 0f), 20).SetLink(gameObject);
                     });
            Play(slamClip ? slamClip : closeClip);
            AnyDoor?.Invoke(this, "door_slam");
        }

        void Play(AudioClip c)
        {
            if (audioSource && c) audioSource.PlayOneShot(c);
        }
    }
}
