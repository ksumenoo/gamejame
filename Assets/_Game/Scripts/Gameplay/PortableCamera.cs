using System;
using DG.Tweening;
using Jam.Interaction;
using Jam.Player;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Gameplay
{
    /// <summary>
    /// Одна камера на штативе, которую фотограф носит с собой.
    /// F — поставить перед собой (объективом туда, куда смотришь); F, глядя на камеру, — взять обратно.
    /// Готовая пластина при подъёме сама уходит в сумку.
    /// Висит на корне CameraStation.
    /// </summary>
    [RequireComponent(typeof(CameraStation))]
    public class PortableCamera : MonoBehaviour
    {
        public FirstPersonController player;
        public Interactor interactor;
        public PlateInventory inventory;
        public bool startCarried = true;

        [Header("Установка")]
        [Tooltip("На каком расстоянии перед игроком ставится штатив, м.")]
        public float placeDistance = 0.9f;
        public float minDistance = 0.4f;
        public LayerMask groundMask = ~0;
        [Tooltip("Если штатив ставят рядом со следами на полу (точкой обзора) — встаёт точно на них.")]
        public float snapRadius = 1.1f;
        [Range(10f, 90f)] public float snapAngle = 60f;

        /// <summary>Сколько раз камеру уже ставили (подсказки).</summary>
        public int PlaceCount { get; private set; }

        [ShowNativeProperty] public bool IsCarried { get; private set; }

        public event Action<PortableCamera> Placed;
        public event Action<PortableCamera> PickedUp;

        CameraStation _station;
        Renderer[] _renderers;
        Collider[] _colliders;
        InputAction _key;

        public string CarryHint => IsCarried ? "F — поставить камеру" : "";

        void Awake()
        {
            _station = GetComponent<CameraStation>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);
            _key = new InputAction("Camera", InputActionType.Button, "<Keyboard>/f");
            if (!player) player = FindAnyObjectByType<FirstPersonController>();
            if (player && !interactor) interactor = player.GetComponentInChildren<Interactor>();
            if (player && !inventory) inventory = player.GetComponentInChildren<PlateInventory>();
        }

        void Start()
        {
            if (startCarried) SetCarried(true);
        }

        void OnEnable() => _key.Enable();
        void OnDisable() => _key.Disable();
        void OnDestroy() => _key.Dispose();

        void Update()
        {
            if (!_key.WasPressedThisFrame() || !player || player.InputLocked) return;
            if (IsCarried) TryPlace();
            else if (interactor && ReferenceEquals(interactor.Current, _station)) TryPickUp();
        }

        [Button("Поставить перед игроком")]
        public bool TryPlace()
        {
            if (!IsCarried || !player) return false;
            Vector3 fwd = player.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return false;
            fwd.Normalize();

            // не ставим в стену: толстый луч на уровне груди
            Vector3 chest = player.transform.position + Vector3.up * 1.1f;
            float dist = placeDistance;
            if (Physics.SphereCast(chest, 0.22f, fwd, out var wall, placeDistance, groundMask, QueryTriggerInteraction.Ignore))
                dist = wall.distance - 0.05f;
            if (dist < minDistance) { Hint("Тут камеру не поставить — слишком тесно."); return false; }

            float feet = player.transform.position.y;
            if (!FindFloor(player.transform.position + fwd * dist, feet, out var floor))
            { Hint("Тут камеру не поставить."); return false; }

            Vector3 pos = floor.point;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);
            TrySnap(ref pos, ref rot, fwd, feet);
            if (!HasRoom(pos)) { Hint("Тут камеру не поставить — мешает мебель."); return false; }

            transform.SetPositionAndRotation(pos + Vector3.up * 0.15f, rot);
            floor.point = pos;
            PlaceCount++;
            SetCarried(false);
            transform.DOKill();
            transform.DOMove(floor.point, 0.18f).SetEase(Ease.InQuad).SetLink(gameObject);
            Placed?.Invoke(this);
            return true;
        }

        /// <summary>Рядом точка обзора и смотрим примерно туда же — ставим ровно на следы штатива.</summary>
        /// <summary>Пол на уровне ног игрока — не крышка стола, не верх двери.</summary>
        bool FindFloor(Vector3 at, float feet, out RaycastHit floor)
        {
            floor = default;
            var hits = Physics.RaycastAll(new Vector3(at.x, feet + 0.6f, at.z), Vector3.down, 1.5f, groundMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool ok = false;
            foreach (var h in hits)
            {
                float dy = Mathf.Abs(h.point.y - feet);
                if (dy > 0.35f || h.normal.y < 0.7f || dy >= best) continue;
                best = dy; floor = h; ok = true;
            }
            return ok;
        }

        /// <summary>Штатив и ящик ни во что не упираются.</summary>
        bool HasRoom(Vector3 pos)
        {
            var cols = Physics.OverlapCapsule(pos + Vector3.up * 0.35f, pos + Vector3.up * 1.25f, 0.16f, groundMask, QueryTriggerInteraction.Ignore);
            foreach (var c in cols)
            {
                if (player && c.transform.IsChildOf(player.transform)) continue;
                if (c.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }

        void TrySnap(ref Vector3 pos, ref Quaternion rot, Vector3 fwd, float feet)
        {
            PastScene best = null;
            float bestD = snapRadius;
            foreach (var s in PastScene.All)
            {
                if (!s || !s.viewPoint || s.Current == PastScene.State.Developed) continue;
                Vector3 d = s.viewPoint.position - pos;
                d.y = 0f;
                Vector3 vf = s.viewPoint.forward;
                vf.y = 0f;
                if (d.magnitude < bestD && Vector3.Angle(vf, fwd) < snapAngle) { bestD = d.magnitude; best = s; }
            }
            if (!best) return;
            Vector3 vp = best.viewPoint.position;
            if (!FindFloor(vp, feet, out var hit)) return;
            Vector3 look = best.viewPoint.forward;
            look.y = 0f;
            pos = hit.point;
            rot = Quaternion.LookRotation(look.normalized, Vector3.up);
        }

        /// <summary>Поставить штатив в точку из кода (нулевая сцена: камера уже стоит в проявочной).</summary>
        public void PlaceAt(Vector3 floorPos, Vector3 forward)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = transform.forward;
            startCarried = false;
            transform.DOKill();
            transform.SetPositionAndRotation(floorPos, Quaternion.LookRotation(forward.normalized, Vector3.up));
            SetCarried(false);
        }

        /// <summary>После смерти камера снова в руках; недоснятая или незабранная пластина пропадает.</summary>
        public void ReturnToPlayer()
        {
            if (IsCarried) return;
            if (_station.Current == CameraStation.State.Exposing) _station.Ruin();
            _station.TakePlate()?.Release();
            transform.DOKill();
            SetCarried(true);
        }

        [Button("Взять")]
        public bool TryPickUp()
        {
            if (IsCarried) return false;
            if (_station.Current == CameraStation.State.Exposing) { Hint("Идёт выдержка."); return false; }
            if (_station.Current == CameraStation.State.PlateReady)
            {
                if (!inventory || !inventory.HasRoom) { Hint("Сумка полна — сначала проявите пластины."); return false; }
                inventory.Add(_station.TakePlate());
            }
            transform.DOKill();
            SetCarried(true);
            PickedUp?.Invoke(this);
            return true;
        }

        void SetCarried(bool carried)
        {
            IsCarried = carried;
            foreach (var r in _renderers) if (r) r.enabled = !carried && !IsMarker(r);
            foreach (var c in _colliders) if (c) c.enabled = !carried;
        }

        // метка открытой заслонки остаётся невидимой
        bool IsMarker(Renderer r) => _station.shutterOpenTarget && r.transform == _station.shutterOpenTarget;

        static void Hint(string text)
        {
            if (PlateCounterUI.Instance) PlateCounterUI.Instance.ShowHint(text);
        }
    }
}
