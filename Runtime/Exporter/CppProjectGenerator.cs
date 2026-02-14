#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Demonixis.UnityJSONSceneExporter
{
    internal sealed class CppProjectGenerator
    {
        public GeneratedProjectManifestEntry WriteProject(string bundleRoot, ExportManifest manifest, ExportOptions options,
                                                          ExportReport report)
        {
            var entry = new GeneratedProjectManifestEntry();
            if (manifest == null || options == null)
                return entry;

            var cppScenes = manifest.scenes
                .Where(s => !string.IsNullOrWhiteSpace(s.sceneCppPath) && !string.IsNullOrWhiteSpace(s.sceneHeaderPath))
                .OrderBy(s => s.sceneName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var jsonScenes = manifest.scenes
                .Where(s => !string.IsNullOrWhiteSpace(s.sceneJsonPath))
                .OrderBy(s => s.sceneName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var useJsonRuntime = !options.convertSceneToCpp;
            var runtimeScenes = useJsonRuntime ? jsonScenes : cppScenes;

            if (runtimeScenes.Count == 0)
            {
                report.errors.Add(useJsonRuntime
                    ? "C++ project generation skipped: no scene JSON files found in manifest."
                    : "C++ project generation skipped: no generated scene C++ files found in manifest.");
                return entry;
            }

            var projectName = ExportUtils.SanitizeIdentifier(options.generatedProjectName, "UnityExportedProject");
            var gameDir = Path.Combine(bundleRoot, "game");
            var gameSrcDir = Path.Combine(gameDir, "src");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(gameSrcDir);

            var defaultSceneName = string.IsNullOrWhiteSpace(runtimeScenes[0].sceneName)
                ? Path.GetFileNameWithoutExtension(useJsonRuntime ? runtimeScenes[0].sceneJsonPath : runtimeScenes[0].sceneHeaderPath)
                : runtimeScenes[0].sceneName;

            var rootCmakePath = Path.Combine(bundleRoot, "CMakeLists.txt");
            var gameCmakePath = Path.Combine(gameDir, "CMakeLists.txt");
            var mainPath = Path.Combine(gameSrcDir, "main.cpp");
            var registryHeaderPath = Path.Combine(gameSrcDir, "generated_scene_registry.hpp");
            var registryCppPath = Path.Combine(gameSrcDir, "generated_scene_registry.cpp");
            var readmePath = Path.Combine(gameDir, "README.md");

            string jsonLoaderHeaderPath = null;
            string jsonLoaderCppPath = null;
            string jsonRuntimeHeaderPath = null;
            string jsonRuntimeCppPath = null;

            if (useJsonRuntime)
            {
                jsonLoaderHeaderPath = Path.Combine(gameSrcDir, "json_scene_loader.hpp");
                jsonLoaderCppPath = Path.Combine(gameSrcDir, "json_scene_loader.cpp");
                jsonRuntimeHeaderPath = Path.Combine(gameSrcDir, "json_runtime_scene.hpp");
                jsonRuntimeCppPath = Path.Combine(gameSrcDir, "json_runtime_scene.cpp");

                File.WriteAllText(jsonLoaderHeaderPath, BuildJsonSceneLoaderHeader());
                File.WriteAllText(jsonLoaderCppPath, BuildJsonSceneLoaderCpp());
                File.WriteAllText(jsonRuntimeHeaderPath, BuildJsonRuntimeSceneHeader());
                File.WriteAllText(jsonRuntimeCppPath, BuildJsonRuntimeSceneCpp());
            }

            File.WriteAllText(rootCmakePath, BuildRootCMake(projectName));
            File.WriteAllText(gameCmakePath, BuildGameCMake(projectName, cppScenes, useJsonRuntime));
            File.WriteAllText(registryHeaderPath, BuildRegistryHeader());
            File.WriteAllText(registryCppPath, BuildRegistryCpp(runtimeScenes, useJsonRuntime));
            File.WriteAllText(mainPath, BuildMain(projectName, defaultSceneName));
            File.WriteAllText(readmePath, BuildReadme(projectName, defaultSceneName, useJsonRuntime));

            entry.rootPath = ".";
            entry.cmakePath = ExportUtils.NormalizeRelativePath("CMakeLists.txt");
            entry.mainPath = ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "main.cpp"));
            entry.sceneRegistryHeaderPath = ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "generated_scene_registry.hpp"));
            entry.sceneRegistryCppPath = ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "generated_scene_registry.cpp"));
            entry.readmePath = ExportUtils.NormalizeRelativePath(Path.Combine("game", "README.md"));
            entry.cyberEngineLinkPath = "CyberEngine";
            entry.cyberEngineLinkCreated = Directory.Exists(Path.Combine(bundleRoot, "CyberEngine"));
            entry.defaultSceneName = defaultSceneName;
            entry.sceneLoadingMode = useJsonRuntime ? "json" : "cpp";
            entry.jsonSceneLoaderHeaderPath = useJsonRuntime
                ? ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "json_scene_loader.hpp"))
                : string.Empty;
            entry.jsonSceneLoaderCppPath = useJsonRuntime
                ? ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "json_scene_loader.cpp"))
                : string.Empty;
            entry.jsonRuntimeSceneHeaderPath = useJsonRuntime
                ? ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "json_runtime_scene.hpp"))
                : string.Empty;
            entry.jsonRuntimeSceneCppPath = useJsonRuntime
                ? ExportUtils.NormalizeRelativePath(Path.Combine("game", "src", "json_runtime_scene.cpp"))
                : string.Empty;
            return entry;
        }

        private static string BuildRegistryHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <scene/scene_manager.hpp>");
            sb.AppendLine("#include <string>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("void RegisterGeneratedScenes(SceneManager& sceneManager);");
            sb.AppendLine("std::vector<std::string> GetGeneratedSceneNames();");
            return sb.ToString();
        }

        private static string BuildRegistryCpp(List<SceneManifestEntry> scenes, bool useJsonRuntime)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#include \"generated_scene_registry.hpp\"");
            if (useJsonRuntime)
                sb.AppendLine("#include \"json_runtime_scene.hpp\"");
            sb.AppendLine();

            if (useJsonRuntime)
            {
                sb.AppendLine("namespace");
                sb.AppendLine("{");
                for (var i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    var className = ExportUtils.SanitizeIdentifier((scene.sceneName ?? "Scene") + "JsonRuntime" + i, "GeneratedJsonRuntimeScene");
                    var sceneName = ExportUtils.EscapeCppString(scene.sceneName ?? className);
                    var sceneJsonPath = ExportUtils.EscapeCppString(scene.sceneJsonPath ?? string.Empty);
                    sb.AppendLine("class " + className + " final : public JsonRuntimeScene");
                    sb.AppendLine("{");
                    sb.AppendLine("  public:");
                    sb.AppendLine("    " + className + "() : JsonRuntimeScene(\"" + sceneName + "\", \"" + sceneJsonPath + "\") {}");
                    sb.AppendLine("};");
                }
                sb.AppendLine("} // namespace");
            }
            else
            {
                foreach (var scene in scenes)
                {
                    var sceneHeaderName = Path.GetFileName(scene.sceneHeaderPath);
                    sb.AppendLine("#include \"" + ExportUtils.EscapeCppString(sceneHeaderName) + "\"");
                }
            }

            sb.AppendLine();
            sb.AppendLine("void RegisterGeneratedScenes(SceneManager& sceneManager)");
            sb.AppendLine("{");
            for (var i = 0; i < scenes.Count; i++)
            {
                var scene = scenes[i];
                var sceneName = ExportUtils.EscapeCppString(scene.sceneName ?? "Scene");
                var className = useJsonRuntime
                    ? ExportUtils.SanitizeIdentifier((scene.sceneName ?? "Scene") + "JsonRuntime" + i, "GeneratedJsonRuntimeScene")
                    : ExportUtils.SanitizeIdentifier(Path.GetFileNameWithoutExtension(scene.sceneHeaderPath), "GeneratedScene");
                sb.AppendLine("    sceneManager.AddScene<" + className + ">(\"" + sceneName + "\");");
            }
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("std::vector<std::string> GetGeneratedSceneNames()");
            sb.AppendLine("{");
            sb.AppendLine("    return {");
            for (var i = 0; i < scenes.Count; i++)
            {
                var sceneName = ExportUtils.EscapeCppString(scenes[i].sceneName ?? "Scene");
                var suffix = i < scenes.Count - 1 ? "," : string.Empty;
                sb.AppendLine("        \"" + sceneName + "\"" + suffix);
            }
            sb.AppendLine("    };");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string BuildMain(string projectName, string defaultSceneName)
        {
            var safeDefaultScene = ExportUtils.EscapeCppString(defaultSceneName);
            var safeProjectName = ExportUtils.EscapeCppString(projectName);

            var sb = new StringBuilder();
            sb.AppendLine("#include \"generated_scene_registry.hpp\"");
            sb.AppendLine("#include <core/game.hpp>");
            sb.AppendLine("#include <core/game_settings.hpp>");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <iostream>");
            sb.AppendLine("#include <string>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("static std::string ParseSceneArgument(int argc, char* argv[])");
            sb.AppendLine("{");
            sb.AppendLine("    for (int i = 1; i < argc; ++i)");
            sb.AppendLine("    {");
            sb.AppendLine("        const std::string arg = argv[i];");
            sb.AppendLine("        if (arg == \"--scene\" && i + 1 < argc)");
            sb.AppendLine("            return argv[i + 1];");
            sb.AppendLine("        const std::string prefix = \"--scene=\";");
            sb.AppendLine("        if (arg.rfind(prefix, 0) == 0)");
            sb.AppendLine("            return arg.substr(prefix.size());");
            sb.AppendLine("    }");
            sb.AppendLine("    return std::string();");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("int main(int argc, char* argv[])");
            sb.AppendLine("{");
            sb.AppendLine("    GameSettings settings;");
            sb.AppendLine("    settings.resolutionWidth = 1280;");
            sb.AppendLine("    settings.resolutionHeight = 720;");
            sb.AppendLine();
            sb.AppendLine("    Game game(\"" + safeProjectName + "\", settings);");
            sb.AppendLine("    auto& sceneManager = game.GetSceneManager();");
            sb.AppendLine("    RegisterGeneratedScenes(sceneManager);");
            sb.AppendLine();
            sb.AppendLine("    const std::vector<std::string> sceneNames = GetGeneratedSceneNames();");
            sb.AppendLine("    if (sceneNames.empty())");
            sb.AppendLine("    {");
            sb.AppendLine("        std::cerr << \"No generated scenes available.\" << std::endl;");
            sb.AppendLine("        return 1;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    std::string initialScene = \"" + safeDefaultScene + "\";");
            sb.AppendLine("    const std::string requestedScene = ParseSceneArgument(argc, argv);");
            sb.AppendLine("    if (!requestedScene.empty())");
            sb.AppendLine("    {");
            sb.AppendLine("        const bool exists = std::find(sceneNames.begin(), sceneNames.end(), requestedScene) != sceneNames.end();");
            sb.AppendLine("        if (exists)");
            sb.AppendLine("            initialScene = requestedScene;");
            sb.AppendLine("        else");
            sb.AppendLine("            std::cerr << \"Unknown scene '\" << requestedScene << \"', loading default scene.\" << std::endl;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    sceneManager.LoadScene(initialScene);");
            sb.AppendLine("    game.Run();");
            sb.AppendLine("    return 0;");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string BuildRootCMake(string projectName)
        {
            var safeProjectName = ExportUtils.SanitizeIdentifier(projectName, "UnityExportedProject");
            var sb = new StringBuilder();
            sb.AppendLine("cmake_minimum_required(VERSION 3.20)");
            sb.AppendLine("project(" + safeProjectName + " VERSION 1.0.0 LANGUAGES CXX)");
            sb.AppendLine();
            sb.AppendLine("set(CMAKE_CXX_STANDARD 20)");
            sb.AppendLine("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
            sb.AppendLine("set(CMAKE_EXPORT_COMPILE_COMMANDS ON)");
            sb.AppendLine();
            sb.AppendLine("set(CYBERENGINE_DIR \"${CMAKE_CURRENT_LIST_DIR}/CyberEngine\")");
            sb.AppendLine("if(NOT EXISTS \"${CYBERENGINE_DIR}/CMakeLists.txt\")");
            sb.AppendLine("    message(FATAL_ERROR \"CyberEngine not found. Copy or clone CyberEngine into ${CMAKE_CURRENT_LIST_DIR}/CyberEngine\")");
            sb.AppendLine("endif()");
            sb.AppendLine();
            sb.AppendLine("function(ensure_ce_compat_link link_name target_path)");
            sb.AppendLine("    set(link_path \"${CMAKE_CURRENT_LIST_DIR}/${link_name}\")");
            sb.AppendLine("    if(EXISTS \"${link_path}\" OR NOT EXISTS \"${target_path}\")");
            sb.AppendLine("        return()");
            sb.AppendLine("    endif()");
            sb.AppendLine("    execute_process(");
            sb.AppendLine("        COMMAND ${CMAKE_COMMAND} -E create_symlink \"${target_path}\" \"${link_path}\"");
            sb.AppendLine("        RESULT_VARIABLE link_result");
            sb.AppendLine("        OUTPUT_QUIET");
            sb.AppendLine("        ERROR_QUIET");
            sb.AppendLine("    )");
            sb.AppendLine("    if(NOT link_result EQUAL 0)");
            sb.AppendLine("        file(COPY \"${target_path}\" DESTINATION \"${CMAKE_CURRENT_LIST_DIR}\")");
            sb.AppendLine("    endif()");
            sb.AppendLine("endfunction()");
            sb.AppendLine();
            sb.AppendLine("ensure_ce_compat_link(\"thirdparty\" \"${CYBERENGINE_DIR}/thirdparty\")");
            sb.AppendLine("ensure_ce_compat_link(\"demos\" \"${CYBERENGINE_DIR}/demos\")");
            sb.AppendLine("ensure_ce_compat_link(\"shaders\" \"${CYBERENGINE_DIR}/shaders\")");
            sb.AppendLine();
            sb.AppendLine("add_subdirectory(\"CyberEngine\" EXCLUDE_FROM_ALL)");
            sb.AppendLine("add_subdirectory(\"game\")");
            return sb.ToString();
        }

        private static string BuildGameCMake(string projectName, List<SceneManifestEntry> scenes, bool useJsonRuntime)
        {
            var safeProjectName = ExportUtils.SanitizeIdentifier(projectName, "UnityExportedProject");
            var sb = new StringBuilder();
            sb.AppendLine("set(GENERATED_SCENE_SOURCES");
            sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/scenes/scene_export_runtime_helper.cpp\"");
            if (useJsonRuntime)
            {
                sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/src/json_scene_loader.cpp\"");
                sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/src/json_runtime_scene.cpp\"");
            }
            else
            {
                foreach (var scene in scenes)
                {
                    var sceneCpp = ToPathRelativeToGameDirectory(scene.sceneCppPath);
                    sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/" + sceneCpp + "\"");
                }
            }
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("add_executable(" + safeProjectName);
            sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/src/main.cpp\"");
            sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/src/generated_scene_registry.cpp\"");
            sb.AppendLine("    ${GENERATED_SCENE_SOURCES}");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("target_include_directories(" + safeProjectName + " PRIVATE");
            sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/src\"");
            sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/scenes\"");
            sb.AppendLine("    \"${CMAKE_CURRENT_LIST_DIR}/components\"");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("target_link_libraries(" + safeProjectName + " PRIVATE CyberEngine)");
            if (useJsonRuntime)
                sb.AppendLine("target_link_libraries(" + safeProjectName + " PRIVATE nlohmann_json::nlohmann_json)");
            sb.AppendLine();
            sb.AppendLine("if(WIN32)");
            sb.AppendLine("    target_link_libraries(" + safeProjectName + " PRIVATE opengl32)");
            sb.AppendLine("elseif (UNIX AND NOT APPLE)");
            sb.AppendLine("    target_link_libraries(" + safeProjectName + " PRIVATE pthread)");
            sb.AppendLine("endif()");
            sb.AppendLine();
            sb.AppendLine("set(EXPORT_ASSETS_DIR \"${CMAKE_CURRENT_LIST_DIR}/../assets\")");
            sb.AppendLine("set(CYBERENGINE_SHADERS_DIR \"${CMAKE_CURRENT_LIST_DIR}/../CyberEngine/shaders\")");
            sb.AppendLine("set(OUTPUT_DIR \"$<TARGET_FILE_DIR:" + safeProjectName + ">\")");
            sb.AppendLine();
            sb.AppendLine("if(WIN32)");
            sb.AppendLine("    add_custom_command(TARGET " + safeProjectName + " POST_BUILD");
            sb.AppendLine("        COMMAND ${CMAKE_COMMAND} -E copy_directory_if_different \"${EXPORT_ASSETS_DIR}\" \"${OUTPUT_DIR}/assets\"");
            sb.AppendLine("        COMMAND ${CMAKE_COMMAND} -E copy_directory_if_different \"${CYBERENGINE_SHADERS_DIR}\" \"${OUTPUT_DIR}/shaders\"");
            sb.AppendLine("    )");
            sb.AppendLine("else()");
            sb.AppendLine("    add_custom_command(TARGET " + safeProjectName + " POST_BUILD");
            sb.AppendLine("        COMMAND ${CMAKE_COMMAND} -E rm -rf \"${OUTPUT_DIR}/assets\" \"${OUTPUT_DIR}/shaders\"");
            sb.AppendLine("        COMMAND ${CMAKE_COMMAND} -E create_symlink \"${EXPORT_ASSETS_DIR}\" \"${OUTPUT_DIR}/assets\"");
            sb.AppendLine("        COMMAND ${CMAKE_COMMAND} -E create_symlink \"${CYBERENGINE_SHADERS_DIR}\" \"${OUTPUT_DIR}/shaders\"");
            sb.AppendLine("    )");
            sb.AppendLine("endif()");
            return sb.ToString();
        }

        private static string BuildReadme(string projectName, string defaultSceneName, bool useJsonRuntime)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Generated CyberEngine Runner");
            sb.AppendLine();
            sb.AppendLine("- Project: `" + projectName + "`");
            sb.AppendLine("- Default scene: `" + defaultSceneName + "`");
            sb.AppendLine("- Scene loading mode: `" + (useJsonRuntime ? "json-runtime-loader" : "generated-cpp-scenes") + "`");
            sb.AppendLine();
            sb.AppendLine("Prerequisite:");
            sb.AppendLine("- Copy or clone CyberEngine into `./CyberEngine`");
            sb.AppendLine();

            sb.AppendLine("Build:");
            sb.AppendLine("- `cmake -S . -B build`");
            sb.AppendLine("- `cmake --build build`");
            sb.AppendLine();
            sb.AppendLine("Run:");
            sb.AppendLine("- `build/game/<exe_name>`");
            sb.AppendLine("- `build/game/<exe_name> --scene \"" + defaultSceneName + "\"`");
            return sb.ToString();
        }

        private static string ToPathRelativeToGameDirectory(string manifestPath)
        {
            var path = (manifestPath ?? string.Empty).Replace('\\', '/');
            const string gamePrefix = "game/";
            if (path.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
                return path.Substring(gamePrefix.Length);
            return path;
        }

        private static string BuildJsonRuntimeSceneHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <scene/scene.hpp>");
            sb.AppendLine("#include <string>");
            sb.AppendLine();
            sb.AppendLine("class JsonRuntimeScene : public Scene");
            sb.AppendLine("{");
            sb.AppendLine("  public:");
            sb.AppendLine("    JsonRuntimeScene(std::string sceneName, std::string sceneJsonRelativePath, std::string exportRoot = \".\");");
            sb.AppendLine("    void Initialize() override;");
            sb.AppendLine("    void Unload() override;");
            sb.AppendLine("    void Update(const GameTime& gameTime) override;");
            sb.AppendLine("    void OnEvent(const GameTime& gameTime, const SDL_Event& event) override;");
            sb.AppendLine("    void OnGUI(const GameTime& gameTime) override;");
            sb.AppendLine();
            sb.AppendLine("  protected:");
            sb.AppendLine("    std::string m_sceneName;");
            sb.AppendLine("    std::string m_sceneJsonRelativePath;");
            sb.AppendLine("    std::string m_exportRoot;");
            sb.AppendLine("    entt::entity m_runtimeCameraEntity = entt::null;");
            sb.AppendLine("    float m_freeCameraYaw = 0.0f;");
            sb.AppendLine("    float m_freeCameraPitch = 0.0f;");
            sb.AppendLine("    bool m_freeCameraMouseLook = false;");
            sb.AppendLine("    bool m_showLightingInspector = true;");
            sb.AppendLine("};");
            return sb.ToString();
        }

        private static string BuildJsonRuntimeSceneCpp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#include \"json_runtime_scene.hpp\"");
            sb.AppendLine("#include \"json_scene_loader.hpp\"");
            sb.AppendLine("#include <SDL3/SDL.h>");
            sb.AppendLine("#include <imgui.h>");
            sb.AppendLine("#include <scene/components/components.hpp>");
            sb.AppendLine("#include <scene/components/lighting_components.hpp>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <iostream>");
            sb.AppendLine("#include <utility>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("JsonRuntimeScene::JsonRuntimeScene(std::string sceneName, std::string sceneJsonRelativePath, std::string exportRoot)");
            sb.AppendLine("    : m_sceneName(std::move(sceneName)),");
            sb.AppendLine("      m_sceneJsonRelativePath(std::move(sceneJsonRelativePath)),");
            sb.AppendLine("      m_exportRoot(std::move(exportRoot))");
            sb.AppendLine("{");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void JsonRuntimeScene::Initialize()");
            sb.AppendLine("{");
            sb.AppendLine("    std::vector<std::string> warnings;");
            sb.AppendLine("    const bool loaded = JsonSceneLoader::LoadScene(m_registry, m_exportRoot, m_sceneJsonRelativePath, &warnings);");
            sb.AppendLine("    if (!loaded)");
            sb.AppendLine("        std::cerr << \"Failed to load scene JSON: \" << m_sceneJsonRelativePath << std::endl;");
            sb.AppendLine();
            sb.AppendLine("    for (const auto& warning : warnings)");
            sb.AppendLine("        std::cerr << \"[JsonRuntimeScene] \" << warning << std::endl;");
            sb.AppendLine();
            sb.AppendLine("    // Attach free camera controls to the active camera (or first available camera).");
            sb.AppendLine("    auto cameraView = m_registry.view<CameraComponent, TransformComponent>();");
            sb.AppendLine("    for (auto cameraEntity : cameraView)");
            sb.AppendLine("    {");
            sb.AppendLine("        auto& camera = cameraView.get<CameraComponent>(cameraEntity);");
            sb.AppendLine("        if (!camera.isActive)");
            sb.AppendLine("            continue;");
            sb.AppendLine("        m_runtimeCameraEntity = cameraEntity;");
            sb.AppendLine("        break;");
            sb.AppendLine("    }");
            sb.AppendLine("    if (m_runtimeCameraEntity == entt::null)");
            sb.AppendLine("    {");
            sb.AppendLine("        for (auto cameraEntity : cameraView)");
            sb.AppendLine("        {");
            sb.AppendLine("            m_runtimeCameraEntity = cameraEntity;");
            sb.AppendLine("            cameraView.get<CameraComponent>(cameraEntity).isActive = true;");
            sb.AppendLine("            break;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    if (m_runtimeCameraEntity != entt::null)");
            sb.AppendLine("    {");
            sb.AppendLine("        const auto& cameraTransform = cameraView.get<TransformComponent>(m_runtimeCameraEntity);");
            sb.AppendLine("        const glm::vec3 forward = glm::normalize(cameraTransform.Forward());");
            sb.AppendLine("        m_freeCameraYaw = glm::degrees(std::atan2(forward.x, forward.z));");
            sb.AppendLine("        m_freeCameraPitch = glm::degrees(-std::asin(glm::clamp(forward.y, -1.0f, 1.0f)));");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void JsonRuntimeScene::Unload()");
            sb.AppendLine("{");
            sb.AppendLine("    m_registry.clear();");
            sb.AppendLine("    m_runtimeCameraEntity = entt::null;");
            sb.AppendLine("    m_freeCameraYaw = 0.0f;");
            sb.AppendLine("    m_freeCameraPitch = 0.0f;");
            sb.AppendLine("    m_freeCameraMouseLook = false;");
            sb.AppendLine("    m_showLightingInspector = true;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void JsonRuntimeScene::Update(const GameTime& gameTime)");
            sb.AppendLine("{");
            sb.AppendLine("    if (m_runtimeCameraEntity == entt::null || !m_registry.valid(m_runtimeCameraEntity) ||");
            sb.AppendLine("        !m_registry.all_of<TransformComponent, CameraComponent>(m_runtimeCameraEntity))");
            sb.AppendLine("        return;");
            sb.AppendLine();
            sb.AppendLine("    const bool* keyState = SDL_GetKeyboardState(nullptr);");
            sb.AppendLine("    auto& camTransform = m_registry.get<TransformComponent>(m_runtimeCameraEntity);");
            sb.AppendLine();
            sb.AppendLine("    constexpr float moveSpeed = 8.0f;");
            sb.AppendLine("    const glm::vec3 forward = camTransform.Forward();");
            sb.AppendLine("    const glm::vec3 right = camTransform.Right();");
            sb.AppendLine("    const glm::vec3 up(0.0f, 1.0f, 0.0f);");
            sb.AppendLine();
            sb.AppendLine("    glm::vec3 movement(0.0f);");
            sb.AppendLine("    if (keyState[SDL_SCANCODE_W] || keyState[SDL_SCANCODE_UP])");
            sb.AppendLine("        movement += forward;");
            sb.AppendLine("    if (keyState[SDL_SCANCODE_S] || keyState[SDL_SCANCODE_DOWN])");
            sb.AppendLine("        movement -= forward;");
            sb.AppendLine("    if (keyState[SDL_SCANCODE_D])");
            sb.AppendLine("        movement += right;");
            sb.AppendLine("    if (keyState[SDL_SCANCODE_A])");
            sb.AppendLine("        movement -= right;");
            sb.AppendLine("    if (keyState[SDL_SCANCODE_E])");
            sb.AppendLine("        movement += up;");
            sb.AppendLine("    if (keyState[SDL_SCANCODE_Q])");
            sb.AppendLine("        movement -= up;");
            sb.AppendLine();
            sb.AppendLine("    if (glm::length(movement) > 0.001f)");
            sb.AppendLine("        camTransform.position += glm::normalize(movement) * moveSpeed * gameTime.deltaTime;");
            sb.AppendLine();
            sb.AppendLine("    const glm::quat qYaw = glm::angleAxis(glm::radians(m_freeCameraYaw), glm::vec3(0.0f, 1.0f, 0.0f));");
            sb.AppendLine("    const glm::quat qPitch = glm::angleAxis(glm::radians(m_freeCameraPitch), glm::vec3(1.0f, 0.0f, 0.0f));");
            sb.AppendLine("    camTransform.rotation = qYaw * qPitch;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void JsonRuntimeScene::OnEvent(const GameTime& gameTime, const SDL_Event& event)");
            sb.AppendLine("{");
            sb.AppendLine("    (void)gameTime;");
            sb.AppendLine();
            sb.AppendLine("    if (event.type == SDL_EVENT_KEY_DOWN && event.key.key == SDLK_TAB)");
            sb.AppendLine("    {");
            sb.AppendLine("        m_freeCameraMouseLook = !m_freeCameraMouseLook;");
            sb.AppendLine("        if (SDL_Window* window = SDL_GetKeyboardFocus())");
            sb.AppendLine("            SDL_SetWindowRelativeMouseMode(window, m_freeCameraMouseLook);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (!m_freeCameraMouseLook)");
            sb.AppendLine("        return;");
            sb.AppendLine();
            sb.AppendLine("    if (event.type == SDL_EVENT_MOUSE_MOTION)");
            sb.AppendLine("    {");
            sb.AppendLine("        constexpr float sensitivity = 0.15f;");
            sb.AppendLine("        m_freeCameraYaw += event.motion.xrel * sensitivity;");
            sb.AppendLine("        m_freeCameraPitch += event.motion.yrel * sensitivity;");
            sb.AppendLine("        m_freeCameraPitch = glm::clamp(m_freeCameraPitch, -89.0f, 89.0f);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("void JsonRuntimeScene::OnGUI(const GameTime& gameTime)");
            sb.AppendLine("{");
            sb.AppendLine("    (void)gameTime;");
            sb.AppendLine();
            sb.AppendLine("    if (!m_showLightingInspector)");
            sb.AppendLine("        return;");
            sb.AppendLine();
            sb.AppendLine("    if (!ImGui::Begin(\"Lighting Inspector\", &m_showLightingInspector))");
            sb.AppendLine("    {");
            sb.AppendLine("        ImGui::End();");
            sb.AppendLine("        return;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    ImGui::TextUnformatted(\"TAB: mouse look, WASD + Q/E: move\");");
            sb.AppendLine();
            sb.AppendLine("    if (ImGui::CollapsingHeader(\"Directional Lights\", ImGuiTreeNodeFlags_DefaultOpen))");
            sb.AppendLine("    {");
            sb.AppendLine("        auto view = m_registry.view<DirectionalLightComponent>();");
            sb.AppendLine("        int index = 0;");
            sb.AppendLine("        for (auto entity : view)");
            sb.AppendLine("        {");
            sb.AppendLine("            auto& light = view.get<DirectionalLightComponent>(entity);");
            sb.AppendLine("            ImGui::PushID(static_cast<int>(static_cast<uint32_t>(entity)));");
            sb.AppendLine("            const std::string label = std::string(\"Directional \") + std::to_string(index++);");
            sb.AppendLine("            if (ImGui::TreeNode(label.c_str()))");
            sb.AppendLine("            {");
            sb.AppendLine("                ImGui::Checkbox(\"Enabled\", &light.enabled);");
            sb.AppendLine("                ImGui::ColorEdit3(\"Color\", &light.color.x);");
            sb.AppendLine("                ImGui::DragFloat(\"Intensity\", &light.intensity, 0.05f, 0.0f, 200.0f);");
            sb.AppendLine("                ImGui::Checkbox(\"Cast Shadows\", &light.castShadows);");
            sb.AppendLine("                ImGui::TreePop();");
            sb.AppendLine("            }");
            sb.AppendLine("            ImGui::PopID();");
            sb.AppendLine("        }");
            sb.AppendLine("        if (index == 0)");
            sb.AppendLine("            ImGui::TextUnformatted(\"No directional lights in scene.\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (ImGui::CollapsingHeader(\"Point Lights\", ImGuiTreeNodeFlags_DefaultOpen))");
            sb.AppendLine("    {");
            sb.AppendLine("        auto view = m_registry.view<PointLightComponent>();");
            sb.AppendLine("        int index = 0;");
            sb.AppendLine("        for (auto entity : view)");
            sb.AppendLine("        {");
            sb.AppendLine("            auto& light = view.get<PointLightComponent>(entity);");
            sb.AppendLine("            ImGui::PushID(static_cast<int>(static_cast<uint32_t>(entity)));");
            sb.AppendLine("            const std::string label = std::string(\"Point \") + std::to_string(index++);");
            sb.AppendLine("            if (ImGui::TreeNode(label.c_str()))");
            sb.AppendLine("            {");
            sb.AppendLine("                ImGui::Checkbox(\"Enabled\", &light.enabled);");
            sb.AppendLine("                ImGui::ColorEdit3(\"Color\", &light.color.x);");
            sb.AppendLine("                ImGui::DragFloat(\"Intensity\", &light.intensity, 0.05f, 0.0f, 200.0f);");
            sb.AppendLine("                ImGui::DragFloat(\"Radius\", &light.radius, 0.05f, 0.0f, 1000.0f);");
            sb.AppendLine("                ImGui::Checkbox(\"Cast Shadows\", &light.castShadows);");
            sb.AppendLine("                ImGui::TreePop();");
            sb.AppendLine("            }");
            sb.AppendLine("            ImGui::PopID();");
            sb.AppendLine("        }");
            sb.AppendLine("        if (index == 0)");
            sb.AppendLine("            ImGui::TextUnformatted(\"No point lights in scene.\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (ImGui::CollapsingHeader(\"Spot Lights\", ImGuiTreeNodeFlags_DefaultOpen))");
            sb.AppendLine("    {");
            sb.AppendLine("        auto view = m_registry.view<SpotLightComponent>();");
            sb.AppendLine("        int index = 0;");
            sb.AppendLine("        for (auto entity : view)");
            sb.AppendLine("        {");
            sb.AppendLine("            auto& light = view.get<SpotLightComponent>(entity);");
            sb.AppendLine("            ImGui::PushID(static_cast<int>(static_cast<uint32_t>(entity)));");
            sb.AppendLine("            const std::string label = std::string(\"Spot \") + std::to_string(index++);");
            sb.AppendLine("            if (ImGui::TreeNode(label.c_str()))");
            sb.AppendLine("            {");
            sb.AppendLine("                ImGui::Checkbox(\"Enabled\", &light.enabled);");
            sb.AppendLine("                ImGui::ColorEdit3(\"Color\", &light.color.x);");
            sb.AppendLine("                ImGui::DragFloat(\"Intensity\", &light.intensity, 0.05f, 0.0f, 200.0f);");
            sb.AppendLine("                ImGui::DragFloat(\"Range\", &light.range, 0.05f, 0.0f, 1000.0f);");
            sb.AppendLine("                float innerDeg = glm::degrees(light.innerConeAngle);");
            sb.AppendLine("                float outerDeg = glm::degrees(light.outerConeAngle);");
            sb.AppendLine("                bool innerChanged = ImGui::SliderFloat(\"Inner Angle\", &innerDeg, 0.0f, 179.0f);");
            sb.AppendLine("                bool outerChanged = ImGui::SliderFloat(\"Outer Angle\", &outerDeg, 0.0f, 179.0f);");
            sb.AppendLine("                if (outerDeg < innerDeg)");
            sb.AppendLine("                    outerDeg = innerDeg;");
            sb.AppendLine("                if (innerChanged || outerChanged)");
            sb.AppendLine("                {");
            sb.AppendLine("                    light.innerConeAngle = glm::radians(innerDeg);");
            sb.AppendLine("                    light.outerConeAngle = glm::radians(outerDeg);");
            sb.AppendLine("                }");
            sb.AppendLine("                ImGui::Checkbox(\"Cast Shadows\", &light.castShadows);");
            sb.AppendLine("                ImGui::TreePop();");
            sb.AppendLine("            }");
            sb.AppendLine("            ImGui::PopID();");
            sb.AppendLine("        }");
            sb.AppendLine("        if (index == 0)");
            sb.AppendLine("            ImGui::TextUnformatted(\"No spot lights in scene.\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    ImGui::End();");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string BuildJsonSceneLoaderHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <entt/entt.hpp>");
            sb.AppendLine("#include <string>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("namespace JsonSceneLoader");
            sb.AppendLine("{");
            sb.AppendLine("    bool LoadScene(entt::registry& registry, const std::string& exportRoot,");
            sb.AppendLine("                   const std::string& sceneJsonRelativePath,");
            sb.AppendLine("                   std::vector<std::string>* outWarnings = nullptr);");
            sb.AppendLine("} // namespace JsonSceneLoader");
            return sb.ToString();
        }

        private static string BuildJsonSceneLoaderCpp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#include \"json_scene_loader.hpp\"");
            sb.AppendLine("#include \"scene_export_runtime_helper.hpp\"");
            sb.AppendLine("#include <assets/mesh_factory.hpp>");
            sb.AppendLine("#include <assets/resource_manager.hpp>");
            sb.AppendLine("#include <graphics/material.hpp>");
            sb.AppendLine("#include <graphics/terrain_material.hpp>");
            sb.AppendLine("#include <nlohmann/json.hpp>");
            sb.AppendLine("#include <scene/components/audio_components.hpp>");
            sb.AppendLine("#include <scene/components/components.hpp>");
            sb.AppendLine("#include <scene/components/lighting_components.hpp>");
            sb.AppendLine("#include <scene/components/mesh_components.hpp>");
            sb.AppendLine("#include <scene/components/physics_components.hpp>");
            sb.AppendLine("#include <scene/hierarchy.hpp>");
            sb.AppendLine("#include <algorithm>");
            sb.AppendLine("#include <array>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <fstream>");
            sb.AppendLine("#include <string>");
            sb.AppendLine("#include <unordered_map>");
            sb.AppendLine("#include <utility>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("namespace");
            sb.AppendLine("{");
            sb.AppendLine("    using json = nlohmann::json;");
            sb.AppendLine();
            sb.AppendLine("    float ReadFloat(const json& value, float fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        return value.is_number() ? value.get<float>() : fallback;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    bool ReadBool(const json& value, bool fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        return value.is_boolean() ? value.get<bool>() : fallback;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    std::string ReadString(const json& value, const std::string& fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        return value.is_string() ? value.get<std::string>() : fallback;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    float ReadArrayFloat(const json& array, size_t index, float fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!array.is_array() || index >= array.size())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        return ReadFloat(array[index], fallback);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    glm::vec2 ReadVec2(const json& array, float x, float y)");
            sb.AppendLine("    {");
            sb.AppendLine("        return glm::vec2(ReadArrayFloat(array, 0, x), ReadArrayFloat(array, 1, y));");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    glm::vec3 ReadVec3(const json& array, float x, float y, float z)");
            sb.AppendLine("    {");
            sb.AppendLine("        return glm::vec3(ReadArrayFloat(array, 0, x), ReadArrayFloat(array, 1, y), ReadArrayFloat(array, 2, z));");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    glm::vec4 ReadVec4(const json& array, float x, float y, float z, float w)");
            sb.AppendLine("    {");
            sb.AppendLine("        return glm::vec4(ReadArrayFloat(array, 0, x), ReadArrayFloat(array, 1, y),");
            sb.AppendLine("                         ReadArrayFloat(array, 2, z), ReadArrayFloat(array, 3, w));");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    glm::quat ReadQuaternion(const json& array)");
            sb.AppendLine("    {");
            sb.AppendLine("        const float x = ReadArrayFloat(array, 0, 0.0f);");
            sb.AppendLine("        const float y = ReadArrayFloat(array, 1, 0.0f);");
            sb.AppendLine("        const float z = ReadArrayFloat(array, 2, 0.0f);");
            sb.AppendLine("        const float w = ReadArrayFloat(array, 3, 1.0f);");
            sb.AppendLine("        return glm::quat(w, x, y, z);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    std::string ReadStringField(const json& object, const char* key, const std::string& fallback = std::string())");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!object.is_object())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        auto it = object.find(key);");
            sb.AppendLine("        if (it == object.end())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        return ReadString(*it, fallback);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    bool ReadBoolField(const json& object, const char* key, bool fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!object.is_object())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        auto it = object.find(key);");
            sb.AppendLine("        if (it == object.end())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        return ReadBool(*it, fallback);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    float ReadFloatField(const json& object, const char* key, float fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!object.is_object())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        auto it = object.find(key);");
            sb.AppendLine("        if (it == object.end())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        return ReadFloat(*it, fallback);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    glm::vec3 ReadVec3Field(const json& object, const char* key, glm::vec3 fallback)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!object.is_object())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        auto it = object.find(key);");
            sb.AppendLine("        if (it == object.end())");
            sb.AppendLine("            return fallback;");
            sb.AppendLine("        return ReadVec3(*it, fallback.x, fallback.y, fallback.z);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    std::string ReadTerrainBlendMapPath(const json& terrainJson)");
            sb.AppendLine("    {");
            sb.AppendLine("        std::string path = ReadStringField(terrainJson, \"splatmapTexture\");");
            sb.AppendLine("        if (!path.empty())");
            sb.AppendLine("            return path;");
            sb.AppendLine("        return ReadStringField(terrainJson, \"weightmapTexture\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    uint32_t LoadTextureCached(ResourceManager& rm, const std::string& exportRoot, const std::string& relativePath,");
            sb.AppendLine("                               std::unordered_map<std::string, uint32_t>& textureCache)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (relativePath.empty())");
            sb.AppendLine("            return ResourceManager::INVALID_ID;");
            sb.AppendLine("        auto it = textureCache.find(relativePath);");
            sb.AppendLine("        if (it != textureCache.end())");
            sb.AppendLine("            return it->second;");
            sb.AppendLine("        const uint32_t textureId = SceneExportRuntime::LoadTexture(rm, exportRoot, relativePath);");
            sb.AppendLine("        textureCache[relativePath] = textureId;");
            sb.AppendLine("        return textureId;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    uint32_t LoadMeshCached(ResourceManager& rm, const std::string& exportRoot, const std::string& relativePath,");
            sb.AppendLine("                            std::unordered_map<std::string, uint32_t>& meshCache)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (relativePath.empty())");
            sb.AppendLine("            return ResourceManager::INVALID_ID;");
            sb.AppendLine("        auto it = meshCache.find(relativePath);");
            sb.AppendLine("        if (it != meshCache.end())");
            sb.AppendLine("            return it->second;");
            sb.AppendLine("        const uint32_t meshId = SceneExportRuntime::LoadFirstMeshFromModel(rm, exportRoot, relativePath);");
            sb.AppendLine("        meshCache[relativePath] = meshId;");
            sb.AppendLine("        return meshId;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    uint32_t BuildMaterial(ResourceManager& rm, const std::string& exportRoot, const json& materialJson,");
            sb.AppendLine("                           std::unordered_map<std::string, uint32_t>& materialCache,");
            sb.AppendLine("                           std::unordered_map<std::string, uint32_t>& textureCache)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!materialJson.is_object())");
            sb.AppendLine("            return ResourceManager::INVALID_ID;");
            sb.AppendLine();
            sb.AppendLine("        std::string key = ReadStringField(materialJson, \"stableId\");");
            sb.AppendLine("        if (key.empty())");
            sb.AppendLine("            key = materialJson.dump();");
            sb.AppendLine();
            sb.AppendLine("        auto it = materialCache.find(key);");
            sb.AppendLine("        if (it != materialCache.end())");
            sb.AppendLine("            return it->second;");
            sb.AppendLine();
            sb.AppendLine("        Material material{};");
            sb.AppendLine("        material.baseColor = ReadVec4(materialJson.value(\"baseColor\", json::array()), 1.0f, 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("        material.emissiveColor = ReadVec4(materialJson.value(\"emissionColor\", json::array()), 0.0f, 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("        material.emissiveColor.w = ReadFloatField(materialJson, \"emissionIntensity\", 0.0f);");
            sb.AppendLine("        material.shininess = ReadFloatField(materialJson, \"shininess\", material.shininess);");
            sb.AppendLine("        material.reflectivity = ReadFloatField(materialJson, \"reflectivity\", material.reflectivity);");
            sb.AppendLine("        material.specularStrength = ReadFloatField(materialJson, \"specularStrength\", material.specularStrength);");
            sb.AppendLine("        material.SetTiling(glm::vec2(ReadArrayFloat(materialJson.value(\"uvScale\", json::array()), 0, 1.0f),");
            sb.AppendLine("                                     ReadArrayFloat(materialJson.value(\"uvScale\", json::array()), 1, 1.0f)));");
            sb.AppendLine();
            sb.AppendLine("        if (ReadBoolField(materialJson, \"receiveShadows\", false))");
            sb.AppendLine("            material.featureFlags |= MaterialFlags::ReceiveShadows;");
            sb.AppendLine("        material.SetDoubleSided(ReadBoolField(materialJson, \"doubleSided\", false));");
            sb.AppendLine("        material.SetTransparent(ReadBoolField(materialJson, \"transparent\", false));");
            sb.AppendLine("        const float alphaCutoff = ReadFloatField(materialJson, \"alphaCutoff\", -1.0f);");
            sb.AppendLine("        material.SetAlphaCutout(alphaCutoff >= 0.0f ? alphaCutoff : 0.0f, alphaCutoff >= 0.0f);");
            sb.AppendLine();
            sb.AppendLine("        const std::string diffusePath = ReadStringField(materialJson, \"diffuseTexture\");");
            sb.AppendLine("        const std::string normalPath = ReadStringField(materialJson, \"normalTexture\");");
            sb.AppendLine("        const std::string specularPath = ReadStringField(materialJson, \"specularTexture\");");
            sb.AppendLine("        const std::string emissivePath = ReadStringField(materialJson, \"emissiveTexture\");");
            sb.AppendLine();
            sb.AppendLine("        material.SetDiffuseTexture(LoadTextureCached(rm, exportRoot, diffusePath, textureCache));");
            sb.AppendLine("        material.SetNormalTexture(LoadTextureCached(rm, exportRoot, normalPath, textureCache));");
            sb.AppendLine("        material.SetSpecularTexture(LoadTextureCached(rm, exportRoot, specularPath, textureCache));");
            sb.AppendLine("        material.SetEmissiveTexture(LoadTextureCached(rm, exportRoot, emissivePath, textureCache));");
            sb.AppendLine();
            sb.AppendLine("        const uint32_t materialId = rm.RegisterMaterial(std::move(material));");
            sb.AppendLine("        materialCache[key] = materialId;");
            sb.AppendLine("        return materialId;");
            sb.AppendLine("    }");
            sb.AppendLine("} // namespace");
            sb.AppendLine();
            sb.AppendLine("bool JsonSceneLoader::LoadScene(entt::registry& registry, const std::string& exportRoot,");
            sb.AppendLine("                                const std::string& sceneJsonRelativePath,");
            sb.AppendLine("                                std::vector<std::string>* outWarnings)");
            sb.AppendLine("{");
            sb.AppendLine("    registry.clear();");
            sb.AppendLine();
            sb.AppendLine("    if (sceneJsonRelativePath.empty())");
            sb.AppendLine("        return false;");
            sb.AppendLine();
            sb.AppendLine("    const std::string scenePath = SceneExportRuntime::ResolveAssetPath(exportRoot, sceneJsonRelativePath);");
            sb.AppendLine("    std::ifstream file(scenePath);");
            sb.AppendLine("    if (!file.is_open())");
            sb.AppendLine("    {");
            sb.AppendLine("        if (outWarnings)");
            sb.AppendLine("            outWarnings->push_back(\"Scene JSON not found: \" + scenePath);");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    json root;");
            sb.AppendLine("    try");
            sb.AppendLine("    {");
            sb.AppendLine("        file >> root;");
            sb.AppendLine("    }");
            sb.AppendLine("    catch (const std::exception& ex)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (outWarnings)");
            sb.AppendLine("            outWarnings->push_back(std::string(\"Invalid scene JSON: \") + ex.what());");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (!root.is_object() || !root.contains(\"entities\") || !root[\"entities\"].is_array())");
            sb.AppendLine("        return false;");
            sb.AppendLine();
            sb.AppendLine("    auto& rm = ResourceManager::GetInstance();");
            sb.AppendLine("    std::unordered_map<std::string, entt::entity> entityMap;");
            sb.AppendLine("    std::unordered_map<std::string, uint32_t> textureCache;");
            sb.AppendLine("    std::unordered_map<std::string, uint32_t> meshCache;");
            sb.AppendLine("    std::unordered_map<std::string, uint32_t> materialCache;");
            sb.AppendLine("    std::vector<std::pair<std::string, std::string>> parentLinks;");
            sb.AppendLine("    parentLinks.reserve(root[\"entities\"].size());");
            sb.AppendLine();
            sb.AppendLine("    bool hasCustomComponents = false;");
            sb.AppendLine();
            sb.AppendLine("    if (root.contains(\"renderSettings\") && root[\"renderSettings\"].is_object() && outWarnings)");
            sb.AppendLine("    {");
            sb.AppendLine("        const auto& rs = root[\"renderSettings\"];");
            sb.AppendLine("        if (ReadBoolField(rs, \"fogEnabled\", false))");
            sb.AppendLine("            outWarnings->push_back(\"RenderSettings fog is exported but not mapped to CyberEngine runtime yet.\");");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (root.contains(\"skybox\") && root[\"skybox\"].is_object())");
            sb.AppendLine("    {");
            sb.AppendLine("        const auto& skyboxJson = root[\"skybox\"];");
            sb.AppendLine("        if (ReadBoolField(skyboxJson, \"enabled\", true))");
            sb.AppendLine("        {");
            sb.AppendLine("            bool skyboxApplied = false;");
            sb.AppendLine("            if (skyboxJson.contains(\"cubemapFacePaths\") && skyboxJson[\"cubemapFacePaths\"].is_array() &&");
            sb.AppendLine("                skyboxJson[\"cubemapFacePaths\"].size() >= 6)");
            sb.AppendLine("            {");
            sb.AppendLine("                std::array<std::string, 6> faces{};");
            sb.AppendLine("                for (size_t i = 0; i < faces.size(); ++i)");
            sb.AppendLine("                    faces[i] = ReadString(skyboxJson[\"cubemapFacePaths\"][i], std::string());");
            sb.AppendLine();
            sb.AppendLine("                const bool validFaces = std::all_of(faces.begin(), faces.end(), [](const std::string& path)");
            sb.AppendLine("                {");
            sb.AppendLine("                    return !path.empty();");
            sb.AppendLine("                });");
            sb.AppendLine();
            sb.AppendLine("                if (validFaces)");
            sb.AppendLine("                {");
            sb.AppendLine("                    const uint32_t cubemapId = SceneExportRuntime::LoadCubemap(rm, exportRoot, faces);");
            sb.AppendLine("                    if (cubemapId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        const entt::entity skyboxEntity = registry.create();");
            sb.AppendLine("                        registry.emplace<NameComponent>(skyboxEntity, NameComponent{\"Skybox\"});");
            sb.AppendLine("                        SkyboxComponent skybox{};");
            sb.AppendLine("                        skybox.cubemapTextureId = cubemapId;");
            sb.AppendLine("                        skybox.enabled = true;");
            sb.AppendLine("                        registry.emplace<SkyboxComponent>(skyboxEntity, skybox);");
            sb.AppendLine("                        skyboxApplied = true;");
            sb.AppendLine("                    }");
            sb.AppendLine("                    else if (outWarnings)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        outWarnings->push_back(\"Skybox cubemap faces exported but failed to load at runtime.\");");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            if (!skyboxApplied && outWarnings)");
            sb.AppendLine("            {");
            sb.AppendLine("                const std::string sourceType = ReadStringField(skyboxJson, \"sourceType\");");
            sb.AppendLine("                if (sourceType == \"panoramic\")");
            sb.AppendLine("                    outWarnings->push_back(\"Skybox panoramic export detected but runtime conversion to cubemap is not implemented.\");");
            sb.AppendLine("                else");
            sb.AppendLine("                    outWarnings->push_back(\"Skybox export detected but no runtime-compatible cubemap faces were found.\");");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    for (const auto& entityJson : root[\"entities\"])");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!entityJson.is_object())");
            sb.AppendLine("            continue;");
            sb.AppendLine();
            sb.AppendLine("        const std::string stableId = ReadStringField(entityJson, \"stableId\");");
            sb.AppendLine("        const std::string parentStableId = ReadStringField(entityJson, \"parentStableId\");");
            sb.AppendLine("        const std::string name = ReadStringField(entityJson, \"name\");");
            sb.AppendLine("        const std::string tag = ReadStringField(entityJson, \"tag\");");
            sb.AppendLine("        const bool isStatic = ReadBoolField(entityJson, \"isStatic\", false);");
            sb.AppendLine("        const bool isActive = ReadBoolField(entityJson, \"isActive\", true);");
            sb.AppendLine();
            sb.AppendLine("        const entt::entity entity = registry.create();");
            sb.AppendLine("        if (!stableId.empty())");
            sb.AppendLine("            entityMap[stableId] = entity;");
            sb.AppendLine("        parentLinks.emplace_back(stableId, parentStableId);");
            sb.AppendLine();
            sb.AppendLine("        registry.emplace<NameComponent>(entity, NameComponent{name});");
            sb.AppendLine("        if (!tag.empty() && tag != \"Untagged\")");
            sb.AppendLine("            registry.emplace<TagComponent>(entity, TagComponent{tag});");
            sb.AppendLine();
            sb.AppendLine("        TransformComponent transform{};");
            sb.AppendLine("        transform.position = ReadVec3(entityJson.value(\"localPosition\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("        transform.rotation = ReadQuaternion(entityJson.value(\"localRotation\", json::array()));");
            sb.AppendLine("        transform.scale = ReadVec3(entityJson.value(\"localScale\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("        registry.emplace<TransformComponent>(entity, transform);");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"model\") && entityJson[\"model\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& modelJson = entityJson[\"model\"];");
            sb.AppendLine("            ModelComponent model{};");
            sb.AppendLine("            model.meshId = LoadMeshCached(rm, exportRoot, ReadStringField(modelJson, \"meshAssetRelativePath\"), meshCache);");
            sb.AppendLine("            if (modelJson.contains(\"material\") && modelJson[\"material\"].is_object())");
            sb.AppendLine("                model.materialId = BuildMaterial(rm, exportRoot, modelJson[\"material\"], materialCache, textureCache);");
            sb.AppendLine("            else");
            sb.AppendLine("                model.materialId = ResourceManager::INVALID_ID;");
            sb.AppendLine("            model.visible = ReadBoolField(modelJson, \"enabled\", true) && isActive;");
            sb.AppendLine("            model.castShadows = ReadBoolField(modelJson, \"castShadows\", true);");
            sb.AppendLine("            model.receiveShadows = ReadBoolField(modelJson, \"receiveShadows\", true);");
            sb.AppendLine("            model.isStatic = ReadBoolField(modelJson, \"isStatic\", isStatic);");
            sb.AppendLine("            registry.emplace<ModelComponent>(entity, model);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"directionalLight\") && entityJson[\"directionalLight\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& lightJson = entityJson[\"directionalLight\"];");
            sb.AppendLine("            DirectionalLightComponent light{};");
            sb.AppendLine("            light.color = ReadVec3(lightJson.value(\"color\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("            light.intensity = ReadFloatField(lightJson, \"intensity\", 1.0f);");
            sb.AppendLine("            light.castShadows = ReadBoolField(lightJson, \"castShadows\", true);");
            sb.AppendLine("            light.enabled = ReadBoolField(lightJson, \"enabled\", true);");
            sb.AppendLine("            registry.emplace<DirectionalLightComponent>(entity, light);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"pointLight\") && entityJson[\"pointLight\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& lightJson = entityJson[\"pointLight\"];");
            sb.AppendLine("            PointLightComponent light{};");
            sb.AppendLine("            light.color = ReadVec3(lightJson.value(\"color\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("            light.intensity = ReadFloatField(lightJson, \"intensity\", 1.0f);");
            sb.AppendLine("            light.radius = ReadFloatField(lightJson, \"radius\", 10.0f);");
            sb.AppendLine("            light.castShadows = ReadBoolField(lightJson, \"castShadows\", false);");
            sb.AppendLine("            light.enabled = ReadBoolField(lightJson, \"enabled\", true);");
            sb.AppendLine("            registry.emplace<PointLightComponent>(entity, light);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"spotLight\") && entityJson[\"spotLight\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& lightJson = entityJson[\"spotLight\"];");
            sb.AppendLine("            SpotLightComponent light{};");
            sb.AppendLine("            light.color = ReadVec3(lightJson.value(\"color\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("            light.intensity = ReadFloatField(lightJson, \"intensity\", 1.0f);");
            sb.AppendLine("            light.range = ReadFloatField(lightJson, \"range\", 10.0f);");
            sb.AppendLine("            light.innerConeAngle = glm::radians(ReadFloatField(lightJson, \"innerConeAngleDegrees\", 15.0f));");
            sb.AppendLine("            light.outerConeAngle = glm::radians(ReadFloatField(lightJson, \"outerConeAngleDegrees\", 30.0f));");
            sb.AppendLine("            light.castShadows = ReadBoolField(lightJson, \"castShadows\", false);");
            sb.AppendLine("            light.enabled = ReadBoolField(lightJson, \"enabled\", true);");
            sb.AppendLine("            registry.emplace<SpotLightComponent>(entity, light);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"reflectionProbe\") && entityJson[\"reflectionProbe\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& probeJson = entityJson[\"reflectionProbe\"];");
            sb.AppendLine("            ReflectionProbeComponent probe{};");
            sb.AppendLine("            probe.cubemapTextureId = LoadTextureCached(rm, exportRoot, ReadStringField(probeJson, \"cubemapPath\"), textureCache);");
            sb.AppendLine("            registry.emplace<ReflectionProbeComponent>(entity, probe);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"camera\") && entityJson[\"camera\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& cameraJson = entityJson[\"camera\"];");
            sb.AppendLine("            CameraComponent camera{};");
            sb.AppendLine("            camera.fov = ReadFloatField(cameraJson, \"fov\", camera.fov);");
            sb.AppendLine("            camera.nearPlane = ReadFloatField(cameraJson, \"nearPlane\", camera.nearPlane);");
            sb.AppendLine("            camera.farPlane = ReadFloatField(cameraJson, \"farPlane\", camera.farPlane);");
            sb.AppendLine("            camera.aspectRatio = ReadFloatField(cameraJson, \"aspect\", camera.aspectRatio);");
            sb.AppendLine("            camera.isActive = ReadBoolField(cameraJson, \"isActive\", true);");
            sb.AppendLine("            registry.emplace<CameraComponent>(entity, camera);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"rigidbody\") && entityJson[\"rigidbody\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& rbJson = entityJson[\"rigidbody\"];");
            sb.AppendLine("            RigidbodyComponent rb{};");
            sb.AppendLine("            rb.isKinematic = ReadBoolField(rbJson, \"isKinematic\", rb.isKinematic);");
            sb.AppendLine("            rb.useGravity = ReadBoolField(rbJson, \"useGravity\", rb.useGravity);");
            sb.AppendLine("            rb.maxLinearVelocity = ReadFloatField(rbJson, \"maxLinearVelocity\", rb.maxLinearVelocity);");
            sb.AppendLine("            rb.maxAngularVelocity = ReadFloatField(rbJson, \"maxAngularVelocity\", rb.maxAngularVelocity);");
            sb.AppendLine("            rb.centerOfMass = ReadVec3(rbJson.value(\"centerOfMass\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            rb.linearVelocity = ReadVec3(rbJson.value(\"linearVelocity\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            rb.angularVelocity = ReadVec3(rbJson.value(\"angularVelocity\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            registry.emplace<RigidbodyComponent>(entity, rb);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"boxCollider\") && entityJson[\"boxCollider\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& cJson = entityJson[\"boxCollider\"];");
            sb.AppendLine("            BoxColliderComponent c{};");
            sb.AppendLine("            c.size = ReadVec3(cJson.value(\"size\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("            c.offset = ReadVec3(cJson.value(\"offset\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            c.isTrigger = ReadBoolField(cJson, \"isTrigger\", c.isTrigger);");
            sb.AppendLine("            registry.emplace<BoxColliderComponent>(entity, c);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"sphereCollider\") && entityJson[\"sphereCollider\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& cJson = entityJson[\"sphereCollider\"];");
            sb.AppendLine("            SphereColliderComponent c{};");
            sb.AppendLine("            c.radius = ReadFloatField(cJson, \"radius\", c.radius);");
            sb.AppendLine("            c.offset = ReadVec3(cJson.value(\"offset\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            c.isTrigger = ReadBoolField(cJson, \"isTrigger\", c.isTrigger);");
            sb.AppendLine("            registry.emplace<SphereColliderComponent>(entity, c);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"capsuleCollider\") && entityJson[\"capsuleCollider\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& cJson = entityJson[\"capsuleCollider\"];");
            sb.AppendLine("            CapsuleColliderComponent c{};");
            sb.AppendLine("            c.height = ReadFloatField(cJson, \"height\", c.height);");
            sb.AppendLine("            c.radius = ReadFloatField(cJson, \"radius\", c.radius);");
            sb.AppendLine("            c.offset = ReadVec3(cJson.value(\"offset\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            c.isTrigger = ReadBoolField(cJson, \"isTrigger\", c.isTrigger);");
            sb.AppendLine("            registry.emplace<CapsuleColliderComponent>(entity, c);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"meshCollider\") && entityJson[\"meshCollider\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& cJson = entityJson[\"meshCollider\"];");
            sb.AppendLine("            MeshColliderComponent c{};");
            sb.AppendLine("            c.meshId = LoadMeshCached(rm, exportRoot, ReadStringField(cJson, \"meshAssetRelativePath\"), meshCache);");
            sb.AppendLine("            c.scale = ReadVec3(cJson.value(\"scale\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("            c.offset = ReadVec3(cJson.value(\"offset\", json::array()), 0.0f, 0.0f, 0.0f);");
            sb.AppendLine("            c.isTrigger = ReadBoolField(cJson, \"isTrigger\", c.isTrigger);");
            sb.AppendLine("            registry.emplace<MeshColliderComponent>(entity, c);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"terrain\") && entityJson[\"terrain\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& terrainJson = entityJson[\"terrain\"];");
            sb.AppendLine("            std::vector<float> heights;");
            sb.AppendLine("            int hmWidth = 0;");
            sb.AppendLine("            int hmHeight = 0;");
            sb.AppendLine("            const std::string heightmapPath = ReadStringField(terrainJson, \"heightmapTexture\");");
            sb.AppendLine("            if (SceneExportRuntime::TryBuildHeightData(exportRoot, heightmapPath, heights, hmWidth, hmHeight))");
            sb.AppendLine("            {");
            sb.AppendLine("                Mesh terrainMesh{};");
            sb.AppendLine("                const glm::vec3 terrainSize = ReadVec3(terrainJson.value(\"size\", json::array()), 1.0f, 1.0f, 1.0f);");
            sb.AppendLine("                MeshFactory::CreateTerrain(terrainMesh, heights, hmWidth, hmHeight, terrainSize);");
            sb.AppendLine();
            sb.AppendLine("                TerrainMaterial terrainMaterial{};");
            sb.AppendLine("                terrainMaterial.featureFlags = TerrainMaterialFlags::ReceiveShadows;");
            sb.AppendLine();
            sb.AppendLine("                if (terrainJson.contains(\"layers\") && terrainJson[\"layers\"].is_array())");
            sb.AppendLine("                {");
            sb.AppendLine("                    const auto& layers = terrainJson[\"layers\"];");
            sb.AppendLine("                    const size_t usedLayerCount = std::min<size_t>(layers.size(), 4);");
            sb.AppendLine();
            sb.AppendLine("                    if (usedLayerCount > 0 && layers[0].is_object())");
            sb.AppendLine("                    {");
            sb.AppendLine("                        const auto& layer0 = layers[0];");
            sb.AppendLine("                        const glm::vec3 specColor = ReadVec3(layer0.value(\"specularColor\", json::array()), 0.5f, 0.5f, 0.5f);");
            sb.AppendLine("                        const float specular = (specColor.x + specColor.y + specColor.z) / 3.0f;");
            sb.AppendLine("                        const float smoothness = std::max(0.0f, ReadFloatField(layer0, \"smoothness\", 0.0f));");
            sb.AppendLine("                        const float metallic = std::max(0.0f, ReadFloatField(layer0, \"metallic\", 0.0f));");
            sb.AppendLine("                        terrainMaterial.specularStrength = specular;");
            sb.AppendLine("                        terrainMaterial.shininess = 4.0f + (smoothness * 124.0f);");
            sb.AppendLine("                        terrainMaterial.reflectivity = metallic * 0.25f;");
            sb.AppendLine();
            sb.AppendLine("                        const glm::vec2 tileSize = ReadVec2(layer0.value(\"tileSize\", json::array()), 1.0f, 1.0f);");
            sb.AppendLine("                        terrainMaterial.uvTilingX = tileSize.x;");
            sb.AppendLine("                        terrainMaterial.uvTilingY = tileSize.y;");
            sb.AppendLine("                    }");
            sb.AppendLine();
            sb.AppendLine("                    if (usedLayerCount > 1)");
            sb.AppendLine("                        terrainMaterial.layer1Start = 1.0f / static_cast<float>(usedLayerCount);");
            sb.AppendLine("                    if (usedLayerCount > 2)");
            sb.AppendLine("                        terrainMaterial.layer2Start = 2.0f / static_cast<float>(usedLayerCount);");
            sb.AppendLine("                    if (usedLayerCount > 3)");
            sb.AppendLine("                        terrainMaterial.layer3Start = 3.0f / static_cast<float>(usedLayerCount);");
            sb.AppendLine();
            sb.AppendLine("                    for (size_t layerIndex = 0; layerIndex < usedLayerCount; ++layerIndex)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        const auto& layerJson = layers[layerIndex];");
            sb.AppendLine("                        if (!layerJson.is_object())");
            sb.AppendLine("                            continue;");
            sb.AppendLine();
            sb.AppendLine("                        const uint32_t albedoId = LoadTextureCached(rm, exportRoot, ReadStringField(layerJson, \"albedoTexture\"), textureCache);");
            sb.AppendLine("                        const uint32_t normalId = LoadTextureCached(rm, exportRoot, ReadStringField(layerJson, \"normalTexture\"), textureCache);");
            sb.AppendLine();
            sb.AppendLine("                        if (layerIndex == 0)");
            sb.AppendLine("                        {");
            sb.AppendLine("                            terrainMaterial.layer0DiffuseMap = albedoId;");
            sb.AppendLine("                            terrainMaterial.layer0NormalMap = normalId;");
            sb.AppendLine("                            if (albedoId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer0DiffuseMap;");
            sb.AppendLine("                            if (normalId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer0NormalMap;");
            sb.AppendLine("                        }");
            sb.AppendLine("                        else if (layerIndex == 1)");
            sb.AppendLine("                        {");
            sb.AppendLine("                            terrainMaterial.layer1DiffuseMap = albedoId;");
            sb.AppendLine("                            terrainMaterial.layer1NormalMap = normalId;");
            sb.AppendLine("                            if (albedoId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer1DiffuseMap;");
            sb.AppendLine("                            if (normalId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer1NormalMap;");
            sb.AppendLine("                        }");
            sb.AppendLine("                        else if (layerIndex == 2)");
            sb.AppendLine("                        {");
            sb.AppendLine("                            terrainMaterial.layer2DiffuseMap = albedoId;");
            sb.AppendLine("                            terrainMaterial.layer2NormalMap = normalId;");
            sb.AppendLine("                            if (albedoId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer2DiffuseMap;");
            sb.AppendLine("                            if (normalId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer2NormalMap;");
            sb.AppendLine("                        }");
            sb.AppendLine("                        else if (layerIndex == 3)");
            sb.AppendLine("                        {");
            sb.AppendLine("                            terrainMaterial.layer3DiffuseMap = albedoId;");
            sb.AppendLine("                            terrainMaterial.layer3NormalMap = normalId;");
            sb.AppendLine("                            if (albedoId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer3DiffuseMap;");
            sb.AppendLine("                            if (normalId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer3NormalMap;");
            sb.AppendLine("                        }");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("                const uint32_t weightMapId = LoadTextureCached(rm, exportRoot, ReadTerrainBlendMapPath(terrainJson), textureCache);");
            sb.AppendLine("                if (weightMapId != ResourceManager::INVALID_ID)");
            sb.AppendLine("                {");
            sb.AppendLine("                    terrainMaterial.weightMap = weightMapId;");
            sb.AppendLine("                    terrainMaterial.featureFlags |= TerrainMaterialFlags::UseWeightMap;");
            sb.AppendLine("                }");
            sb.AppendLine();
            sb.AppendLine("                TerrainComponent terrain{};");
            sb.AppendLine("                terrain.meshId = rm.RegisterMesh(std::move(terrainMesh));");
            sb.AppendLine("                terrain.terrainMaterialId = rm.RegisterTerrainMaterial(std::move(terrainMaterial));");
            sb.AppendLine("                terrain.visible = ReadBoolField(terrainJson, \"enabled\", true);");
            sb.AppendLine("                terrain.castShadows = true;");
            sb.AppendLine("                terrain.receiveShadows = true;");
            sb.AppendLine("                terrain.isStatic = isStatic;");
            sb.AppendLine("                registry.emplace<TerrainComponent>(entity, terrain);");
            sb.AppendLine("            }");
            sb.AppendLine("            else if (outWarnings)");
            sb.AppendLine("            {");
            sb.AppendLine("                outWarnings->push_back(\"Failed to load terrain heightmap for entity: \" + name);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"audioSource\") && entityJson[\"audioSource\"].is_object())");
            sb.AppendLine("        {");
            sb.AppendLine("            const auto& audioJson = entityJson[\"audioSource\"];");
            sb.AppendLine("            AudioSourceComponent audio{};");
            sb.AppendLine("            audio.audioId = ResourceManager::INVALID_ID;");
            sb.AppendLine("            audio.volume = ReadFloatField(audioJson, \"volume\", audio.volume);");
            sb.AppendLine("            audio.pitch = ReadFloatField(audioJson, \"pitch\", audio.pitch);");
            sb.AppendLine("            audio.loop = ReadBoolField(audioJson, \"loop\", audio.loop);");
            sb.AppendLine("            registry.emplace<AudioSourceComponent>(entity, audio);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        if (entityJson.contains(\"customComponents\") && entityJson[\"customComponents\"].is_array() && !entityJson[\"customComponents\"].empty())");
            sb.AppendLine("            hasCustomComponents = true;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    for (const auto& link : parentLinks)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (link.first.empty() || link.second.empty())");
            sb.AppendLine("            continue;");
            sb.AppendLine();
            sb.AppendLine("        auto childIt = entityMap.find(link.first);");
            sb.AppendLine("        auto parentIt = entityMap.find(link.second);");
            sb.AppendLine("        if (childIt == entityMap.end() || parentIt == entityMap.end())");
            sb.AppendLine("            continue;");
            sb.AppendLine();
            sb.AppendLine("        Hierarchy::AttachChild(registry, parentIt->second, childIt->second);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (hasCustomComponents && outWarnings)");
            sb.AppendLine("        outWarnings->push_back(\"Custom components are present but are not instantiated by the JSON runtime loader.\");");
            sb.AppendLine();
            sb.AppendLine("    return true;");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static bool TryCreateDirectorySymlink(string targetPath, string linkPath, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    error = "CyberEngine root path is empty.";
                    return false;
                }

                var absoluteTarget = Path.GetFullPath(targetPath);
                if (!Directory.Exists(absoluteTarget))
                {
                    error = "CyberEngine root path does not exist: " + absoluteTarget;
                    return false;
                }

                var parent = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                TryDeletePath(linkPath);

#if UNITY_EDITOR_WIN
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /D \"" + linkPath + "\" \"" + absoluteTarget + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
#else
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/ln",
                    Arguments = "-sfn \"" + absoluteTarget + "\" \"" + linkPath + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
#endif

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        error = "Failed to start symlink process.";
                        return false;
                    }

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        var stdOut = process.StandardOutput.ReadToEnd();
                        var stdErr = process.StandardError.ReadToEnd();
                        error = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                        if (string.IsNullOrWhiteSpace(error))
                            error = "Symlink command exited with code " + process.ExitCode;
                        return false;
                    }
                }

                return Directory.Exists(linkPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TryDeletePath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (File.Exists(path))
                    File.Delete(path);

                if (Directory.Exists(path))
                {
                    var attr = File.GetAttributes(path);
                    var isSymlink = (attr & FileAttributes.ReparsePoint) != 0;
                    if (isSymlink)
                        Directory.Delete(path);
                    else
                        Directory.Delete(path, true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
#endif
