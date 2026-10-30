using DG.Tweening;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Фигура из прошлого: квад с шейдером Jam/Billboard на слое Past (глазом не видно, обскуры видят).
    /// Живые фигуры чуть покачиваются — на длинной выдержке они смазываются.
    /// Мёртвые неподвижны — на пластине выходят резкими. Так работает правило «резкий = мёртвый».
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public class PastFigure : MonoBehaviour
    {
        [Tooltip("Живой — качается и смазывается на пластине. Мёртвый — неподвижен и резкий.")]
        public bool alive = true;
        [Min(0f)] public float swayAmplitude = 0.14f;
        [Min(0.2f)] public float swayPeriod = 1.7f;

        Vector3 _base;
        Tween _tw;
        bool _hasBase;

        void OnEnable()
        {
            if (!_hasBase)
            {
                _base = transform.localPosition;
                _hasBase = true;
            }
            transform.localPosition = _base;
            if (!alive || swayAmplitude <= 0f) return;

            // качание в своей плоскости: случайная фаза, чтобы фигуры не двигались синхронно
            Vector3 axis = Vector3.right; // вдоль ряда фигур, в осях сцены
            transform.localPosition = _base - axis * swayAmplitude;
            _tw = transform.DOLocalMove(_base + axis * swayAmplitude, swayPeriod * 0.5f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetDelay(Random.Range(0f, swayPeriod))
                .SetLink(gameObject);
        }

        void OnDisable()
        {
            _tw?.Kill();
            if (_hasBase) transform.localPosition = _base;
        }
    }
}
