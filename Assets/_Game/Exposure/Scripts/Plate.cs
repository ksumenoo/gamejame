using System.Collections.Generic;
using UnityEngine;

namespace Jam.Exposure
{
    /// <summary>Что камера узнала про объект за выдержку.</summary>
    public struct SubjectReading
    {
        public ExposureSubject Subject;
        /// <summary>Доля выдержки, когда объект был в кадре (0..1).</summary>
        public float VisibleFraction;
        /// <summary>Доля выдержки, проведённая в одной точке (0..1). Высокая = резкий силуэт.</summary>
        public float Sharpness;
        /// <summary>Точка, где объект простоял дольше всего (или где его застала вспышка).</summary>
        public Vector3 SharpestPosition;
        public bool IsSharp;
        public bool CaughtByFlash;
    }

    /// <summary>Готовая пластина: картинка + данные о том, кто на ней отпечатался.</summary>
    public class Plate
    {
        public RenderTexture Image;
        public ObscuraCamera Source;
        public float Duration;
        public float TakenAt;
        public bool Flash;
        /// <summary>Где стоял объектив и куда смотрел в начале выдержки — по этому сцена понимает, «её» ли это кадр.</summary>
        public Vector3 Position;
        public Vector3 Forward;
        /// <summary>Номера сцен прошлого, которые сложились в этом кадре (проставляет StoryDirector в момент съёмки).</summary>
        public readonly List<int> Scenes = new();
        /// <summary>Проявлена, но ни одна сцена не сложилась — пластина «засвечена», пустая.</summary>
        public bool Blank;
        public readonly List<SubjectReading> Readings = new();

        public bool TryGetReading(ExposureSubject subject, out SubjectReading reading)
        {
            foreach (var r in Readings)
            {
                if (r.Subject == subject)
                {
                    reading = r;
                    return true;
                }
            }
            reading = default;
            return false;
        }

        public void Release()
        {
            if (Image == null) return;
            Image.Release();
            Object.Destroy(Image);
            Image = null;
        }
    }
}
