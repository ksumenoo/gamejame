using System.Collections.Generic;
using Jam.Audio;
using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Акт 2: камера живого фотографа на штативе. Сама открывает и закрывает заслонку по циклу.
    /// Пока заслонка открыта, перед объективом светит холодный конус — так призрак видит, куда смотрит камера.
    /// Sees() — попадает ли точка в кадр открытой камеры (конус + прямая видимость).
    /// </summary>
    public class PhotographerCamera : MonoBehaviour
    {
        public static readonly List<PhotographerCamera> All = new();

        [Tooltip("Дальность, на которой камера «видит», м.")]
        public float range = 9f;
        [Tooltip("Половина угла кадра, градусов.")]
        [Range(10f, 60f)] public float halfAngle = 32f;
        public float openSeconds = 6f;
        public float closedSeconds = 3f;
        [Tooltip("Высота объектива над полом, м.")]
        public float lensHeight = 1.1f;
        public LayerMask occluders = ~0;
        public Light beam;

        public bool IsOpen { get; private set; }
        public Vector3 LensPosition => transform.position + Vector3.up * lensHeight + transform.forward * 0.2f;

        float _t;

        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() { All.Remove(this); IsOpen = false; }

        /// <summary>Поставить штатив и начать цикл с закрытой заслонки.</summary>
        public void Place(Vector3 floorPos, Vector3 forward)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = transform.forward;
            transform.SetPositionAndRotation(floorPos, Quaternion.LookRotation(forward.normalized, Vector3.up));
            gameObject.SetActive(true);
            _t = 0f;
            SetOpen(false, silent: true);
        }

        public void Remove()
        {
            SetOpen(false, silent: true);
            gameObject.SetActive(false);
        }

        void Update()
        {
            _t += Time.deltaTime;
            if (IsOpen ? _t >= openSeconds : _t >= closedSeconds)
            {
                _t = 0f;
                SetOpen(!IsOpen, silent: false);
            }
        }

        void SetOpen(bool open, bool silent)
        {
            IsOpen = open;
            if (beam) beam.enabled = open;
            if (!silent) JamAudio.Play(open ? "shutter_open" : "shutter_close", LensPosition);
        }

        /// <summary>Точка в кадре открытой камеры и ничем не заслонена.</summary>
        public bool Sees(Vector3 point)
        {
            if (!IsOpen) return false;
            Vector3 lens = LensPosition;
            Vector3 to = point - lens;
            float d = to.magnitude;
            if (d > range || d < 0.01f) return false;
            if (Vector3.Angle(transform.forward, to) > halfAngle) return false;
            // заслонило что-то, кроме самого игрока
            if (Physics.Linecast(lens, point, out var hit, occluders, QueryTriggerInteraction.Ignore)
                && !hit.collider.GetComponentInParent<FirstPersonController>())
                return false;
            return true;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 0.8f, 1f, 0.6f);
            Gizmos.DrawRay(LensPosition, Quaternion.AngleAxis(halfAngle, Vector3.up) * transform.forward * range);
            Gizmos.DrawRay(LensPosition, Quaternion.AngleAxis(-halfAngle, Vector3.up) * transform.forward * range);
        }
#endif
    }
}
