using System.Collections.Generic;
using System.Text.RegularExpressions;
using DG.Tweening;
using Jam.Audio;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;
using UnityEngine.AI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Акт 2: призрак роняет вещи, чтобы напугать живого фотографа. Посмотреть на вещь и держать E, не двигаясь.
    /// Шкаф раскачивается и падает плашмя, стул опрокидывается, посуда слетает со стола и бьётся.
    /// Каждая вещь — один раз за попытку; при рестарте акта всё возвращается на места (ResetAll).
    /// Вещи — это меши дома F_shkaf*, F_chair*, F_serviz*, F_kuvshin*, F_butilka*, F_podnos*:
    /// куски одной вещи собираются под общий корень при входе в акт 2 (InstallAll).
    /// Испуг: шкаф 18 %, посуда 9 %, стул 6 % от шкалы — если он видит; в той же комнате ×0,7, слышит издалека ×0,35.
    /// </summary>
    public class Poltergeist : MonoBehaviour, IInteractable
    {
        public enum Kind { Wardrobe, Chair, Dishes }

        public Kind kind;
        [Tooltip("Сколько держать E (стоя на месте), с.")]
        public float holdSeconds = 0.9f;

        public static readonly List<Poltergeist> All = new();
        static readonly Regex Key = new(@"^(F_(?:shkaf[A-Za-z]*|chair[A-Za-z0-9]*|serviz\d*|kuvshin[A-Za-z]*|butilka[A-Za-z]*|podnos[A-Za-z]*))_", RegexOptions.IgnoreCase);

        public bool Done { get; private set; }

        Interactor _holder;
        FirstPersonController _body;
        float _hold;
        Transform _pivot;
        Vector3 _homePos;
        Quaternion _homeRot;
        readonly List<(Transform t, Vector3 p, Quaternion r, Vector3 s)> _parts = new();
        readonly List<GameObject> _shards = new();
        Tween _wobble;
        GameObject _obstacle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetStatics() => All.Clear();

        /// <summary>Имя меша → ключ вещи («F_shkafBed») или null. Его же использует сборщик уровня, чтобы не батчить эти меши.</summary>
        public static string GroupKey(string meshName)
        {
            var m = Key.Match(meshName ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        static float FearOf(Kind k) => k switch { Kind.Wardrobe => 0.18f, Kind.Dishes => 0.09f, _ => 0.06f };

        // ---------------- сборка ----------------

        public static void InstallAll()
        {
            if (All.Count > 0) return;
            var groups = new Dictionary<string, List<Transform>>();
            foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                string k = GroupKey(r.name);
                if (k == null) continue;
                if (!groups.TryGetValue(k, out var list)) groups[k] = list = new List<Transform>();
                list.Add(r.transform);
            }
            foreach (var kv in groups)
            {
                var b = new Bounds(kv.Value[0].position, Vector3.zero);
                foreach (var t in kv.Value) { var rr = t.GetComponent<Renderer>(); if (rr) b.Encapsulate(rr.bounds); }
                var go = new GameObject("Prop_" + kv.Key.Substring(2));
                go.transform.SetParent(kv.Value[0].parent, true);
                go.transform.position = new Vector3(b.center.x, b.min.y, b.center.z);
                var p = go.AddComponent<Poltergeist>();
                string low = kv.Key.ToLowerInvariant();
                p.kind = low.StartsWith("f_shkaf") ? Kind.Wardrobe : low.StartsWith("f_chair") ? Kind.Chair : Kind.Dishes;
                foreach (var t in kv.Value)
                {
                    t.SetParent(go.transform, true);
                    p._parts.Add((t, t.localPosition, t.localRotation, t.localScale));
                }
                p._homePos = go.transform.position;
                p._homeRot = go.transform.rotation;
                All.Add(p);
            }
            Debug.Log($"[Poltergeist] Можно уронить: {All.Count} вещей.");
        }

        public static void ResetAll()
        {
            foreach (var p in All) if (p) p.ResetProp();
        }

        void ResetProp()
        {
            transform.DOKill();
            _wobble?.Kill();
            foreach (var s in _shards) if (s) Destroy(s);
            _shards.Clear();
            if (_obstacle) Destroy(_obstacle);
            if (_pivot)
            {
                transform.SetParent(_pivot.parent, true);
                Destroy(_pivot.gameObject);
                _pivot = null;
            }
            transform.SetPositionAndRotation(_homePos, _homeRot);
            foreach (var (t, pos, rot, scale) in _parts)
            {
                if (!t) continue;
                t.DOKill();
                t.SetParent(transform, false);
                t.localPosition = pos; t.localRotation = rot; t.localScale = scale;
                t.gameObject.SetActive(true);
                foreach (var c in t.GetComponents<Collider>()) c.enabled = true;
            }
            Done = false;
            _holder = null;
            _hold = 0f;
        }

        // ---------------- взаимодействие ----------------

        public string Prompt
        {
            get
            {
                if (Done || !Candelabra.GhostMode) return "";
                string what = kind switch { Kind.Wardrobe => "Опрокинуть шкаф", Kind.Chair => "Опрокинуть стул", _ => "Смахнуть посуду" };
                return _hold > 0f ? $"{what}… {Mathf.RoundToInt(_hold / holdSeconds * 100f)}%" : what + " (держать E)";
            }
        }

        public bool CanInteract(Interactor who) => Candelabra.GhostMode && !Done && !_holder;

        public void Interact(Interactor who)
        {
            _holder = who;
            _body = who ? who.GetComponentInParent<FirstPersonController>() : null;
            _hold = 0f;
            _wobble?.Kill();
            // вещь начинает дрожать — видно, что призрак её «держит»
            _wobble = transform.DOShakeRotation(holdSeconds, kind == Kind.Wardrobe ? 2.5f : 6f, 18, 90f, false).SetLink(gameObject);
            Play("prop_creak", "door_close");
        }

        void Update()
        {
            if (!_holder) return;
            bool still = !_body || _body.CurrentSpeed < 0.3f;
            if (Done || !_holder.UseHeld || !still || !ReferenceEquals(_holder.Current, this))
            {
                _holder = null;
                _hold = 0f;
                _wobble?.Kill(true);
                return;
            }
            _hold += Time.deltaTime;
            if (_hold >= holdSeconds)
            {
                _holder = null;
                _hold = 0f;
                _wobble?.Kill(true);
                Fall();
            }
        }

        // ---------------- падение ----------------

        void Fall()
        {
            if (Done) return;
            Done = true;
            if (kind == Kind.Dishes) Smash();
            else TipOver(kind == Kind.Wardrobe ? 1.0f : 0.55f);
        }

        Bounds PartsBounds()
        {
            var b = new Bounds(transform.position, Vector3.zero);
            bool first = true;
            foreach (var (t, _, _, _) in _parts)
            {
                var r = t ? t.GetComponent<Renderer>() : null;
                if (!r) continue;
                if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// <summary>
        /// Куда и насколько падать. Пробуем 4 направления (по осям): из нижнего ребра на разной высоте пускаем лучи
        /// и смотрим, во что упрётся вещь при повороте — стол, стена, другая мебель. Берём направление, где можно
        /// упасть дальше всего; если впереди стол — вещь не проходит сквозь него, а заваливается на его край.
        /// </summary>
        (Vector3 dir, float angle) FallPlan(Bounds b)
        {
            float H = Mathf.Max(0.3f, b.size.y);
            Vector3 bestDir = Vector3.forward;
            float bestAngle = -1f;
            Vector3[] dirs = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            foreach (var d in dirs)
            {
                float half = Mathf.Abs(b.extents.x * d.x) + Mathf.Abs(b.extents.z * d.z);
                Vector3 side = Vector3.Cross(Vector3.up, d);
                float width = Mathf.Abs(b.extents.x * side.x) + Mathf.Abs(b.extents.z * side.z);
                Vector3 edge = new Vector3(b.center.x, b.min.y, b.center.z) + d * half;
                float allowed = 90f;
                for (float y = 0.12f; y <= H; y += 0.15f)
                for (int k = -1; k <= 1; k++)
                {
                    Vector3 o = edge + Vector3.up * y + side * (k * width * 0.8f) - d * 0.03f;
                    // старт уже внутри чего-то (стул задвинут под столешницу) — падать сюда нельзя
                    if (Blocked(Physics.OverlapSphere(o, 0.03f, ~0, QueryTriggerInteraction.Ignore))) { allowed = Mathf.Min(allowed, 0f); continue; }
                    foreach (var h in Physics.RaycastAll(o, d, H, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (IsMine(h.collider) || h.normal.y > 0.7f) continue;
                        if (Mathf.Sqrt(h.distance * h.distance + y * y) > H) continue; // так далеко вещь не достаёт
                        allowed = Mathf.Min(allowed, Mathf.Atan2(h.distance, y) * Mathf.Rad2Deg);
                    }
                }
                // чуть предпочитаем падение от стены (шкаф стоит к ней спиной) — при равных условиях
                float score = allowed + Random.Range(0f, 2f);
                if (score > bestAngle) { bestAngle = score; bestDir = d; }
            }
            return (bestDir, Mathf.Clamp(bestAngle - 3f, 15f, 90f));
        }

        bool IsMine(Collider c) => c && (c.transform.IsChildOf(transform) || c.isTrigger);

        bool Blocked(Collider[] cols)
        {
            foreach (var c in cols) if (!IsMine(c)) return true;
            return false;
        }

        /// <summary>Раскачался — и рухнул плашмя, поворачиваясь вокруг нижнего ребра, с отскоком в конце.</summary>
        void TipOver(float fallTime)
        {
            var b = PartsBounds();
            var (dir, fall) = FallPlan(b);
            float half = Mathf.Abs(b.extents.x * dir.x) + Mathf.Abs(b.extents.z * dir.z);
            Vector3 edge = new Vector3(b.center.x, b.min.y, b.center.z) + dir * half;
            _pivot = new GameObject(name + "_Pivot").transform;
            _pivot.SetParent(transform.parent, false);
            _pivot.SetPositionAndRotation(edge, Quaternion.identity);
            transform.SetParent(_pivot, true);

            Vector3 axis = Vector3.Cross(Vector3.up, dir);
            Quaternion Tilt(float a) => Quaternion.AngleAxis(a, axis);
            float lean = kind == Kind.Wardrobe ? 7f : 12f;

            var seq = DOTween.Sequence().SetLink(gameObject);
            // раскачка: качнулся вперёд, назад — и пошёл
            seq.Append(_pivot.DORotateQuaternion(Tilt(lean), 0.28f).SetEase(Ease.OutQuad));
            seq.Append(_pivot.DORotateQuaternion(Tilt(-lean * 0.35f), 0.22f).SetEase(Ease.InOutQuad));
            seq.AppendCallback(() => Play("prop_creak", "door_close"));
            // падение: ускоряется, как под тяжестью
            seq.Append(_pivot.DORotateQuaternion(Tilt(fall), fallTime * Mathf.Lerp(0.6f, 1f, fall / 90f)).SetEase(Ease.InCubic));
            seq.AppendCallback(() =>
            {
                Play(kind == Kind.Wardrobe ? "prop_heavy" : "prop_fall", "door_slam");
                ShakeNearbyCamera(kind == Kind.Wardrobe ? 0.35f : 0.12f);
                Startle();
            });
            // удар: подпрыгнул и улёгся
            seq.Append(_pivot.DORotateQuaternion(Tilt(fall - 6f), 0.09f).SetEase(Ease.OutQuad));
            seq.Append(_pivot.DORotateQuaternion(Tilt(fall), 0.12f).SetEase(Ease.InQuad));
            seq.Append(_pivot.DORotateQuaternion(Tilt(fall - 1.5f), 0.06f));
            seq.Append(_pivot.DORotateQuaternion(Tilt(fall), 0.06f));
            seq.AppendCallback(CarveObstacle);
        }

        /// <summary>Упавшую вещь обходят: вырезаем её из навигации, фотограф больше не проходит сквозь шкаф.</summary>
        void CarveObstacle()
        {
            var b = PartsBounds();
            _obstacle = new GameObject(name + "_Obstacle");
            _obstacle.transform.SetParent(_pivot ? _pivot.parent : transform.parent, true);
            _obstacle.transform.SetPositionAndRotation(b.center, Quaternion.identity);
            var o = _obstacle.AddComponent<NavMeshObstacle>();
            o.shape = NavMeshObstacleShape.Box;
            o.size = new Vector3(Mathf.Max(0.2f, b.size.x), Mathf.Max(0.5f, b.size.y), Mathf.Max(0.2f, b.size.z));
            o.carving = true;
            o.carveOnlyStationary = false;
        }

        /// <summary>Посуда слетает со стола в разные стороны, бьётся о пол и разлетается осколками.</summary>
        void Smash()
        {
            Vector3 center = PartsBounds().center;
            float lastLand = 0f;
            foreach (var (t, _, _, _) in _parts)
            {
                if (!t) continue;
                foreach (var c in t.GetComponents<Collider>()) c.enabled = false;
                var r = t.GetComponent<Renderer>();
                Vector3 from = r ? r.bounds.center : t.position;
                Vector3 outDir = new Vector3(from.x - center.x, 0f, from.z - center.z);
                if (outDir.sqrMagnitude < 1e-3f) outDir = Random.insideUnitSphere;
                outDir.y = 0f;
                outDir = outDir.normalized;
                // на чём стоит (стол) — вылетаем за его край, а не сквозь столешницу
                float exit = 0f;
                if (Physics.Raycast(from + Vector3.up * 0.05f, Vector3.down, out var sup, 0.6f, ~0, QueryTriggerInteraction.Ignore) && !IsMine(sup.collider))
                {
                    var sb = sup.collider.bounds;
                    sb.Expand(new Vector3(0.25f, 0f, 0.25f));
                    while (exit < 2.5f && sb.Contains(new Vector3(from.x, sb.center.y, from.z) + outDir * exit)) exit += 0.05f;
                }
                Vector3 landC = from + outDir * (exit + Random.Range(0.15f, 0.4f));
                float bottom = r ? r.bounds.min.y : t.position.y;
                float floorY = FloorUnder(landC, bottom);
                Vector3 land = t.position + new Vector3(landC.x - from.x, floorY - bottom, landC.z - from.z);
                float delay = Random.Range(0f, 0.18f);
                float time = Random.Range(0.42f, 0.6f);
                lastLand = Mathf.Max(lastLand, delay + time);
                var s = DOTween.Sequence().SetLink(gameObject).SetDelay(delay);
                s.Append(t.DOJump(land, 0.5f, 1, time).SetEase(Ease.Linear)); // повыше дугу — перелетает край столешницы
                s.Join(t.DORotate(new Vector3(Random.Range(-200f, 200f), Random.Range(-180f, 180f), Random.Range(-200f, 200f)), time, RotateMode.WorldAxisAdd));
                s.AppendCallback(() => Shatter(t, r));
            }
            DOVirtual.DelayedCall(0.25f, Startle).SetLink(gameObject); // звон слышен сразу, как посыпалось
            DOVirtual.DelayedCall(lastLand, () => Play("dish_break", "flash_empty")).SetLink(gameObject);
        }

        static float FloorUnder(Vector3 p, float fromY)
        {
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, fromY + 0.2f, p.z), Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
                if (h.normal.y > 0.7f && h.point.y < fromY - 0.3f) return h.point.y; // пол, а не та же столешница
            return fromY - 0.75f;
        }

        /// <summary>Вещь сплющилась в черепки, вокруг разлетелись осколки.</summary>
        void Shatter(Transform t, Renderer r)
        {
            if (!t) return;
            var ls = t.localScale;
            t.DOScale(new Vector3(ls.x * 1.25f, ls.y * 0.18f, ls.z * 1.25f), 0.06f).SetLink(gameObject);
            var mat = r ? r.sharedMaterial : null;
            for (int i = 0; i < 5; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(s.GetComponent<Collider>());
                s.name = "Shard";
                s.transform.SetParent(transform.parent, true);
                s.transform.position = t.position + Vector3.up * 0.03f;
                s.transform.localScale = Vector3.one * Random.Range(0.025f, 0.05f);
                s.transform.rotation = Random.rotation;
                if (mat) s.GetComponent<Renderer>().sharedMaterial = mat;
                Vector3 to = t.position + new Vector3(Random.Range(-0.5f, 0.5f), 0f, Random.Range(-0.5f, 0.5f));
                s.transform.DOJump(to, Random.Range(0.08f, 0.2f), 1, Random.Range(0.25f, 0.45f)).SetLink(s);
                _shards.Add(s);
            }
        }

        void Startle()
        {
            var ph = Act2Director.Instance ? Act2Director.Instance.photographer : null;
            if (!ph) return;
            var b = PartsBounds();
            Vector3 at = b.center;
            float d = Vector3.Distance(new Vector3(at.x, 0f, at.z), new Vector3(ph.transform.position.x, 0f, ph.transform.position.z));
            float k = ph.InSight(at) ? 1f
                    : Candelabra.SameRoom(ph.transform.position + Vector3.up * 1.6f, at) ? 0.7f
                    : d < 12f ? 0.35f : 0.1f;
            ph.Startle(at, FearOf(kind) * k, this);
        }

        void ShakeNearbyCamera(float strength)
        {
            var cam = Camera.main;
            if (!cam || strength <= 0f) return;
            float d = Vector3.Distance(cam.transform.position, transform.position);
            if (d > 8f) return;
            cam.transform.DOShakePosition(0.3f, strength * (1f - d / 8f) * 0.15f, 20).SetLink(cam.gameObject);
        }

        void Play(string key, string fallback)
        {
            var bank = JamAudio.Instance ? JamAudio.Instance.bank : null;
            var e = bank ? bank.Get(key) : null;
            JamAudio.Play(e != null && e.clips.Count > 0 ? key : fallback, transform.position + Vector3.up);
        }

        void OnDestroy() => All.Remove(this);
    }
}
