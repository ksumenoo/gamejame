using DG.Tweening;
using Jam.Audio;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Jam.UI
{
    /// <summary>
    /// Отклик пункта меню: при наведении чуть вырастает и тикает механизм выдержки, при нажатии щёлкает затвор.
    /// Работает и в паузе (время стоит, поэтому анимация на реальном времени).
    /// </summary>
    public class MenuButtonFX : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public float hoverScale = 1.06f;
        Selectable _sel;

        void Awake() => _sel = GetComponent<Selectable>();

        public void OnPointerEnter(PointerEventData e)
        {
            if (_sel && !_sel.interactable) return;
            transform.DOKill();
            transform.DOScale(hoverScale, 0.12f).SetUpdate(true).SetLink(gameObject);
            JamAudio.Play("exposure_tick");
        }

        public void OnPointerExit(PointerEventData e)
        {
            transform.DOKill();
            transform.DOScale(1f, 0.12f).SetUpdate(true).SetLink(gameObject);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (_sel && !_sel.interactable) return;
            JamAudio.Play("shutter_close");
        }

        void OnDisable()
        {
            transform.DOKill();
            transform.localScale = Vector3.one;
        }
    }
}
