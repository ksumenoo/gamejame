using UnityEditor;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// Импорт картинок художников в PS1-духе: point-фильтр, без мип-мапов и сжатия.
    /// Textures/ — тайлы (Repeat), Characters/ и UI/ — спрайты и пластины (Clamp, прозрачность).
    /// </summary>
    public class TexturePostprocessor : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            string p = assetPath.Replace('\\', '/');
            if (!p.StartsWith("Assets/Art/2D/")) return;
            if (p.Contains("/Palette/")) return;

            var ti = (TextureImporter)assetImporter;
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.npotScale = TextureImporterNPOTScale.None;

            if (p.StartsWith("Assets/Art/2D/Textures/"))
            {
                ti.textureType = TextureImporterType.Default;
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.alphaIsTransparency = false;
                ti.maxTextureSize = 256;
            }
            else
            {
                ti.textureType = TextureImporterType.Default;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.alphaIsTransparency = true;
                ti.maxTextureSize = 1024;
            }
        }
    }
}
