using UnityEngine;
using UnityEngine.InputSystem;

namespace Jam.Player
{
    /// <summary>
    /// Контроллер от первого лица: WASD, мышь, Shift — бег, C — красться.
    /// Скорость важна для механики выдержки (акт 2), поэтому три темпа.
    /// Ctrl не используем: в браузере Ctrl+W закрывает вкладку.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("Ссылки")]
        [Tooltip("Объект на уровне глаз, в нём камера. Наклон по вертикали крутит его.")]
        public Transform cameraPivot;

        [Header("Скорость, м/с")]
        public float walkSpeed = 2.2f;
        public float sneakSpeed = 1.0f;
        public float runSpeed = 4.0f;
        public float acceleration = 12f;
        public float gravity = -20f;

        [Header("Обзор")]
        [Tooltip("Градусов на пиксель движения мыши.")]
        public float lookSensitivity = 0.1f;
        [Range(10f, 89f)] public float maxPitch = 85f;

        [Header("Покачивание головы")]
        public float bobAmplitude = 0.035f;
        public float bobFrequency = 1.8f;

        /// <summary>Заблокировать ввод (проявка, кат-сцена, меню).</summary>
        public bool InputLocked { get; set; }
        /// <summary>Горизонтальная скорость прямо сейчас, м/с.</summary>
        public float CurrentSpeed { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsSneaking { get; private set; }
        /// <summary>Только тихий шаг: бега нет (начало игры — пока камеры не взяты).</summary>
        public bool ForceSneak { get; set; }
        /// <summary>Множитель скорости от снаряжения (поднятая обскура — 0.4). Бег при множителе меньше 1 запрещён.</summary>
        public float SpeedMultiplier { get; set; } = 1f;
        /// <summary>Множители мыши по осям: (-1, -1) — управление для перевёрнутой картинки после перелома.</summary>
        public Vector2 LookInvert { get; set; } = Vector2.one;
        /// <summary>-1 — стрейф наоборот (картинка перевёрнута, лево и право на экране поменялись).</summary>
        public float StrafeInvert { get; set; } = 1f;

        /// <summary>Акт 2: ходим по потолку — тяжесть тянет вверх, голова внизу капсулы.</summary>
        public bool Ceiling { get; private set; }
        /// <summary>Стоим на поверхности (на полу или, в акте 2, на потолке).</summary>
        public bool Grounded => Ceiling ? (_cc.collisionFlags & CollisionFlags.Above) != 0 : _cc.isGrounded;

        /// <summary>Вызывается на каждый шаг — сюда вешаем звук шагов.</summary>
        public event System.Action<FirstPersonController> Footstep;

        CharacterController _cc;
        InputAction _move, _look, _run, _sneak;
        Vector3 _velocity;
        float _pitch, _bobPhase, _pivotBaseY, _pivotFloorY, _verticalVelocity;
        int _lastStepIndex;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (cameraPivot) _pivotBaseY = _pivotFloorY = cameraPivot.localPosition.y;

            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");

            _look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            _run = new InputAction("Run", InputActionType.Button, "<Keyboard>/leftShift");
            _sneak = new InputAction("Sneak", InputActionType.Button, "<Keyboard>/c");
        }

        void OnEnable()
        {
            _move.Enable(); _look.Enable(); _run.Enable(); _sneak.Enable();
            LockCursor(true);
        }

        void OnDisable()
        {
            _move.Disable(); _look.Disable(); _run.Disable(); _sneak.Disable();
        }

        void OnDestroy()
        {
            _move.Dispose(); _look.Dispose(); _run.Dispose(); _sneak.Dispose();
        }

        void Update()
        {
            HandleCursor();
            bool canLook = !InputLocked && Cursor.lockState == CursorLockMode.Locked;

            if (canLook) Look(_look.ReadValue<Vector2>());
            Move(InputLocked ? Vector2.zero : _move.ReadValue<Vector2>());
            HeadBob();
        }

        void Look(Vector2 delta)
        {
            delta = Vector2.Scale(delta, LookInvert);
            transform.Rotate(0f, delta.x * lookSensitivity, 0f);
            _pitch = Mathf.Clamp(_pitch - delta.y * lookSensitivity, -maxPitch, maxPitch);
            if (cameraPivot) cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        void Move(Vector2 input)
        {
            IsSneaking = !InputLocked && (ForceSneak || _sneak.IsPressed());
            IsRunning = !InputLocked && !IsSneaking && SpeedMultiplier >= 0.99f && _run.IsPressed() && input.y > 0.1f;
            float targetSpeed = (IsSneaking ? sneakSpeed : IsRunning ? runSpeed : walkSpeed) * SpeedMultiplier;

            Vector3 wish = transform.right * (input.x * StrafeInvert) + transform.forward * input.y;
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            Vector3 targetVel = wish * targetSpeed;
            _velocity = Vector3.MoveTowards(_velocity, targetVel, acceleration * Time.deltaTime);

            // в акте 2 тяжесть наоборот: «вниз» — это к потолку
            float g = Ceiling ? -gravity : gravity;
            if (Grounded && _verticalVelocity * Mathf.Sign(g) > 0f) _verticalVelocity = Mathf.Sign(g) * 2f;
            _verticalVelocity += g * Time.deltaTime;

            _cc.Move((_velocity + Vector3.up * _verticalVelocity) * Time.deltaTime);

            Vector3 horizontal = _cc.velocity; horizontal.y = 0f;
            CurrentSpeed = horizontal.magnitude;
        }

        void HeadBob()
        {
            if (!cameraPivot) return;
            float speed01 = Mathf.Clamp01(CurrentSpeed / runSpeed);

            if (CurrentSpeed > 0.1f && Grounded)
            {
                _bobPhase += Time.deltaTime * bobFrequency * Mathf.Lerp(0.6f, 1.6f, speed01) * Mathf.PI * 2f;
                int stepIndex = Mathf.FloorToInt(_bobPhase / Mathf.PI);
                if (stepIndex != _lastStepIndex)
                {
                    _lastStepIndex = stepIndex;
                    Footstep?.Invoke(this);
                }
            }
            else
            {
                _bobPhase = Mathf.Lerp(_bobPhase, Mathf.Round(_bobPhase / Mathf.PI) * Mathf.PI, Time.deltaTime * 6f);
            }

            var p = cameraPivot.localPosition;
            p.y = _pivotBaseY + Mathf.Abs(Mathf.Sin(_bobPhase)) * bobAmplitude * (0.5f + speed01);
            cameraPivot.localPosition = p;
        }

        /// <summary>
        /// Включить хождение по потолку: тяжесть вверх, глаза у «нижнего» конца капсулы (голова свисает вниз).
        /// Камеру по Z переворачивает TwistSequence, мышь и стрейф — LookInvert / StrafeInvert.
        /// </summary>
        public void SetCeiling(bool on)
        {
            Ceiling = on;
            _verticalVelocity = 0f;
            float h = _cc ? _cc.height : 1.8f;
            _pivotBaseY = on ? Mathf.Max(0.05f, h - _pivotFloorY) : _pivotFloorY;
            if (cameraPivot)
            {
                var p = cameraPivot.localPosition;
                p.y = _pivotBaseY;
                cameraPivot.localPosition = p;
            }
        }

        /// <summary>Переставить игрока (CharacterController иначе не даст сдвинуть).</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (_cc) _cc.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            if (_cc) _cc.enabled = true;
            _velocity = Vector3.zero;
            _verticalVelocity = 0f;
        }

        void HandleCursor()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) LockCursor(false);
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked && !InputLocked)
                LockCursor(true);
        }

        public static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
