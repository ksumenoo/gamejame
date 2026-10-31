using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Отладочный режим без проявочной: пластина «проявляется» сразу при подборе —
    /// открывается на весь экран и засчитывается StoryDirector.
    /// В обычной игре выключено (autoDevelopOnPickup = false), проявка идёт в Darkroom.
    /// </summary>
    public class PlateHUD : MonoBehaviour
    {
        public PlateInventory inventory;
        public PlateViewer viewer;
        [Tooltip("Проявлять пластину сразу при подборе (если проявочной нет или для отладки).")]
        public bool autoDevelopOnPickup;

        int _lastCount;

        void OnEnable() { if (inventory) inventory.Changed += OnChanged; }
        void OnDisable() { if (inventory) inventory.Changed -= OnChanged; }

        void OnChanged(PlateInventory inv)
        {
            if (autoDevelopOnPickup && inv.Count > _lastCount)
            {
                var plate = inv.Plates[inv.Count - 1];
                var scene = StoryDirector.Instance ? StoryDirector.Instance.TryDevelop(plate) : null;
                bool sting = scene || plate.Readings.Any(r => r.IsSharp);
                if (viewer) viewer.ShowAsync(plate, sting).Forget();
            }
            _lastCount = inv.Count;
        }
    }
}
