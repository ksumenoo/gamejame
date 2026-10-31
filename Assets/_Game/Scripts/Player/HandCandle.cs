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
        [Tooltip("Насколько огонёк клонится в сторону Маргарет, когда она рядом, градусы. Свеча — компас страха.")]
        public float leanAngle = 40f;
        [Tooltip("Компас: дальше warnDistance огонёк всё равно чуть тянется к ней — до этого расстояния, м (сквозь стены тоже).")]
        public float compassRange = 18f;
        [Tooltip("Наклон на самой границе компаса, градусы. Ближе — сильнее, до leanAngle.")]
        public float compassMinLean = 18f;
        [Tooltip("Мысль, когда свеча погасла впервые.")]
        public string thoughtOut = "Огонь клонился к ней… и умер. Нужен другой огонь.";

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
        Quaternion _flameRot;
        Vector3 _flamePos;
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
            if (flameVisual)
            {
                _flameScale = flameVisual.transform.localScale;
                _flameRot = flameVisual.transform.localRotation;
                _flamePos = flameVisual.transform.localPosition;
            }
            Flicker();
        }

        void LateUpdate()
        {
            // огонёк дышит вместе со светом
            if (flameVisual && IsLit)
                flameVisual.transform.localScale = _flameScale * Mathf.Clamp(_light.intensity / baseIntensity, 0.35f, 1.25f);
            if (flameVisual && IsLit) LeanTowardMargaret();
        }

        void Update()
        {
            if (IsLit && margaret && margaret.DistanceToPlayer < extinguishDistance)
                Extinguish();
        }

        /// <summary>Огонёк тянется к Маргарет: чем ближе она, тем сильнее — так видно, откуда она идёт.</summary>
        void LeanTowardMargaret()
        {
            var t = flameVisual.transform;
            Quaternion restRot = t.parent ? t.parent.rotation * _flameRot : _flameRot;
            Vector3 restPos = t.parent ? t.parent.TransformPoint(_flamePos) : _flamePos;
            Vector3 dir = margaret && margaret.isActiveAndEnabled ? margaret.transform.position - restPos : Vector3.zero;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 1e-3f || dist > compassRange) { t.SetPositionAndRotation(restPos, restRot); return; }
            // компас: издалека — лёгкий наклон, у warnDistance и ближе — сильный (плюс дрожь пламени из Flicker)
            float far01 = Mathf.InverseLerp(compassRange, warnDistance, dist);
            float lean = Mathf.Lerp(compassMinLean * far01, leanAngle, Near);
            // огонёк крошечный — при наклоне ещё и вытягиваем его, чтобы направление читалось издалека
            float baseHalf = t.lossyScale.y * 0.5f;
            Vector3 root = restPos - Vector3.up * baseHalf; // основание огонька — на фитиле
            var ls = t.localScale;
            t.localScale = new Vector3(ls.x * 0.85f, ls.y * (1f + lean / 45f), ls.z * 0.85f);
            // наклоняем вокруг основания, а не вокруг центра — так он «тянется», а не съезжает
            float half = t.lossyScale.y * 0.5f;
            float wobble = 1f + 0.15f * Mathf.Sin(Time.time * 23f);
            var q = Quaternion.AngleAxis(lean * wobble, Vector3.Cross(Vector3.up, dir / dist));
            t.SetPositionAndRotation(root + q * (Vector3.up * half), q * restRot);
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
            if (_outCount++ == 0) Thoughts.Once("m_candle_out", thoughtOut);
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
