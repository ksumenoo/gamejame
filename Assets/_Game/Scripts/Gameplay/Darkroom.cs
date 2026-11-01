using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using Jam.Audio;
using Jam.Exposure;
using Jam.Interaction;
using Jam.Player;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Проявочная. Верстак с кюветами: E — «Проявить пластины».
    /// Красный свет, несколько секунд в темноте, затем пластины висят на верёвке (их можно рассмотреть по E),
    /// StoryDirector засчитывает сцены, ставится чекпойнт у верстака. Проявленная сцена сразу показывается на весь экран.
    /// Зона комнаты — просто Bounds (без триггер-коллайдера, чтобы не мешать лучу взаимодействия).
    /// </summary>
    public class Darkroom : MonoBehaviour, IInteractable
    {
        [Header("Ссылки")]
        public PlateInventory inventory;
        public PlateViewer viewer;
        public FirstPersonController player;
        public Interactor interactor;
        public HandObscura hand;
        public DeathAndCheckpoint checkpoint;

        [Header("Комната")]
        [Tooltip("Где стоит игрок у верстака — сюда же ставится чекпойнт.")]
        public Transform standPoint;
        [Tooltip("Красная лампа.")]
        public Light redLight;
        public float redIdle = 0.5f;
        public float redDeveloping = 2.2f;
        [Tooltip("Пластина в сумке, игрок снаружи: красный свет разгорается и пульсирует, выливаясь в коридор, — зовёт проявлять.")]
        public float redCalling = 1.6f;
        public float redCallingRange = 8f;
        [Tooltip("Обычный свет комнаты — гаснет, пока игрок внутри.")]
        public Light roomLight;
        public Bounds roomBounds;

        [Header("Верёвка")]
        public Transform rope;
        public Vector3 ropeStart = new(-1.1f, 0f, 0f);
        public Vector3 ropeEnd = new(1.1f, 0f, 0f);
        [Min(1)] public int ropeSlots = 6;
        public Vector2 hungPlateSize = new(0.32f, 0.24f);
        [Tooltip("Материал URP/Unlit для висящих пластин — копируется на каждую.")]
        public Material hungPlateMaterial;
        [Tooltip("Шаблон прищепки (выключенный объект). Копия вешается на верх каждой пластины.")]
        public GameObject clothespin;

        [Header("Проявка")]
        [Min(0.1f)] public float developSeconds = 0.8f;

        [Header("Звук (необязательно)")]
        public AudioSource audioSource;
        public AudioClip developClip;

        [ShowNativeProperty] public bool Busy { get; private set; }
        [ShowNativeProperty] public bool PlayerInside { get; private set; }

        public event Action<Darkroom, IReadOnlyList<Plate>> PlatesDeveloped;
        public event Action<Darkroom> DevelopStarted;

        readonly List<HungPlate> _hung = new();
        float _roomLightBase;

        public string Prompt => Busy ? "Проявка…"
            : inventory && inventory.Count > 0 ? $"Проявить пластины ({inventory.Count})"
            : "Нет пластин";

        public bool CanInteract(Interactor who) => !Busy && inventory && inventory.Count > 0;

        public void Interact(Interactor who)
        {
            if (CanInteract(who)) DevelopAsync(destroyCancellationToken).Forget();
        }

        void Start()
        {
            if (roomLight) _roomLightBase = roomLight.intensity;
            if (redLight) redLight.intensity = redIdle;
        }

        float _redRangeBase = -1f;
        float _nextBeacon;

        /// <summary>Без текста: в сумке есть пластина — красный свет проявочной зовёт из коридора.</summary>
        void UpdateCalling()
        {
            if (!redLight || Busy) return;
            if (_redRangeBase < 0f) _redRangeBase = redLight.range;
            // зовёт, пока камеры ждут на верстаке, и когда в сумке есть что проявить
            bool calling = ((inventory && inventory.Count > 0) || GearPickup.Waiting) && !PlayerInside;
            float targetI = calling ? redCalling * (0.75f + 0.25f * Mathf.Sin(Time.time * 2.2f)) : redIdle;
            float targetR = calling ? redCallingRange : _redRangeBase;
            redLight.intensity = Mathf.MoveTowards(redLight.intensity, targetI, Time.deltaTime * 2f);
            // в сумке пластины — проявочная ещё и щёлкает затвором: тот же звук, что ведёт к обскуре и к штативу
            if (calling && !GearPickup.Waiting && Time.time >= _nextBeacon)
            {
                _nextBeacon = Time.time + 8f;
                if (player && (player.transform.position - transform.position).sqrMagnitude > 4f * 4f)
                    JamAudio.Play("shutter_close", transform.position + Vector3.up);
            }
            redLight.range = Mathf.MoveTowards(redLight.range, targetR, Time.deltaTime * 4f);
        }

        void Update()
        {
            UpdateCalling();
            if (!player) return;
            bool inside = roomBounds.size != Vector3.zero && roomBounds.Contains(player.transform.position + Vector3.up);
            if (inside == PlayerInside) return;
            PlayerInside = inside;
            if (roomLight)
            {
                roomLight.DOKill();
                roomLight.DOIntensity(inside ? 0f : _roomLightBase, 0.6f).SetLink(gameObject);
            }
        }

        [Button("Проявить (тест)")]
        void DebugDevelop()
        {
            if (Application.isPlaying) DevelopAsync(destroyCancellationToken).Forget();
        }

        async UniTaskVoid DevelopAsync(CancellationToken ct)
        {
            if (Busy || !inventory || inventory.Count == 0) return;
            Busy = true;
            var plates = inventory.TakeAll();
            PastScene developedScene = null;
            Plate developedPlate = null;

            try
            {
                if (hand) hand.SetHeld(false);
                // управление не отнимаем: короткий всплеск красного — и сразу снимок
                if (audioSource && developClip) audioSource.PlayOneShot(developClip);
                DevelopStarted?.Invoke(this);

                if (redLight)
                {
                    redLight.DOKill();
                    redLight.DOIntensity(redDeveloping, 0.8f).SetLink(gameObject);
                }
                await UniTask.Delay(TimeSpan.FromSeconds(developSeconds), cancellationToken: ct);

                // тёмные сцены (коридор, проявочная) иначе на пластине — только шум и белый призрак
                foreach (var plate in plates) AutoLevels(plate.Image);

                foreach (var plate in plates)
                {
                    var scene = StoryDirector.Instance ? StoryDirector.Instance.TryDevelop(plate) : null;
                    plate.Blank = scene == null && plate.Scenes.Count == 0;
                    Hang(plate);
                    if (scene && !developedScene)
                    {
                        developedScene = scene;
                        developedPlate = plate;
                    }
                }

                if (checkpoint && standPoint) checkpoint.SetCheckpoint(standPoint.position, standPoint.rotation);
                if (redLight)
                {
                    redLight.DOKill();
                    redLight.DOIntensity(redIdle, 1.2f).SetLink(gameObject);
                }
            }
            finally
            {
                Busy = false;
            }

            PlatesDeveloped?.Invoke(this, plates);

            // сюжетная пластина — сразу на весь экран, со стингером
            if (viewer)
            {
                var toShow = developedPlate ?? plates.LastOrDefault();
                if (toShow != null) await viewer.ShowAsync(toShow, sting: developedScene != null);
            }
        }

        /// <summary>
        /// Автояркость пластины при проявке: самое тёмное — в чёрное, светлое — в белое (яркий призрак может уйти в белое),
        /// средние тона чуть приподняты. Тёмная сцена становится читаемой, светлая почти не меняется.
        /// </summary>
        static void AutoLevels(RenderTexture rt)
        {
            if (!rt) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = prev;
            var px = t.GetPixels32();
            var hist = new int[256];
            foreach (var c in px) hist[(c.r * 77 + c.g * 150 + c.b * 29) >> 8]++;
            int Pct(float q)
            {
                int need = (int)(px.Length * q), acc = 0;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= need) return i; }
                return 255;
            }
            int lo = Pct(0.01f), hi = Pct(0.96f), mid = Pct(0.5f);
            if (hi - lo < 10) { Destroy(t); return; } // ровный кадр — не трогаем
            // тёмный кадр с парой ярких пятен (красный свет, свеча): растяжка по светлым почти ничего не даёт —
            // тогда тянем по середине тонов, яркие пятна пусть уходят в белое
            float gain = Mathf.Min(5f, Mathf.Max(235f / (hi - lo), 105f / Mathf.Max(1, mid - lo)));
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                float v = Mathf.Clamp01(((i - lo) * gain + 8f) / 255f);
                lut[i] = (byte)Mathf.RoundToInt(Mathf.Pow(v, 0.85f) * 255f); // средние тона чуть светлее
            }
            for (int i = 0; i < px.Length; i++) { var c = px[i]; px[i] = new Color32(lut[c.r], lut[c.g], lut[c.b], c.a); }
            t.SetPixels32(px);
            t.Apply();
            Graphics.Blit(t, rt);
            Destroy(t);
        }

        /// <summary>Продолжение из сохранения: снимок снова висит на верёвке.</summary>
        public void HangRestored(Texture tex)
        {
            if (!tex) return;
            var rt = new RenderTexture(tex.width, tex.height, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "RestoredPlate" };
            rt.Create();
            Graphics.Blit(tex, rt);
            Hang(new Plate { Image = rt });
        }

        void Hang(Plate plate)
        {
            if (!rope || plate == null || !plate.Image) return;

            if (_hung.Count >= ropeSlots)
            {
                var oldest = _hung[0];
                _hung.RemoveAt(0);
                if (oldest) Destroy(oldest.gameObject); // пластину при этом освобождает HungPlate
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "HungPlate";
            Destroy(go.GetComponent<Collider>());
            var box = go.AddComponent<BoxCollider>(); // у квада MeshCollider односторонний — луч сзади не попадёт
            box.size = new Vector3(1f, 1f, 0.05f);
            box.isTrigger = true; // взгляд (E) её находит, а тело проходит сквозь — не застрять под верёвкой

            go.transform.SetParent(rope, false);
            go.transform.localScale = new Vector3(hungPlateSize.x, hungPlateSize.y, 1f);
            go.transform.localRotation = Quaternion.identity;

            if (clothespin)
            {
                // держатель гасит неравномерный масштаб квада — прищепка не вытягивается
                var holder = new GameObject("Clothespin").transform;
                holder.SetParent(go.transform, false);
                holder.localScale = new Vector3(1f / hungPlateSize.x, 1f / hungPlateSize.y, 1f);
                holder.localPosition = new Vector3(0f, 0.5f - 0.045f / hungPlateSize.y, 0f);
                var pin = Instantiate(clothespin, holder);
                pin.transform.localPosition = Vector3.zero;
                pin.SetActive(true);
            }

            var hung = go.AddComponent<HungPlate>();
            hung.Init(plate, viewer, hungPlateMaterial);
            _hung.Add(hung);
            Relayout();

            // пластина «вешается» — коротко качнуть
            go.transform.DOPunchRotation(new Vector3(0f, 0f, 12f), 0.6f, 6, 0.4f).SetLink(go);
        }

        void Relayout()
        {
            int n = _hung.Count;
            for (int i = 0; i < n; i++)
            {
                float t = ropeSlots <= 1 ? 0.5f : (float)i / (ropeSlots - 1);
                _hung[i].transform.localPosition = Vector3.Lerp(ropeStart, ropeEnd, t) + Vector3.down * (hungPlateSize.y * 0.5f + 0.03f);
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.4f);
            Gizmos.DrawWireCube(roomBounds.center, roomBounds.size);
            if (rope)
            {
                Gizmos.DrawLine(rope.TransformPoint(ropeStart), rope.TransformPoint(ropeEnd));
            }
        }
#endif
    }
}
