using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Audio;
using Jam.Interaction;
using Jam.Player;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Gameplay
{
    /// <summary>
    /// Акт 2 — призрак. После перелома игрок ходит по потолку, в дом входит живой фотограф и ставит камеры.
    /// Двигаешься — камеры тебя не видят. Замер в кадре открытой камеры — растёт «проявленность»; дошла до конца —
    /// вспышка и рестарт акта. Цель: напугать фотографа (шкала испуга вверху: свечи гаснут у него на глазах, хлопают двери, призрак висит над ним, Маргарет шепчет). Шкала полна или все свечи погасли — фотограф бежит,
    /// роняет пластину — финал и титры.
    /// </summary>
    public class Act2Director : MonoBehaviour
    {
        public static Act2Director Instance { get; private set; }

        [Header("Ссылки")]
        public FirstPersonController player;
        public Interactor interactor;
        public TwistSequence twist;
        public LivingPhotographer photographer;
        public PlateViewer viewer;
        public EndCredits credits;
        public CanvasGroup whiteFlash;
        public GhostMeterUI meter;
        [Tooltip("Пластина, которую роняет фотограф (P_Final_Act2, иначе P_Final). Пусто — сразу титры.")]
        public Texture finalPlate;

        [Header("Проявленность")]
        [Tooltip("За сколько секунд неподвижности в кадре призрак проявляется целиком.")]
        public float fillSeconds = 2.5f;
        [Tooltip("Как быстро «проявленность» уходит, когда в кадре тебя нет, в долях за секунду.")]
        public float decayPerSecond = 0.15f;
        [Tooltip("Скорость, ниже которой призрак считается неподвижным, м/с.")]
        public float stillSpeed = 0.25f;

        [Header("Мысли призрака (не инструкции)")]
        public string thoughtStart = "Дом висит над головой… Это я — по ту сторону стекла.";
        public string thoughtPhotographer = "Кто-то пришёл. Со штативом. Он снимает при свечах — как я тогда.";
        public string thoughtSeen = "Его камера смотрит прямо на меня. Если замру — проявлюсь на его пластине.";
        public string thoughtFirstCandle = "Огонь умирает рядом со мной. Как рядом с ней.";
        public string thoughtRelit = "Он упрямый. Я тоже был упрямым.";
        public string thoughtDoor = "Он боится этого дома. Как и я когда-то.";
        public string thoughtStuck = "Пока горит свет, он будет снимать. А рядом со мной огонь не живёт.";
        public string thoughtFlash = "Магний. Как у меня тогда… От вспышки не убежать — только спрятаться за стеной.";

        [Header("Показать без слов")]
        [Tooltip("Где-то вдали сама хлопает дверь — фотограф уходит на звук. Через столько секунд после первого перезажжённого канделябра.")]
        public float doorDemoAfterRelit = 4f;
        [Tooltip("…или через столько секунд после начала акта, если он так ничего и не перезажёг.")]
        public float doorDemoFallback = 50f;
        [Tooltip("Ни одного погашенного канделябра столько секунд — мысль-подсказка.")]
        public float stuckSeconds = 90f;
        public string thoughtRestart = "Ещё мгновение — и я остался бы на его пластине. Навсегда.";
        public string thoughtFlee = "Последняя свеча. Беги отсюда. Беги, пока она тебя не поставила к стойке.";
        public string thoughtFinale = "Он выронил пластину. Мы все на ней — и я тоже. Резкие. Неподвижные.";

        [ShowNativeProperty] public bool Active { get; private set; }
        [ShowNativeProperty] public float Exposure01 { get; private set; }

        public event Action Started;
        public event Action Finished;

        bool _onCeiling, _restarting, _finished, _seenOnce, _candleOnce, _doorShown;
        FearMeterUI _fearUi;
        float _beganAt;
        Vector3 _startPos;
        Quaternion _startRot;
        InputAction _debugJump;

        void Awake()
        {
            Instance = this;
            _debugJump = new InputAction("Act2Debug", InputActionType.Button, "<Keyboard>/f9");
        }

        void OnEnable() => _debugJump.Enable();
        void OnDisable() => _debugJump.Disable();

        void Start()
        {
            if (twist) twist.Act2Started += Begin;
            Candelabra.AnyExtinguished += OnCandleOut;
            Door.AnyDoor += OnDoor;
            if (photographer) { photographer.Escaped += OnEscaped; photographer.Relit += OnRelit; }
        }

        bool _relitOnce;
        void OnRelit(LivingPhotographer p, Candelabra c)
        {
            if (!Active || _relitOnce) return;
            _relitOnce = true;
            Hint("a2_relit", thoughtRelit);
            DOVirtual.DelayedCall(doorDemoAfterRelit, ShowDoorDemo).SetLink(gameObject);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (twist) twist.Act2Started -= Begin;
            Candelabra.AnyExtinguished -= OnCandleOut;
            Door.AnyDoor -= OnDoor;
            if (photographer) { photographer.Escaped -= OnEscaped; photographer.Relit -= OnRelit; }
            Candelabra.GhostMode = false;
            Door.GhostMode = false;
            _debugJump.Dispose();
        }

        // ---------------- переход ----------------

        /// <summary>Перенести игрока на потолок (зовёт TwistSequence во время белой вспышки).</summary>
        public void EnterCeiling()
        {
            if (_onCeiling || !player) return;
            _onCeiling = true;
            DisableAct1Things();
            IgnoreFurnitureCollisions();

            var cc = player.GetComponent<CharacterController>();
            float h = cc ? cc.height : 1.8f;
            Vector3 p = player.transform.position;
            float ceilY = p.y + 3f;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.up, out var hit, 6f, ~0, QueryTriggerInteraction.Ignore))
                ceilY = hit.point.y;
            else Debug.LogWarning("[Act2] Над игроком нет потолка — ставлю на 3 м выше.");

            player.SetCeiling(true);
            player.Teleport(new Vector3(p.x, ceilY - h - 0.03f, p.z), player.transform.rotation);
            _startPos = player.transform.position;
            _startRot = player.transform.rotation;
        }

        /// <summary>Акт 2 пошёл (после переворота картинки).</summary>
        public void Begin()
        {
            if (Active) return;
            if (!_onCeiling) EnterCeiling();
            Active = true;
            Candelabra.GhostMode = true;
            Door.GhostMode = true;
            if (interactor) interactor.range = Mathf.Max(interactor.range, 3.5f); // свечи теперь далеко внизу
            foreach (var c in Candelabra.All.ToList()) c.Relight();
            Exposure01 = 0f;
            // призрак видит призраков: Маргарет теперь видна без обскуры и идёт рядом
            int ghostLayer = LayerMask.NameToLayer("Ghost");
            var eyes = player ? player.GetComponentsInChildren<Camera>(true).FirstOrDefault(c => c.enabled && c.targetTexture == null) : null;
            if (!eyes) eyes = Camera.main;
            if (eyes && ghostLayer >= 0) eyes.cullingMask |= 1 << ghostLayer;
            var mg = FindAnyObjectByType<MargaretAI>(FindObjectsInactive.Include);
            if (mg && !mg.Companion) mg.BecomeCompanion();
            Poltergeist.InstallAll(); // шкафы, стулья, посуду теперь можно ронять
            // шкала испуга фотографа — цель акта: напугать его так, чтобы сбежал
            if (!_fearUi && photographer)
            {
                var canvas = PlateCounterUI.Instance ? PlateCounterUI.Instance.GetComponentInParent<Canvas>() : FindAnyObjectByType<Canvas>();
                if (canvas) canvas = canvas.rootCanvas;
                _fearUi = FearMeterUI.Create(canvas, photographer);
            }
            Hint("a2_start", thoughtStart);
            _beganAt = Time.time;
            if (photographer) photographer.Begin();
            DOVirtual.DelayedCall(7f, () => { if (Active) Hint("a2_photographer", thoughtPhotographer); }).SetLink(gameObject);
            DOVirtual.DelayedCall(doorDemoFallback, ShowDoorDemo).SetLink(gameObject);
            Started?.Invoke();
        }

        void DisableAct1Things()
        {
            // руки призрака пусты: ни свечи, ни обскуры, ни магния, ни камеры
            var hand = FindAnyObjectByType<HandObscura>();
            if (hand) { hand.SetHeld(false); hand.enabled = false; }
            var flash = FindAnyObjectByType<MagnesiumFlash>();
            if (flash) flash.enabled = false;
            var candle = FindAnyObjectByType<HandCandle>();
            if (candle)
            {
                candle.enabled = false;
                if (candle.flameVisual) candle.flameVisual.SetActive(false);
            }
            var portable = FindAnyObjectByType<PortableCamera>();
            if (portable) { portable.ReturnToPlayer(); portable.enabled = false; }
            var hud = FindAnyObjectByType<ExposureHUD>();
            if (hud) { if (hud.label) hud.label.text = ""; hud.enabled = false; }
            var flashUi = FindAnyObjectByType<FlashCounterUI>();
            if (flashUi) flashUi.gameObject.SetActive(false);
            if (PlateCounterUI.Instance && PlateCounterUI.Instance.counter) PlateCounterUI.Instance.counter.enabled = false;

            if (player)
            {
                foreach (var l in player.GetComponentsInChildren<Light>(true))
                    if (l.name == "CandleLight") l.enabled = false;
                foreach (var t in player.GetComponentsInChildren<Transform>(true))
                    if (t.name.EndsWith("_InHand")) t.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// На потолке мешает всё, что свисает вниз: перемычки, рамки дверей, шкаф, часы.
        /// Призрак проходит сквозь мебель и двери — упирается только в стены и ходит по потолку.
        /// </summary>
        void IgnoreFurnitureCollisions()
        {
            var cc = player ? player.GetComponent<CharacterController>() : null;
            if (!cc) return;
            int n = 0;
            foreach (var col in FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (!col || col == cc || col.isTrigger) continue;
                if (col.GetComponentInParent<FirstPersonController>()) continue;
                var b = col.bounds;
                bool walls = col.name == "Walls";
                bool ceiling = b.min.y > 2.6f && b.size.y < 0.25f;
                bool blocker = col.name.StartsWith("FrontDoorBlocker");
                if (walls || ceiling || blocker) continue;
                Physics.IgnoreCollision(cc, col, true);
                n++;
            }
            Debug.Log($"[Act2] Призрак проходит сквозь {n} предметов.");
        }

        // ---------------- игра ----------------

        void Update()
        {
            if (_debugJump.WasPressedThisFrame() && !Active && Application.isEditor) DebugJump();
            if (!Active || _restarting || _finished || !player) return;

            Vector3 body = player.transform.position + Vector3.up * 0.9f;
            bool seen = PhotographerCamera.All.Any(c => c && c.Sees(body));
            bool still = player.CurrentSpeed < stillSpeed;

            if (seen && still) Exposure01 += Time.deltaTime / Mathf.Max(0.1f, fillSeconds);
            else if (!seen) Exposure01 -= decayPerSecond * Time.deltaTime;
            Exposure01 = Mathf.Clamp01(Exposure01);
            if (meter) meter.Set(Exposure01, seen);

            if (seen && !_seenOnce) { _seenOnce = true; Hint("a2_seen", thoughtSeen); }
            if (!_candleOnce && Time.time - _beganAt > stuckSeconds) Thoughts.Once("stuck_a2", thoughtStuck);
            if (Exposure01 >= 1f) RestartAsync(destroyCancellationToken).Forget();
        }

        void OnCandleOut(Candelabra c)
        {
            if (!Active || _finished) return;
            if (photographer) photographer.HearCandle(c);
            if (!_candleOnce) { _candleOnce = true; Hint("a2_first_candle", thoughtFirstCandle); }
            if (Candelabra.All.Where(x => x && x.HasFlame).All(x => !x.IsLit))
            {
                Hint("a2_flee", thoughtFlee);
                if (photographer) photographer.Flee();
                else OnEscaped(null);
            }
        }

        void OnDoor(Door d, string kind)
        {
            if (!Active || _finished || !d || !photographer) return;
            // хлопок пугает сильнее, чем скрип открывающейся двери; одна и та же дверь подряд — слабее
            if (kind == "door_slam") photographer.HearNoise(d.transform.position, 0.5f, d);
            else if (kind == "door_open") photographer.HearNoise(d.transform.position, 0.2f, d);
        }

        void OnEscaped(LivingPhotographer p)
        {
            if (_finished) return;
            FinaleAsync(destroyCancellationToken).Forget();
        }

        async UniTaskVoid RestartAsync(CancellationToken ct)
        {
            _restarting = true;
            try
            {
                Lock(true);
                JamAudio.Play("flash");
                if (whiteFlash) { whiteFlash.DOKill(); whiteFlash.alpha = 1f; }
                if (meter) meter.Clear();
                await UniTask.Delay(900, cancellationToken: ct);

                Exposure01 = 0f;
                player.Teleport(_startPos, _startRot);
                foreach (var c in Candelabra.All.ToList()) c.Relight();
                Poltergeist.ResetAll(); // упавшее — снова на местах
                if (photographer) { photographer.Stop(); photographer.Begin(); }

                if (whiteFlash) await Fade(whiteFlash, 0f, 0.8f, ct);
                Hint("a2_restart", thoughtRestart);
            }
            catch (OperationCanceledException) { }
            finally
            {
                Lock(false);
                _restarting = false;
            }
        }

        async UniTaskVoid FinaleAsync(CancellationToken ct)
        {
            _finished = true;
            Active = false;
            if (_fearUi) _fearUi.Hide();
            try
            {
                if (meter) meter.Clear();
                foreach (var c in PhotographerCamera.All.ToList()) if (c) c.Remove();
                await UniTask.Delay(1500, cancellationToken: ct);
                Hint("a2_finale", thoughtFinale);
                await UniTask.Delay(2500, cancellationToken: ct);
                // пластина, которую он уронил: картинка художников, иначе последняя проявленная из альбома
                Texture plate = finalPlate;
                if (!plate)
                {
                    var album = FindAnyObjectByType<FamilyAlbum>();
                    if (album) plate = album.LastDeveloped;
                }
                if (viewer && plate) await viewer.ShowTextureAsync(plate, sting: true, flip: true);
                Lock(true);
                Finished?.Invoke();
                if (credits) await credits.ShowAsync();
            }
            catch (OperationCanceledException) { }
        }

        // ---------------- отладка ----------------

        /// <summary>F9 в редакторе: сразу в акт 2 (без шести сцен).</summary>
        [Button("Сразу в акт 2 (тест)")]
        public void DebugJump()
        {
            if (!Application.isPlaying || Active) return;
            var margaret = FindAnyObjectByType<MargaretAI>(FindObjectsInactive.Include);
            if (margaret) margaret.BecomeCompanion();
            EnterCeiling();
            var cam = player ? player.GetComponentInChildren<Camera>() : null;
            if (cam) cam.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
            if (player)
            {
                player.LookInvert = new Vector2(-1f, -1f);
                player.StrafeInvert = -1f;
            }
            Begin();
        }

        void Lock(bool locked)
        {
            if (player) player.InputLocked = locked;
            if (interactor) interactor.Locked = locked;
        }

        static void Hint(string id, string text) => Thoughts.Say(id, text);

        /// <summary>Фотограф попал магнием: призрак резко проявляется, даже если двигался.</summary>
        public void FlashHit(float amount)
        {
            if (!Active || _restarting || _finished) return;
            Exposure01 = Mathf.Clamp01(Exposure01 + amount);
            if (meter) meter.Set(Exposure01, true);
            if (whiteFlash)
            {
                whiteFlash.DOKill();
                whiteFlash.alpha = 0.85f;
                whiteFlash.DOFade(0f, 0.6f).SetLink(gameObject);
            }
            Thoughts.Once("a2_flash", thoughtFlash);
        }

        /// <summary>
        /// Сценка без слов: где-то вдали сама хлопает дверь, фотограф бросает дело и идёт на звук.
        /// Игрок видит, что дверью его можно увести, — дальше хлопает сам.
        /// </summary>
        void ShowDoorDemo()
        {
            if (_doorShown || !Active || _finished || !photographer) return;
            Vector3 ph = photographer.transform.position;
            Door best = null;
            float bestScore = float.MinValue;
            foreach (var d in FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                if (!d || !d.isActiveAndEnabled) continue;
                float dist = Vector3.Distance(d.transform.position, ph);
                if (dist < 4f || dist > 15f) continue; // слышно, но не под носом
                // открытая дверь в другой комнате — лучше всего: хлопок и уход фотографа видно и слышно
                float score = (d.IsOpen ? 10f : 0f) + (Candelabra.SameRoom(ph + Vector3.up, d.transform.position + Vector3.up) ? 0f : 5f) - Mathf.Abs(dist - 8f);
                if (score > bestScore) { bestScore = score; best = d; }
            }
            if (!best) return;
            _doorShown = true;
            best.Slam(); // Door.AnyDoor → OnDoor → фотограф идёт на звук
            DOVirtual.DelayedCall(2.5f, () => { if (Active) Hint("a2_door", thoughtDoor); }).SetLink(gameObject);
        }

        static async UniTask Fade(CanvasGroup g, float to, float time, CancellationToken ct)
        {
            var t = g.DOFade(to, time);
            while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, ct);
        }
    }
}
