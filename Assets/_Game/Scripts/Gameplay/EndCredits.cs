using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Jam.Gameplay
{
    /// <summary>Титры: чёрный экран, текст проявляется. Заглушка — вызывается в конце игры.</summary>
    public class EndCredits : MonoBehaviour
    {
        public CanvasGroup group;
        public Text text;
        [TextArea(4, 12)]
        public string credits = "ВЫДЕРЖКА\n\n3D, Unity — ...\n2D — ...\n2D — ...\n\nГеймджем 2026";

        void Awake()
        {
            if (group) group.alpha = 0f;
        }

        public async UniTask ShowAsync()
        {
            if (text) text.text = credits;
            if (!group) return;
            group.blocksRaycasts = true;
            var t = group.DOFade(1f, 2.5f).SetLink(gameObject);
            while (t.IsActive() && !t.IsComplete()) await UniTask.Yield(PlayerLoopTiming.Update, destroyCancellationToken);
        }
    }
}
