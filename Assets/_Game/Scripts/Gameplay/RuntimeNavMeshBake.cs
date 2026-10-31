using Unity.AI.Navigation;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Устарело: NavMesh теперь запекается в редакторе при сборке уровня (LevelBuilder).
    /// Оставлено для старых сцен — строит NavMesh при старте, только если запечённого нет.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(NavMeshSurface))]
    public class RuntimeNavMeshBake : MonoBehaviour
    {
        void Awake()
        {
            var s = GetComponent<NavMeshSurface>();
            if (!s.navMeshData) s.BuildNavMesh();
        }
    }
}
