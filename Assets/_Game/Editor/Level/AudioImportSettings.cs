using UnityEditor;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// Импорт звука под браузер: музыка (Assets/Audio/Music) — потоком и сжатой, чтобы не раздувать билд
    /// (wav на 36 МБ превращается примерно в 2–3 МБ); короткие звуки — сжатые, в памяти.
    /// </summary>
    public class AudioImportSettings : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            string p = assetPath.Replace('\\', '/');
            if (!p.StartsWith("Assets/Audio/")) return;
            var ai = (AudioImporter)assetImporter;
            var s = ai.defaultSampleSettings;
            if (p.Contains("/Music/"))
            {
                s.loadType = AudioClipLoadType.Streaming;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.45f;
                ai.loadInBackground = true;
            }
            else
            {
                s.loadType = AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.6f;
            }
            ai.defaultSampleSettings = s;
        }
    }
}
