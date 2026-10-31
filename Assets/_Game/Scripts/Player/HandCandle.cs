using System;
using DG.Tweening;
using Jam.Gameplay;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Player
{
    /// <summary>
    /// Свеча в руке — единственный свет игрока. Мерцает (DOTween), гаснет, когда Маргарет рядом.
    /// Зажигается заново у канделябра (Candelabra, E).
    /// </summary>
    public class HandCandle : MonoBehaviour
    {
        public Transform anchor;
        public MargaretAI margaret;
        public Vector3 localOffset = new(0.28f, -0.3f, 0.45f);
        public float baseIntensity = 1.4f;
        public float range = 6f;
        public Color color = new(1f, 0.72f, 0.42f);
        [Tooltip("Маргарет ближе этого — свеча гаснет.")]
        public float extinguishDistance = 4f;
        [Tooltip("С этого расстояния свеча начинает дрожать и тускнеть — предупреждение.")]
        public float warnDistance = 8f;
        [Tooltip("Огонёк на свече (модель в руке) — прячется, когда свеча гаснет.")]
        public GameObject flameVisual;

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip extinguishClip;
        public AudioClip lightClip;

        [ShowNativeProperty] public bool IsLit { get; private set; } = true;

        public event Action<HandCandle> Extinguished;
        public event Action<HandCandle> Lit;

        Light _light;
        Tween _flicker;
        Vector3 _flameScale;
        int _outCount;

        /// <summary>0 — Маргарет далеко, 1 — вот-вот погаснет.</summary>
        float Near => margaret ? Mathf.InverseLerp(warnDistance, extinguishDistance, margaret.DistanceToPlayer) : 0f;

        void Awake()
        {
            if (!anchor) { var c = GetComponentInChildren<Camera>(); if (c) anchor = c.transform; }
            var lg = new GameObject("CandleLight");
            lg.transform.SetParent(anchor ? anchor : transform, false);
            lg.transform.localPosition = localOffset;
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = color;
            _light.range = range;
            _light.intensity = baseIntensity;
            _light.shadows = LightShadows.None;
        }

        void Start()
        {
            if (flameVisual) _flameScale = flameVisual.transform.localScale;
            Flicker();
        }

        void LateUpdate()
        {
            // огонёк дышит вместе со светом
            if (flameVisual && IsLit)
                flameVisual.transform.localScale = _flameScale * Mathf.Clamp(_light.intensity / baseIntensity, 0.35f, 1.25f);
        }

        void Update()
        {
            if (IsLit && margaret && margaret.DistanceToPlayer < extinguishDistance)
                Extinguish();
        }

        [Button("Погасить (тест)")]
        public void Extinguish()
        {
            if (!IsLit) return;
            IsLit = false;
            _flicker?.Kill();
            _light.DOKill();
            // короткий всполох и темнота
            _light.DOIntensity(baseIntensity * 1.6f, 0.05f).SetLink(gameObject)
                  .OnComplete(() => _light.DOIntensity(0f, 0.25f).SetLink(gameObject));
            if (flameVisual) flameVisual.SetActive(false);
            if (audioSource && extinguishClip) audioSource.PlayOneShot(extinguishClip);
            if (_outCount++ == 0 && PlateCounterUI.Instance)
                PlateCounterUI.Instance.ShowHint("Свеча погасла — Маргарет рядом. Зажгите её у канделябра или у фонаря в проявочной (E).");
            Extinguished?.Invoke(this);
        }

        [Button("Зажечь (тест)")]
        public void Relight()
        {
            if (IsLit) return;
            IsLit = true;
            if (flameVisual) flameVisual.SetActive(true);
            _light.DOKill();
            _light.DOIntensity(baseIntensity, 0.4f).SetLink(gameObject).OnComplete(Flicker);
            if (audioSource && lightClip) audioSource.PlayOneShot(lightClip);
            Lit?.Invoke(this);
        }

        void Flicker()
        {
            if (!IsLit) return;
            // чем ближе Маргарет, тем тусклее и нервнее пламя
            float near = Near;
            float target = baseIntensity * (1f - 0.55f * near) * UnityEngine.Random.Range(0.75f - 0.4f * near, 1.1f);
            _flicker = _light.DOIntensity(target, UnityEngine.Random.Range(0.06f, 0.2f) * (1f - 0.7f * near))
                .SetEase(Ease.InOutSine)
                .SetLink(gameObject)
                .OnComplete(Flicker);
        }
    }
}
