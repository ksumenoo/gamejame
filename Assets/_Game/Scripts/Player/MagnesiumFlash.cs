using System;
using DG.Tweening;
using Jam.Gameplay;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Player
{
    /// <summary>
    /// Вспышка магния (ЛКМ). Зарядов мало. Маргарет в конусе перед игроком оглушена,
    /// но вспышка громкая — если не попал, она идёт на звук.
    /// </summary>
    public class MagnesiumFlash : MonoBehaviour
    {
        public Transform eyes;
        public MargaretAI margaret;
        [Tooltip("Слои, которые закрывают обзор: стены и мебель.")]
        public LayerMask occluders = ~0;

        [Min(0)] public int charges = 3;
        [Tooltip("Больше этого магния в сумке не бывает.")]
        [Min(1)] public int maxCharges = 3;
        [Tooltip("Сколько магния даётся после каждой смерти (не больше maxCharges).")]
        [Min(0)] public int chargesPerDeath = 1;
        public float range = 7f;
        [Range(10f, 180f)] public float coneAngle = 60f;
        public float stunSeconds = 4f;
        public float cooldown = 1.2f;

        [Header("Свет")]
        public float peakIntensity = 9f;
        public float flashRange = 12f;
        public float flashTime = 0.15f;

        [Header("Звук (необязательно)")]
        [Tooltip("Модель вспышки в руке — дёргается при выстреле.")]
        public Transform viewModel;

        public AudioSource audioSource;
        public AudioClip flashClip;
        public AudioClip emptyClip;

        [ShowNativeProperty] public int Charges => charges;

        public event Action<MagnesiumFlash> Fired;
        public event Action<int> ChargesChanged;
        public event Action<MagnesiumFlash> Empty;

        FirstPersonController _fpc;
        InputAction _fire;
        Light _light;
        float _cool;

        void Awake()
        {
            _fpc = GetComponentInParent<FirstPersonController>();
            if (!eyes) { var c = GetComponentInChildren<Camera>(); if (c) eyes = c.transform; }

            var lg = new GameObject("FlashLight");
            lg.transform.SetParent(eyes ? eyes : transform, false);
            lg.transform.localPosition = new Vector3(0f, 0f, 0.3f);
            _light = lg.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(0.95f, 0.97f, 1f);
            _light.range = flashRange;
            _light.intensity = 0f;
            _light.shadows = LightShadows.None;
            _light.enabled = false;

            _fire = new InputAction("Flash", InputActionType.Button, "<Mouse>/leftButton");
        }

        void Start() => ChargesChanged?.Invoke(charges);
        void OnEnable() => _fire.Enable();
        void OnDisable() => _fire.Disable();
        void OnDestroy() => _fire.Dispose();

        void Update()
        {
            _cool -= Time.deltaTime;
            if (!_fire.WasPressedThisFrame()) return;
            // первый клик захватывает мышь, а не стреляет; в меню и при пластине — тоже нет
            if (Cursor.lockState != CursorLockMode.Locked || (_fpc && _fpc.InputLocked)) return;
            Fire();
        }

        [Button("Вспышка (тест)")]
        public void Fire()
        {
            if (_cool > 0f) return;
            if (charges <= 0)
            {
                if (audioSource && emptyClip) audioSource.PlayOneShot(emptyClip);
                Empty?.Invoke(this);
                return;
            }
            charges--;
            _cool = cooldown;
            ChargesChanged?.Invoke(charges);
            if (audioSource && flashClip) audioSource.PlayOneShot(flashClip);

            _light.DOKill();
            _light.enabled = true;
            _light.intensity = peakIntensity;
            _light.DOIntensity(0f, flashTime * 3f).SetEase(Ease.OutQuad).SetDelay(flashTime).SetLink(gameObject)
                  .OnComplete(() => _light.enabled = false);

            if (margaret)
            {
                if (InCone(margaret.transform.position + Vector3.up * 1.2f)) margaret.Stun(stunSeconds);
                else margaret.HearNoise(eyes ? eyes.position : transform.position);
            }
            if (viewModel) { viewModel.DOKill(true); viewModel.DOPunchPosition(new Vector3(0f, 0.04f, -0.05f), 0.3f, 6, 0.5f).SetLink(gameObject); }
            Fired?.Invoke(this);
        }

        public void AddCharges(int n)
        {
            charges = Mathf.Clamp(charges + n, 0, maxCharges);
            ChargesChanged?.Invoke(charges);
        }

        bool InCone(Vector3 target)
        {
            if (!eyes) return false;
            Vector3 to = target - eyes.position;
            if (to.magnitude > range) return false;
            if (Vector3.Angle(eyes.forward, to) > coneAngle * 0.5f) return false;
            return !Physics.Linecast(eyes.position, target, occluders, QueryTriggerInteraction.Ignore);
        }
    }
}
