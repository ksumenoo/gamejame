using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Временный показ: когда игрок забирает пластину, она сразу открывается на весь экран.
    /// Когда появится проявочная, этот компонент выключаем — пластины будут показываться там.
    /// </summary>
    public class PlateHUD : MonoBehaviour
    {
        public PlateInventory inventory;
        public PlateViewer viewer;

        int _lastCount;

        void OnEnable() { if (inventory) inventory.Changed += OnChanged; }
        void OnDisable() { if (inventory) inventory.Changed -= OnChanged; }

        void OnChanged(PlateInventory inv)
        {
            if (viewer && inv.Count > _lastCount)
            {
                var plate = inv.Plates[inv.Count - 1];
                bool sting = plate.Readings.Any(r => r.IsSharp);
                viewer.ShowAsync(plate, sting).Forget();
            }
            _lastCount = inv.Count;
        }
    }
}
