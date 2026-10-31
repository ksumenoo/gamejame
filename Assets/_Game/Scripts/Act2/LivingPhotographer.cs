using System;
using System.Collections.Generic;
using DG.Tweening;
using Jam.Audio;
using Jam.Player;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.AI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Акт 2: живой фотограф. Ходит по полу между точками обзора (там, где «видел шлейфы»),
    /// ставит штатив и снимает. На шум (погасшая свеча, хлопнувшая дверь) идёт проверить и ставит камеру туда.
    /// Все свечи погасли — бежит к выходу (Escaped).
    /// Спрайты художников: C_Photographer_Stand и C_Photographer_Walk_1…4 — подставляет LevelBuilder; нет — заглушка.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class LivingPhotographer : MonoBehaviour
    {
        public enum State { Waiting, Walking, Setting, Watching, Relighting, Fleeing, Gone }

        [Header("Маршрут")]
        [Tooltip("Где он снимает (точки обзора сцен).")]
        public List<Transform> points = new();
        public Transform spawn;
        public Transform exit;

        [Header("Камеры")]
        public List<PhotographerCamera> tripods = new();
        [Tooltip("Сколько секунд он стоит у поставленной камеры, прежде чем идти дальше.")]
        public float watchSeconds = 8f;
        public float setupSeconds = 1.5f;
        [Tooltip("Сколько секунд он зажигает погасшую свечу. Пока призрак рядом со свечой, фитиль не берётся — время стоит.")]
        public float relightSeconds = 5f;
        [Tooltip("С какой вероятностью он идёт снимать к горящей свече, а не к точке сцены (он снимает при свете).")]
        [Range(0f, 1f)] public float candleBias = 0.7f;
        [Tooltip("Ближе этого к уже стоящей камере вторую не ставит — переставляет ту.")]
        public float tripodSpacing = 2.5f;
        [Tooltip("Во что не ставить штатив (стены, мебель).")]
        public LayerMask tripodBlockers = ~0;

        [Header("Магний")]
        [Tooltip("Замах перед вспышкой: шипит магний, лампа разгорается — успеть уйти из виду, с.")]
        public float flashWindup = 1.2f;
        [Tooltip("Между вспышками не меньше, с.")]
        public float flashCooldown = 8f;
        [Tooltip("Дальше этого вспышка призрака не берёт, м.")]
        public float flashRange = 9f;
        [Tooltip("Призрак в этом угле от того, куда он целился, — попал, градусы (половина конуса).")]
        [Range(10f, 90f)] public float flashHalfAngle = 55f;
        [Tooltip("Сколько «проявленности» даёт попадание (1 — целиком). Вспышка ловит и на бегу.")]
        [Range(0f, 1f)] public float flashExposure = 0.5f;
        [Tooltip("Магний ограничен (иначе — бесконечный, только перезарядка flashCooldown).")]
        public bool limitedFlash = false;
        [Tooltip("Если ограничен: сколько вспышек на попытку акта. Кончился — щёлкает впустую.")]
        [Min(0)] public int flashCharges = 3;

        /// <summary>Сколько вспышек у него осталось.</summary>
        public int FlashesLeft { get; private set; }

        [Header("Скорость")]
        public float walkSpeed = 1.3f;
        public float hurrySpeed = 2.2f;
        public float fleeSpeed = 3.8f;

        [Header("Вид")]
        public Renderer visual;
        public Texture2D standTexture;
        public Texture2D[] walkFrames;
        public float frameTime = 0.16f;

        [ShowNativeProperty] public State Current { get; private set; } = State.Waiting;
        /// <summary>Испуг 0..1. Дошёл до 1 — бросает всё и бежит из дома (победа).</summary>
        public float Fear { get; private set; }
        public event Action<float> FearChanged;

        [Header("Испуг (0..1)")]
        [Tooltip("Свеча погасла у него на глазах (в той же комнате, перед ним).")]
        public float candleSeenFear = 0.11f;
        [Tooltip("Свеча погасла где-то ещё.")]
        public float candleUnseenFear = 0.03f;
        [Tooltip("Хлопнула дверь рядом (вплотную — столько, на краю радиуса — меньше).")]
        public float doorFear = 0.09f;
        public float doorRadius = 9f;
        [Tooltip("Тот же источник (дверь, свеча) снова в течение этого времени — пугает слабее, с.")]
        public float repeatWindow = 15f;
        [Range(0f, 1f)] public float repeatFactor = 0.3f;
        [Tooltip("Призрак висит прямо над ним и не двигается — холод по спине, в секунду.")]
        public float hoverFear = 0.045f;
        public float hoverRadius = 1.3f;
        [Tooltip("Маргарет рядом с ним — шёпот за спиной, в секунду.")]
        public float margaretFear = 0.02f;
        public float margaretRadius = 3.5f;
        [Tooltip("Общий множитель всех испугов: меньше — дольше пугать. 0,5 ≈ вдвое больше действий.")]
        [Range(0.1f, 2f)] public float fearGainScale = 0.5f;
        [Tooltip("Испуг сам уходит, если его не пугать, в секунду (0,001 = 6 % в минуту).")]
        public float fearDecayPerSec = 0.001f;

        readonly Dictionary<UnityEngine.Object, float> _lastScare = new();
        MargaretAI _margaret;
        float _whisperT;

        public event Action<LivingPhotographer> Escaped;
        /// <summary>Он снова зажёг свечу, которую погасил призрак.</summary>
        public event Action<LivingPhotographer, Candelabra> Relit;

        NavMeshAgent _agent;
        FirstPersonController _ghost;
        CharacterController _ghostBody;
        Light _flashLight;
        float _flashT = -1f, _nextFlash;
        Vector3 _flashAim;
        bool _emptyClicked;
        float _timer, _frameT;
        int _frame, _nextTripod, _nextPoint;
        Candelabra _relight;
        Vector3 _setupFacing, _progressPos;
        float _progressTime;
        Material _mat;
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.enabled = false;
            if (visual) _mat = visual.material; // своя копия — листаем кадры
            foreach (var t in tripods) if (t) t.gameObject.SetActive(false);
            if (visual) visual.enabled = false;
        }

        /// <summary>Входит в дом (начало акта 2 и после рестарта).</summary>
        public void Begin()
        {
            CancelFlash();
            _nextFlash = Time.time + 10f; // дать оглядеться после входа и после рестарта
            FlashesLeft = flashCharges;
            _emptyClicked = false;
            Fear = 0f;
            _lastScare.Clear();
            FearChanged?.Invoke(0f);
            _relight = null;
            foreach (var t in tripods) if (t) t.Remove();
            _nextTripod = 0;
            _nextPoint = UnityEngine.Random.Range(0, Mathf.Max(1, points.Count));
            gameObject.SetActive(true);
            if (visual) visual.enabled = true;
            _agent.enabled = true;
            Vector3 start = spawn ? spawn.position : transform.position;
            if (NavMesh.SamplePosition(start, out var hit, 3f, NavMesh.AllAreas)) _agent.Warp(hit.position);
            else Debug.LogWarning("[Photographer] Нет NavMesh у входа.", this);
            GoToNextPoint();
        }

        /// <summary>Шум в доме: идёт посмотреть и ставит туда камеру.</summary>
        public void HearNoise(Vector3 position, float fear, UnityEngine.Object source = null)
        {
            if (Current == State.Fleeing || Current == State.Gone || !_agent.isOnNavMesh) return;
            float d = Flat(position - transform.position).magnitude;
            if (d < doorRadius) Scare(doorFear * Mathf.Lerp(1f, 0.35f, d / doorRadius) * Mathf.Max(0.5f, fear * 2f), source, position);
            TryFlash();
            _relight = null; // отвлёкся — погасшая свеча так и стоит тёмной
            // встаёт в паре метров от источника, лицом к нему
            Vector3 from = transform.position;
            Vector3 dir = position - from;
            dir.y = 0f;
            Vector3 target = dir.magnitude > 2.5f ? position - dir.normalized * 2.5f : from;
            _setupFacing = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            Walk(target, hurrySpeed);
        }

        /// <summary>Погасла свеча: идёт к ней и зажигает снова, потом ставит туда камеру.</summary>
        public void HearCandle(Candelabra c)
        {
            if (!c) return;
            if (Current == State.Fleeing || Current == State.Gone || !_agent.isOnNavMesh) return;
            Scare(InSight(c.transform.position + Vector3.up) ? candleSeenFear : candleUnseenFear, c, c.transform.position);
            TryFlash();
            if (_relight && _relight != c && !_relight.IsLit) return; // уже идёт к другой — сначала ту
            _relight = c;
            Vector3 dir = c.transform.position - transform.position;
            dir.y = 0f;
            _setupFacing = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            Walk(c.transform.position, hurrySpeed);
        }

        /// <summary>Все свечи погасли — бежит прочь из дома.</summary>
        public void Flee()
        {
            if (Current == State.Fleeing || Current == State.Gone) return;
            Current = State.Fleeing;
            if (!_agent.isOnNavMesh) { Leave(); return; }
            _agent.isStopped = false;
            _agent.speed = fleeSpeed;
            Vector3 dest = exit ? exit.position : (spawn ? spawn.position : transform.position);
            if (NavMesh.SamplePosition(dest, out var hit, 3f, NavMesh.AllAreas)) _agent.SetDestination(hit.position);
            else Leave();
        }

        public void Stop()
        {
            CancelFlash();
            if (_agent.enabled && _agent.isOnNavMesh) _agent.ResetPath();
            _agent.enabled = false;
            foreach (var t in tripods) if (t) t.Remove();
            if (visual) visual.enabled = false;
            Current = State.Waiting;
        }

        void Update()
        {
            if (!_agent.enabled || !_agent.isOnNavMesh) return;
            Animate();
            UpdateFear();
            if (Current == State.Fleeing || Current == State.Gone) { if (Current == State.Fleeing && Arrived()) Leave(); return; }
            if (UpdateFlash()) return; // замах — всё остальное ждёт
            switch (Current)
            {
                case State.Waiting:
                    // некуда было идти (точка вне сетки) — пробуем следующую
                    _timer -= Time.deltaTime;
                    if (_timer <= -2f) { _timer = 0f; GoToNextPoint(); }
                    break;
                case State.Walking:
                    if (Arrived() || (_relight && Flat(_relight.transform.position - transform.position).magnitude < 1.3f))
                    {
                        _agent.ResetPath();
                        if (_relight && !_relight.IsLit) { Current = State.Relighting; _timer = relightSeconds; }
                        else { _relight = null; Current = State.Setting; _timer = setupSeconds; }
                        break;
                    }
                    // застрял — 6 с без продвижения: ставит камеру там, где стоит
                    if ((transform.position - _progressPos).sqrMagnitude > 0.25f) { _progressPos = transform.position; _progressTime = Time.time; }
                    else if (Time.time - _progressTime > 6f) { _progressTime = Time.time; Current = State.Setting; _timer = setupSeconds; _agent.ResetPath(); }
                    break;
                case State.Setting:
                    _timer -= Time.deltaTime;
                    if (_timer <= 0f) { PlaceTripod(); Current = State.Watching; _timer = watchSeconds; }
                    break;
                case State.Watching:
                    _timer -= Time.deltaTime;
                    if (_timer <= 0f) GoToNextPoint();
                    break;
                case State.Relighting:
                    // рядом с призраком огонь не живёт: фитиль не берётся, пока тот не уйдёт, — и он бьёт магнием
                    if (_relight && GhostNear(_relight.transform.position, _relight.ghostRadius * 1.3f)) TryFlash();
                    else _timer -= Time.deltaTime;
                    if (_relight)
                    {
                        Vector3 look = Flat(_relight.transform.position - transform.position);
                        if (look.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                    }
                    if (_timer <= 0f)
                    {
                        var c = _relight;
                        _relight = null;
                        if (c && !c.IsLit) { c.Relight(); Relit?.Invoke(this, c); }
                        // пока возился, погасла ещё одна — сразу к ней
                        if (GoRelightAny(1f)) break;
                        // отходит на пару шагов и ставит камеру на эту свечу: следующего, кто её погасит, снимет
                        if (c)
                        {
                            Vector3 away = Flat(transform.position - c.transform.position);
                            away = away.sqrMagnitude > 1e-4f ? away.normalized : -transform.forward;
                            _setupFacing = -away;
                            Walk(c.transform.position + away * 2.6f, walkSpeed);
                            if (Current == State.Walking) break;
                        }
                        Current = State.Setting;
                        _timer = setupSeconds;
                    }
                    break;
                case State.Fleeing:
                    if (Arrived()) Leave();
                    break;
            }
        }

        bool Arrived() => !_agent.pathPending && _agent.remainingDistance <= Mathf.Max(0.5f, _agent.stoppingDistance);

        /// <summary>С вероятностью chance идёт к ближайшей погасшей свече. true — пошёл.</summary>
        bool GoRelightAny(float chance)
        {
            Candelabra best = null;
            float bestD = float.MaxValue;
            foreach (var c in Candelabra.All)
            {
                if (!c || !c.HasFlame || c.IsLit) continue;
                float d = Flat(c.transform.position - transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (!best || UnityEngine.Random.value > chance) return false;
            _relight = best;
            Vector3 dir = Flat(best.transform.position - transform.position);
            _setupFacing = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            Walk(best.transform.position, hurrySpeed);
            return Current == State.Walking;
        }

        void GoToNextPoint()
        {
            // помнит о погасших свечах: в половине случаев сначала идёт зажечь
            if (GoRelightAny(0.5f)) return;
            // он снимает при свете: чаще всего идёт к горящей свече и ставит камеру на неё
            var lit = Candelabra.All.FindAll(c => c && c.HasFlame && c.IsLit);
            if (lit.Count > 0 && (points.Count == 0 || UnityEngine.Random.value < candleBias))
            {
                var c = lit[UnityEngine.Random.Range(0, lit.Count)];
                Vector3 away = Flat(transform.position - c.transform.position);
                if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
                away.Normalize();
                _setupFacing = -away;
                Walk(c.transform.position + away * 2.6f, Mathf.Min(hurrySpeed, walkSpeed + Fear * 1.2f));
                return;
            }
            if (points.Count == 0) { Current = State.Waiting; return; }
            var p = points[_nextPoint % points.Count];
            _nextPoint += 1 + UnityEngine.Random.Range(0, Mathf.Max(1, points.Count - 1)); // не по кругу, вразнобой
            if (!p) return;
            _setupFacing = p.forward;
            // чем больше напуган, тем быстрее и дёрганее ходит
            Walk(p.position, Mathf.Min(hurrySpeed, walkSpeed + Fear * 1.2f));
        }

        void Walk(Vector3 target, float speed)
        {
            if (!_agent.isOnNavMesh) return;
            if (!NavMesh.SamplePosition(target, out var hit, 2f, NavMesh.AllAreas)) { Current = State.Waiting; return; }
            _agent.isStopped = false;
            _agent.speed = speed;
            _agent.SetDestination(hit.position);
            Current = State.Walking;
            _progressPos = transform.position;
            _progressTime = Time.time;
        }

        void PlaceTripod()
        {
            if (tripods.Count == 0) return;
            // камера уже стоит рядом — переставляет её, а не ставит вторую впритык
            PhotographerCamera t = null;
            foreach (var x in tripods)
                if (x && x.gameObject.activeSelf && Flat(x.transform.position - transform.position).magnitude < tripodSpacing) { t = x; break; }
            if (!t)
            {
                t = tripods[_nextTripod % tripods.Count]; // иначе самую старую забирает и ставит здесь
                _nextTripod++;
            }
            if (!t) return;
            Vector3 f = _setupFacing;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = transform.forward;
            f.Normalize();
            // штатив в шаге перед собой (сам стоит за ним, как фотограф), но не в стене и не в мебели
            float dist = 1.0f;
            Vector3 chest = transform.position + Vector3.up * 1.2f;
            if (Physics.SphereCast(chest, 0.25f, f, out var hit, dist + 0.4f, tripodBlockers, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(0.5f, hit.distance - 0.4f);
            Vector3 spot = transform.position + f * dist;
            if (NavMesh.SamplePosition(spot, out var nh, 0.6f, NavMesh.AllAreas)) spot = nh.position;
            t.Place(spot, f);
            transform.rotation = Quaternion.LookRotation(f, Vector3.up);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        // ---------------- испуг ----------------

        /// <summary>Видит ли он точку: та же комната, недалеко и примерно перед ним.</summary>
        public bool InSight(Vector3 p)
        {
            Vector3 to = Flat(p - transform.position);
            return to.magnitude < 10f && Vector3.Angle(transform.forward, to) < 75f && Candelabra.SameRoom(Eyes, p);
        }

        /// <summary>
        /// Грохот в доме (упал шкаф, разбилась посуда): испуг сразу, он оборачивается, бьёт магнием, если видит призрака,
        /// и идёт посмотреть, что упало.
        /// </summary>
        public void Startle(Vector3 position, float amount, UnityEngine.Object source = null)
        {
            if (Current == State.Fleeing || Current == State.Gone || !_agent.isOnNavMesh) return;
            Scare(amount, source, position);
            if (Current == State.Fleeing) return;
            TryFlash();
            _relight = null;
            Vector3 dir = Flat(position - transform.position);
            Vector3 target = dir.magnitude > 2.5f ? position - dir.normalized * 2.5f : transform.position;
            _setupFacing = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            Walk(target, hurrySpeed);
        }

        /// <summary>Напугать. source — чтобы одна и та же дверь/свеча раз за разом пугала слабее.</summary>
        public void Scare(float amount, UnityEngine.Object source = null, Vector3? from = null)
        {
            if (amount <= 0f || Current == State.Fleeing || Current == State.Gone) return;
            if (source)
            {
                if (_lastScare.TryGetValue(source, out var t) && Time.time - t < repeatWindow) amount *= repeatFactor;
                _lastScare[source] = Time.time;
            }
            amount *= fearGainScale;
            bool big = amount >= 0.05f;
            Fear = Mathf.Clamp01(Fear + amount);
            FearChanged?.Invoke(Fear);
            if (big)
            {
                // вздрогнул: дёрнулся на месте и обернулся на источник
                if (visual) visual.transform.DOPunchPosition(Vector3.up * 0.08f, 0.35f, 8, 0.5f).SetLink(gameObject);
                if (from.HasValue)
                {
                    Vector3 look = Flat(from.Value - transform.position);
                    if (look.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                }
            }
            if (Fear >= 1f) Flee();
        }

        void UpdateFear()
        {
            if (Current == State.Fleeing || Current == State.Gone || Current == State.Waiting) return;
            float dt = Time.deltaTime;
            float before = Fear;
            Fear = Mathf.Max(0f, Fear - fearDecayPerSec * dt);

            // призрак завис прямо над ним
            if (FindGhost() && _ghost.CurrentSpeed < 0.3f && Flat(_ghost.transform.position - transform.position).magnitude < hoverRadius)
                Fear += hoverFear * fearGainScale * dt;

            // Маргарет рядом — шёпот за спиной (она на нашей стороне)
            if (!_margaret) _margaret = FindAnyObjectByType<MargaretAI>();
            if (_margaret && _margaret.isActiveAndEnabled && _margaret.Companion
                && Flat(_margaret.transform.position - transform.position).magnitude < margaretRadius)
            {
                Fear += margaretFear * fearGainScale * dt;
                _whisperT -= dt;
                if (_whisperT <= 0f) { _whisperT = 4f; JamAudio.Play("margaret_whisper", transform.position + Vector3.up * 1.5f); }
            }

            Fear = Mathf.Clamp01(Fear);
            if (!Mathf.Approximately(before, Fear)) FearChanged?.Invoke(Fear);
            if (Fear >= 1f) Flee();
        }

        // ---------------- магний ----------------

        Vector3 GhostPoint => _ghostBody ? _ghostBody.bounds.center : _ghost.transform.position + Vector3.up * 0.9f;
        Vector3 Eyes => transform.position + Vector3.up * 1.6f;

        bool FindGhost()
        {
            if (!_ghost)
            {
                _ghost = FindAnyObjectByType<FirstPersonController>();
                if (_ghost) _ghostBody = _ghost.GetComponent<CharacterController>();
            }
            return _ghost && _ghost.Ceiling;
        }

        bool GhostNear(Vector3 p, float radius) => FindGhost() && Flat(_ghost.transform.position - p).magnitude < radius;

        /// <summary>Видит ли он призрака: близко и не за стеной.</summary>
        bool CanSeeGhost()
        {
            if (!FindGhost()) return false;
            Vector3 g = GhostPoint;
            return (g - Eyes).magnitude <= flashRange && Candelabra.SameRoom(Eyes, g);
        }

        /// <summary>Призрак на виду — поворачивается к нему и начинает замах.</summary>
        bool TryFlash()
        {
            if (_flashT >= 0f || Time.time < _nextFlash || Current == State.Fleeing || Current == State.Gone) return false;
            if (!CanSeeGhost()) return false;
            if (limitedFlash && FlashesLeft <= 0)
            {
                // магний кончился: один пустой щелчок — призрак слышит, что бояться больше нечего
                if (!_emptyClicked) { _emptyClicked = true; JamAudio.Play("flash_empty", transform.position); }
                return false;
            }
            FlashesLeft--;
            _flashT = flashWindup;
            _flashAim = Flat(GhostPoint - transform.position);
            if (_flashAim.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(_flashAim.normalized, Vector3.up);
            if (_agent.isOnNavMesh) _agent.isStopped = true;
            EnsureFlashLight();
            JamAudio.Play("flash_empty", transform.position); // щелчок затвора лампы, дальше шипит магний
            return true;
        }

        /// <summary>true — идёт замах, остальное поведение на паузе.</summary>
        bool UpdateFlash()
        {
            if (_flashT < 0f) return false;
            _flashT -= Time.deltaTime;
            float k = 1f - Mathf.Clamp01(_flashT / Mathf.Max(0.01f, flashWindup));
            if (_flashLight) _flashLight.intensity = Mathf.Lerp(0.2f, 2.2f, k) * UnityEngine.Random.Range(0.6f, 1.2f);
            if (_flashT > 0f) return true;

            _flashT = -1f;
            _nextFlash = Time.time + flashCooldown;
            JamAudio.Play("flash", transform.position);
            if (_flashLight)
            {
                _flashLight.DOKill();
                _flashLight.intensity = 14f;
                _flashLight.DOIntensity(0f, 0.35f).SetLink(gameObject);
            }
            // вспышка ловит и на бегу — спасает только стена или уйти из конуса
            if (CanSeeGhost())
            {
                Vector3 to = Flat(GhostPoint - transform.position);
                bool inCone = to.sqrMagnitude < 1f || Vector3.Angle(_flashAim, to) <= flashHalfAngle;
                if (inCone && Act2Director.Instance) Act2Director.Instance.FlashHit(flashExposure);
            }
            if (_agent.isOnNavMesh) _agent.isStopped = false;
            return false;
        }

        void CancelFlash()
        {
            _flashT = -1f;
            if (_flashLight) { _flashLight.DOKill(); _flashLight.intensity = 0f; }
        }

        void EnsureFlashLight()
        {
            if (_flashLight) return;
            var go = new GameObject("MagnesiumLight");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.9f, 0.3f);
            _flashLight = go.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.color = new Color(0.9f, 0.95f, 1f);
            _flashLight.range = flashRange + 2f;
            _flashLight.intensity = 0f;
            _flashLight.shadows = LightShadows.None;
        }

        void Leave()
        {
            Current = State.Gone;
            JamAudio.Play("door_slam", transform.position);
            if (_agent.enabled && _agent.isOnNavMesh) _agent.ResetPath();
            _agent.enabled = false;
            if (visual) visual.enabled = false;
            Escaped?.Invoke(this);
        }

        void Animate()
        {
            if (!_mat) return;
            bool moving = _agent.velocity.sqrMagnitude > 0.04f;
            Texture2D tex = standTexture;
            if (moving && walkFrames != null && walkFrames.Length > 0)
            {
                _frameT += Time.deltaTime;
                if (_frameT >= frameTime) { _frameT = 0f; _frame = (_frame + 1) % walkFrames.Length; }
                if (walkFrames[_frame]) tex = walkFrames[_frame];
            }
            if (tex && _mat.HasProperty(BaseMap) && _mat.GetTexture(BaseMap) != tex) _mat.SetTexture(BaseMap, tex);
        }

        void OnDestroy()
        {
            if (_mat) Destroy(_mat);
        }
    }
}
