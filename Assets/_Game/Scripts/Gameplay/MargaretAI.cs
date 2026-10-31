using System;
using System.Linq;
using System.Collections.Generic;
using Jam.Exposure;
using Jam.Player;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Jam.Gameplay
{
    /// <summary>
    /// Маргарет — охотница. Глазом не видна (слой Ghost), только в обскурах.
    /// Патруль → слышит (бег, шаги рядом, открытая заслонка) → идёт проверить → видит игрока → погоня → поимка.
    /// Агрессия (число проявленных сцен) делает её быстрее и чутче. При агрессии 0 — обучение: только бродит.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class MargaretAI : MonoBehaviour
    {
        public enum State { Patrol, Investigate, Chase, Stunned, Caught }

        [Header("Ссылки")]
        public List<Transform> patrolPoints = new();
        public FirstPersonController player;
        [Tooltip("Глаза игрока — до них проверяем прямую видимость.")]
        public Transform playerEyes;
        [Tooltip("Слои, которые закрывают обзор: стены и мебель.")]
        public LayerMask occluders = ~0;

        [Header("Скорость, м/с (растёт с агрессией)")]
        public float walkSpeed = 1.0f;
        public float chaseSpeed = 2.3f;
        public float speedPerAggression = 0.07f;

        [Header("Чувства")]
        public float hearRunRadius = 6f; // бег слышно через пару комнат, а не через весь дом
        public float hearWalkRadius = 3f;
        public float sightRadius = 6f;
        [Range(10f, 180f)] public float sightAngle = 140f;
        public float exposureAttractRadius = 30f;
        public float sensesPerAggression = 0.05f;
        [Tooltip("После проявки она уходит в дальний конец дома и столько секунд никого не замечает.")]
        public float graceAfterDevelop = 12f;
        [Tooltip("Сколько секунд она ещё стоит после конца выдержки, прежде чем очнуться.")]
        public float wakeDelay = 1.5f;
        [Tooltip("После посмертного портрета (выдержка у стойки дошла до конца) она уходит и столько секунд никого не трогает — успеть проявить.")]
        public float graceAfterPortrait = 25f;
        [Tooltip("Проявочная — безопасная комната: внутри игрока не видит и не слышит.")]
        public Darkroom safeRoom;

        [Header("Поведение")]
        public float investigateWait = 5f;
        [Tooltip("Куда она идёт на щелчок заслонки: столько метров перед объективом.")]
        public float lureDistance = 2f;
        public float loseSightTime = 3.5f;
        public float killDistance = 0.9f;
        public float repathInterval = 0.25f;

        [ShowNativeProperty] public State Current { get; private set; } = State.Patrol;
        [ShowNativeProperty] public int Aggression => StoryDirector.Instance ? StoryDirector.Instance.Aggression : 1;

        /// <summary>Расстояние до игрока — для свечей, звука, эффектов близости.</summary>
        public float DistanceToPlayer => player ? Vector3.Distance(transform.position, player.transform.position) : float.MaxValue;

        public event Action<MargaretAI> CaughtPlayer;
        public event Action<MargaretAI, State> StateChanged;
        /// <summary>Встала к стойке перед объективом.</summary>
        public event Action<MargaretAI> Posed;

        NavMeshAgent _agent;
        Vector3 _target;
        float _timer, _repath, _lastSeen, _stuck;
        int _stuckCount;
        Vector3 _stuckAt;
        Vector3 _progressPos;
        float _progressTime;
        bool _waiting;
        ObscuraCamera _posingFor; // пришла на щелчок заслонки: стоит и смотрит в объектив, пока идёт выдержка
        readonly List<ObscuraCamera> _cams = new();

        float Mult => 1f + Mathf.Max(0, Aggression - 1) * sensesPerAggression;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.enabled = false; // включим, когда NavMesh построен
        }

        void Start()
        {
            _agent.enabled = true;
            if (NavMesh.SamplePosition(transform.position, out var hit, 3f, NavMesh.AllAreas))
                _agent.Warp(hit.position);
            else
                Debug.LogWarning("[Margaret] Нет NavMesh рядом со стартом.", this);

            if (!safeRoom) safeRoom = FindAnyObjectByType<Darkroom>();
            if (safeRoom) safeRoom.PlatesDeveloped += OnPlatesDeveloped;
            _cams.AddRange(FindObjectsByType<ObscuraCamera>());
            foreach (var c in _cams) { c.ExposureStarted += OnExposureStarted; c.ExposureFinished += OnExposureFinished; }

            SetState(State.Patrol);
            PickPatrolPoint();
        }

        void OnDestroy()
        {
            foreach (var c in _cams) if (c) { c.ExposureStarted -= OnExposureStarted; c.ExposureFinished -= OnExposureFinished; }
        }

        // ---------------- API ----------------

        /// <summary>Оглушить (вспышка магния).</summary>
        public void Stun(float seconds)
        {
            if (Current == State.Caught) return;
            SetState(State.Stunned);
            _timer = seconds;
            if (_agent.isOnNavMesh) _agent.isStopped = true;
        }

        /// <summary>Громкий звук (вспышка, хлопок двери): если не оглушена и не в погоне — идёт проверить.</summary>
        public void HearNoise(Vector3 position)
        {
            if (Current == State.Stunned || Current == State.Caught || Current == State.Chase || Aggression <= 0) return;
            Investigate(position);
        }

        /// <summary>После смерти игрока: уйти подальше и начать патруль заново.</summary>
        public void ResetAwayFrom(Vector3 position)
        {
            Transform far = null;
            float best = -1f;
            foreach (var p in patrolPoints)
            {
                if (!p) continue;
                float d = (p.position - position).sqrMagnitude;
                if (d > best) { best = d; far = p; }
            }
            if (far) WarpNear(far.position);
            if (_agent.isOnNavMesh) _agent.isStopped = false;
            SetState(State.Patrol);
            PickPatrolPoint();
        }

        // ---------------- логика ----------------

        void Update()
        {
            if (!_agent.enabled) return;
            if (!_agent.isOnNavMesh)
            {
                // сошла с сетки (телепорт мимо NavMesh) — возвращаем на ближайшую точку, иначе замрёт навсегда
                if (NavMesh.SamplePosition(transform.position, out var back, 4f, NavMesh.AllAreas)) _agent.Warp(back.position);
                else Unstick();
                return;
            }
            float speedBonus = Mathf.Max(0, Aggression - 1) * speedPerAggression;
            WatchProgress();
            // страховка: поймала, а смерть/респаун не сбросили её — через 6 с уходит сама
            if (Current == State.Caught && Time.time - _caughtAt > 6f) { ResetAwayFrom(player ? player.transform.position : transform.position); return; }

            switch (Current)
            {
                case State.Patrol:
                    _agent.speed = walkSpeed + speedBonus;
                    if (Arrived() || Stuck()) PickPatrolPoint();
                    Senses();
                    break;

                case State.Investigate:
                    _agent.speed = walkSpeed * 1.4f + speedBonus;
                    if (!_waiting && (Arrived() || Stuck()))
                    {
                        _waiting = true;
                        _timer = investigateWait;
                    }
                    if (_posingFor && _waiting)
                    {
                        // замерла перед объективом: смотрит в него, ничего вокруг не замечает
                        Vector3 look = _posingFor.transform.position - transform.position;
                        look.y = 0f;
                        if (look.sqrMagnitude > 1e-4f)
                            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), Time.deltaTime * 4f);
                        if (_posingFor.IsExposing) break;
                        _posingFor = null;           // выдержка кончилась — очнулась
                        _timer = Mathf.Min(_timer, wakeDelay);
                    }
                    if (_waiting)
                    {
                        _timer -= Time.deltaTime;
                        if (_timer <= 0f) { SetState(State.Patrol); PickPatrolPoint(); }
                    }
                    Senses();
                    break;

                case State.Chase:
                    if (safeRoom && safeRoom.PlayerInside) { SetState(State.Patrol); PickPatrolPoint(); break; } // в проявочную не идёт
                    _agent.speed = chaseSpeed + speedBonus;
                    _repath -= Time.deltaTime;
                    if (_repath <= 0f && player)
                    {
                        _repath = repathInterval;
                        _agent.SetDestination(player.transform.position);
                    }
                    if (CanSeePlayer()) _lastSeen = Time.time;
                    else if (Time.time - _lastSeen > loseSightTime) Investigate(player.transform.position);
                    if (DistanceToPlayer < killDistance) Catch();
                    break;

                case State.Stunned:
                    _timer -= Time.deltaTime;
                    if (_timer <= 0f)
                    {
                        _agent.isStopped = false;
                        if (player) Investigate(player.transform.position);
                        else SetState(State.Patrol);
                    }
                    break;
            }
        }

        float _graceUntil;

        void OnPlatesDeveloped(Darkroom room, IReadOnlyList<Plate> plates) => Calm(graceAfterDevelop, room.transform.position);

        /// <summary>Отогнать и успокоить на время: уходит в дальнюю от точки комнату, никого не замечает.</summary>
        public void Calm(float seconds, Vector3 awayFrom)
        {
            // после поимки тоже: иначе она так и остаётся в Caught — стоит на месте и не приходит к стойке
            ResetAwayFrom(awayFrom);
            _graceUntil = Time.time + seconds;
        }

        void Senses()
        {
            if (Time.time < _graceUntil) return;
            if (safeRoom && safeRoom.PlayerInside) return;
            if (!player || Aggression <= 0) return; // обучение: только бродит
            float d = DistanceToPlayer;

            if (d < sightRadius * Mult && CanSeePlayer())
            {
                _lastSeen = Time.time;
                SetState(State.Chase);
                _repath = 0f;
                return;
            }
            bool heardRun = player.IsRunning && d < hearRunRadius * Mult;
            bool heardSteps = player.CurrentSpeed > 0.6f && !player.IsSneaking && d < hearWalkRadius * Mult;
            if (heardRun || heardSteps) Investigate(player.transform.position);
        }

        bool CanSeePlayer()
        {
            if (!player) return false;
            Vector3 eyes = transform.position + Vector3.up * 1.6f;
            Vector3 target = playerEyes ? playerEyes.position : player.transform.position + Vector3.up * 1.6f;
            Vector3 to = target - eyes;
            if (to.magnitude > sightRadius * Mult * 1.5f) return false;
            Vector3 flat = new(to.x, 0f, to.z);
            if (Current != State.Chase && Vector3.Angle(transform.forward, flat) > sightAngle * 0.5f) return false;
            return !Physics.Linecast(eyes, target, occluders, QueryTriggerInteraction.Ignore);
        }

        void OnExposureStarted(ObscuraCamera cam)
        {
            if (Current == State.Caught) return;
            // камера на точке сцены «посмертного портрета» — Маргарет сама встаёт к стойке и замирает на всю выдержку
            foreach (var sc in PastScene.All)
            {
                if (!sc || !sc.IsActive || !sc.requireMargaretSharp || !sc.posePoint || !sc.viewPoint) continue;
                Vector3 dv = cam.transform.position - sc.viewPoint.position;
                dv.y = 0f;
                if (dv.magnitude > sc.matchRadius) continue;
                // встаёт вплотную перед стойкой, со стороны объектива — иначе стойка заслонит её от камеры
                Vector3 toCam = sc.viewPoint.position - sc.posePoint.position;
                toCam.y = 0f;
                Vector3 posePos = sc.posePoint.position + (toCam.sqrMagnitude > 1e-4f ? toCam.normalized * 0.35f : Vector3.zero);
                if (NavMesh.SamplePosition(posePos, out var pose, 1.5f, NavMesh.AllAreas))
                {
                    _agent.Warp(pose.position);
                    _target = pose.position;
                    _agent.isStopped = false;
                    _agent.SetDestination(pose.position);
                    SetState(State.Investigate);
                    _waiting = true;
                    _timer = investigateWait;
                    _posingFor = cam;
                    _portraitFor = cam;
                    Posed?.Invoke(this);
                    return;
                }
            }
            if (Aggression <= 0 || Current == State.Chase || Current == State.Caught || Current == State.Stunned) return;
            if ((cam.transform.position - transform.position).sqrMagnitude > exposureAttractRadius * exposureAttractRadius) return;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            Vector3 point = cam.transform.position + (fwd.sqrMagnitude > 1e-4f ? fwd.normalized * lureDistance : Vector3.zero);
            // если перед объективом стена — встанет у неё
            if (Physics.Linecast(cam.transform.position, point, out var hit, occluders, QueryTriggerInteraction.Ignore))
                point = hit.point - fwd.normalized * 0.3f;
            Investigate(point);
            _posingFor = cam;
        }

        ObscuraCamera _portraitFor;

        // портрет снят до конца — «получила своё»: уходит и даёт время донести пластину до проявочной
        void OnExposureFinished(ObscuraCamera cam, Plate plate)
        {
            if (cam != _portraitFor) return;
            _portraitFor = null;
            Calm(graceAfterPortrait, cam.transform.position);
        }

        void Investigate(Vector3 point)
        {
            _posingFor = null;
            _stuck = 0f;
            if (NavMesh.SamplePosition(point, out var hit, 2f, NavMesh.AllAreas)) point = hit.position;
            _target = point;
            _waiting = false;
            _agent.isStopped = false;
            _agent.SetDestination(_target);
            SetState(State.Investigate);
        }

        void PickPatrolPoint()
        {
            _stuck = 0f;
            if (patrolPoints.Count == 0) return;
            var path = new NavMeshPath();
            for (int i = 0; i < 8; i++)
            {
                var p = patrolPoints[Random.Range(0, patrolPoints.Count)];
                if (!p || (p.position - transform.position).sqrMagnitude <= 4f) continue;
                // только туда, куда реально можно дойти — иначе упрётся в край острова и встанет
                if (!NavMesh.SamplePosition(p.position, out var hit, 2f, NavMesh.AllAreas)) continue;
                if (!_agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                _agent.SetPath(path);
                return;
            }
            Debug.LogWarning($"[Margaret] Не нашла достижимой точки маршрута из {transform.position} — переношу на другую точку.", this);
            Unstick();
        }

        /// <summary>
        /// Должна идти, но за 4 с не сдвинулась и на полметра (трётся об угол, дёргается на месте) — переносим.
        /// Скорость тут не годится: при дёрганьи она не нулевая.
        /// </summary>
        void WatchProgress()
        {
            bool shouldMove = Current == State.Patrol || (Current == State.Investigate && !_waiting);
            if (!shouldMove || (transform.position - _progressPos).sqrMagnitude > 0.25f)
            {
                _progressPos = transform.position;
                _progressTime = Time.time;
                return;
            }
            if (Time.time - _progressTime < 4f) return;
            Debug.LogWarning($"[Margaret] Застряла в {transform.position} ({Current}, цель {_agent.destination}) — переношу.", this);
            _progressTime = Time.time;
            SetState(State.Patrol);
            Unstick();
            PickPatrolPoint();
        }

        bool WarpNear(Vector3 point)
        {
            if (!NavMesh.SamplePosition(point, out var hit, 2f, NavMesh.AllAreas)) return false;
            _agent.Warp(hit.position);
            return true;
        }

        /// <summary>Застряла (отрезанный кусок сетки, угол мебели) — переносим на случайную точку маршрута подальше от игрока.</summary>
        void Unstick()
        {
            _stuckCount = 0;
            var pts = patrolPoints.Where(p => p).ToList();
            if (pts.Count == 0) return;
            Vector3 from = player ? player.transform.position : transform.position;
            foreach (var p in pts.OrderByDescending(p => (p.position - from).sqrMagnitude + Random.value * 10f))
                if (WarpNear(p.position)) break;
            if (_agent.isOnNavMesh) { _agent.isStopped = false; _agent.ResetPath(); }
        }

        /// <summary>Стоит на месте, хотя идти ещё есть куда, дольше 2 с.</summary>
        bool Stuck()
        {
            if (_agent.pathPending) return false;
            bool partialDone = _agent.pathStatus != NavMeshPathStatus.PathComplete && _agent.remainingDistance < 0.6f;
            if (_agent.velocity.sqrMagnitude < 0.02f) _stuck += Time.deltaTime;
            else _stuck = 0f;
            if (partialDone || _stuck > 2f)
            {
                _stuck = 0f;
                // третий раз подряд на одном месте — сама не выберется
                if ((transform.position - _stuckAt).sqrMagnitude < 1f) _stuckCount++;
                else { _stuckCount = 1; _stuckAt = transform.position; }
                if (_stuckCount >= 3 && Current == State.Patrol) Unstick();
                return true;
            }
            return false;
        }

        float _caughtAt;

        void Catch()
        {
            _caughtAt = Time.time;
            SetState(State.Caught);
            _agent.isStopped = true;
            CaughtPlayer?.Invoke(this);
        }

        bool Arrived() => !_agent.pathPending && _agent.remainingDistance <= Mathf.Max(0.4f, _agent.stoppingDistance);

        void SetState(State s)
        {
            if (Current == s) return;
            if (s != State.Investigate) _posingFor = null;
            Current = s;
            StateChanged?.Invoke(this, s);
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, sightRadius);
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, hearRunRadius);
        }
#endif
    }
}
