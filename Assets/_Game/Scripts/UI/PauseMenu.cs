using Jam.Gameplay;
using Jam.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Jam.UI
{
    /// <summary>
    /// Пауза в игре: Продолжить / Настройки / В меню. Ставится сама в любую сцену с игроком.
    /// Открывается клавишей P и сама, когда курсор перестал быть захвачен (Esc в браузере, ушёл во вкладку) —
    /// но не во время письма, проявки, смерти и просмотра пластины: там игрок и так заблокирован (InputLocked).
    /// После «Продолжить» в браузере курсор захватит следующий клик по игре.
    /// </summary>
    public class PauseMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }

        FirstPersonController _player;
        Canvas _canvas;
        CanvasGroup _group;
        RectTransform _main, _settings;
        bool _wasLocked;
        float _openedAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryInstall();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) => TryInstall();

        static void TryInstall()
        {
            IsOpen = false;
            if (FindAnyObjectByType<PauseMenu>() || !FindAnyObjectByType<FirstPersonController>()) return;
            new GameObject("PauseMenu").AddComponent<PauseMenu>();
        }

        void Start()
        {
            _player = FindAnyObjectByType<FirstPersonController>();
            MenuUI.EnsureEventSystem();

            _canvas = MenuUI.CreateCanvas("PauseCanvas", 400, transform);
            _group = _canvas.GetComponent<CanvasGroup>();
            var root = _canvas.transform;
            MenuUI.Fill("Dim", root, new Color(0f, 0f, 0f, 0.72f)).raycastTarget = true; // клики не проходят в игру
            MenuUI.LeftShade(root, 0.6f, 0.6f);

            _main = MenuUI.Column(root, "Main", 150f, 260f);
            MenuUI.Label(_main, "Пауза", 80, MenuUI.Paper, MenuUI.Bold, 120f);
            MenuUI.Spacer(_main, 40f);
            MenuUI.Button(_main, "Продолжить", 48, Close);
            MenuUI.Button(_main, "Настройки", 48, () => Show(_settings));
            if (GameSession.HasMenu) MenuUI.Button(_main, "В меню", 48, GameSession.ReturnToMenu);

            _settings = MenuUI.Settings(root, 150f, 260f, () => Show(_main));
            SetVisible(false);
        }

        void OnDestroy()
        {
            if (IsOpen) { IsOpen = false; Time.timeScale = 1f; AudioListener.pause = false; }
        }

        void Update()
        {
            if (!_player) { _player = FindAnyObjectByType<FirstPersonController>(); if (!_player) return; }
            var kb = Keyboard.current;
            bool locked = Cursor.lockState == CursorLockMode.Locked;

            if (IsOpen)
            {
                bool back = kb != null && (kb.pKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame);
                if (back && Time.unscaledTime - _openedAt > 0.25f)
                {
                    if (_settings && _settings.gameObject.activeSelf && !kb.pKey.wasPressedThisFrame) Show(_main);
                    else Close();
                }
            }
            else if (!_player.InputLocked)
            {
                bool lostCursor = _wasLocked && !locked;   // Esc в браузере, alt-tab, другая вкладка
                bool pressedP = kb != null && kb.pKey.wasPressedThisFrame;
                if (lostCursor || pressedP) Open();
            }
            _wasLocked = locked;
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _openedAt = Time.unscaledTime;
            GameSettings.Pause(true);
            Show(_main);
            SetVisible(true);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            SetVisible(false);
            GameSettings.Pause(false);
        }

        void Show(RectTransform panel)
        {
            MenuUI.Show(_main, panel == _main);
            MenuUI.Show(_settings, panel == _settings);
        }

        void SetVisible(bool on)
        {
            if (!_group) return;
            _group.alpha = on ? 1f : 0f;
            _group.blocksRaycasts = on;
            _group.interactable = on;
        }
    }
}
