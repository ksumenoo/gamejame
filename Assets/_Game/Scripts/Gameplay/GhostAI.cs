using System;
using System.Collections.Generic;
using Jam.Exposure;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Jam.Gameplay
{
    /// <summary>
    /// Призрак акта 1: ходит по кругу через точки маршрута. На одной из точек-остановок
    /// (выбирается случайно при старте) замирает и смотрит в ближайшую камеру — на пластине он выйдет резким.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class GhostAI : MonoBehaviour
    {
        [Serializable]
        public class Waypoint
        {
            public Transform point;
            public bool canStop;
        }

        public List<Waypoint> route = new();
        [Min(0.1f)] public float walkSpeed = 1.2f;
        [Min(0f)] public float stopDuration = 8f;
        [Min(0.05f)] public float arriveDistance = 0.35f;
        [Tooltip("Выбирать остановку случайно. Выключи, чтобы задать её вручную в forcedStopIndex.")]
        public bool randomizeStop = true;
        public int forcedStopIndex = -1;

        public int ActiveStopIndex { get; private set; } = -1;
        public bool IsStanding { get; private set; }
        public Transform FacingTarget { get; private set; }

        public event Action<GhostAI> StartedStanding;
        public event Action<GhostAI> StoppedStanding;

        NavMeshAgent _agent;
        ExposureSubject _subject;
        int _index;
        float _waitLeft;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _subject = GetComponentInChildren<ExposureSubject>();
            _agent.enabled = false; // включим, когда NavMesh уже построен
        }

        void Start()
        {
            ChooseStop();

            _agent.enabled = true;
            if (NavMesh.SamplePosition(transform.position, out var hit, 3f, NavMesh.AllAreas))
                _agent.Warp(hit.position);
            else
                Debug.LogWarning("[Ghost] Нет NavMesh рядом со стартом призрака.", this);

            _agent.speed = walkSpeed;
            _agent.updateRotation = true;
            _index = 0;
            GoTo(_index);
        }

        void ChooseStop()
        {
            var stops = new List<int>();
            for (int i = 0; i < route.Count; i++)
                if (route[i].canStop) stops.Add(i);

            if (!randomizeStop && forcedStopIndex >= 0 && forcedStopIndex < route.Count) ActiveStopIndex = forcedStopIndex;
            else if (stops.Count > 0) ActiveStopIndex = stops[Random.Range(0, stops.Count)];
            else ActiveStopIndex = -1;

            if (ActiveStopIndex >= 0)
                Debug.Log($"[Ghost] Остановка в этом прохождении: {route[ActiveStopIndex].point.name}");
        }

        void Update()
        {
            if (!_agent.enabled || !_agent.isOnNavMesh || route.Count == 0) return;

            if (IsStanding)
            {
                _waitLeft -= Time.deltaTime;
                if (FacingTarget)
                {
                    Vector3 dir = FacingTarget.position - transform.position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 4f);
                }
                if (_waitLeft <= 0f)
                {
                    IsStanding = false;
                    _agent.isStopped = false;
                    _agent.updateRotation = true;
                    StoppedStanding?.Invoke(this);
                    Next();
                }
                return;
            }

            if (!_agent.pathPending && _agent.remainingDistance <= arriveDistance)
            {
                if (_index == ActiveStopIndex)
                {
                    IsStanding = true;
                    _waitLeft = stopDuration;
                    _agent.isStopped = true;
                    _agent.updateRotation = false;
                    FacingTarget = PickCameraToStareAt();
                    StartedStanding?.Invoke(this);
                }
                else
                {
                    Next();
                }
            }
        }

        void Next()
        {
            _index = (_index + 1) % route.Count;
            GoTo(_index);
        }

        void GoTo(int i)
        {
            var p = route[i].point;
            if (p) _agent.SetDestination(p.position);
        }

        /// <summary>Ближайшая камера, которая его видит; если таких нет — просто ближайшая.</summary>
        Transform PickCameraToStareAt()
        {
            var cams = FindObjectsByType<ObscuraCamera>();
            Transform best = null, bestVisible = null;
            float bestD = float.MaxValue, bestVisD = float.MaxValue;
            foreach (var c in cams)
            {
                float d = (c.transform.position - transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c.transform; }
                if (_subject && c.CanSee(_subject) && d < bestVisD) { bestVisD = d; bestVisible = c.transform; }
            }
            return bestVisible ? bestVisible : best;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            for (int i = 0; i < route.Count; i++)
            {
                var a = route[i].point;
                var b = route[(i + 1) % route.Count].point;
                if (!a || !b) continue;
                Gizmos.color = route[i].canStop ? (i == ActiveStopIndex ? Color.red : new Color(1f, 0.5f, 0.3f)) : new Color(0.6f, 0.6f, 1f);
                Gizmos.DrawWireSphere(a.position, route[i].canStop ? 0.35f : 0.15f);
                Gizmos.color = new Color(0.6f, 0.6f, 1f, 0.6f);
                Gizmos.DrawLine(a.position, b.position);
            }
        }
#endif
    }
}
