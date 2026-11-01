using System;
using DG.Tweening;
using Jam.Interaction;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Конец акта 2, шаг 1: шкала полна — фотограф оцепенел. Подойти и держать E — последняя пугалка:
    /// экран белеет вместе с удержанием; отпустил — сходит на нет. Дотерпел — Done, фотограф бежит.
    /// Ставится на фотографа в момент оцепенения (Act2Director).
    /// </summary>
    public class PhotographerFinalScare : MonoBehaviour, IInteractable
    {
        public float holdSeconds = 1.6f;
        public float range = 4f;
        public CanvasGroup whiteFlash;
        public event Action Done;

        Interactor _holder;
        float _t;
        bool _done;

        public string Prompt => _done ? "" : "Напугать (держать E)";
        public bool CanInteract(Interactor who) => !_done;

        public void Interact(Interactor who)
        {
            if (_done) return;
            _holder = who;
        }

        void Awake()
        {
            // луч взгляда должен попадать в фигуру: невидимый триггер в рост человека
            if (!GetComponent<Collider>())
            {
                var c = gameObject.AddComponent<CapsuleCollider>();
                c.isTrigger = true;
                c.height = 1.9f;
                c.radius = 0.45f;
                c.center = new Vector3(0f, 0.95f, 0f);
            }
        }

        void Update()
        {
            if (_done) return;
            bool holding = _holder && _holder.UseHeld;
            if (holding)
            {
                Vector3 d = _holder.transform.position - transform.position;
                d.y = 0f;
                holding = d.magnitude <= range;
            }
            _t = holding ? _t + Time.deltaTime : Mathf.MoveTowards(_t, 0f, Time.deltaTime * 2f);
            if (!holding && _t <= 0f) _holder = null;
            if (whiteFlash) whiteFlash.alpha = Mathf.Clamp01(_t / holdSeconds) * 0.75f;
            if (_t > 0f) HoldBar.Report(_t / holdSeconds);
            if (_t >= holdSeconds)
            {
                _done = true;
                Done?.Invoke();
                Destroy(this);
            }
        }
    }

    /// <summary>
    /// Конец акта 2, шаг 2: пластина, которую уронил сбежавший фотограф. Лежит у стула возле входной двери и светится —
    /// её видно и вслед убегающему, и от двери. Поднял (E) — Taken, дальше финальный снимок и титры.
    /// </summary>
    public class DroppedPlate : MonoBehaviour, IInteractable
    {
        public event Action Taken;
        Light _light;
        bool _taken;

        public string Prompt => _taken ? "" : "Поднять пластину";
        public bool CanInteract(Interactor who) => !_taken;

        public void Interact(Interactor who)
        {
            if (_taken) return;
            _taken = true;
            Taken?.Invoke();
            transform.DOScale(0f, 0.3f).SetLink(gameObject).OnComplete(() => Destroy(gameObject));
        }

        public static DroppedPlate Spawn(Vector3 floorPos, float yaw, Texture tex)
        {
            var root = new GameObject("DroppedPlate");
            root.transform.SetPositionAndRotation(floorPos + Vector3.up * 0.01f, Quaternion.Euler(0f, yaw, 0f));

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.name = "Plate";
            quad.transform.SetParent(root.transform, false);
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // лежит лицом вверх
            quad.transform.localScale = new Vector3(0.4f, 0.3f, 1f);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            if (tex) mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", new Color(1.25f, 1.12f, 0.9f));
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;               // взгляд находит, тело проходит
            col.center = new Vector3(0f, 0.15f, 0f);
            col.size = new Vector3(0.9f, 0.35f, 0.9f); // крупнее пластины — легко навести и с потолка

            var lg = new GameObject("Glow");
            lg.transform.SetParent(root.transform, false);
            lg.transform.localPosition = Vector3.up * 0.35f;
            var p = root.AddComponent<DroppedPlate>();
            p._light = lg.AddComponent<Light>();
            p._light.type = LightType.Point;
            p._light.color = new Color(1f, 0.85f, 0.6f);
            p._light.range = 3f;
            p._light.shadows = LightShadows.None;
            return p;
        }

        void Update()
        {
            if (_light) _light.intensity = 1.1f + 0.6f * Mathf.Sin(Time.time * 2.2f); // дышит — заметно издалека
        }
    }
}
