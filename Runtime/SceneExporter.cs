using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Demonixis.UnityJSONSceneExporter
{
#if UNITY_EDITOR
    [CustomEditor(typeof(SceneExporter))]
    public class SceneExporterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var script = (SceneExporter)target;
            GUILayout.Space(8);

            if (GUILayout.Button("Export"))
                script.Export();
        }
    }
#endif

    public class SceneExporter : MonoBehaviour
    {
        [SerializeField]
        private bool m_LogEnabled = true;

        [SerializeField]
        private AssetScope m_AssetScope = AssetScope.DependenciesOnly;

        [SerializeField]
        private SceneSelectionMode m_SceneSelectionMode = SceneSelectionMode.BuildSettings;

        [SerializeField]
        private List<string> m_AdditionalScenePaths = new List<string>();

        [SerializeField]
        private bool m_GenerateCpp = true;

        [SerializeField]
        private bool m_GenerateJson = true;

        [SerializeField]
        private bool m_GenerateCppProject = true;

        [SerializeField]
        private bool m_ConvertSceneToCpp = true;

        [SerializeField]
        private string m_CyberEngineRootPath = "/Users/yann/Projects/CyberEngine";

        [SerializeField]
        private string m_GeneratedProjectName = string.Empty;

        [SerializeField]
        private bool m_FailOnError = false;

        [SerializeField]
        private bool m_CleanOutput = true;

        [SerializeField]
        private string m_BaseSceneClass = "Scene";

        [SerializeField]
        private string m_OutputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnityToCyberEngineExport");

        [ContextMenu("Export")]
        public void Export()
        {
#if UNITY_EDITOR
            var options = BuildOptionsFromInspector();
            try
            {
                var result = SceneExportPipeline.Run(options);

                if (m_LogEnabled)
                {
                    Debug.Log("Unity export completed: " + result.bundleRoot);
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "Scenes={0}, Entities={1}, Warnings={2}, Errors={3}",
                        result.report.stats.sceneCount,
                        result.report.stats.entityCount,
                        result.report.warnings.Count,
                        result.report.errors.Count));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Export failed: " + ex);
                throw;
            }
#else
            Debug.LogError("Scene export is editor-only.");
#endif
        }

