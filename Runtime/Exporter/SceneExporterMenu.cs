#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Demonixis.UnityJSONSceneExporter
{
    internal sealed class SceneExporterWindow : EditorWindow
    {
        private bool m_ConvertSceneToCpp = true;

        [MenuItem("Tools/CyberEngine Exporter/Export...")]
        private static void OpenWindow()
        {
            var window = GetWindow<SceneExporterWindow>("CyberEngine Export");
            window.minSize = new Vector2(360.0f, 180.0f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("CyberEngine Export", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Build Settings scenes are exported.", MessageType.Info);

            m_ConvertSceneToCpp = EditorGUILayout.ToggleLeft("Convert Scene to C++", m_ConvertSceneToCpp);

            GUILayout.Space(12.0f);
            if (GUILayout.Button("Export complet raw", GUILayout.Height(30.0f)))
                RunExport(BuildOptions(rawExport: true));

            GUILayout.Space(4.0f);
            if (GUILayout.Button("Export C++ + Projet", GUILayout.Height(30.0f)))
                RunExport(BuildOptions(rawExport: false));
        }

        private ExportOptions BuildOptions(bool rawExport)
        {
            return new ExportOptions
            {
                outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnityToCyberEngineExport"),
                assetScope = rawExport ? AssetScope.AllAssets : AssetScope.DependenciesOnly,
                sceneSelectionMode = SceneSelectionMode.BuildSettings,
                scenePaths = SceneExporter.ResolveScenePaths(SceneSelectionMode.BuildSettings, new List<string>()),
                generateCpp = !rawExport,
                generateJson = true,
                generateCppProject = !rawExport,
                convertSceneToCpp = rawExport ? false : m_ConvertSceneToCpp,
                cyberEngineRootPath = "/Users/yann/Projects/CyberEngine",
                generatedProjectName = SceneExporter.ResolveGeneratedProjectName(null),
                baseSceneClass = "Scene",
                failOnError = false,
                cleanOutput = true
            };
        }

        private static void RunExport(ExportOptions options)
        {
            try
            {
                var result = SceneExportPipeline.Run(options);
                Debug.Log("Unity export completed: " + result.bundleRoot);
                EditorUtility.RevealInFinder(result.bundleRoot);
            }
            catch (Exception ex)
            {
                Debug.LogError("Export failed: " + ex);
                EditorUtility.DisplayDialog("CyberEngine Exporter", "Export failed. Check Console and report.json.", "OK");
            }
        }
    }

    internal static class SceneExporterMenu
    {
        [MenuItem("Tools/CyberEngine Exporter/Export Using Selected SceneExporter")]
        private static void ExportUsingSelectedSceneExporter()
        {
            var exporter = GetSelectedSceneExporter();
            if (exporter == null)
            {
                EditorUtility.DisplayDialog("CyberEngine Exporter", "Select a GameObject with a SceneExporter component.", "OK");
                return;
            }

            try
            {
                exporter.Export();
            }
            catch (Exception ex)
            {
                Debug.LogError("Export failed: " + ex);
                EditorUtility.DisplayDialog("CyberEngine Exporter", "Export failed. Check Console and report.json.", "OK");
            }
        }

        [MenuItem("Tools/CyberEngine Exporter/Export Using Selected SceneExporter", true)]
        private static bool ValidateExportUsingSelectedSceneExporter()
        {
            return GetSelectedSceneExporter() != null;
        }

        [MenuItem("Tools/CyberEngine Exporter/Open Last Default Export Folder")]
        private static void OpenDefaultExportFolder()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnityToCyberEngineExport");
            if (Directory.Exists(path))
                EditorUtility.RevealInFinder(path);
            else
                EditorUtility.DisplayDialog("CyberEngine Exporter", "No default export folder found yet.", "OK");
        }

        private static SceneExporter GetSelectedSceneExporter()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
                return null;

            return selected.GetComponent<SceneExporter>();
        }
    }
}
#endif
