using System;
using System.Collections.Generic;
using System.Linq;
using Jam.Exposure;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Сцена прошлого в комнате: набор PastFigure, «точка обзора», откуда она складывается, и условия.
    /// Состояние меняет StoryDirector: скрыта → открыта → проявлена.
    /// Видна в обскуре, только если открыта и условия выполнены; целиком — только с точки обзора.
    /// Засчитывается пластина, снятая с точки обзора (±matchRadius, ±matchAngle) при выполненных условиях.
    /// </summary>
    public class PastScene : MonoBehaviour
    {
        public enum State { Hidden, Visible, Developed }

        [Tooltip("Номер сцены: 1…6.")]
        public int index = 1;
        public State startState = State.Visible;

        [Header("Точка обзора")]
        [Tooltip("Где должен стоять объектив и куда смотреть. Пусто — сцену видно отовсюду.")]
        public Transform viewPoint;
        [Tooltip("Сцена целиком видна в этом радиусе от точки, м.")]
        public float viewRadius = 0.8f;
        [Tooltip("Дальше этого расстояния за радиусом сцена рассыпается полностью, м.")]
        public float viewFade = 6f;
        [Range(5f, 90f)] public float viewAngle = 30f;
        [Tooltip("Пластина засчитывается, если объектив был не дальше этого от точки, м.")]
        public float matchRadius = 1.2f;
        [Range(5f, 90f)] public float matchAngle = 35f;

        [Header("Условия")]
        [Tooltip("Что должно быть включено в комнате, чтобы прошлое проступило.")]
        public List<SceneSwitch> switches = new();
        [Tooltip("На пластине должна быть резкой Маргарет (сцена в коридоре).")]
        public bool requireMargaretSharp;
        public ExposureSubject margaret;
        [Tooltip("Куда встанет Маргарет, когда камеру поставят на точку обзора этой сцены и откроют заслонку (стойка для посмертного фото).")]
        public Transform posePoint;

        /// <summary>Все сцены на уровне.</summary>
        public static readonly List<PastScene> All = new();

        [ShowNativeProperty] public State Current { get; private set; } = State.Hidden;
        [ShowNativeProperty] public bool ConditionsMet => switches.Count == 0 || switches.All(s => s && s.IsOn);
        /// <summary>Открыта и условия выполнены — её можно найти и снять.</summary>
        public bool IsActive => Current == State.Visible && ConditionsMet;

        public event Action<PastScene> StateChanged;

        bool _initialized;
        bool _figuresShown;
        readonly List<Material> _mats = new();

        static readonly int ViewPointId = Shader.PropertyToID("_ViewPoint");
        static readonly int ViewDirId = Shader.PropertyToID("_ViewDir");
        static readonly int ViewRadiusId = Shader.PropertyToID("_ViewRadius");
        static readonly int ViewFadeId = Shader.PropertyToID("_ViewFade");
        static readonly int ViewAngleId = Shader.PropertyToID("_ViewAngle");

        // если StoryDirector уже задал состояние раньше нас — не перетираем его стартовым
        void Awake()
        {
            SetupMaterials();
            if (!_initialized) Apply(startState, notify: false);
        }

        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() => All.Remove(this);

        /// <summary>Насколько взгляд из pos по fwd совпадает с точкой обзора: 0 — никак, 1 — точно (как в шейдере).</summary>
        public float Alignment(Vector3 pos, Vector3 fwd)
        {
            if (!viewPoint) return 0f;
            float d = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(viewPoint.position.x, viewPoint.position.z));
            float byDist = Mathf.Clamp01((viewRadius + viewFade - d) / Mathf.Max(viewFade, 0.01f));
            Vector2 cf = new Vector2(fwd.x, fwd.z).normalized;
            Vector2 vf = new Vector2(viewPoint.forward.x, viewPoint.forward.z).normalized;
            float cosMax = Mathf.Cos(viewAngle * Mathf.Deg2Rad);
            float byAngle = Mathf.Clamp01((Vector2.Dot(cf, vf) - (cosMax - 0.25f)) / 0.25f);
            return byDist * byAngle;
        }

        void OnDestroy()
        {
            foreach (var m in _mats) if (m) Destroy(m);
        }

        void Update()
        {
            // условия могут выполниться в любой момент — тогда прошлое проступает
            bool show = Current == State.Visible && ConditionsMet;
            if (show != _figuresShown) ShowFigures(show);
        }

        [Button("Скрыть")] void DebugHide() => SetState(State.Hidden);
        [Button("Показать")] void DebugShow() => SetState(State.Visible);

        public void SetState(State state) => Apply(state, notify: true);

        /// <summary>Эта пластина — кадр этой сцены?</summary>
        public bool Matches(Plate plate)
        {
            if (plate == null || Current != State.Visible || !ConditionsMet) return false;
            if (viewPoint)
            {
                Vector2 a = new(plate.Position.x, plate.Position.z);
                Vector2 b = new(viewPoint.position.x, viewPoint.position.z);
                if (Vector2.Distance(a, b) > matchRadius) return false;
                Vector2 f1 = new Vector2(plate.Forward.x, plate.Forward.z).normalized;
                Vector2 f2 = new Vector2(viewPoint.forward.x, viewPoint.forward.z).normalized;
                if (Vector2.Angle(f1, f2) > matchAngle) return false;
            }
            if (requireMargaretSharp)
            {
                if (!margaret || !plate.TryGetReading(margaret, out var r) || !r.IsSharp) return false;
            }
            return true;
        }

        void Apply(State state, bool notify)
        {
            _initialized = true;
            Current = state;
            ShowFigures(state == State.Visible && ConditionsMet);
            if (notify) StateChanged?.Invoke(this);
        }

        void ShowFigures(bool on)
        {
            _figuresShown = on;
            foreach (Transform child in transform)
                child.gameObject.SetActive(on);
        }

        /// <summary>Своя копия материала на каждую фигуру — с точкой обзора этой сцены.</summary>
        void SetupMaterials()
        {
            if (!viewPoint) return;
            var copies = new Dictionary<Material, Material>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                var src = r.sharedMaterial;
                if (!src || !src.HasProperty(ViewRadiusId)) continue;
                if (!copies.TryGetValue(src, out var m))
                {
                    m = new Material(src) { name = src.name + "_Scene" + index };
                    m.SetVector(ViewPointId, viewPoint.position);
                    m.SetVector(ViewDirId, viewPoint.forward);
                    m.SetFloat(ViewRadiusId, viewRadius);
                    m.SetFloat(ViewFadeId, viewFade);
                    m.SetFloat(ViewAngleId, viewAngle);
                    copies[src] = m;
                    _mats.Add(m);
                }
                r.sharedMaterial = m;
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!viewPoint) return;
            Gizmos.color = new Color(1f, 0.85f, 0.3f, 0.8f);
            Gizmos.DrawWireSphere(viewPoint.position, matchRadius);
            Gizmos.DrawRay(viewPoint.position, viewPoint.forward * 2f);
            Gizmos.DrawLine(viewPoint.position, transform.position);
        }
#endif
    }
}