#if UNITY_EDITOR
        public static void ExecuteExportFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            var options = ParseOptionsFromArgs(args);

            var result = SceneExportPipeline.Run(options);
            Debug.Log("Batch export completed: " + result.bundleRoot);

            if (result.HasErrors && options.failOnError)
                throw new InvalidOperationException("Batch export completed with errors. See report.json for details.");
        }

        private ExportOptions BuildOptionsFromInspector()
        {
            var scenePaths = ResolveScenePaths(m_SceneSelectionMode, m_AdditionalScenePaths);
            return new ExportOptions
            {
                outputRoot = m_OutputRoot,
                assetScope = m_AssetScope,
                sceneSelectionMode = m_SceneSelectionMode,
                scenePaths = scenePaths,
                generateCpp = m_GenerateCpp,
                generateJson = m_GenerateJson,
                generateCppProject = m_GenerateCppProject,
                convertSceneToCpp = m_ConvertSceneToCpp,
                cyberEngineRootPath = string.IsNullOrWhiteSpace(m_CyberEngineRootPath)
                    ? "/Users/yann/Projects/CyberEngine"
                    : m_CyberEngineRootPath,
                generatedProjectName = ResolveGeneratedProjectName(m_GeneratedProjectName),
                baseSceneClass = string.IsNullOrWhiteSpace(m_BaseSceneClass) ? "Scene" : m_BaseSceneClass,
                failOnError = m_FailOnError,
                cleanOutput = m_CleanOutput
            };
        }

        private static ExportOptions ParseOptionsFromArgs(string[] args)
        {
            var options = new ExportOptions();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("-", StringComparison.Ordinal))
                    continue;

                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    map[arg] = args[i + 1];
                    i++;
                }
                else
                {
                    map[arg] = "true";
                }
            }

            if (map.TryGetValue("-ueArgsJson", out var jsonArgs) && !string.IsNullOrWhiteSpace(jsonArgs))
            {
                var fromJson = JsonConvert.DeserializeObject<BatchExportArgs>(jsonArgs);
                if (fromJson != null)
                {
                    if (!string.IsNullOrWhiteSpace(fromJson.outputRoot))
                        options.outputRoot = fromJson.outputRoot;

                    if (!string.IsNullOrWhiteSpace(fromJson.assetScope) &&
                        Enum.TryParse(fromJson.assetScope, true, out AssetScope parsedScope))
                        options.assetScope = parsedScope;

                    if (!string.IsNullOrWhiteSpace(fromJson.sceneSelectionMode) &&
                        Enum.TryParse(fromJson.sceneSelectionMode, true, out SceneSelectionMode parsedSceneSelectionMode))
                        options.sceneSelectionMode = parsedSceneSelectionMode;

                    if (fromJson.scenes != null && fromJson.scenes.Count > 0)
                        options.scenePaths = fromJson.scenes.Select(ExportUtils.NormalizeRelativePath).ToList();

                    if (fromJson.generateCpp.HasValue)
                        options.generateCpp = fromJson.generateCpp.Value;
                    if (fromJson.generateJson.HasValue)
                        options.generateJson = fromJson.generateJson.Value;
                    if (fromJson.generateCppProject.HasValue)
                        options.generateCppProject = fromJson.generateCppProject.Value;
                    if (fromJson.convertSceneToCpp.HasValue)
                        options.convertSceneToCpp = fromJson.convertSceneToCpp.Value;
                    if (!string.IsNullOrWhiteSpace(fromJson.cyberEngineRootPath))
                        options.cyberEngineRootPath = fromJson.cyberEngineRootPath;
                    if (!string.IsNullOrWhiteSpace(fromJson.generatedProjectName))
                        options.generatedProjectName = fromJson.generatedProjectName;
                    if (!string.IsNullOrWhiteSpace(fromJson.baseSceneClass))
                        options.baseSceneClass = fromJson.baseSceneClass;
                    if (fromJson.failOnError.HasValue)
                        options.failOnError = fromJson.failOnError.Value;
                    if (fromJson.cleanOutput.HasValue)
                        options.cleanOutput = fromJson.cleanOutput.Value;
                }
            }

            if (map.TryGetValue("-ueOutputRoot", out var outputRoot) && !string.IsNullOrWhiteSpace(outputRoot))
                options.outputRoot = outputRoot;

            if (map.TryGetValue("-ueAssetScope", out var scopeRaw) &&
                Enum.TryParse(scopeRaw, true, out AssetScope parsedAssetScope))
                options.assetScope = parsedAssetScope;

            if (map.TryGetValue("-ueSceneSelectionMode", out var sceneSelectionRaw) &&
                Enum.TryParse(sceneSelectionRaw, true, out SceneSelectionMode parsedSceneSelectionModeArg))
                options.sceneSelectionMode = parsedSceneSelectionModeArg;

            if (map.TryGetValue("-ueScenes", out var scenesRaw) && !string.IsNullOrWhiteSpace(scenesRaw))
            {
                options.scenePaths = scenesRaw
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => ExportUtils.NormalizeRelativePath(s.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (options.sceneSelectionMode == SceneSelectionMode.BuildSettings || options.sceneSelectionMode == SceneSelectionMode.ActiveScene)
                    options.sceneSelectionMode = SceneSelectionMode.ExplicitList;
            }

            if (map.TryGetValue("-ueGenerateCpp", out var generateCppRaw) && bool.TryParse(generateCppRaw, out var generateCpp))
                options.generateCpp = generateCpp;

            if (map.TryGetValue("-ueGenerateJson", out var generateJsonRaw) && bool.TryParse(generateJsonRaw, out var generateJson))
                options.generateJson = generateJson;

            if (map.TryGetValue("-ueGenerateCppProject", out var generateCppProjectRaw) && bool.TryParse(generateCppProjectRaw, out var generateCppProject))
                options.generateCppProject = generateCppProject;

            if (map.TryGetValue("-ueConvertSceneToCpp", out var convertSceneToCppRaw) && bool.TryParse(convertSceneToCppRaw, out var convertSceneToCpp))
                options.convertSceneToCpp = convertSceneToCpp;

            if (map.TryGetValue("-ueCyberEngineRootPath", out var cyberEngineRootPath) && !string.IsNullOrWhiteSpace(cyberEngineRootPath))
                options.cyberEngineRootPath = cyberEngineRootPath;

            if (map.TryGetValue("-ueGeneratedProjectName", out var generatedProjectName) && !string.IsNullOrWhiteSpace(generatedProjectName))
                options.generatedProjectName = generatedProjectName;

            if (map.TryGetValue("-ueBaseSceneClass", out var baseSceneClass) && !string.IsNullOrWhiteSpace(baseSceneClass))
                options.baseSceneClass = baseSceneClass;

            if (map.TryGetValue("-ueFailOnError", out var failOnErrorRaw) && bool.TryParse(failOnErrorRaw, out var failOnError))
                options.failOnError = failOnError;

            if (map.TryGetValue("-ueCleanOutput", out var cleanOutputRaw) && bool.TryParse(cleanOutputRaw, out var cleanOutput))
                options.cleanOutput = cleanOutput;

            options.scenePaths = ResolveScenePaths(options.sceneSelectionMode, options.scenePaths);
            options.generatedProjectName = ResolveGeneratedProjectName(options.generatedProjectName);

            return options;
        }

        internal static List<string> ResolveScenePaths(SceneSelectionMode mode, IEnumerable<string> explicitPaths)
        {
            var paths = new List<string>();

            if (mode == SceneSelectionMode.ActiveScene)
            {
                var active = SceneManager.GetActiveScene();
                if (!string.IsNullOrWhiteSpace(active.path))
                    paths.Add(ExportUtils.NormalizeRelativePath(active.path));
            }
            else if (mode == SceneSelectionMode.BuildSettings)
            {
                paths.AddRange(EditorBuildSettings.scenes
                    .Where(s => s != null && s.enabled && !string.IsNullOrWhiteSpace(s.path))
                    .Select(s => ExportUtils.NormalizeRelativePath(s.path)));
            }

            if (explicitPaths != null)
            {
                foreach (var path in explicitPaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(ExportUtils.NormalizeRelativePath(path));
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string ResolveGeneratedProjectName(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            var productName = string.IsNullOrWhiteSpace(Application.productName)
                ? "UnityExported"
                : Application.productName;

            return ExportUtils.SanitizeIdentifier(productName + "Exported", "UnityExportedProject");
        }
#endif
    }
}
