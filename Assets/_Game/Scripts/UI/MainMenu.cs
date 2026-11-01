using DG.Tweening;
using Jam.Gameplay;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Jam.UI
{
    /// <summary>
    /// Главное меню (сцена MainMenu): Новая игра / Продолжить / Настройки / Помощь / Об игре / Выход.
    /// «Продолжить» — с последней проявки (SaveGame), неактивно без сохранения. «Выход» — только в Windows-версии:
    /// в браузере выйти из игры нельзя. Интерфейс собирается кодом. Фон — арт художника Resources/Menu/menu_bg,
    /// если он есть, иначе то, что видит камера сцены (Tools → Jam → Build Menu Scene).
    /// Тексты «Помощи» и «Об игре» — здесь же, ниже (HelpText, ControlsText, AboutText).
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        [Tooltip("Слева сверху: координаты колонки меню (от левого края, от середины экрана вверх).")]
        public Vector2 columnPos = new(150f, 300f);

        public const string HelpText =
            "Вы фотограф. Дом помнит, что в нём случилось, но прошлое видно только через объектив.\n\n" +
            "• Обскура ПКМ - камера в руках. Мир в ней перевернут и показывает больше, чем человеческий глаз: людей из прошлого и Маргарет.\n" +
            "• Найдите место, откуда сцена из прошлого видна в обскуре целиком. Поставьте туда штатив F и откройте заслонку E.\n" +
            "• Пока идёт выдержка, оставайтесь рядом. В темноте выдержка идёт вдвое дольше, канделябры можно зажечь E.\n" +
            "• На долгой выдержке живые смазываются, а мёртвые выходят резкими.\n" +
            "• Заберите пластину E и проявите её на верстаке в проявочной — проявленный снимок меняет дом.\n" +
            "• Маргарет видна только в обскуре. Края экрана холодеют она рядом. Она слышит бег, так что если красться - меньше шанс быть услышанным. Магний ненадолго её ослепляет.\n" +
            "• Если она поймает вас — очнётесь у верстака, а непроявленные пластины пропадут. Игра сохраняется сама при каждой проявке.";

        public const string ControlsText =
            "WASD - ходить\n" +
            "Mouse — осмотреться\n" +
            "Shift — бежать\n" +
            "C — красться\n" +
            "ПКМ — посмотреть в обскуру\n" +
            "ЛКМ — вспышка магния\n" +
            "E - действие\n" +
            "F - поставить / забрать штатив\n" +
            "Space — пропустить письмо\n" +
            "Tab — пауза";

        /// <summary>Помощь в паузе во втором акте — там играешь за другого, правила другие.</summary>
        public const string GhostHelpText =
            "Теперь вы — призрак. В дом пришёл живой фотограф, и его нужно выгнать.\n\n" +
            "• Пугайте его: хлопайте дверьми, роняйте шкафы и стулья, ломайте посуду и тыквы.\n" +
            "• Свечи гаснут, если постоять рядом или подержать E. В темноте ему страшнее.\n" +
            "• Когда шкала испуга заполнится, фотограф оцепенеет — подойдите к нему и напугайте.";

        const string AboutText =
            "«Выдержка» — хоррор о викторианской посмертной фотографии.\n\n" +
            "Англия, 1890-е. Фотографа зовут снять дом семьи Эшдаун. Маргарет Эшдаун снимала посмертные портреты, " +
            "потеряла дочь и умерла сама, а дом остался ждать последнего снимка.\n\n" +
            "Из-за долгой выдержки первых камер живые должны были неподвижно стоять во время съемки, а покойный был идеальной «неподвижной» моделью. " +
            "Посмертное фото часто было единственным портретом человека за всю его жизнь.";

        Canvas _canvas;
        RectTransform _main, _settings, _help, _about, _confirm, _credits;
        RectTransform _creditsText;
        float _creditsH, _creditsEnd;
        Image _fader, _dim;
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
            GameSettings.Apply(); // громкость музыки меню
            Build();

            // из чёрного — как проявляется пластина
            _fader.color = Color.black;
            _fader.DOFade(0f, 1.6f).SetEase(Ease.InQuad).SetUpdate(true).SetLink(gameObject);
        }

        void Update()
        {
            // Esc в подменю — назад
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && _main && !_main.gameObject.activeSelf && !_leaving) Open(_main);

            // титры едут; пробел/ЛКМ — быстрее; доехали — постояли и назад в меню
            if (_credits && _credits.gameObject.activeSelf && _creditsText)
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                bool fast = (kb != null && kb.spaceKey.isPressed) || (mouse != null && mouse.leftButton.isPressed);
                var p = _creditsText.anchoredPosition;
                if (p.y < _creditsEnd) p.y = Mathf.Min(_creditsEnd, p.y + 70f * (fast ? 6f : 1f) * Time.unscaledDeltaTime);
                else if ((_creditsHold += Time.unscaledDeltaTime) > 6f) Open(_main);
                _creditsText.anchoredPosition = p;
            }
        }

        float _creditsHold;

        void OpenCredits()
        {
            Open(_credits);
            Canvas.ForceUpdateCanvases();
            var text = _creditsText.GetComponent<Text>();
            _creditsH = text.preferredHeight;
            _creditsText.sizeDelta = new Vector2(1200f, _creditsH);
            float parentH = ((RectTransform)_credits).rect.height;
            _creditsText.anchoredPosition = new Vector2(0f, -parentH * 0.5f);
            _creditsEnd = _creditsH - 30f;
            _creditsHold = 0f;
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
                MenuUI.Fill("Dark", root, new Color(0.03f, 0.025f, 0.025f, 1f));
            }
            MenuUI.LeftShade(root, 0.6f, 0.85f);
            // под длинными текстами — затемнение всего экрана, чтобы читалось на любом фоне
            _dim = MenuUI.Fill("TextDim", root, new Color(0f, 0f, 0f, 0.7f));

            // ---- главное ----
            _main = MenuUI.Column(root, "Main", columnPos.x, columnPos.y + 60f);
            MenuUI.Label(_main, "Выдержка", 112, MenuUI.Paper, MenuUI.Bold, 150f);
            MenuUI.Spacer(_main, 40f);
            var newGame = MenuUI.Button(_main, "Новая игра", 46, NewGame);
            var cont = MenuUI.Button(_main, "Продолжить", 46, Continue);
            cont.interactable = SaveGame.HasSave;
            var settings = MenuUI.Button(_main, "Настройки", 46, () => Open(_settings));
            var help = MenuUI.Button(_main, "Помощь", 46, () => Open(_help));
            var about = MenuUI.Button(_main, "Об игре", 46, () => Open(_about));
            var creditsBtn = MenuUI.Button(_main, "Титры", 46, OpenCredits);
            Button quit = null;
