using DG.Tweening;
using Jam.Gameplay;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Jam.UI
{
    /// <summary>
    /// Главное меню (сцена MainMenu): «Выдержка», Играть / Настройки / Авторы. Кнопки «Выход» нет — в браузере она ничего не делает.
    /// Интерфейс собирается кодом. Фон — арт художника Resources/Menu/menu_bg, если он есть,
    /// иначе то, что видит камера сцены (натюрморт, Tools → Jam → Build Menu Scene).
    /// Если компонента в сцене нет, он ставится сам при загрузке сцены MainMenu.
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        [Tooltip("Слева сверху: координаты колонки меню (от левого края, от середины экрана вверх).")]
        public Vector2 columnPos = new(150f, 300f);

        Canvas _canvas;
        RectTransform _main, _settings, _credits;
        Image _fader;
        Button[] _buttons;
        bool _leaving;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoInstall()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (s.name == GameSession.MenuScene && !FindAnyObjectByType<MainMenu>())
                new GameObject("MainMenu").AddComponent<MainMenu>();
        }

        void Start()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
            FirstPersonController.LockCursor(false);
            MenuUI.EnsureEventSystem();
            Build();

            // из чёрного — как проявляется пластина
            _fader.color = Color.black;
            _fader.DOFade(0f, 1.6f).SetEase(Ease.InQuad).SetUpdate(true).SetLink(gameObject);
        }

        void Build()
        {
            _canvas = MenuUI.CreateCanvas("MenuCanvas", 200, transform);
            var root = _canvas.transform;

            var art = Resources.Load<Texture2D>("Menu/menu_bg");
            if (art)
            {
                var bg = MenuUI.Stretch(MenuUI.Rect("Art", root)).gameObject.AddComponent<RawImage>();
                bg.texture = art;
                bg.raycastTarget = false;
                var fit = bg.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = art.width / (float)Mathf.Max(1, art.height);
            }
            else if (!Camera.main)
            {
                // ни арта, ни сцены — просто тёмный фон
                MenuUI.Fill("Dark", root, new Color(0.03f, 0.025f, 0.025f, 1f));
            }
            MenuUI.LeftShade(root, 0.6f, 0.85f);

            // ---- главное ----
            _main = MenuUI.Column(root, "Main", columnPos.x, columnPos.y);
            MenuUI.Label(_main, "Выдержка", 112, MenuUI.Paper, MenuUI.Bold, 150f);
            MenuUI.Spacer(_main, 60f);
            var play = MenuUI.Button(_main, "Играть", 48, Play);
            var settings = MenuUI.Button(_main, "Настройки", 48, () => Open(_settings));
            var credits = MenuUI.Button(_main, "Авторы", 48, () => Open(_credits));
            _buttons = new[] { play, settings, credits };

            // ---- настройки ----
            _settings = MenuUI.Settings(root, columnPos.x, columnPos.y, () => Open(_main));

            // ---- авторы ----
            _credits = MenuUI.Column(root, "Credits", columnPos.x, columnPos.y, 900f);
            MenuUI.Label(_credits, "Авторы", 64, MenuUI.Paper, MenuUI.Bold, 110f);
            var text = Resources.Load<TextAsset>("Text/credits");
            var body = MenuUI.Label(_credits, text ? text.text.Trim() : "Геймджем 2026", 30, MenuUI.Paper, null, 420f);
            body.alignment = TextAnchor.UpperLeft;
            body.lineSpacing = 1.15f;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            MenuUI.Spacer(_credits, 20f);
            MenuUI.Button(_credits, "Назад", 44, () => Open(_main));

            _fader = MenuUI.Fill("Fader", root, Color.black);
            Open(_main);
        }

        void Open(RectTransform panel)
        {
            MenuUI.Show(_main, panel == _main);
            MenuUI.Show(_settings, panel == _settings);
            MenuUI.Show(_credits, panel == _credits);
        }

        void Play()
        {
            if (_leaving) return;
            _leaving = true;
            foreach (var b in _buttons) if (b) b.interactable = false;
            _fader.DOKill();
            _fader.DOFade(1f, 0.9f).SetEase(Ease.InQuad).SetUpdate(true).SetLink(gameObject)
                .OnComplete(GameSession.StartGame);
        }
    }
}
