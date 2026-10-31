using System;
using DG.Tweening;
using Jam.Audio;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Канделябр — свет в комнате.
    /// Акт 1: рядом с горящим канделябром выдержка идёт с обычной скоростью, в темноте — медленнее (CameraStation).
    ///   Маргарет гасит канделябры, мимо которых проходит. E с горящей свечой в руке — зажечь канделябр;
    ///   E с погасшей свечой — зажечь от него свечу.
    /// Акт 2 (GhostMode): рядом с призраком огонь гаснет — постоять под канделябром (или держать E).
    ///   Первый раз гаснет почти сразу: так игра показывает правило без слов.
    /// </summary>
    public class Candelabra : MonoBehaviour, IInteractable
    {
        public Light flame;
        public float baseIntensity = 0.9f;
        [Tooltip("Акт 2: сколько секунд стоять рядом (или держать E), чтобы погасить.")]
        public float holdSeconds = 1.5f;
        [Tooltip("Акт 2: призрак ближе этого по горизонтали — огонь начинает умирать, м.")]
        public float ghostRadius = 1.6f;

        /// <summary>Акт 2: огонь гаснет рядом с игроком-призраком.</summary>
        public static bool GhostMode;
        /// <summary>Все канделябры на уровне.</summary>
        public static readonly System.Collections.Generic.List<Candelabra> All = new();
        public static event Action<Candelabra> AnyExtinguished;
        /// <summary>Игрок зажёг канделябр свечой из руки (акт 1).</summary>
        public static event Action<Candelabra> LitByPlayer;

        public bool IsLit { get; private set; } = true;
        /// <summary>Настоящий канделябр с огнём (фонарь в проявочной — нет: его не гасят).</summary>
        public bool HasFlame => flame;

        static bool s_ghostTaught;
        static FirstPersonController s_player;

        Interactor _holder;
        FirstPersonController _holderBody;
        float _hold;
        bool _dying;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            GhostMode = false;
            s_ghostTaught = false;
            s_player = null;
            All.Clear();
            AnyExtinguished = null;
            LitByPlayer = null;
        }

        // ---------------- свет в комнате ----------------

        /// <summary>
        /// Светло ли в точке: горящий канделябр в той же комнате (не за стеной) ближе radius.
        /// Если рядом вообще нет канделябров с огнём — считаем, что светло (правило в этой комнате не действует).
        /// </summary>
        public static bool IsLightAt(Vector3 pos, float radius)
        {
            bool anySource = false;
            foreach (var c in All)
            {
                if (!c || !c.HasFlame) continue;
                Vector3 p = c.flame.transform.position;
                if ((p - pos).sqrMagnitude > radius * radius || !SameRoom(pos, p)) continue;
                anySource = true;
                if (c.IsLit) return true;
            }
            return !anySource;
        }

        /// <summary>Между точками нет стен (коллайдер «Walls»).</summary>
        public static bool SameRoom(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return true;
            foreach (var h in Physics.RaycastAll(a, d / len, len, ~0, QueryTriggerInteraction.Ignore))
                if (h.collider && h.collider.name == "Walls") return false;
            return true;
        }

        // ---------------- взаимодействие ----------------

        public string Prompt
        {
            get
            {
                if (!GhostMode) return IsLit ? "Зажечь свечу" : "Зажечь канделябр";
                if (!IsLit || !HasFlame) return "";
                return _hold > 0f ? $"Гаснет… {Mathf.RoundToInt(_hold / holdSeconds * 100f)}%" : "Погасить свечу (держать E)";
            }
        }

        public bool CanInteract(Interactor who)
        {
            if (GhostMode) return IsLit && HasFlame && !_holder;
            var c = who ? who.GetComponentInParent<HandCandle>() : null;
            if (!c) return false;
            if (!IsLit) return c.IsLit && HasFlame; // зажечь канделябр от свечи
            return !c.IsLit;                        // зажечь свечу от канделябра
        }

        public void Interact(Interactor who)
        {
            if (GhostMode)
            {
                _holder = who;
                _holderBody = who ? who.GetComponentInParent<FirstPersonController>() : null;
                if (flame) flame.DOKill();
                return;
            }
            var c = who.GetComponentInParent<HandCandle>();
            if (!c) return;
            if (!IsLit)
            {
                if (!c.IsLit) return;
                Relight();
                JamAudio.Play("candle_light", transform.position);
                LitByPlayer?.Invoke(this);
            }
            else c.Relight();
            if (flame) flame.transform.DOPunchScale(Vector3.one * 0.3f, 0.3f).SetLink(gameObject);
        }

        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() => All.Remove(this);

        void Start() => Flicker();

        void Update()
        {
            if (!GhostMode || !IsLit || !HasFlame) { _holder = null; _hold = 0f; return; }

            bool holding = _holder && _holder.UseHeld && ReferenceEquals(_holder.Current, this)
                           && (!_holderBody || _holderBody.CurrentSpeed < 0.3f);
            if (_holder && !holding) _holder = null;

            if (!s_player) s_player = FindAnyObjectByType<FirstPersonController>();
            bool near = false;
            bool still = false;
            if (s_player)
            {
                Vector3 d = s_player.transform.position - transform.position;
                d.y = 0f;
                near = d.magnitude < ghostRadius;
                still = s_player.CurrentSpeed < 0.3f;
            }

            // первый раз — гаснет сам, стоит только пройти рядом: игрок видит правило
            if (near && !s_ghostTaught)
            {
                s_ghostTaught = true;
                _dying = true;
                if (flame)
                {
                    flame.DOKill();
                    flame.DOIntensity(baseIntensity * 0.2f, 0.6f).SetLink(gameObject).OnComplete(() => { _dying = false; Extinguish(); });
                }
                else { _dying = false; Extinguish(); }
                return;
            }
            if (_dying) return;

            if (holding || (near && still))
            {
                _hold += Time.deltaTime;
                if (flame) flame.intensity = baseIntensity * Mathf.Lerp(1f, 0.25f, _hold / holdSeconds) * UnityEngine.Random.Range(0.6f, 1.1f);
                if (_hold >= holdSeconds)
                {
                    _holder = null;
                    _hold = 0f;
                    Extinguish();
                }
            }
            else if (_hold > 0f)
            {
                _hold = 0f; // отошёл — пламя снова ровное
                Flicker();
            }
            else if (near && flame && UnityEngine.Random.value < 0.1f)
            {
                // призрак рядом, но движется — пламя дрожит
                flame.intensity = baseIntensity * UnityEngine.Random.Range(0.5f, 1f);
            }
        }

        [NaughtyAttributes.Button("Погасить (тест)")]
        public void Extinguish()
        {
            if (!IsLit) return;
            IsLit = false;
            _hold = 0f;
            if (flame)
            {
                flame.DOKill();
                flame.DOIntensity(0f, 0.25f).SetLink(gameObject);
            }
            SetFlameDots(false);
            JamAudio.Play("candle_out", transform.position);
            AnyExtinguished?.Invoke(this);
        }

        public void Relight()
        {
            if (IsLit) return;
            IsLit = true;
            _dying = false;
            SetFlameDots(true);
            if (flame)
            {
                flame.DOKill();
                flame.DOIntensity(baseIntensity, 0.4f).SetLink(gameObject).OnComplete(Flicker);
            }
        }

        void SetFlameDots(bool on)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
                if (t.name == "FlameDot") t.gameObject.SetActive(on);
        }

        void Flicker()
        {
            if (!flame || !IsLit) return;
            flame.DOIntensity(baseIntensity * UnityEngine.Random.Range(0.8f, 1.1f), UnityEngine.Random.Range(0.08f, 0.25f))
                 .SetLink(gameObject)
                 .OnComplete(Flicker);
        }
    }
}
