using System;
using DG.Tweening;
using Jam.Interaction;
using NaughtyAttributes;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Условие сцены прошлого: что-то в комнате надо вернуть «как тогда» — зажечь свечи на столе, завесить зеркало,
    /// разжечь камин. E переключает состояние. Объекты ON / OFF (дочерние, необязательно) показывают состояние.
    /// В Blender: объект с именем SWITCH_S{номер сцены}_{что угодно}, внутри — дочерние ON и/или OFF.
    /// </summary>
    public class SceneSwitch : MonoBehaviour, IInteractable
    {
        public int sceneIndex;
        [Tooltip("Текст подсказки, когда переключатель выключен / включён.")]
        public string promptOff = "Зажечь";
        public string promptOn = "Погасить";
        [Tooltip("Можно ли выключить обратно.")]
        public bool reversible = true;
        public GameObject onVisual;
        public GameObject offVisual;
        public Light onLight;

        [ShowNativeProperty] public bool IsOn { get; private set; }
        public event Action<SceneSwitch> Changed;

        public string Prompt => IsOn ? (reversible ? promptOn : "") : promptOff;
        public bool CanInteract(Interactor who) => !IsOn || reversible;

        void Awake() => Apply(false, animate: false);

        public void Interact(Interactor who) => Set(!IsOn);

        [Button("Переключить (тест)")]
        void Toggle() => Set(!IsOn);

        public void Set(bool on)
        {
            if (on == IsOn) return;
            Apply(on, animate: true);
            Changed?.Invoke(this);
        }

        void Apply(bool on, bool animate)
        {
            IsOn = on;
            if (onVisual) onVisual.SetActive(on);
            if (offVisual) offVisual.SetActive(!on);
            if (onLight)
            {
                onLight.enabled = true;
                onLight.DOKill();
                float target = on ? 1.2f : 0f;
                if (animate) onLight.DOIntensity(target, 0.4f).SetLink(gameObject);
                else onLight.intensity = target;
            }
            if (animate) transform.DOPunchScale(Vector3.one * 0.05f, 0.25f).SetLink(gameObject);
        }
    }
}
