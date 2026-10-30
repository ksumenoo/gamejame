using System;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Сцена прошлого в комнате: набор PastFigure + штатив, который её снимает.
    /// Состояние меняет StoryDirector: скрыта → видна (в обскуре) → проявлена (на пластине).
    /// </summary>
    public class PastScene : MonoBehaviour
    {
        public enum State { Hidden, Visible, Developed }

        [Tooltip("Номер сцены: 1 — столовая, 2 — спальня, ... 6 — проявочная.")]
        public int index = 1;
        [Tooltip("Штатив, снимок с которого засчитывается за эту сцену.")]
        public CameraStation station;
        public State startState = State.Visible;

        [ShowNativeProperty] public State Current { get; private set; } = State.Hidden;

        public event Action<PastScene> StateChanged;

        void Awake() => Apply(startState, notify: false);

        [Button("Скрыть")] void DebugHide() => SetState(State.Hidden);
        [Button("Показать")] void DebugShow() => SetState(State.Visible);

        public void SetState(State state) => Apply(state, notify: true);

        void Apply(State state, bool notify)
        {
            Current = state;
            // проявленная сцена исчезает из комнаты: она теперь живёт на пластине
            bool figuresOn = state == State.Visible;
            foreach (Transform child in transform)
                child.gameObject.SetActive(figuresOn);
            if (notify) StateChanged?.Invoke(this);
        }
    }
}