#if !UNITY_WEBGL || UNITY_EDITOR
            quit = MenuUI.Button(_main, "Выход", 46, Quit);
#endif
            _buttons = new[] { newGame, cont, settings, help, about, creditsBtn, quit };

            // ---- настройки ----
            _settings = MenuUI.Settings(root, columnPos.x, columnPos.y, () => Open(_main));

            // ---- помощь: слева «Как играть», справа «Управление» ----
            _help = MenuUI.Stretch(MenuUI.Rect("Help", root));
            var howTo = MenuUI.Column(_help, "HowTo", columnPos.x, 470f, 900f);
            MenuUI.Label(howTo, "Помощь", 64, MenuUI.Paper, MenuUI.Bold, 100f);
            MenuUI.Label(howTo, "Как играть", 38, MenuUI.Red, MenuUI.Bold, 56f);
            MenuUI.Paragraph(howTo, HelpText, 26, 900f);
            MenuUI.Spacer(howTo, 16f);
            MenuUI.Button(howTo, "Назад", 44, () => Open(_main));
            var keys = MenuUI.Column(_help, "Controls", columnPos.x + 1000f, 370f, 650f);
            MenuUI.Label(keys, "Управление", 38, MenuUI.Red, MenuUI.Bold, 56f);
            MenuUI.Paragraph(keys, ControlsText, 26, 650f).lineSpacing = 1.35f;

            // ---- об игре ----
            _about = MenuUI.Column(root, "About", columnPos.x, 470f, 1150f);
            MenuUI.Label(_about, "Об игре", 64, MenuUI.Paper, MenuUI.Bold, 100f);
            MenuUI.Paragraph(_about, AboutText, 28, 1150f);
            MenuUI.Spacer(_about, 10f);
            MenuUI.Label(_about, "Команда", 38, MenuUI.Red, MenuUI.Bold, 56f);
            MenuUI.Paragraph(_about, TeamText(), 28, 1150f);
            MenuUI.Spacer(_about, 16f);
            MenuUI.Button(_about, "Назад", 44, () => Open(_main));

            // ---- новая игра поверх сохранения ----
            _confirm = MenuUI.Column(root, "Confirm", columnPos.x, columnPos.y, 900f);
            MenuUI.Label(_confirm, "Начать заново?", 64, MenuUI.Paper, MenuUI.Bold, 100f);
            MenuUI.Paragraph(_confirm, "Сохранённая игра будет стёрта.", 30, 900f, MenuUI.PaperDim);
            MenuUI.Spacer(_confirm, 30f);
            MenuUI.Button(_confirm, "Начать заново", 46, () => Leave(GameSession.StartGame));
            MenuUI.Button(_confirm, "Отмена", 46, () => Open(_main));

            // ---- титры: те же, что в конце игры (Resources/Text/credits.txt), едут снизу вверх ----
            _credits = MenuUI.Stretch(MenuUI.Rect("Credits", root));
            MenuUI.Fill("Black", _credits, Color.black).raycastTarget = true;
            var ct = MenuUI.Label(_credits, "", 34, MenuUI.Paper);
            ct.raycastTarget = false;
            ct.supportRichText = true;
            ct.alignment = TextAnchor.UpperCenter;
            ct.horizontalOverflow = HorizontalWrapMode.Wrap;
            ct.verticalOverflow = VerticalWrapMode.Overflow;
            ct.lineSpacing = 1.1f;
            var file = Resources.Load<TextAsset>("Text/credits");
            ct.text = EndCredits.Format(file ? file.text : "ВЫДЕРЖКА");
            var le = ct.GetComponent<LayoutElement>();
            if (le) le.ignoreLayout = true;
            _creditsText = ct.rectTransform;
            _creditsText.anchorMin = _creditsText.anchorMax = new Vector2(0.5f, 0.5f);
            _creditsText.pivot = new Vector2(0.5f, 1f);
            _creditsText.sizeDelta = new Vector2(1200f, 100f);
            var hint = MenuUI.Label(_credits, "Esc — назад", 24, MenuUI.PaperDim);
            hint.alignment = TextAnchor.LowerRight;
            var hr = hint.rectTransform;
            hr.anchorMin = hr.anchorMax = hr.pivot = new Vector2(1f, 0f);
            hr.anchoredPosition = new Vector2(-40f, 30f);
            hr.sizeDelta = new Vector2(400f, 40f);

            _fader = MenuUI.Fill("Fader", root, Color.black);
            Open(_main);
        }

        /// <summary>Команда с настоящими ролями — Assets/Resources/Text/team.txt (титры в конце — отдельный файл credits.txt).</summary>
        static string TeamText()
        {
            var team = Resources.Load<TextAsset>("Text/team");
            if (team) return team.text.Trim();
            var file = Resources.Load<TextAsset>("Text/credits");
            if (!file) return "Геймджем 2026";
            var lines = file.text.Trim().Split('\n');
            // первая строка «ВЫДЕРЖКА» (вся заглавными) — заголовок титров, здесь он не нужен
            int start = lines.Length > 1 && lines[0].Trim() == lines[0].Trim().ToUpperInvariant() ? 1 : 0;
            return string.Join("\n", lines, start, lines.Length - start).Trim();
        }

        void Open(RectTransform panel)
        {
            MenuUI.Show(_main, panel == _main);
            MenuUI.Show(_settings, panel == _settings);
            MenuUI.Show(_help, panel == _help);
            MenuUI.Show(_about, panel == _about);
            MenuUI.Show(_confirm, panel == _confirm);
            MenuUI.Show(_credits, panel == _credits);
            if (_dim) _dim.enabled = panel == _help || panel == _about;
        }

        void NewGame()
        {
            if (SaveGame.HasSave) Open(_confirm);
            else Leave(GameSession.StartGame);
        }

        void Continue() => Leave(GameSession.ContinueGame);

        void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void Leave(System.Action go)
        {
            if (_leaving) return;
            _leaving = true;
            foreach (var b in _buttons) if (b) b.interactable = false;
            _fader.DOKill();
            _fader.DOFade(1f, 0.9f).SetEase(Ease.InQuad).SetUpdate(true).SetLink(gameObject)
                .OnComplete(() => go());
        }
    }
}
