#if UNITY_EDITOR
using System.IO;
using System.Text;

namespace Demonixis.UnityJSONSceneExporter
{
    internal sealed class CppRuntimeHelperGenerator
    {
        public const string HeaderFileName = "scene_export_runtime_helper.hpp";
        public const string CppFileName = "scene_export_runtime_helper.cpp";

        public void WriteFiles(string bundleRoot)
        {
            var scenesDir = Path.Combine(bundleRoot, "game", "scenes");
            Directory.CreateDirectory(scenesDir);

            var headerPath = Path.Combine(scenesDir, HeaderFileName);
            var cppPath = Path.Combine(scenesDir, CppFileName);

            File.WriteAllText(headerPath, BuildHeader());
            File.WriteAllText(cppPath, BuildCpp());
        }

        private static string BuildHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <assets/resource_manager.hpp>");
            sb.AppendLine("#include <array>");
            sb.AppendLine("#include <cstdint>");
            sb.AppendLine("#include <string>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("namespace SceneExportRuntime");
            sb.AppendLine("{");
            sb.AppendLine("    std::string ResolveAssetPath(const std::string& exportRoot, const std::string& relativePath);");
            sb.AppendLine("    uint32_t LoadTexture(ResourceManager& resourceManager, const std::string& exportRoot,");
            sb.AppendLine("                         const std::string& relativePath);");
            sb.AppendLine("    uint32_t LoadFirstMeshFromModel(ResourceManager& resourceManager, const std::string& exportRoot,");
            sb.AppendLine("                                    const std::string& relativePath);");
            sb.AppendLine("    uint32_t LoadCubemap(ResourceManager& resourceManager, const std::string& exportRoot,");
            sb.AppendLine("                         const std::array<std::string, 6>& relativeFacePaths);");
            sb.AppendLine("    bool TryBuildHeightData(const std::string& exportRoot, const std::string& relativePath,");
            sb.AppendLine("                            std::vector<float>& outHeights, int& outWidth, int& outHeight);");
            sb.AppendLine("} // namespace SceneExportRuntime");
            return sb.ToString();
        }

