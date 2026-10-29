using System.Collections.Generic;
using UnityEngine;

namespace Jam.Exposure
{
    /// <summary>
    /// Вешается на всё, что камера-обскура должна "замечать" геймплейно:
    /// призрака, игрока (в акте 2), фотографа. Картинку на пластине это не меняет —
    /// картинку даёт сам рендер, а здесь только данные для логики.
    /// </summary>
    public class ExposureSubject : MonoBehaviour
    {
        public static readonly List<ExposureSubject> All = new();

        [Tooltip("Точка, по которой проверяем видимость (голова/центр тела). Пусто = transform.")]
        public Transform probe;

        [Tooltip("Множитель резкости от освещения. >1 — светло, отпечатывается быстрее. Можно менять из зон света.")]
        [Min(0f)] public float lightFactor = 1f;

        [Tooltip("Если выключено — камеры объект игнорируют (например, игрок в акте 1).")]
        public bool detectable = true;

        public Vector3 ProbePosition => probe ? probe.position : transform.position;

        /// <summary>Накопленная проявленность (акт 2: игрок). 0..1, 1 = пойман.</summary>
        public float Exposure { get; set; }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);
    }
}
