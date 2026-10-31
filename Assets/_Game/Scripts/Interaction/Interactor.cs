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
        [Min(0.5f)] public float range = 3f;
        [Tooltip("Если точный луч ничего не задел — пробуем «толстым» лучом этого радиуса, чтобы не целиться в пиксель.")]
        [Min(0f)] public float assistRadius = 0.15f;
        public LayerMask mask = ~0;

        public IInteractable Current { get; private set; }
        public bool Locked { get; set; }
        /// <summary>E зажата прямо сейчас (для действий «держать E»).</summary>
        public bool UseHeld => _use != null && _use.IsPressed();

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
                // тонкие предметы (подставка канделябра, ящик камеры) — прощаем промах на пару сантиметров
                if (found == null && assistRadius > 0f &&
                    Physics.SphereCast(ray, assistRadius, out var sh, range, mask, QueryTriggerInteraction.Collide))
                    found = sh.collider.GetComponentInParent<IInteractable>();
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
