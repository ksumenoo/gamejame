using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Тлеющие угли: тёплый красный свет, медленно дышит и вздрагивает. Ставит сборщик уровня на метку …_embers.</summary>
    [RequireComponent(typeof(Light))]
    public class EmberGlow : MonoBehaviour
    {
        public float baseIntensity = 1.1f;
        Light _light;
        float _seed;

        void Awake()
        {
            _light = GetComponent<Light>();
            _seed = Random.value * 100f;
        }

        void Update()
        {
            float t = Time.time + _seed;
            float slow = Mathf.PerlinNoise(t * 0.35f, 0.1f);   // угли то разгораются, то темнеют
            float fast = Mathf.PerlinNoise(t * 4f, 0.7f);      // мелкая дрожь
            _light.intensity = baseIntensity * (0.55f + slow * 0.55f + (fast - 0.5f) * 0.15f);
        }
    }
}
