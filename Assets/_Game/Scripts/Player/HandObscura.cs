using System;
using DG.Tweening;
using NaughtyAttributes;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Jam.Player
{
    /// <summary>
    /// Ручная камера-обскура (ПКМ). Пока поднята — вторая камера рендерит живую картинку в маленькую RT,
    /// а рамка на экране показывает её вверх ногами, тускло, с кипящим зерном. Только так видно Маргарет
    /// и сцены прошлого (слои Ghost и Past). Игрок при этом идёт медленно.
    /// </summary>
    public class HandObscura : MonoBehaviour
    {
        [Header("Ссылки")]
        [Tooltip("Глаза игрока: обскура смотрит туда же.")]
        public Camera eyes;
        public RectTransform frame;
        public CanvasGroup frameGroup;
        public RawImage view;
        [Tooltip("Материал на шейдере Jam/PlateDisplay — для живой картинки берётся его копия.")]
        public Material viewMaterial;
        [Tooltip("Модель обскуры в руках (слой PlayerOnly), необязательно.")]
        public Transform handModel;

        [Header("Картинка")]
        public Vector2Int resolution = new(320, 240);
        [Min(1f)] public float framesPerSecond = 15f;
        [Range(20f, 100f)] public float fieldOfView = 60f;
        public LayerMask cullingMask = ~0;
        [Range(0f, 2f)] public float brightness = 1.1f;

        [Header("Ощущение")]
        [Range(0.1f, 1f)] public float speedMultiplier = 0.4f;
        [Min(0.05f)] public float raiseTime = 0.25f;
        public float hiddenFrameOffset = -160f;

        [ShowNativeProperty] public bool IsHeld { get; private set; }
        /// <summary>Рамка полностью поднята — картинка на экране.</summary>
        [ShowNativeProperty] public bool IsRaised { get; private set; }

        public event Action<HandObscura> Raised;
        public event Action<HandObscura> Lowered;
        /// <summary>Игрок встал точно на точку обзора сцены — картинка «защёлкнулась».</summary>
        public event Action<HandObscura, Jam.Gameplay.PastScene> SceneAligned;

        [Header("Тепло-холодно")]
        [Tooltip("С какого совпадения картинка считается «защёлкнутой».")]
        [Range(0.5f, 1f)] public float alignLock = 0.95f;

        /// <summary>Совпадение с ближайшей точкой обзора сейчас (0..1).</summary>
        [ShowNativeProperty] public float Alignment { get; private set; }
        Jam.Gameplay.PastScene _locked;

        public RenderTexture Texture => _rt;

        Camera _cam;
        RenderTexture _rt;
        Material _mat;
        InputAction _aim;
        FirstPersonController _fpc;
        Sequence _seq;
        float _timer;
        Vector3 _handDownPos, _handUpPos;

        static readonly int Develop = Shader.PropertyToID("_Develop");
        static readonly int LiveGrain = Shader.PropertyToID("_LiveGrain");
        static readonly int Brightness = Shader.PropertyToID("_Brightness");
        static readonly int Vignette = Shader.PropertyToID("_Vignette");
        static readonly int Grain = Shader.PropertyToID("_Grain");
        static readonly int OverlayStrength = Shader.PropertyToID("_OverlayStrength");

        void Awake()
        {
            _fpc = GetComponentInParent<FirstPersonController>();
            if (!eyes) eyes = GetComponentInChildren<Camera>();

            var camGo = new GameObject("HandObscuraCamera");
            camGo.transform.SetParent(eyes ? eyes.transform : transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.fieldOfView = fieldOfView;
            _cam.nearClipPlane = 0.08f;
            _cam.farClipPlane = 30f;
            _cam.cullingMask = cullingMask;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;

            _rt = new RenderTexture(resolution.x, resolution.y, 16, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                name = "HandObscura",
            };
            _rt.Create();

            if (viewMaterial)
            {
                _mat = new Material(viewMaterial);
                _mat.SetFloat(Develop, 1f);
                _mat.SetFloat(LiveGrain, 1f);
                _mat.SetFloat(Brightness, brightness);
                _mat.SetFloat(Vignette, 1.0f);
                _mat.SetFloat(Grain, 0.05f);
                _mat.SetFloat(OverlayStrength, 0f);
            }
            if (view)
            {
                view.texture = _rt;
                view.material = _mat;
            }
            if (frameGroup) frameGroup.alpha = 0f;
            if (frame) frame.anchoredPosition = new Vector2(0f, hiddenFrameOffset);
            if (handModel)
            {
                _handUpPos = handModel.localPosition;
                _handDownPos = _handUpPos + new Vector3(0f, -0.16f, 0f); // опущена — верх ящика виден снизу экрана
                handModel.localPosition = _handDownPos;
            }

            _aim = new InputAction("Obscura", InputActionType.Button, "<Mouse>/rightButton");
        }

        void OnEnable() => _aim.Enable();

        void OnDisable()
        {
            _aim.Disable();
            if (IsHeld) SetHeld(false);
        }

        void OnDestroy()
        {
            _aim.Dispose();
            _seq?.Kill();
            if (_rt) { _rt.Release(); Destroy(_rt); }
            if (_mat) Destroy(_mat);
        }

        void Update()
        {
            bool locked = _fpc && _fpc.InputLocked;
            bool want = _aim.IsPressed() && !locked;
            if (want != IsHeld) SetHeld(want);

            if (!IsHeld) return;
            UpdateAlignment();
            _timer += Time.deltaTime;
            if (_timer >= 1f / framesPerSecond)
            {
                _timer = 0f;
                RenderNow();
            }
        }

        /// <summary>Поднять или опустить обскуру из кода (катсцены, смерть).</summary>
        public void SetHeld(bool held)
        {
            IsHeld = held;
            if (_fpc) _fpc.SpeedMultiplier = held ? speedMultiplier : 1f;
            if (held) RenderNow();

            _seq?.Kill();
            _seq = DOTween.Sequence().SetLink(gameObject);
            if (frameGroup) _seq.Join(frameGroup.DOFade(held ? 1f : 0f, raiseTime));
            if (frame) _seq.Join(frame.DOAnchorPosY(held ? 0f : hiddenFrameOffset, raiseTime).SetEase(held ? Ease.OutCubic : Ease.InCubic));
            if (handModel) _seq.Join(handModel.DOLocalMove(held ? _handUpPos : _handDownPos, raiseTime).SetEase(Ease.OutCubic));

            if (!held && IsRaised)
            {
                IsRaised = false;
                Lowered?.Invoke(this);
            }
            if (held)
            {
                _seq.OnComplete(() =>
                {
                    IsRaised = true;
                    Raised?.Invoke(this);
                });
            }
        }

        /// <summary>Чем точнее встал — тем спокойнее зерно и ярче картинка; на точке — щелчок.</summary>
        void UpdateAlignment()
        {
            if (!eyes) return;
            Jam.Gameplay.PastScene best = null;
            float a = 0f;
            foreach (var s in Jam.Gameplay.PastScene.All)
            {
                if (!s || !s.IsActive) continue;
                float v = s.Alignment(eyes.transform.position, eyes.transform.forward);
                if (v > a) { a = v; best = s; }
            }
            Alignment = a;
            if (_mat)
            {
                _mat.SetFloat(Grain, Mathf.Lerp(0.05f, 0.012f, a * a));
                _mat.SetFloat(Brightness, brightness * Mathf.Lerp(1f, 1.25f, a * a));
            }
            if (best && a >= alignLock && _locked != best)
            {
                _locked = best;
                if (frame) frame.DOPunchScale(Vector3.one * 0.04f, 0.3f, 6, 0.5f).SetLink(gameObject);
                SceneAligned?.Invoke(this, best);
            }
            else if (_locked && (a < 0.6f || best != _locked)) _locked = null;
        }

        void RenderNow()
        {
            if (!_cam) return;
            _cam.fieldOfView = fieldOfView;
            _cam.cullingMask = cullingMask;
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = _rt };
            if (RenderPipeline.SupportsRenderRequest(_cam, request))
            {
                RenderPipeline.SubmitRenderRequest(_cam, request);
            }
            else
            {
                _cam.targetTexture = _rt;
                _cam.Render();
                _cam.targetTexture = null;
            }
        }
    }
}
