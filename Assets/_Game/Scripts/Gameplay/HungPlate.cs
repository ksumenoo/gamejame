using System.Linq;
using Cysharp.Threading.Tasks;
using Jam.Exposure;
using Jam.Interaction;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Проявленная пластина на верёвке. E — рассмотреть на весь экран.</summary>
    public class HungPlate : MonoBehaviour, IInteractable
    {
        public Plate Plate { get; private set; }

        PlateViewer _viewer;
        Material _mat;

        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public string Prompt => "Рассмотреть пластину";
        public bool CanInteract(Interactor who) => _viewer && !_viewer.IsOpen && Plate != null;

        public void Interact(Interactor who)
        {
            bool sharp = Plate.Readings.Any(r => r.IsSharp);
            _viewer.ShowAsync(Plate, sharp).Forget();
        }

        public void Init(Plate plate, PlateViewer viewer, Material template)
        {
            Plate = plate;
            _viewer = viewer;

            var r = GetComponent<Renderer>();
            if (!r) return;
            if (template)
            {
                _mat = new Material(template);
                _mat.SetTexture(BaseMap, plate.Image);
                // переворот как у обскуры
                _mat.SetTextureScale(BaseMap, new Vector2(-1f, -1f));
                _mat.SetTextureOffset(BaseMap, new Vector2(1f, 1f));
                // тёплый тон «под сепию»; пустая пластина — белёсая, засвеченная
                _mat.SetColor(BaseColor, plate.Blank ? new Color(1.6f, 1.55f, 1.45f) : new Color(0.95f, 0.82f, 0.62f));
                r.sharedMaterial = _mat;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void OnDestroy()
        {
            Plate?.Release();
            if (_mat) Destroy(_mat);
        }
    }
}
