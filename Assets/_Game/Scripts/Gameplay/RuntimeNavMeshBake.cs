using Unity.AI.Navigation;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Строит NavMesh при старте сцены. Дом маленький, это занимает миллисекунды,
    /// зато не нужно хранить запечённый ассет и не забыть перезапечь после правок дома.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(NavMeshSurface))]
    public class RuntimeNavMeshBake : MonoBehaviour
    {
        void Awake() => GetComponent<NavMeshSurface>().BuildNavMesh();
    }
}
