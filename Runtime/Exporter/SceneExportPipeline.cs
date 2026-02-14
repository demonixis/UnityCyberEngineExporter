#if UNITY_EDITOR
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Demonixis.UnityJSONSceneExporter
{
    internal static class SceneExportPipeline
    {
        public static ExportRunResult Run(ExportOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            options.scenePaths = SceneExporter.ResolveScenePaths(options.sceneSelectionMode, options.scenePaths);
            options.generatedProjectName = SceneExporter.ResolveGeneratedProjectName(options.generatedProjectName);
            if (string.IsNullOrWhiteSpace(options.outputRoot))
            {
                options.outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnityToCyberEngineExport");
            }

            var watch = Stopwatch.StartNew();
            var result = new ExportRunResult();
            var report = new ExportReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            result.report = report;

            var manifest = new ExportManifest
            {
                projectName = Application.productName,
                generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                options = new ExportOptionsSnapshot
                {
                    outputRoot = options.outputRoot,
                    assetScope = options.assetScope.ToString(),
                    sceneSelectionMode = options.sceneSelectionMode.ToString(),
                    generateCpp = options.generateCpp,
                    generateJson = options.generateJson,
                    generateCppProject = options.generateCppProject,
                    convertSceneToCpp = options.convertSceneToCpp,
                    cyberEngineRootPath = options.cyberEngineRootPath,
                    generatedProjectName = options.generatedProjectName,
                    baseSceneClass = options.baseSceneClass,
                    failOnError = options.failOnError,
                    cleanOutput = options.cleanOutput
                }
            };
            result.manifest = manifest;

            var safeProjectFolderName = BuildSafeDirectoryName(Application.productName);
            var bundleRoot = Path.Combine(Path.GetFullPath(options.outputRoot), safeProjectFolderName);
            result.bundleRoot = bundleRoot;

            if (!options.IsValid(out var validationError))
            {
                report.errors.Add("Invalid export options: " + validationError);
                watch.Stop();
                report.durationSeconds = (float)watch.Elapsed.TotalSeconds;
                TryWriteFailureOutputs(bundleRoot, report, manifest);

                if (options.failOnError)
                    throw new InvalidOperationException("Invalid export options: " + validationError);

                return result;
            }

            try
            {
                PrepareOutputDirectories(bundleRoot, options.cleanOutput);
                var assets = new AssetExportDatabase(bundleRoot, manifest, report);
                var customComponents = new CustomComponentGenerator();
                var collector = new SceneCollector(assets, customComponents, report);

                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    throw new InvalidOperationException("Export cancelled because modified scenes were not saved.");

                var originalScenePath = SceneManager.GetActiveScene().path;
                var sceneDataList = new List<SceneExportData>();
                foreach (var scenePath in options.scenePaths
                             .Where(p => !string.IsNullOrWhiteSpace(p))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), scenePath)))
                    {
                        report.errors.Add("Scene path not found: " + scenePath);
                        continue;
                    }

                    try
                    {
                        var sceneData = collector.CollectScene(scenePath);
                        sceneDataList.Add(sceneData);
                        report.stats.entityCount += sceneData.entities.Count;
                        foreach (var warning in sceneData.warnings)
                        {
                            if (!report.warnings.Contains(warning))
                                report.warnings.Add(warning);
                        }
                    }
                    catch (Exception sceneEx)
                    {
                        report.errors.Add("Scene export failed for " + scenePath + ": " + sceneEx.Message);
                    }
                }

                if (!string.IsNullOrWhiteSpace(originalScenePath) && File.Exists(Path.Combine(Directory.GetCurrentDirectory(), originalScenePath)))
                {
                    EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
                }

                if (sceneDataList.Count == 0)
                {
                    report.errors.Add("No scenes were exported. Check Build Settings scene list and input scene paths.");
                }

                var discoveredAssets = SceneCollector.DiscoverAssets(options);
                assets.ExportDiscoveredAssets(discoveredAssets);

                if (options.generateJson)
                {
                    foreach (var sceneData in sceneDataList)
                    {
                        var jsonFileName = sceneData.sceneName + ".scene.json";
                        var jsonRelativePath = ExportUtils.NormalizeRelativePath(Path.Combine("assets", "data", "scenes", jsonFileName));
                        var jsonAbsolutePath = Path.Combine(bundleRoot, jsonRelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(jsonAbsolutePath) ?? bundleRoot);
                        File.WriteAllText(jsonAbsolutePath, JsonConvert.SerializeObject(sceneData, Formatting.Indented));

                        var sceneEntry = FindOrCreateSceneEntry(manifest, sceneData);
                        sceneEntry.sceneJsonPath = jsonRelativePath;
                    }
                }

                if (options.generateCpp)
                {
                    var runtimeHelperGenerator = new CppRuntimeHelperGenerator();
                    runtimeHelperGenerator.WriteFiles(bundleRoot);

                    if (options.convertSceneToCpp)
                    {
                        var cpp = new CppSceneGenerator();
                        foreach (var sceneData in sceneDataList)
                        {
                            var generatedEntry = cpp.WriteSceneFiles(bundleRoot, sceneData, options);
                            var sceneEntry = FindOrCreateSceneEntry(manifest, sceneData);
                            sceneEntry.sceneHeaderPath = generatedEntry.sceneHeaderPath;
                            sceneEntry.sceneCppPath = generatedEntry.sceneCppPath;
                            sceneEntry.entityCount = generatedEntry.entityCount;
                            sceneEntry.customComponentCount = generatedEntry.customComponentCount;
                            sceneEntry.warningCount = generatedEntry.warningCount;
                        }
                    }
                    else
                    {
                        report.warnings.Add("Scene C++ generation disabled (convertSceneToCpp=false). Generated project will load scenes from JSON.");
                    }

                    customComponents.WriteHeaders(bundleRoot, manifest);
                    report.stats.generatedCustomComponentCount = customComponents.Schemas.Count;
                }

                if (options.generateCppProject)
                {
                    if (!options.generateCpp)
                    {
                        report.errors.Add("C++ project generation requires generateCpp=true.");
                    }
                    else
                    {
                        var projectGenerator = new CppProjectGenerator();
                        manifest.generatedProject = projectGenerator.WriteProject(bundleRoot, manifest, options, report);
                    }
                }

                var componentAudit = collector.BuildComponentAuditReport(manifest.generatedAtUtc);
                manifest.componentAudit = componentAudit.summary;
                report.stats.totalComponentTypeCount = componentAudit.summary.totalComponentTypeCount;
                report.stats.unsupportedBuiltinTypeCount = componentAudit.summary.unsupportedBuiltinTypeCount;
                report.stats.unsupportedBuiltinInstanceCount = componentAudit.summary.unsupportedBuiltinInstanceCount;
                WriteComponentAuditFiles(bundleRoot, componentAudit);

                report.stats.sceneCount = sceneDataList.Count;
                report.stats.materialCount = sceneDataList
                    .SelectMany(s => s.entities)
                    .Where(e => e.model != null && e.model.material != null)
                    .Select(e => e.model.material.stableId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                WriteGameDataFile(bundleRoot, manifest);

                result.scenes = sceneDataList;

                watch.Stop();
                report.durationSeconds = (float)watch.Elapsed.TotalSeconds;
                WriteSuccessOutputs(bundleRoot, report, manifest);

                if (result.HasErrors && options.failOnError)
                    throw new InvalidOperationException("Export completed with errors. See report.json.");
            }
            catch (Exception ex)
            {
                if (!report.errors.Contains(ex.Message))
                    report.errors.Add(ex.Message);

                watch.Stop();
                report.durationSeconds = (float)watch.Elapsed.TotalSeconds;
                TryWriteFailureOutputs(bundleRoot, report, manifest);

                if (options.failOnError)
                    throw;
            }

            return result;
        }

        private static SceneManifestEntry FindOrCreateSceneEntry(ExportManifest manifest, SceneExportData sceneData)
        {
            var entry = manifest.scenes.FirstOrDefault(s => string.Equals(s.sceneAssetPath, sceneData.sceneAssetPath, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                return entry;

            entry = new SceneManifestEntry
            {
                sceneName = sceneData.sceneName,
                sceneAssetPath = sceneData.sceneAssetPath,
                entityCount = sceneData.entities.Count,
                customComponentCount = sceneData.entities.Sum(e => e.customComponents != null ? e.customComponents.Count : 0),
                warningCount = sceneData.warnings.Count
            };
            manifest.scenes.Add(entry);
            return entry;
        }

        private static void PrepareOutputDirectories(string root, bool cleanOutput)
        {
            if (cleanOutput && Directory.Exists(root))
                Directory.Delete(root, true);

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            Directory.CreateDirectory(Path.Combine(root, "assets", "data"));
            Directory.CreateDirectory(Path.Combine(root, "assets", "data", "scenes"));
            Directory.CreateDirectory(Path.Combine(root, "assets", "models"));
            Directory.CreateDirectory(Path.Combine(root, "assets", "textures"));
            Directory.CreateDirectory(Path.Combine(root, "assets", "audio"));
            Directory.CreateDirectory(Path.Combine(root, "assets", "terrains"));
            Directory.CreateDirectory(Path.Combine(root, "game"));
            Directory.CreateDirectory(Path.Combine(root, "game", "scenes"));
            Directory.CreateDirectory(Path.Combine(root, "game", "components"));
            Directory.CreateDirectory(Path.Combine(root, "game", "components", "generated"));
            Directory.CreateDirectory(Path.Combine(root, "game", "src"));
        }

        private static void WriteComponentAuditFiles(string bundleRoot, ComponentAuditReport componentAudit)
        {
            var auditJsonPath = Path.Combine(bundleRoot, "assets", "data", "component_audit.json");
            Directory.CreateDirectory(Path.GetDirectoryName(auditJsonPath) ?? bundleRoot);
            File.WriteAllText(auditJsonPath, JsonConvert.SerializeObject(componentAudit, Formatting.Indented));

            var auditMarkdownPath = Path.Combine(bundleRoot, "assets", "data", "component_audit.md");
            File.WriteAllText(auditMarkdownPath, BuildComponentAuditMarkdown(componentAudit));
        }

        private static string BuildComponentAuditMarkdown(ComponentAuditReport audit)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Component Audit");
            sb.AppendLine();
            sb.AppendLine("Generated: " + audit.generatedAtUtc);
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine("- Total component types: " + audit.summary.totalComponentTypeCount);
            sb.AppendLine("- Unsupported built-in types: " + audit.summary.unsupportedBuiltinTypeCount);
            sb.AppendLine("- Unsupported built-in instances: " + audit.summary.unsupportedBuiltinInstanceCount);
            sb.AppendLine();

            sb.AppendLine("## Top Missing Built-in Components");
            if (audit.summary.topMissing == null || audit.summary.topMissing.Count == 0)
            {
                sb.AppendLine("- None");
            }
            else
            {
                foreach (var top in audit.summary.topMissing)
                {
                    sb.AppendLine("- `" + top.typeName + "` usage=" + top.usageCount +
                                  " impactWeight=" + top.impactWeight.ToString("0.##", CultureInfo.InvariantCulture) +
                                  " score=" + top.score.ToString("0.##", CultureInfo.InvariantCulture));
                }
            }
            sb.AppendLine();

            sb.AppendLine("## Structural Gaps");
            foreach (var gap in audit.structuralGaps)
            {
                sb.AppendLine("- " + gap.title + ": " + (gap.detected ? "DETECTED" : "not detected") +
                              " (count=" + gap.relatedComponentCount + ")");
                if (gap.relatedTypes != null && gap.relatedTypes.Count > 0)
                    sb.AppendLine("  related: " + string.Join(", ", gap.relatedTypes));
            }
            sb.AppendLine();

            sb.AppendLine("## Components");
            foreach (var component in audit.components.OrderByDescending(c => c.score).ThenBy(c => c.typeName, StringComparer.Ordinal))
            {
                sb.AppendLine("### `" + component.typeName + "`");
                sb.AppendLine("- classification: `" + component.classification + "`");
                sb.AppendLine("- usageCount: " + component.usageCount);
                sb.AppendLine("- impactWeight: " + component.impactWeight.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("- score: " + component.score.ToString("0.##", CultureInfo.InvariantCulture));

                if (component.scenes != null && component.scenes.Count > 0)
                {
                    sb.AppendLine("- scenes:");
                    foreach (var scene in component.scenes)
                        sb.AppendLine("  - " + scene.sceneName + ": " + scene.count);
                }

                if (component.examples != null && component.examples.Count > 0)
                {
                    sb.AppendLine("- examples:");
                    foreach (var example in component.examples)
                        sb.AppendLine("  - " + example);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static void WriteSuccessOutputs(string bundleRoot, ExportReport report, ExportManifest manifest)
        {
            var dataDir = Path.Combine(bundleRoot, "assets", "data");
            Directory.CreateDirectory(dataDir);

            var manifestPath = Path.Combine(dataDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            var reportJsonPath = Path.Combine(dataDir, "report.json");
            File.WriteAllText(reportJsonPath, JsonConvert.SerializeObject(report, Formatting.Indented));

            var reportTextPath = Path.Combine(dataDir, "report.txt");
            File.WriteAllText(reportTextPath, BuildHumanReport(report, manifest));
        }

        private static void WriteGameDataFile(string bundleRoot, ExportManifest manifest)
        {
            if (manifest == null)
                return;

            var gameData = new GameExportData
            {
                projectName = manifest.projectName,
                generatedAtUtc = manifest.generatedAtUtc,
                defaultSceneName = manifest.generatedProject != null && !string.IsNullOrWhiteSpace(manifest.generatedProject.defaultSceneName)
                    ? manifest.generatedProject.defaultSceneName
                    : (manifest.scenes.Count > 0 ? manifest.scenes[0].sceneName : string.Empty)
            };

            foreach (var scene in manifest.scenes.OrderBy(s => s.sceneName, StringComparer.OrdinalIgnoreCase))
            {
                gameData.scenes.Add(new GameSceneEntry
                {
                    sceneName = scene.sceneName,
                    sceneJsonPath = scene.sceneJsonPath,
                    sceneHeaderPath = scene.sceneHeaderPath,
                    sceneCppPath = scene.sceneCppPath
                });
            }

            var relativePath = ExportUtils.NormalizeRelativePath(Path.Combine("assets", "data", "game.json"));
            var absolutePath = Path.Combine(bundleRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? bundleRoot);
            File.WriteAllText(absolutePath, JsonConvert.SerializeObject(gameData, Formatting.Indented));
            manifest.gameDataPath = relativePath;
        }

        private static void TryWriteFailureOutputs(string bundleRoot, ExportReport report, ExportManifest manifest)
        {
            try
            {
                Directory.CreateDirectory(bundleRoot);
                var dataDir = Path.Combine(bundleRoot, "assets", "data");
                Directory.CreateDirectory(dataDir);

                var manifestPath = Path.Combine(dataDir, "manifest.json");
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

                var reportJsonPath = Path.Combine(dataDir, "report.json");
                File.WriteAllText(reportJsonPath, JsonConvert.SerializeObject(report, Formatting.Indented));

                var reportTextPath = Path.Combine(dataDir, "report.txt");
                File.WriteAllText(reportTextPath, BuildHumanReport(report, manifest));
            }
            catch
            {
                // Best effort for failure report writing.
            }
        }

        private static string BuildHumanReport(ExportReport report, ExportManifest manifest)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Unity -> CyberEngine Export Report");
            sb.AppendLine("Generated: " + report.generatedAtUtc);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Duration: {0:0.00}s", report.durationSeconds));
            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine("- Scenes: " + report.stats.sceneCount);
            sb.AppendLine("- Entities: " + report.stats.entityCount);
            sb.AppendLine("- Materials: " + report.stats.materialCount);
            sb.AppendLine("- Texture assets: " + report.stats.textureCount);
            sb.AppendLine("- Model assets: " + report.stats.modelAssetCount);
            sb.AppendLine("- Audio assets: " + report.stats.audioAssetCount);
            sb.AppendLine("- Terrain assets: " + report.stats.terrainAssetCount);
            sb.AppendLine("- Generated custom components: " + report.stats.generatedCustomComponentCount);
            sb.AppendLine("- Component types: " + report.stats.totalComponentTypeCount);
            sb.AppendLine("- Unsupported built-in component types: " + report.stats.unsupportedBuiltinTypeCount);
            sb.AppendLine("- Unsupported built-in component instances: " + report.stats.unsupportedBuiltinInstanceCount);
            sb.AppendLine();

            if (manifest.scenes.Count > 0)
            {
                sb.AppendLine("Scenes:");
                foreach (var scene in manifest.scenes)
                {
                    sb.AppendLine("- " + scene.sceneName + " (entities=" + scene.entityCount + ", warnings=" + scene.warningCount + ")");
                }
                if (!string.IsNullOrWhiteSpace(manifest.gameDataPath))
                    sb.AppendLine("- Game data: " + manifest.gameDataPath);
                sb.AppendLine();
            }

            if (manifest.componentAudit != null && manifest.componentAudit.topMissing != null && manifest.componentAudit.topMissing.Count > 0)
            {
                sb.AppendLine("Top missing built-in components:");
                foreach (var missing in manifest.componentAudit.topMissing.Take(10))
                {
                    sb.AppendLine("- " + missing.typeName + " (usage=" + missing.usageCount +
                                  ", score=" + missing.score.ToString("0.##", CultureInfo.InvariantCulture) + ")");
                }
                sb.AppendLine();
            }

            if (manifest.generatedProject != null)
            {
                sb.AppendLine("Generated C++ project:");
                sb.AppendLine("- Root: " + manifest.generatedProject.rootPath);
                sb.AppendLine("- Default scene: " + manifest.generatedProject.defaultSceneName);
                sb.AppendLine("- Scene loading mode: " + (string.IsNullOrWhiteSpace(manifest.generatedProject.sceneLoadingMode)
                    ? "cpp"
                    : manifest.generatedProject.sceneLoadingMode));
                sb.AppendLine("- CyberEngine folder required: CyberEngine/");
                sb.AppendLine("- Build: cmake -S . -B build");
                sb.AppendLine("- Build: cmake --build build");
                sb.AppendLine("- Run: build/game/<exe_name> --scene \"" + manifest.generatedProject.defaultSceneName + "\"");
                sb.AppendLine();
            }

            if (report.warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (var warning in report.warnings)
                    sb.AppendLine("- " + warning);
                sb.AppendLine();
            }

            if (report.errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                foreach (var error in report.errors)
                    sb.AppendLine("- " + error);
            }
            else
            {
                sb.AppendLine("Errors: none");
            }

            return sb.ToString();
        }

        private static string BuildSafeDirectoryName(string name)
        {
            var value = string.IsNullOrWhiteSpace(name) ? "UnityExportedProject" : name.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
                value = value.Replace(c, '_');

            if (string.IsNullOrWhiteSpace(value))
                value = "UnityExportedProject";

            return value;
        }
    }
}
#endif
