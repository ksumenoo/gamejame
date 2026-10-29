using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Interaction
{
    /// <summary>Всё, с чем можно взаимодействовать на E.</summary>
    public interface IInteractable
    {
        /// <summary>Текст подсказки. Может меняться каждый кадр ("Выдержка… 40%").</summary>
        string Prompt { get; }
        /// <summary>Можно ли нажать прямо сейчас. Если нет — подсказка показывается серой.</summary>
        bool CanInteract(Interactor who);
        void Interact(Interactor who);
    }

    /// <summary>
    /// Луч из центра камеры игрока. Находит IInteractable на объекте или его родителях, жмёт по E.
    /// </summary>
    public class Interactor : MonoBehaviour
    {
        public Camera viewCamera;
        [Min(0.5f)] public float range = 2.2f;
        public LayerMask mask = ~0;

        public IInteractable Current { get; private set; }
        public bool Locked { get; set; }

        public event Action<IInteractable> FocusChanged;
        public event Action<IInteractable> Interacted;

        InputAction _use;

        void Awake()
        {
            if (!viewCamera) viewCamera = GetComponentInChildren<Camera>();
            _use = new InputAction("Use", InputActionType.Button, "<Keyboard>/e");
        }

        void OnEnable() => _use.Enable();
        void OnDisable() => _use.Disable();
        void OnDestroy() => _use.Dispose();

        void Update()
        {
            IInteractable found = null;
            if (!Locked && viewCamera)
            {
                var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                if (Physics.Raycast(ray, out var hit, range, mask, QueryTriggerInteraction.Collide))
                    found = hit.collider.GetComponentInParent<IInteractable>();
            }

            if (!ReferenceEquals(found, Current))
            {
                Current = found;
                FocusChanged?.Invoke(Current);
            }

            if (Current != null && _use.WasPressedThisFrame() && Current.CanInteract(this))
            {
                Current.Interact(this);
                Interacted?.Invoke(Current);
            }
        }
    }
}
