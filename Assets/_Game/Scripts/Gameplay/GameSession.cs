using DG.Tweening;
using Jam.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Jam.Gameplay
{
    /// <summary>
    /// Переходы между меню и игрой. Перед выходом в меню сбрасывает статическое состояние игры
    /// (режим призрака у свечей и дверей, сказанные подсказки, ожидание обскуры, события),
    /// иначе вторая партия после «В меню» → «Играть» началась бы с остатками первой.
    /// </summary>
    public static class GameSession
    {
        public const string MenuScene = "MainMenu";
        public const string GameScene = "Game";

        /// <summary>Сцена меню есть в Build Settings — значит, кнопка «В меню» имеет смысл.</summary>
        public static bool HasMenu => Application.CanStreamedLevelBeLoaded(MenuScene);

        public static void StartGame()
        {
            ResetForNewGame();
            Load(GameScene);
        }

        public static void ReturnToMenu()
        {
            ResetForNewGame();
            FirstPersonController.LockCursor(false);
            Load(HasMenu ? MenuScene : SceneManager.GetActiveScene().name);
        }

        static void Load(string scene)
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
            DOTween.KillAll();
            SceneManager.LoadScene(scene);
        }

        /// <summary>Всё статическое, что живёт дольше сцены, — в исходное состояние.</summary>
        public static void ResetForNewGame()
        {
            Candelabra.ResetStatics();
            Door.GhostMode = false;
            Door.ClearAnyDoor();
            Thoughts.ResetStatics();
            GearPickup.ResetStatics();
            Poltergeist.ResetStatics();
        }
    }
}
