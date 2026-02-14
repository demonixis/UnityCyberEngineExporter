using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Demonixis.UnityJSONSceneExporter
{
    public enum AssetScope
    {
        DependenciesOnly = 0,
        AllAssets = 1
    }

    public enum SceneSelectionMode
    {
        ActiveScene = 0,
        BuildSettings = 1,
        ExplicitList = 2
    }

    [Serializable]
    public class ExportOptions
    {
        public string outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnityToCyberEngineExport");
        public AssetScope assetScope = AssetScope.DependenciesOnly;
        public SceneSelectionMode sceneSelectionMode = SceneSelectionMode.BuildSettings;
        public List<string> scenePaths = new List<string>();
        public bool generateCpp = true;
        public bool generateJson = true;
        public bool generateCppProject = true;
        public bool convertSceneToCpp = true;
        public string cyberEngineRootPath = "/Users/yann/Projects/CyberEngine";
        public string generatedProjectName = "UnityExportedProject";
        public string baseSceneClass = "Scene";
        public bool failOnError = false;
        public bool cleanOutput = true;

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                error = "Output root is empty.";
                return false;
            }

            if (scenePaths == null || scenePaths.Count == 0)
            {
                if (sceneSelectionMode == SceneSelectionMode.BuildSettings)
                    error = "No scenes were selected for export. BuildSettings mode resolved zero enabled scenes.";
                else
                    error = "No scenes were selected for export.";
                return false;
            }

            if (!generateCpp && !generateJson)
            {
                error = "At least one output mode must be enabled (C++ or JSON).";
                return false;
            }

            if (!convertSceneToCpp && !generateJson)
            {
                error = "JSON output is required when convertSceneToCpp is disabled.";
                return false;
            }

            if (generateCppProject && !generateCpp)
            {
                error = "C++ project generation requires generateCpp=true.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(baseSceneClass))
                baseSceneClass = "Scene";

            if (string.IsNullOrWhiteSpace(generatedProjectName))
                generatedProjectName = "UnityExportedProject";

            error = string.Empty;
            return true;
        }
    }

    [Serializable]
    public class BatchExportArgs
    {
        public string outputRoot;
        public string assetScope;
        public string sceneSelectionMode;
        public List<string> scenes;
        public bool? generateCpp;
        public bool? generateJson;
        public bool? generateCppProject;
        public bool? convertSceneToCpp;
        public string cyberEngineRootPath;
        public string generatedProjectName;
        public string baseSceneClass;
        public bool? failOnError;
        public bool? cleanOutput;
    }

    [Serializable]
    public class ExportRunResult
    {
        public string bundleRoot;
        public ExportManifest manifest;
        public ExportReport report;
        public List<SceneExportData> scenes = new List<SceneExportData>();

        public bool HasErrors
        {
            get { return report != null && report.errors != null && report.errors.Count > 0; }
        }
    }
}
