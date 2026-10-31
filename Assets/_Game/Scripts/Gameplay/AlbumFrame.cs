using DG.Tweening;
using Jam.Interaction;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Одна рамка альбома. E — рассмотреть снимок.</summary>
    public class AlbumFrame : MonoBehaviour, IInteractable
    {
        public int sceneIndex;
        public Renderer picture;
        [Tooltip("Какая часть снимка видна по ширине/высоте (рамка уже снимка — обрезаем по центру).")]
        public Vector2 crop = Vector2.one;

        FamilyAlbum _album;
        Texture _tex;
        Material _mat;
        bool _developed;

        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public string Prompt => _tex ? (_developed ? "Рассмотреть снимок" : "Рассмотреть выцветший снимок") : "";
        public bool CanInteract(Interactor who) => _tex && _album;
        public void Interact(Interactor who) => _album.Show(_tex, _developed, sceneIndex);

        public void Init(FamilyAlbum album, Texture hint, Material template)
        {
            _album = album;
            _tex = hint;
            if (!picture) picture = GetComponentInChildren<Renderer>();
            if (!picture || !template) return;
            _mat = new Material(template);
            // в альбоме — отпечаток, он прямой (перевёрнута только картинка на стекле обскуры)
            _mat.SetTextureScale(BaseMap, new Vector2(crop.x, crop.y));
            _mat.SetTextureOffset(BaseMap, new Vector2((1f - crop.x) * 0.5f, (1f - crop.y) * 0.5f));
            picture.sharedMaterial = _mat;
            Apply();
        }

        public void SetDeveloped(Texture tex)
        {
            _tex = tex;
            _developed = true;
            Apply();
            transform.DOPunchRotation(new Vector3(0f, 0f, 6f), 0.6f, 6, 0.4f).SetLink(gameObject);
        }

        void Apply()
        {
            if (!_mat) return;
            _mat.SetTexture(BaseMap, _tex);
            // подсказка — бледная, выцветшая; проявленная — тёплая сепия
            _mat.SetColor(BaseColor, _developed ? new Color(1.3f, 1.12f, 0.85f) : new Color(1.1f, 1.05f, 0.95f));
        }

        void OnDestroy()
        {
            if (_mat) Destroy(_mat);
        }
    }
}
