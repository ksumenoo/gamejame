using DG.Tweening;
using Jam.Interaction;
using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Канделябр: всегда горит, E — зажечь от него свечу в руке.</summary>
    public class Candelabra : MonoBehaviour, IInteractable
    {
        public Light flame;
        public float baseIntensity = 0.9f;

        public string Prompt => "Зажечь свечу";

        public bool CanInteract(Interactor who)
        {
            var c = who ? who.GetComponentInParent<HandCandle>() : null;
            return c && !c.IsLit;
        }

        public void Interact(Interactor who)
        {
            var c = who.GetComponentInParent<HandCandle>();
            if (c) c.Relight();
            if (flame) flame.transform.DOPunchScale(Vector3.one * 0.3f, 0.3f).SetLink(gameObject);
        }

        void Start() => Flicker();

        void Flicker()
        {
            if (!flame) return;
            flame.DOIntensity(baseIntensity * Random.Range(0.8f, 1.1f), Random.Range(0.08f, 0.25f))
                 .SetLink(gameObject)
                 .OnComplete(Flicker);
        }
    }
}
