using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>
    /// Временный показ пластины: когда игрок забирает пластину, она проявляется в углу экрана.
    /// Заменится проявочной.
    /// </summary>
    public class PlateHUD : MonoBehaviour
    {
        public PlateInventory inventory;
        public RawImage view;
        [Tooltip("Материал с шейдером Jam/PlateDisplay")]
        public Material plateMaterial;
        [Min(0.1f)] public float developSeconds = 2.5f;

        Material _mat;
        int _lastCount;
        float _t = -1f;

        static readonly int DevelopId = Shader.PropertyToID("_Develop");

        void Awake()
        {
            if (plateMaterial) _mat = new Material(plateMaterial);
            if (view)
            {
                view.material = _mat;
                view.enabled = false;
            }
        }

        void OnEnable() { if (inventory) inventory.Changed += OnChanged; }
        void OnDisable() { if (inventory) inventory.Changed -= OnChanged; }

        void OnChanged(PlateInventory inv)
        {
            if (!view) return;
            if (inv.Count > _lastCount)
            {
                var plate = inv.Plates[inv.Count - 1];
                view.texture = plate.Image;
                view.enabled = true;
                _t = 0f;
                if (_mat) _mat.SetFloat(DevelopId, 0f);
            }
            else if (inv.Count == 0)
            {
                view.enabled = false;
            }
            _lastCount = inv.Count;
        }

        void Update()
        {
            if (_t < 0f || !_mat) return;
            _t += Time.deltaTime / developSeconds;
            _mat.SetFloat(DevelopId, Mathf.Clamp01(_t));
            if (_t >= 1f) _t = -1f;
        }

        void OnDestroy()
        {
            if (_mat) Destroy(_mat);
        }
    }
}
