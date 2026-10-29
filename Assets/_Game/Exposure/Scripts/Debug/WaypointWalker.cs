using System.Collections.Generic;
using UnityEngine;

namespace Jam.Exposure.Debugging
{
    /// <summary>
    /// Тестовый "призрак" без NavMesh: ходит по точкам и стоит на некоторых.
    /// Нужен, чтобы проверить шлейф и резкость до того, как появится настоящий ИИ.
    /// </summary>
    public class WaypointWalker : MonoBehaviour
    {
        [System.Serializable]
        public struct Stop
        {
            public Transform point;
            [Min(0f)] public float wait;
            [Tooltip("Повернуться лицом к этой цели во время стояния (например, к камере).")]
            public Transform lookAt;
        }

        public List<Stop> route = new();
        [Min(0.1f)] public float speed = 1.4f;

        int _index;
        float _waitLeft;

        void Update()
        {
            if (route.Count == 0) return;
            var stop = route[_index];
            if (!stop.point) { Next(); return; }

            if (_waitLeft > 0f)
            {
                _waitLeft -= Time.deltaTime;
                if (stop.lookAt)
                {
                    Vector3 dir = stop.lookAt.position - transform.position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 4f);
                }
                if (_waitLeft <= 0f) Next();
                return;
            }

            Vector3 target = stop.point.position;
            transform.position = Vector3.MoveTowards(transform.position, target, speed * Time.deltaTime);
            Vector3 move = target - transform.position;
            move.y = 0f;
            if (move.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(move), Time.deltaTime * 8f);

            if ((transform.position - target).sqrMagnitude < 0.0004f)
                _waitLeft = Mathf.Max(stop.wait, 0.0001f);
        }

        void Next() => _index = (_index + 1) % route.Count;
    }
}
