using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Jam.Exposure
{
    /// <summary>
    /// Камера-обскура с длинной выдержкой.
    /// Пока открыта заслонка, раз в frameInterval рендерит кадр в маленькую RT
    /// и усредняет его с предыдущими: неподвижное остаётся резким, движущееся размазывается.
    /// Параллельно собирает данные о ExposureSubject'ах для геймплея (резкий / шлейф / вспышка).
    ///
    /// Настройка: Camera на объекте выключать не нужно — скрипт сделает это сам.
    /// Culling Mask камеры = что отпечатывается на пластине (включи слой Ghost).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ObscuraCamera : MonoBehaviour
    {
        [Header("Пластина")]
        [Tooltip("Шейдер Hidden/Jam/ExposureAccumulate. Ссылка нужна, чтобы он попал в WebGL-билд.")]
        public Shader accumulateShader;
        public Vector2Int resolution = new(320, 240);
        [Tooltip("Как часто снимаем кадр во время выдержки. 0.1 = 10 кадров в секунду.")]
        [Min(0.02f)] public float frameInterval = 0.1f;
        [Min(0.1f)] public float defaultDuration = 5f;

        [Header("Резкость")]
        [Tooltip("Насколько объект может сдвинуться и всё ещё считаться 'стоящим на месте', м.")]
        [Min(0.01f)] public float sharpRadius = 0.35f;
        [Tooltip("Доля выдержки на одном месте, начиная с которой силуэт резкий.")]
        [Range(0.05f, 1f)] public float sharpThreshold = 0.6f;
        [Tooltip("Слои, которые перекрывают обзор (стены, мебель). Слой призрака сюда не включай.")]
        public LayerMask occluders = ~0;

        [Header("Вспышка магния")]
        [Tooltip("Вес кадра со вспышкой в итоговой пластине. 0.6 = силуэт поверх шлейфа хорошо читается.")]
        [Range(0f, 1f)] public float flashWeight = 0.6f;
        [Tooltip("Свет, который включается на кадр вспышки (необязательно).")]
        public Light flashLight;

        public bool IsExposing { get; private set; }
        /// <summary>0..1 — сколько выдержки прошло.</summary>
        public float Progress { get; private set; }

        public event Action<ObscuraCamera> ExposureStarted;
        public event Action<ObscuraCamera, Plate> ExposureFinished;
        /// <summary>Выдержку прервали (игрок отпустил E или отошёл) — пластины не будет.</summary>
        public event Action<ObscuraCamera> ExposureCancelled;

        Camera _cam;
        Material _accumulate;
        RenderTexture _frame, _accumA, _accumB;
        int _ticksTotal, _ticksDone;

        readonly Dictionary<ExposureSubject, List<Vector3>> _samples = new();
        Coroutine _routine;
        Vector3 _startPos, _startFwd;

        static readonly int PrevTexId = Shader.PropertyToID("_PrevTex");
        static readonly int WeightId = Shader.PropertyToID("_Weight");

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.enabled = false; // рендерим только вручную, во время выдержки

            var shader = accumulateShader ? accumulateShader : Shader.Find("Hidden/Jam/ExposureAccumulate");
            if (shader == null)
                Debug.LogError("[Obscura] Не найден шейдер ExposureAccumulate. Перетащи его в поле Accumulate Shader.", this);
            else
                _accumulate = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            _frame = CreateRT(RenderTextureFormat.ARGB32, depth: 16);
            if (flashLight) flashLight.enabled = false;
        }

        void OnDestroy()
        {
            ReleaseRT(ref _frame);
            ReleaseRT(ref _accumA);
            ReleaseRT(ref _accumB);
            if (_accumulate) Destroy(_accumulate);
        }

        // ---------- Публичное API ----------

        public void BeginExposure() => BeginExposure(defaultDuration, false);

        public void BeginExposure(float duration, bool flashAtEnd)
        {
            if (IsExposing || _accumulate == null) return;
            _routine = StartCoroutine(ExposeRoutine(Mathf.Max(frameInterval, duration), flashAtEnd));
        }

        /// <summary>Прервать выдержку: накопленное выбрасывается, пластины нет.</summary>
        public void CancelExposure()
        {
            if (!IsExposing) return;
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            ReleaseRT(ref _accumA);
            ReleaseRT(ref _accumB);
            _samples.Clear();
            IsExposing = false;
            Progress = 0f;
            ExposureCancelled?.Invoke(this);
        }

        /// <summary>
        /// Текущая резкость объекта по ходу выдержки (для шкалы "проявленности" в акте 2).
        /// Растёт, пока объект стоит в кадре.
        /// </summary>
        public float LiveSharpness(ExposureSubject subject)
        {
            if (!IsExposing || !_samples.TryGetValue(subject, out var list)) return 0f;
            return ComputeSharpness(list, _ticksTotal, subject.lightFactor, out _);
        }

        /// <summary>Видит ли камера объект прямо сейчас (в кадре, на рендерящемся слое, не за стеной).</summary>
        public bool CanSee(ExposureSubject subject)
        {
            if (!subject || !subject.detectable || !subject.isActiveAndEnabled) return false;
            if ((_cam.cullingMask & (1 << subject.gameObject.layer)) == 0) return false;

            Vector3 p = subject.ProbePosition;
            Vector3 vp = _cam.WorldToViewportPoint(p);
            if (vp.z < _cam.nearClipPlane || vp.z > _cam.farClipPlane) return false;
            if (vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) return false;

            Vector3 from = _cam.transform.position;
            if (Physics.Linecast(from, p, out var hit, occluders, QueryTriggerInteraction.Ignore))
                return hit.transform == subject.transform || hit.transform.IsChildOf(subject.transform);
            return true;
        }

        // ---------- Выдержка ----------

        IEnumerator ExposeRoutine(float duration, bool flashAtEnd)
        {
            IsExposing = true;
            Progress = 0f;
            _samples.Clear();

            _accumA = CreateRT(AccumFormat(), depth: 0);
            _accumB = CreateRT(AccumFormat(), depth: 0);
            ClearRT(_accumA);

            _ticksTotal = Mathf.Max(1, Mathf.RoundToInt(duration / frameInterval));
            _ticksDone = 0;
            float startTime = Time.time;
            _startPos = transform.position;
            _startFwd = transform.forward;

            ExposureStarted?.Invoke(this);

            var wait = new WaitForSeconds(frameInterval);
            for (int i = 0; i < _ticksTotal; i++)
            {
                RenderFrame();
                Accumulate(1f / (i + 1)); // скользящее среднее всех кадров
                SampleSubjects();
                _ticksDone = i + 1;
                Progress = (float)_ticksDone / _ticksTotal;
                yield return wait;
            }

            var caught = new HashSet<ExposureSubject>();
            if (flashAtEnd)
            {
                if (flashLight) flashLight.enabled = true;
                RenderFrame();
                if (flashLight) flashLight.enabled = false;
                Accumulate(flashWeight);
                foreach (var s in ExposureSubject.All)
                    if (CanSee(s)) caught.Add(s);
            }

            var plate = BuildPlate(duration, flashAtEnd, startTime, caught);

            // накопитель уходит в пластину, второй освобождаем
            _accumA = null;
            ReleaseRT(ref _accumB);

            IsExposing = false;
            Progress = 1f;
            _routine = null;
            ExposureFinished?.Invoke(this, plate);
        }

        void RenderFrame()
        {
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = _frame };
            if (RenderPipeline.SupportsRenderRequest(_cam, request))
            {
                RenderPipeline.SubmitRenderRequest(_cam, request);
            }
            else
            {
                _cam.targetTexture = _frame;
                _cam.Render();
                _cam.targetTexture = null;
            }
        }

        void Accumulate(float weight)
        {
            _accumulate.SetTexture(PrevTexId, _accumA);
            _accumulate.SetFloat(WeightId, weight);
            Graphics.Blit(_frame, _accumB, _accumulate);
            (_accumA, _accumB) = (_accumB, _accumA);
        }

        void SampleSubjects()
        {
            foreach (var s in ExposureSubject.All)
            {
                if (!CanSee(s)) continue;
                if (!_samples.TryGetValue(s, out var list))
                    _samples[s] = list = new List<Vector3>(_ticksTotal);
                list.Add(s.ProbePosition);
            }
        }

        Plate BuildPlate(float duration, bool flash, float startTime, HashSet<ExposureSubject> caught)
        {
            var plate = new Plate
            {
                Image = _accumA,
                Source = this,
                Duration = duration,
                TakenAt = startTime,
                Flash = flash,
                Position = _startPos,
                Forward = _startFwd,
            };

            var subjects = new HashSet<ExposureSubject>(_samples.Keys);
            subjects.UnionWith(caught);

            foreach (var s in subjects)
            {
                _samples.TryGetValue(s, out var list);
                list ??= new List<Vector3>();

                float sharp = ComputeSharpness(list, _ticksTotal, s.lightFactor, out var sharpestPos);
                bool flashed = caught.Contains(s);
                if (flashed) sharpestPos = s.ProbePosition;

                plate.Readings.Add(new SubjectReading
                {
                    Subject = s,
                    VisibleFraction = (float)list.Count / _ticksTotal,
                    Sharpness = sharp,
                    SharpestPosition = sharpestPos,
                    IsSharp = flashed || sharp >= sharpThreshold,
                    CaughtByFlash = flashed,
                });
            }
            return plate;
        }

        /// <summary>Самое долгое "стояние" в одной точке / вся выдержка, с поправкой на свет.</summary>
        float ComputeSharpness(List<Vector3> positions, int totalTicks, float lightFactor, out Vector3 sharpestPos)
        {
            sharpestPos = Vector3.zero;
            if (positions == null || positions.Count == 0) return 0f;

            float r2 = sharpRadius * sharpRadius;
            int best = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                int count = 0;
                for (int j = 0; j < positions.Count; j++)
                    if ((positions[j] - positions[i]).sqrMagnitude <= r2) count++;
                if (count > best)
                {
                    best = count;
                    sharpestPos = positions[i];
                }
            }
            return Mathf.Clamp01((float)best / totalTicks * lightFactor);
        }

        // ---------- RT ----------

        RenderTextureFormat AccumFormat() =>
            SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                ? RenderTextureFormat.ARGBHalf   // меньше полос при усреднении
                : RenderTextureFormat.ARGB32;    // запасной вариант для слабого WebGL

        RenderTexture CreateRT(RenderTextureFormat format, int depth)
        {
            var rt = new RenderTexture(resolution.x, resolution.y, depth, format)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = $"Obscura_{name}",
            };
            rt.Create();
            return rt;
        }

        static void ClearRT(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prev;
        }

        static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            var cam = GetComponent<Camera>();
            if (!cam) return;
            Gizmos.color = IsExposing ? Color.red : new Color(1f, 0.8f, 0.3f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, cam.fieldOfView, Mathf.Min(cam.farClipPlane, 12f), cam.nearClipPlane, (float)resolution.x / resolution.y);
        }
#endif
    }
}