        private static string BuildCpp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("#include \"scene_export_runtime_helper.hpp\"");
            sb.AppendLine("#include <assets/model_importer.hpp>");
            sb.AppendLine("#include <assets/texture_importer.hpp>");
            sb.AppendLine("#include <iostream>");
            sb.AppendLine("#include <filesystem>");
            sb.AppendLine("#include <utility>");
            sb.AppendLine();
            sb.AppendLine("namespace");
            sb.AppendLine("{");
            sb.AppendLine("    bool TryFindFirstMeshIdRecursive(const ModelNode& node, uint32_t& outMeshId)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!node.meshIds.empty())");
            sb.AppendLine("        {");
            sb.AppendLine("            outMeshId = node.meshIds[0];");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine("        for (const auto& child : node.children)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (TryFindFirstMeshIdRecursive(child, outMeshId))");
            sb.AppendLine("                return true;");
            sb.AppendLine("        }");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
            sb.AppendLine("} // namespace");
            sb.AppendLine();
            sb.AppendLine("std::string SceneExportRuntime::ResolveAssetPath(const std::string& exportRoot, const std::string& relativePath)");
            sb.AppendLine("{");
            sb.AppendLine("    if (relativePath.empty())");
            sb.AppendLine("        return std::string();");
            sb.AppendLine("    std::filesystem::path path(relativePath);");
            sb.AppendLine("    if (path.is_absolute())");
            sb.AppendLine("        return path.generic_string();");
            sb.AppendLine("    std::filesystem::path base(exportRoot.empty() ? std::string(\".\") : exportRoot);");
            sb.AppendLine("    return (base / path).generic_string();");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint32_t SceneExportRuntime::LoadTexture(ResourceManager& resourceManager, const std::string& exportRoot,");
            sb.AppendLine("                                      const std::string& relativePath)");
            sb.AppendLine("{");
            sb.AppendLine("    if (relativePath.empty())");
            sb.AppendLine("        return ResourceManager::INVALID_ID;");
            sb.AppendLine("    Texture texture(0, 0);");
            sb.AppendLine("    const std::string absolutePath = ResolveAssetPath(exportRoot, relativePath);");
            sb.AppendLine("    if (!TextureImporter::LoadTexture(absolutePath, texture))");
            sb.AppendLine("    {");
            sb.AppendLine("        std::cerr << \"[SceneExportRuntime] Failed to load texture: \" << absolutePath << std::endl;");
            sb.AppendLine("        return ResourceManager::INVALID_ID;");
            sb.AppendLine("    }");
            sb.AppendLine("    return resourceManager.RegisterTexture(std::move(texture));");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint32_t SceneExportRuntime::LoadFirstMeshFromModel(ResourceManager& resourceManager, const std::string& exportRoot,");
            sb.AppendLine("                                                 const std::string& relativePath)");
            sb.AppendLine("{");
            sb.AppendLine("    if (relativePath.empty())");
            sb.AppendLine("        return ResourceManager::INVALID_ID;");
            sb.AppendLine("    ModelImporter importer(resourceManager);");
            sb.AppendLine("    ModelNode node;");
            sb.AppendLine("    const std::string absolutePath = ResolveAssetPath(exportRoot, relativePath);");
            sb.AppendLine("    if (!importer.LoadModel(absolutePath, node))");
            sb.AppendLine("    {");
                sb.AppendLine("        std::cerr << \"[SceneExportRuntime] Failed to load model: \" << absolutePath << std::endl;");
                sb.AppendLine("        return ResourceManager::INVALID_ID;");
            sb.AppendLine("    }");
            sb.AppendLine("    uint32_t meshId = ResourceManager::INVALID_ID;");
            sb.AppendLine("    if (!TryFindFirstMeshIdRecursive(node, meshId))");
            sb.AppendLine("    {");
            sb.AppendLine("        std::cerr << \"[SceneExportRuntime] Model loaded but no mesh found: \" << absolutePath << std::endl;");
            sb.AppendLine("        return ResourceManager::INVALID_ID;");
            sb.AppendLine("    }");
            sb.AppendLine("    return meshId;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("uint32_t SceneExportRuntime::LoadCubemap(ResourceManager& resourceManager, const std::string& exportRoot,");
            sb.AppendLine("                                      const std::array<std::string, 6>& relativeFacePaths)");
            sb.AppendLine("{");
            sb.AppendLine("    std::array<std::string, 6> absolutePaths{};");
            sb.AppendLine("    for (size_t i = 0; i < relativeFacePaths.size(); ++i)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (relativeFacePaths[i].empty())");
            sb.AppendLine("            return ResourceManager::INVALID_ID;");
            sb.AppendLine("        absolutePaths[i] = ResolveAssetPath(exportRoot, relativeFacePaths[i]);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    Texture cubemap(0, 0);");
            sb.AppendLine("    if (!TextureImporter::LoadCubemap(absolutePaths, cubemap))");
            sb.AppendLine("        return ResourceManager::INVALID_ID;");
            sb.AppendLine();
            sb.AppendLine("    return resourceManager.RegisterTexture(std::move(cubemap));");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("bool SceneExportRuntime::TryBuildHeightData(const std::string& exportRoot, const std::string& relativePath,");
            sb.AppendLine("                                         std::vector<float>& outHeights, int& outWidth, int& outHeight)");
            sb.AppendLine("{");
            sb.AppendLine("    outHeights.clear();");
            sb.AppendLine("    std::vector<uint8_t> pixels;");
            sb.AppendLine("    if (!TextureImporter::LoadTexture(ResolveAssetPath(exportRoot, relativePath), outWidth, outHeight, pixels))");
            sb.AppendLine("        return false;");
            sb.AppendLine("    const size_t pixelCount = static_cast<size_t>(outWidth) * static_cast<size_t>(outHeight);");
            sb.AppendLine("    if (pixels.size() < pixelCount * 4)");
            sb.AppendLine("        return false;");
            sb.AppendLine("    outHeights.resize(pixelCount, 0.0f);");
            sb.AppendLine("    for (size_t i = 0; i < pixelCount; ++i)");
            sb.AppendLine("        outHeights[i] = static_cast<float>(pixels[i * 4]) / 255.0f;");
            sb.AppendLine("    return true;");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
#endif
