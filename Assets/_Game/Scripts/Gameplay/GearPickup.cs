using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using Jam.Audio;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Начало игры. У фотографа только свеча, бежать нельзя — только тихий шаг. Он идёт по тёмному дому
    /// к проявочной, по пути сам гаснет канделябр: Маргарет рядом, но её не видно.
    /// В проявочной — нулевая сцена-обучение: штатив Маргарет уже стоит и смотрит на верстак,
    /// на верстаке лежит её обскура. Взял обскуру → «Shift — бег», в стекле видна она сама у верстака.
    /// Открыл заслонку у готового штатива, забрал пластину, проявил тут же — механика понятна на примере.
    /// Пока нулевая сцена не проявлена, Маргарет только бродит (агрессия = число проявленных сцен).
    /// Ставится сам при запуске сцены (пересобирать уровень не нужно). Выключить: GearPickup.Enabled = false.
    /// </summary>
    public class GearPickup : MonoBehaviour, IInteractable
    {
        public static bool Enabled = true;
        /// <summary>Обскура ещё лежит в проявочной.</summary>
        public static bool Waiting { get; private set; }
        public static event Action Taken;

        [Tooltip("Когда игрок впервые подходит к канделябру ближе этого по пути — тот гаснет сам, м.")]
        public float scareRadius = 5f;

        HandObscura _hand;
        PortableCamera _portable;
        MagnesiumFlash _flash;
        FlashCounterUI _flashUi;
        FirstPersonController _player;
        Candelabra _scareCandle;
        bool _scared;
        Vector3? _startPos;

        public string Prompt => "Взять обскуру";
        public bool CanInteract(Interactor who) => Waiting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Waiting = false; Taken = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!Enabled || FindAnyObjectByType<GearPickup>()) return;
            var room = FindAnyObjectByType<Darkroom>();
            var hand = FindAnyObjectByType<HandObscura>();
            if (!room || !room.standPoint || !hand) return;
            var go = new GameObject("GearPickup");
            go.AddComponent<GearPickup>().Setup(room, hand);
        }

        void Setup(Darkroom room, HandObscura hand)
        {
            _hand = hand;
            _player = hand.GetComponentInParent<FirstPersonController>();
            _portable = FindAnyObjectByType<PortableCamera>();
            _flash = FindAnyObjectByType<MagnesiumFlash>();
            _flashUi = FindAnyObjectByType<FlashCounterUI>(FindObjectsInactive.Include);

            // оси проявочной: fwd — от места игрока к верстаку, into — от верстака в комнату
            Transform sp = room.standPoint;
            Vector3 fwd = sp.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 into = -fwd;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 bench = room.transform.position; // корень верстака стоит на полу

            // обскура лежит на свободном месте верстака — не на кюветах и не на сушилке
            Vector3 top = FreeBenchSpot(sp.position, fwd, right);
            transform.SetPositionAndRotation(top, Quaternion.LookRotation(into, Vector3.up));
            if (hand.handModel)
            {
                // в руке модель повёрнута под камеру — копируем относительно взгляда без наклона, чтобы на столе стояла ровно
                Transform eyes = hand.eyes ? hand.eyes.transform : hand.transform;
                Vector3 look = Flat(eyes.forward);
                if (look.sqrMagnitude < 1e-4f) look = Vector3.forward;
                CopyMeshes(hand.handModel, transform, eyes.position, Quaternion.LookRotation(look.normalized, Vector3.up));
                // донышком на столешницу, центром в точку
                var bb = new Bounds();
                bool any = false;
                foreach (var r in GetComponentsInChildren<Renderer>()) { if (!any) { bb = r.bounds; any = true; } else bb.Encapsulate(r.bounds); }
                if (any)
                {
                    Vector3 shift = new Vector3(top.x - bb.center.x, top.y - bb.min.y, top.z - bb.center.z);
                    foreach (Transform c in transform) c.position += shift;
                }
            }
            var col = gameObject.AddComponent<BoxCollider>();
            var b = new Bounds(transform.position, Vector3.one * 0.15f);
            foreach (var r in GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            col.center = transform.InverseTransformPoint(b.center);
            col.size = b.size + Vector3.one * 0.05f;
            col.isTrigger = true; // не мешает ходить, но ловится лучом взаимодействия

            // нулевая сцена — в прихожей: она сама у семейного альбома, её штатив уже стоит напротив.
            // Игрок видит штатив с первых шагов, а смысл его понимает, когда вернётся с обскурой.
            var album = FindAnyObjectByType<FamilyAlbum>();
            if (_portable && album && _player)
            {
                Vector3 a = album.transform.position;
                Vector3 toSpawn = Flat(_player.transform.position - a);
                // поперёк прихожей — по главной оси от альбома к центру; вдоль — прочь от места старта
                Vector3 n = Mathf.Abs(toSpawn.x) >= Mathf.Abs(toSpawn.z) ? new Vector3(Mathf.Sign(toSpawn.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(toSpawn.z));
                Vector3 t = Vector3.Cross(Vector3.up, n);
                if (Vector3.Dot(t, toSpawn) > 0f) t = -t;
                float floorY = _player.transform.position.y;
                Vector3 figurePos = new Vector3(a.x, floorY, a.z) + n * 0.55f;
                Vector3 tripodPos = new Vector3(a.x, floorY, a.z) + n * 1.7f + t * 1.2f;
                _portable.PlaceAt(tripodPos, Flat(figurePos - tripodPos));
                var st = _portable.GetComponent<CameraStation>();
                if (st && st.obscura) CreateSceneZero(st.obscura.transform, figurePos, album.transform.parent);
            }
            else if (_portable)
            {
                // запасной вариант: альбома нет — всё в проявочной, как раньше
                Vector3 figurePos = bench + into * 0.45f - right * 0.4f;
                Vector3 tripodPos = bench + into * 2.1f - right * 0.8f;
                tripodPos.y = sp.position.y - 0.05f;
                _portable.PlaceAt(tripodPos, Flat(figurePos - tripodPos));
                var st = _portable.GetComponent<CameraStation>();
                if (st && st.obscura) CreateSceneZero(st.obscura.transform, figurePos, room.transform.parent);
            }

            // до нулевой проявки в обскуре пусто: ни остальных сцен, ни Маргарет — учимся на одной
            HideUntilFirstPlate();

            SetGear(false);
            Waiting = true;
            if (_player) _player.ForceSneak = true;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>Свободное место на столешнице: луч сверху попадает в саму столешницу, рядом ничего не стоит.</summary>
        static Vector3 FreeBenchSpot(Vector3 stand, Vector3 fwd, Vector3 right)
        {
            Vector3 fallback = stand + fwd * 0.75f + Vector3.up * 0.9f;
            float bestScore = float.MaxValue;
            Vector3 best = fallback;
            for (float depth = 0.6f; depth <= 0.95f; depth += 0.175f)
            for (float side = -0.75f; side <= 0.751f; side += 0.125f)
            {
                Vector3 probe = stand + fwd * depth + right * side + Vector3.up * 2.2f;
                if (!Physics.Raycast(probe, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore)) continue;
                // ровная поверхность на высоте столешницы — не кювета, не сушилка, не фонарь
                float h = hit.point.y - stand.y;
                if (hit.normal.y < 0.9f || h < 0.6f || h > 1.15f) continue;
                string n = hit.collider.name.ToLowerInvariant();
                bool benchItself = n.StartsWith("bench") || n.Contains("bench") || n.Contains("verstak") || n.Contains("table") || n.Contains("stol");
                if (n.StartsWith("dr_") && !benchItself) continue; // кювета, сушилка, фонарь от 3D-шника
                // вокруг точки пусто (кроме самой столешницы)
                bool busy = false;
                foreach (var c in Physics.OverlapBox(hit.point + Vector3.up * 0.14f, new Vector3(0.16f, 0.12f, 0.16f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                    if (c != hit.collider && !c.name.StartsWith("Bench")) { busy = true; break; }
                if (busy) continue;
                float score = Mathf.Abs(side) * 0.3f + Mathf.Abs(depth - 0.75f); // ближе к середине и к краю, где стоят
                if (score < bestScore) { bestScore = score; best = hit.point; }
            }
            return best;
        }

        StoryDirector _story;
        readonly List<PastScene> _held = new();
        int _ghostBit;

        void HideUntilFirstPlate()
        {
            _story = StoryDirector.Instance ? StoryDirector.Instance : FindAnyObjectByType<StoryDirector>();
            foreach (var s in PastScene.All)
                if (s && s.index > 0 && s.Current == PastScene.State.Visible) { s.SetState(PastScene.State.Hidden); _held.Add(s); }
            int ghost = LayerMask.NameToLayer("Ghost");
            if (_hand && ghost >= 0 && (_hand.cullingMask & (1 << ghost)) != 0)
            {
                _ghostBit = 1 << ghost;
                _hand.cullingMask &= ~_ghostBit;
            }
            if (_story) _story.SceneDeveloped += OnSceneDeveloped;
        }

        void OnSceneDeveloped(PastScene s)
        {
            if (!s || s.index != 0) return;
            if (_story) _story.SceneDeveloped -= OnSceneDeveloped;
            // первая пластина проявлена — дом «открывается»: сцены 1–4 видны, Маргарет снова в стекле, и она просыпается
            foreach (var h in _held) if (h && h.Current == PastScene.State.Hidden) h.SetState(PastScene.State.Visible);
            _held.Clear();
            if (_hand && _ghostBit != 0) _hand.cullingMask |= _ghostBit;
        }

        /// <summary>Сцена 0 из готовой фигуры (мать из сцены 2): та же логика, что у остальных шести.</summary>
        static void CreateSceneZero(Transform lens, Vector3 floorPos, Transform parent)
        {
            var src = PastScene.All.Where(s => s).OrderBy(s => s.index == 2 ? 0 : 1)
                .SelectMany(s => s.GetComponentsInChildren<PastFigure>(true))
                .FirstOrDefault(f => f.name.Contains("Mother")) ?? FindAnyObjectByType<PastFigure>(FindObjectsInactive.Include);
            if (!src) { Debug.LogWarning("[Gear] Нет фигур — нулевая сцена не создана."); return; }

            var vp = new GameObject("Scene_00_View").transform;
            vp.SetParent(parent, false);
            vp.SetPositionAndRotation(lens.position, Quaternion.LookRotation(Flat(floorPos - lens.position), Vector3.up));

            var go = new GameObject("Scene_00");
            go.SetActive(false); // Awake сцены — после того, как заданы точка и фигура
            go.transform.SetParent(parent, false);
            go.layer = src.gameObject.layer;
            go.transform.SetPositionAndRotation(floorPos, Quaternion.LookRotation(Flat(lens.position - floorPos) * -1f));

            var fig = Instantiate(src.gameObject, go.transform);
            fig.name = "Scene0_" + src.name;
            fig.SetActive(true);
            float h = fig.transform.lossyScale.y;
            fig.transform.localPosition = new Vector3(0f, h * 0.5f, 0f);
            fig.transform.localRotation = Quaternion.identity;
            var pf = fig.GetComponent<PastFigure>();
            if (pf) pf.alive = true; // при жизни — качается, на пластине выйдет смазанной

            var scene = go.AddComponent<PastScene>();
            scene.index = 0;
            scene.viewPoint = vp;
            scene.startState = PastScene.State.Visible;
            scene.viewRadius = 0.8f;
            scene.viewFade = 3.5f; // комната маленькая: проступает по мере подхода к штативу
            go.SetActive(true);

            var story = StoryDirector.Instance ? StoryDirector.Instance : FindAnyObjectByType<StoryDirector>();
            if (story && !story.scenes.Contains(scene)) story.scenes.Insert(0, scene);
        }

        /// <summary>Копии мешей: положение и поворот — относительно рамки (origin, frame) исходника.</summary>
        static void CopyMeshes(Transform src, Transform parent, Vector3 origin, Quaternion frame)
        {
            var inv = Quaternion.Inverse(frame);
            foreach (var mr in src.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var go = new GameObject(mr.name + "_Copy");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = inv * (mr.transform.position - origin);
                go.transform.localRotation = inv * mr.transform.rotation;
                go.transform.localScale = mr.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                go.layer = 0;
            }
        }

        void SetGear(bool on)
        {
            if (_hand)
            {
                if (!on) _hand.SetHeld(false);
                _hand.enabled = on;
                if (_hand.handModel) _hand.handModel.gameObject.SetActive(on);
            }
            if (_flash)
            {
                _flash.enabled = on;
                if (_flash.viewModel) _flash.viewModel.gameObject.SetActive(on);
            }
            if (_flashUi) _flashUi.gameObject.SetActive(on);
        }

        public void Interact(Interactor who)
        {
            if (!Waiting) return;
            Waiting = false;
            SetGear(true);
            if (_player) _player.ForceSneak = false;
            JamAudio.Play("shutter_close", transform.position);
            // бег открывается именно здесь — игрок сразу понимает, что дальше придётся от кого-то бегать
            Thoughts.Say("k_run", null, "Shift — бег");
            Taken?.Invoke();
            // сам объект остаётся (ждёт проявки нулевой сцены), исчезает только копия обскуры на столе
            var col = GetComponent<Collider>();
            if (col) col.enabled = false;
            transform.DOScale(0f, 0.25f).SetLink(gameObject);
        }

        void Update()
        {
            // по пути к проявочной первый канделябр рядом с игроком гаснет сам — дом не пуст
            if (!Waiting || _scared || !_player || _player.InputLocked) return;
            Vector3 me = _player.transform.position;
            if (_startPos == null) _startPos = me;
            if ((me - _startPos.Value).magnitude < 4f) return; // не сразу у входа: сначала пара шагов
            foreach (var c in Candelabra.All)
            {
                if (!c || !c.HasFlame || !c.IsLit) continue;
                Vector3 d = c.transform.position - me; d.y = 0f;
                if (d.magnitude > scareRadius || !Candelabra.SameRoom(me + Vector3.up, c.transform.position + Vector3.up)) continue;
                _scared = true;
                _scareCandle = c;
                DOVirtual.DelayedCall(0.8f, () =>
                {
                    if (!_scareCandle) return;
                    _scareCandle.Extinguish();
                    JamAudio.Play("margaret_whisper", _scareCandle.transform.position + Vector3.up);
                }).SetLink(gameObject);
                break;
            }
        }
    }
}
