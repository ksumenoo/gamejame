using UnityEditor;

namespace Jam.EditorTools
{
    /// <summary>
    /// Настройки импорта моделей из Blender.
    /// House.fbx — коллайдеры на все меши и UV для лайтмапов.
    /// Props/*.fbx — без коллайдеров (их ставит код), без камер, света и анимаций.
    /// </summary>
    public class HouseModelPostprocessor : AssetPostprocessor
    {
        public const string HousePath = "Assets/Art/3D/Models/House.fbx";
        public const string PropsFolder = "Assets/Art/3D/Models/Props/";

        void OnPreprocessModel()
        {
            string path = assetPath.Replace('\\', '/');
            bool isHouse = path == HousePath;
            bool isProp = path.StartsWith(PropsFolder);
            if (!isHouse && !isProp) return;

            var mi = (ModelImporter)assetImporter;
            mi.addCollider = isHouse;
            mi.generateSecondaryUV = isHouse;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importBlendShapes = false;
            mi.isReadable = false;
        }
    }
}
