using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Короткие подсказки по управлению в первые минуты — по одной, каждая один раз, по событию.
    /// Сюжет не объясняют: только какие кнопки жать. Пишет в общую строку подсказок (PlateCounterUI).
    /// </summary>
    public class TutorialHints : MonoBehaviour
    {
        public IntroLetter letter;
        public HandObscura hand;
        public PortableCamera portable;
        public Darkroom darkroom;

        public string afterLetter = "ПКМ — смотреть через обскуру. Только в ней видно прошлое дома.";
        public string firstRaise = "Найдите место, где прошлое складывается целиком. Снимки в прихожей подскажут, откуда смотреть.";
        public string firstAligned = "Здесь. F — поставить камеру, затем держать E, пока идёт выдержка.";
        public string firstDeveloped = "Снимок занял своё место в альбоме в прихожей.";
        public string firstBlank = "На пластине пусто. Снимать нужно с того места, откуда видно прошлое целиком.";
        [Header("Сцена с Маргарет (посмертный портрет) — мысли фотографа, не инструкции")]
        [Tooltip("Обскура впервые совпала с точкой сцены у стойки: там её тень из прошлого.")]
        public string poseSceneSeen = "У стойки — её тень. Посмертный портрет… Кажется, она ждёт, когда откроют заслонку.";
        [Tooltip("Маргарет сама встала к стойке, когда открылась заслонка.")]
        public string posed = "Она пришла. Стоит, как тогда. Только бы не закрыть заслонку раньше времени.";
        [Tooltip("Пластину проявили, а сцена у стойки так и не засчиталась.")]
        public string poseFailed = "Она смазана. На посмертном портрете стоят неподвижно — до самого конца выдержки.";

        bool _raised, _aligned, _developed, _blank, _poseOpened, _posed;

        void Start()
        {
            if (letter) letter.Closed += () => Show(afterLetter);
            else Show(afterLetter);
            if (hand)
            {
                hand.Raised += h => { if (!_raised) { _raised = true; Show(firstRaise); } };
                hand.SceneAligned += (h, s) => { if (!_aligned && portable && portable.IsCarried) { _aligned = true; Show(firstAligned); } };
            }
            if (darkroom)
                darkroom.PlatesDeveloped += (d, plates) =>
                {
                    bool any = false, blank = false;
                    foreach (var p in plates) { if (p.Blank) blank = true; else any = true; }
                    if (any && !_developed) { _developed = true; Show(firstDeveloped); }
                    else if (blank && !any && !_blank) { _blank = true; Show(firstBlank); }
                    // сцена с Маргарет открыта, но ни одна пластина её не засчитала
                    if (_posed && plates.Count > 0) StartCoroutine(CheckPoseLater());
                };

            // подсказки сцены с Маргарет: когда открылась и когда она встала к стойке
            if (hand)
                hand.SceneAligned += (h, s) => { if (!_poseOpened && s && s.requireMargaretSharp) { _poseOpened = true; Show(poseSceneSeen); } };
            var margaret = FindAnyObjectByType<MargaretAI>();
            if (margaret) margaret.Posed += m => { if (!_posed) { _posed = true; Show(posed); } };
        }

        // сюжет засчитывает пластины по тому же событию — смотрим чуть позже, осталась ли сцена неснятой
        System.Collections.IEnumerator CheckPoseLater()
        {
            yield return new WaitForSeconds(1f);
            var gate = PastScene.All.Find(s => s && s.requireMargaretSharp && s.Current == PastScene.State.Visible);
            if (gate) Show(poseFailed);
        }

        static void Show(string text)
        {
            if (PlateCounterUI.Instance) PlateCounterUI.Instance.ShowHint(text);
        }
    }
}
