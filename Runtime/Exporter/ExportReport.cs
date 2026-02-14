using System;
using System.Collections.Generic;

namespace Demonixis.UnityJSONSceneExporter
{
    [Serializable]
    public class ExportReport
    {
        public string schemaVersion = "1.1.0";
        public string generatedAtUtc;
        public float durationSeconds;
        public ExportStats stats = new ExportStats();
        public List<string> warnings = new List<string>();
        public List<string> errors = new List<string>();
    }

    [Serializable]
    public class ExportStats
    {
        public int sceneCount;
        public int entityCount;
        public int materialCount;
        public int textureCount;
        public int modelAssetCount;
        public int audioAssetCount;
        public int terrainAssetCount;
        public int generatedCustomComponentCount;
        public int totalComponentTypeCount;
        public int unsupportedBuiltinTypeCount;
        public int unsupportedBuiltinInstanceCount;
        public long totalAssetBytes;
    }
}
