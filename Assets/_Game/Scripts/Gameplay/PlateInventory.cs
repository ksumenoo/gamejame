using System;
using System.Collections.Generic;
using Jam.Exposure;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Пластины, которые игрок несёт в проявочную.</summary>
    public class PlateInventory : MonoBehaviour
    {
        [Min(1)] public int capacity = 3;

        readonly List<Plate> _plates = new();
        public IReadOnlyList<Plate> Plates => _plates;
        public bool HasRoom => _plates.Count < capacity;
        public int Count => _plates.Count;

        public event Action<PlateInventory> Changed;

        public bool Add(Plate plate)
        {
            if (plate == null || !HasRoom) return false;
            _plates.Add(plate);
            Changed?.Invoke(this);
            return true;
        }

        /// <summary>Отдать все пластины (например, в проявочную).</summary>
        public List<Plate> TakeAll()
        {
            var result = new List<Plate>(_plates);
            _plates.Clear();
            Changed?.Invoke(this);
            return result;
        }

        /// <summary>Потерять пластины (призрак дотянулся).</summary>
        public void DropAll()
        {
            foreach (var p in _plates) p.Release();
            _plates.Clear();
            Changed?.Invoke(this);
        }
    }
}
