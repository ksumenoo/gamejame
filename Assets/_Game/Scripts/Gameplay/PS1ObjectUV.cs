using System.Collections.Generic;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Gameplay
{
    /// <summary>
    /// Шейдер дома (Jam/PS1Lit) тайлит текстуру по координатам мира — стенам и полу развёртка не нужна.
    /// Но у того, что движется, мировые координаты поверхности меняются, и текстура «ползёт» по предмету.
    /// Поэтому всему подвижному — предметы в руках, двери, переносная камера, падающие вещи акта 2 —
    /// при загрузке сцены ставим копию материала с _ObjectUV = 1: текстура привязана к самой модели.
    /// </summary>
    public static class PS1ObjectUV
    {
        static readonly int ObjectUVId = Shader.PropertyToID("_ObjectUV");
        static readonly Dictionary<Material, Material> Clones = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplyAll();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) => ApplyAll();

        static void ApplyAll()
        {
            foreach (var c in Object.FindObjectsByType<FirstPersonController>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Apply(c.gameObject);
            foreach (var c in Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Apply(c.gameObject);
            foreach (var c in Object.FindObjectsByType<PortableCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Apply(c.gameObject);
            foreach (var c in Object.FindObjectsByType<Poltergeist>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Apply(c.gameObject);
        }

        /// <summary>Перевести все PS1-материалы под объектом на привязку к модели.</summary>
        public static void Apply(GameObject root)
        {
            if (!root) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (!m || !m.HasProperty(ObjectUVId) || m.GetFloat(ObjectUVId) > 0.5f) continue;
                    if (!Clones.TryGetValue(m, out var c) || !c)
                    {
                        c = new Material(m) { name = m.name + " (object UV)" };
                        c.SetFloat(ObjectUVId, 1f);
                        Clones[m] = c;
                    }
                    mats[i] = c;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
        }
    }
}
