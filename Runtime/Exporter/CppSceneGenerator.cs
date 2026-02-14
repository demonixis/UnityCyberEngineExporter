#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Demonixis.UnityJSONSceneExporter
{
    internal sealed class CppSceneGenerator
    {
        public SceneManifestEntry WriteSceneFiles(string bundleRoot, SceneExportData sceneData, ExportOptions options)
        {
            var className = BuildSceneClassName(sceneData.sceneName);
            var sceneDir = Path.Combine(bundleRoot, "game", "scenes");
            Directory.CreateDirectory(sceneDir);

            var headerFileName = className + ".hpp";
            var cppFileName = className + ".cpp";

            var headerPath = Path.Combine(sceneDir, headerFileName);
            var cppPath = Path.Combine(sceneDir, cppFileName);

            File.WriteAllText(headerPath, BuildHeader(className, options.baseSceneClass));
            File.WriteAllText(cppPath, BuildCpp(className, sceneData));

            return new SceneManifestEntry
            {
                sceneName = sceneData.sceneName,
                sceneAssetPath = sceneData.sceneAssetPath,
                sceneHeaderPath = ExportUtils.NormalizeRelativePath(Path.Combine("game", "scenes", headerFileName)),
                sceneCppPath = ExportUtils.NormalizeRelativePath(Path.Combine("game", "scenes", cppFileName)),
                entityCount = sceneData.entities.Count,
                customComponentCount = sceneData.entities.Sum(e => e.customComponents != null ? e.customComponents.Count : 0),
                warningCount = sceneData.warnings.Count
            };
        }

        private static string BuildSceneClassName(string sceneName)
        {
            var clean = ExportUtils.SanitizeIdentifier(sceneName, "Exported");
            if (!clean.EndsWith("Scene", StringComparison.Ordinal))
                clean += "Scene";
            return clean;
        }

        private static string BuildHeader(string className, string baseSceneClass)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <scene/scene.hpp>");
            sb.AppendLine("#include <string>");
            sb.AppendLine();
            sb.AppendLine("class " + className + " : public " + baseSceneClass);
            sb.AppendLine("{");
            sb.AppendLine("  public:");
            sb.AppendLine("    explicit " + className + "(std::string exportRoot = \".\");");
            sb.AppendLine("    void Initialize() override;");
            sb.AppendLine("    void Unload() override;");
            sb.AppendLine("    void Update(const GameTime& gameTime) override;");
            sb.AppendLine("    void OnEvent(const GameTime& gameTime, const SDL_Event& event) override;");
            sb.AppendLine("    void OnGUI(const GameTime& gameTime) override;");
            sb.AppendLine();
            sb.AppendLine("  private:");
            sb.AppendLine("    std::string m_exportRoot;");
            sb.AppendLine("    entt::entity m_runtimeCameraEntity = entt::null;");
            sb.AppendLine("    float m_freeCameraYaw = 0.0f;");
            sb.AppendLine("    float m_freeCameraPitch = 0.0f;");
            sb.AppendLine("    bool m_freeCameraMouseLook = false;");
            sb.AppendLine("    bool m_showLightingInspector = true;");
            sb.AppendLine("};");
            return sb.ToString();
        }

        private static string BuildCpp(string className, SceneExportData sceneData)
        {
            var materialMap = BuildMaterialMap(sceneData);
            var textureVars = BuildTextureVarMap(sceneData);
            var meshVars = BuildMeshVarMap(sceneData);
            var hasCustomComponents = sceneData.entities.Any(e => e.customComponents != null && e.customComponents.Count > 0);

            var sb = new StringBuilder();
            sb.AppendLine("#include \"" + className + ".hpp\"");
            sb.AppendLine("#include \"scene_export_runtime_helper.hpp\"");
            sb.AppendLine("#include <assets/mesh_factory.hpp>");
            sb.AppendLine("#include <assets/resource_manager.hpp>");
            sb.AppendLine("#include <SDL3/SDL.h>");
            sb.AppendLine("#include <graphics/material.hpp>");
            sb.AppendLine("#include <graphics/terrain_material.hpp>");
            sb.AppendLine("#include <imgui.h>");
            sb.AppendLine("#include <scene/components/audio_components.hpp>");
            sb.AppendLine("#include <scene/components/components.hpp>");
            sb.AppendLine("#include <scene/components/lighting_components.hpp>");
            sb.AppendLine("#include <scene/components/mesh_components.hpp>");
            sb.AppendLine("#include <scene/components/physics_components.hpp>");
            sb.AppendLine("#include <scene/hierarchy.hpp>");
            if (hasCustomComponents)
                sb.AppendLine("#include \"../components/generated_components.hpp\"");
            sb.AppendLine("#include <array>");
            sb.AppendLine("#include <cmath>");
            sb.AppendLine("#include <unordered_map>");
            sb.AppendLine("#include <utility>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();

            sb.AppendLine(className + "::" + className + "(std::string exportRoot) : m_exportRoot(std::move(exportRoot)) {}");
            sb.AppendLine();

            WriteInitialize(sb, className, sceneData, materialMap, textureVars, meshVars);
            WriteUnload(sb, className);
            WriteUpdate(sb, className);
            WriteOnEvent(sb, className);
            WriteOnGUI(sb, className);

            return sb.ToString();
        }

        private static Dictionary<string, string> BuildMaterialMap(SceneExportData sceneData)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var uniqueMaterials = sceneData.entities
                .Where(e => e.model != null && e.model.material != null)
                .Select(e => e.model.material)
                .GroupBy(m => string.IsNullOrWhiteSpace(m.stableId) ? (m.name ?? "default") : m.stableId)
                .Select(g => g.First())
                .OrderBy(m => m.stableId, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < uniqueMaterials.Count; i++)
            {
                var material = uniqueMaterials[i];
                var key = string.IsNullOrWhiteSpace(material.stableId) ? (material.name ?? "default") : material.stableId;
                map[key] = "materialId_" + i.ToString(CultureInfo.InvariantCulture);
            }

            return map;
        }

        private static Dictionary<string, string> BuildTextureVarMap(SceneExportData sceneData)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entity in sceneData.entities)
            {
                if (entity.model != null && entity.model.material != null)
                {
                    AddIfNotEmpty(paths, entity.model.material.diffuseTexture);
                    AddIfNotEmpty(paths, entity.model.material.normalTexture);
                    AddIfNotEmpty(paths, entity.model.material.specularTexture);
                    AddIfNotEmpty(paths, entity.model.material.emissiveTexture);
                }

                if (entity.terrain != null)
                {
                    AddIfNotEmpty(paths, GetTerrainBlendMapPath(entity.terrain));
                    if (entity.terrain.layers != null)
                    {
                        foreach (var layer in entity.terrain.layers)
                        {
                            AddIfNotEmpty(paths, layer.albedoTexture);
                            AddIfNotEmpty(paths, layer.normalTexture);
                        }
                    }
                }

                if (entity.reflectionProbe != null)
                    AddIfNotEmpty(paths, entity.reflectionProbe.cubemapPath);
            }

            return paths.OrderBy(p => p, StringComparer.Ordinal)
                .Select((p, idx) => new KeyValuePair<string, string>(p, "tex_" + idx.ToString(CultureInfo.InvariantCulture)))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        private static Dictionary<string, string> BuildMeshVarMap(SceneExportData sceneData)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in sceneData.entities)
            {
                if (entity.model != null)
                    AddIfNotEmpty(paths, entity.model.meshAssetRelativePath);

                if (entity.meshCollider != null)
                    AddIfNotEmpty(paths, entity.meshCollider.meshAssetRelativePath);
            }

            return paths.OrderBy(p => p, StringComparer.Ordinal)
                .Select((p, idx) => new KeyValuePair<string, string>(p, "mesh_" + idx.ToString(CultureInfo.InvariantCulture)))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        private static void AddIfNotEmpty(HashSet<string> set, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                set.Add(value);
        }

        private static void WriteInitialize(StringBuilder sb, string className, SceneExportData sceneData,
            Dictionary<string, string> materialVars,
            Dictionary<string, string> textureVars,
            Dictionary<string, string> meshVars)
        {
            sb.AppendLine("void " + className + "::Initialize()");
            sb.AppendLine("{");
            sb.AppendLine("    auto& rm = ResourceManager::GetInstance();");
            sb.AppendLine("    std::unordered_map<std::string, entt::entity> entityMap;");
            sb.AppendLine("    entityMap.reserve(" + sceneData.entities.Count.ToString(CultureInfo.InvariantCulture) + ");");
            sb.AppendLine();

            foreach (var tex in textureVars.OrderBy(kv => kv.Value, StringComparer.Ordinal))
            {
                sb.AppendLine("    const uint32_t " + tex.Value + " = SceneExportRuntime::LoadTexture(rm, m_exportRoot, \"" + ExportUtils.EscapeCppString(tex.Key) + "\");");
            }

            if (textureVars.Count > 0)
                sb.AppendLine();

            foreach (var mesh in meshVars.OrderBy(kv => kv.Value, StringComparer.Ordinal))
            {
                sb.AppendLine("    const uint32_t " + mesh.Value + " = SceneExportRuntime::LoadFirstMeshFromModel(rm, m_exportRoot, \"" + ExportUtils.EscapeCppString(mesh.Key) + "\");");
            }

            if (meshVars.Count > 0)
                sb.AppendLine();

            var uniqueMaterials = sceneData.entities
                .Where(e => e.model != null && e.model.material != null)
                .Select(e => e.model.material)
                .GroupBy(m => string.IsNullOrWhiteSpace(m.stableId) ? (m.name ?? "default") : m.stableId)
                .Select(g => g.First())
                .OrderBy(m => m.stableId, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < uniqueMaterials.Count; i++)
            {
                var mat = uniqueMaterials[i];
                var matVar = "material_" + i.ToString(CultureInfo.InvariantCulture);
                var idVar = "materialId_" + i.ToString(CultureInfo.InvariantCulture);

                sb.AppendLine("    Material " + matVar + "{};");
                sb.AppendLine("    " + matVar + ".baseColor = " + ToVec4Literal(mat.baseColor, 1.0f, 1.0f, 1.0f, 1.0f) + ";");
                sb.AppendLine("    " + matVar + ".emissiveColor = " + ToVec4Literal(mat.emissionColor, 0.0f, 0.0f, 0.0f, 0.0f) + ";");
                sb.AppendLine("    " + matVar + ".emissiveColor.w = " + ExportUtils.ToFloatLiteral(mat.emissionIntensity) + ";");
                sb.AppendLine("    " + matVar + ".shininess = " + ExportUtils.ToFloatLiteral(mat.shininess) + ";");
                sb.AppendLine("    " + matVar + ".reflectivity = " + ExportUtils.ToFloatLiteral(mat.reflectivity) + ";");
                sb.AppendLine("    " + matVar + ".specularStrength = " + ExportUtils.ToFloatLiteral(mat.specularStrength) + ";");
                sb.AppendLine("    " + matVar + ".SetTiling(glm::vec2(" + ExportUtils.ToFloatLiteral(GetFloat(mat.uvScale, 0, 1.0f)) + ", " +
                              ExportUtils.ToFloatLiteral(GetFloat(mat.uvScale, 1, 1.0f)) + "));");
                sb.AppendLine("    uint32_t " + matVar + "Flags = 0;");

                if (mat.receiveShadows)
                    sb.AppendLine("    " + matVar + "Flags |= MaterialFlags::ReceiveShadows;");
                sb.AppendLine("    " + matVar + ".SetDoubleSided(" + BoolLiteral(mat.doubleSided) + ");");
                sb.AppendLine("    " + matVar + ".SetTransparent(" + BoolLiteral(mat.transparent) + ");");
                if (mat.alphaCutoff >= 0.0f)
                    sb.AppendLine("    " + matVar + ".SetAlphaCutout(" + ExportUtils.ToFloatLiteral(mat.alphaCutoff) + ", true);");
                else
                    sb.AppendLine("    " + matVar + ".SetAlphaCutout(0.0f, false);");

                AppendTextureAssignment(sb, matVar, "SetDiffuseTexture", GetTextureVar(textureVars, mat.diffuseTexture));
                AppendTextureAssignment(sb, matVar, "SetNormalTexture", GetTextureVar(textureVars, mat.normalTexture));
                AppendTextureAssignment(sb, matVar, "SetSpecularTexture", GetTextureVar(textureVars, mat.specularTexture));
                AppendTextureAssignment(sb, matVar, "SetEmissiveTexture", GetTextureVar(textureVars, mat.emissiveTexture));

                sb.AppendLine("    " + matVar + ".featureFlags |= " + matVar + "Flags;");
                sb.AppendLine("    const uint32_t " + idVar + " = rm.RegisterMaterial(std::move(" + matVar + "));");
                sb.AppendLine();
            }

            if (sceneData.renderSettings != null)
            {
                sb.AppendLine("    // RenderSettings export (runtime mapping partial in CyberEngine V1)");
                sb.AppendLine("    // AmbientLight: " + ToVec3Literal(sceneData.renderSettings.ambientLight, 0.0f, 0.0f, 0.0f));
                sb.AppendLine("    // AmbientIntensity: " + ExportUtils.ToFloatLiteral(sceneData.renderSettings.ambientIntensity));
                sb.AppendLine("    // FogEnabled: " + BoolLiteral(sceneData.renderSettings.fogEnabled));
                sb.AppendLine("    // FogColor: " + ToVec3Literal(sceneData.renderSettings.fogColor, 0.0f, 0.0f, 0.0f));
                sb.AppendLine("    // FogDensity: " + ExportUtils.ToFloatLiteral(sceneData.renderSettings.fogDensity));
                sb.AppendLine();
            }

            if (HasCubemapSkybox(sceneData))
            {
                sb.AppendLine("    const std::array<std::string, 6> skyboxFaces = {");
                for (var i = 0; i < 6; i++)
                {
                    var suffix = i < 5 ? "," : string.Empty;
                    sb.AppendLine("        \"" + ExportUtils.EscapeCppString(GetSkyboxFace(sceneData, i)) + "\"" + suffix);
                }
                sb.AppendLine("    };");
                sb.AppendLine("    const uint32_t skyboxCubemapId = SceneExportRuntime::LoadCubemap(rm, m_exportRoot, skyboxFaces);");
                sb.AppendLine("    if (skyboxCubemapId != ResourceManager::INVALID_ID)");
                sb.AppendLine("    {");
                sb.AppendLine("        const entt::entity skyboxEntity = m_registry.create();");
                sb.AppendLine("        m_registry.emplace<NameComponent>(skyboxEntity, NameComponent{\"Skybox\"});");
                sb.AppendLine("        SkyboxComponent skybox{};");
                sb.AppendLine("        skybox.cubemapTextureId = skyboxCubemapId;");
                sb.AppendLine("        skybox.enabled = true;");
                sb.AppendLine("        m_registry.emplace<SkyboxComponent>(skyboxEntity, skybox);");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            var entities = sceneData.entities.OrderBy(e => e.stableId, StringComparer.Ordinal).ToList();
            for (var i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                var varName = "entity_" + i.ToString(CultureInfo.InvariantCulture);
                var entityName = ExportUtils.EscapeCppString(entity.name ?? string.Empty);
                var entityStableId = ExportUtils.EscapeCppString(entity.stableId ?? string.Empty);

                sb.AppendLine("    // ----- BEGIN ENTITY: " + entityStableId + " | " + entityName + " -----");
                sb.AppendLine("    const entt::entity " + varName + " = m_registry.create();");
                sb.AppendLine("    entityMap[\"" + entityStableId + "\"] = " + varName + ";");
                sb.AppendLine("    m_registry.emplace<NameComponent>(" + varName + ", NameComponent{\"" + entityName + "\"});");
                if (!string.IsNullOrWhiteSpace(entity.tag) && entity.tag != "Untagged")
                    sb.AppendLine("    m_registry.emplace<TagComponent>(" + varName + ", TagComponent{\"" + ExportUtils.EscapeCppString(entity.tag) + "\"});");

                sb.AppendLine("    TransformComponent transform{};");
                sb.AppendLine("    transform.position = " + ToVec3Literal(entity.localPosition, 0.0f, 0.0f, 0.0f) + ";");
                sb.AppendLine("    transform.rotation = " + ToQuatLiteral(entity.localRotation) + ";");
                sb.AppendLine("    transform.scale = " + ToVec3Literal(entity.localScale, 1.0f, 1.0f, 1.0f) + ";");
                sb.AppendLine("    m_registry.emplace<TransformComponent>(" + varName + ", transform);");

                if (entity.model != null)
                {
                    sb.AppendLine("    ModelComponent model{};");
                    sb.AppendLine("    model.meshId = " + GetMeshVar(meshVars, entity.model.meshAssetRelativePath) + ";");
                    var matKey = entity.model.material != null
                        ? (string.IsNullOrWhiteSpace(entity.model.material.stableId)
                            ? (entity.model.material.name ?? "default")
                            : entity.model.material.stableId)
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(matKey) && materialVars.TryGetValue(matKey, out var matVar))
                        sb.AppendLine("    model.materialId = " + matVar + ";");
                    else
                        sb.AppendLine("    model.materialId = ResourceManager::INVALID_ID;");
                    sb.AppendLine("    model.visible = " + BoolLiteral(entity.model.enabled && entity.isActive) + ";");
                    sb.AppendLine("    model.castShadows = " + BoolLiteral(entity.model.castShadows) + ";");
                    sb.AppendLine("    model.receiveShadows = " + BoolLiteral(entity.model.receiveShadows) + ";");
                    sb.AppendLine("    model.isStatic = " + BoolLiteral(entity.model.isStatic) + ";");
                    sb.AppendLine("    m_registry.emplace<ModelComponent>(" + varName + ", model);");
                }

                if (entity.directionalLight != null)
                {
                    sb.AppendLine("    DirectionalLightComponent light{};");
                    sb.AppendLine("    light.color = " + ToVec3Literal(entity.directionalLight.color, 1.0f, 1.0f, 1.0f) + ";");
                    sb.AppendLine("    light.intensity = " + ExportUtils.ToFloatLiteral(entity.directionalLight.intensity) + ";");
                    sb.AppendLine("    light.castShadows = " + BoolLiteral(entity.directionalLight.castShadows) + ";");
                    sb.AppendLine("    light.enabled = " + BoolLiteral(entity.directionalLight.enabled) + ";");
                    sb.AppendLine("    m_registry.emplace<DirectionalLightComponent>(" + varName + ", light);");
                }

                if (entity.pointLight != null)
                {
                    sb.AppendLine("    PointLightComponent light{};");
                    sb.AppendLine("    light.color = " + ToVec3Literal(entity.pointLight.color, 1.0f, 1.0f, 1.0f) + ";");
                    sb.AppendLine("    light.intensity = " + ExportUtils.ToFloatLiteral(entity.pointLight.intensity) + ";");
                    sb.AppendLine("    light.radius = " + ExportUtils.ToFloatLiteral(entity.pointLight.radius) + ";");
                    sb.AppendLine("    light.castShadows = " + BoolLiteral(entity.pointLight.castShadows) + ";");
                    sb.AppendLine("    light.enabled = " + BoolLiteral(entity.pointLight.enabled) + ";");
                    sb.AppendLine("    m_registry.emplace<PointLightComponent>(" + varName + ", light);");
                }

                if (entity.spotLight != null)
                {
                    sb.AppendLine("    SpotLightComponent light{};");
                    sb.AppendLine("    light.color = " + ToVec3Literal(entity.spotLight.color, 1.0f, 1.0f, 1.0f) + ";");
                    sb.AppendLine("    light.intensity = " + ExportUtils.ToFloatLiteral(entity.spotLight.intensity) + ";");
                    sb.AppendLine("    light.range = " + ExportUtils.ToFloatLiteral(entity.spotLight.range) + ";");
                    sb.AppendLine("    light.innerConeAngle = glm::radians(" + ExportUtils.ToFloatLiteral(entity.spotLight.innerConeAngleDegrees) + ");");
                    sb.AppendLine("    light.outerConeAngle = glm::radians(" + ExportUtils.ToFloatLiteral(entity.spotLight.outerConeAngleDegrees) + ");");
                    sb.AppendLine("    light.castShadows = " + BoolLiteral(entity.spotLight.castShadows) + ";");
                    sb.AppendLine("    light.enabled = " + BoolLiteral(entity.spotLight.enabled) + ";");
                    sb.AppendLine("    m_registry.emplace<SpotLightComponent>(" + varName + ", light);");
                }

                if (entity.reflectionProbe != null)
                {
                    sb.AppendLine("    ReflectionProbeComponent probe{};");
                    sb.AppendLine("    probe.cubemapTextureId = " + GetTextureVar(textureVars, entity.reflectionProbe.cubemapPath) + ";");
                    sb.AppendLine("    m_registry.emplace<ReflectionProbeComponent>(" + varName + ", probe);");
                }

                if (entity.camera != null)
                {
                    sb.AppendLine("    CameraComponent camera{};");
                    sb.AppendLine("    camera.fov = " + ExportUtils.ToFloatLiteral(entity.camera.fov) + ";");
                    sb.AppendLine("    camera.nearPlane = " + ExportUtils.ToFloatLiteral(entity.camera.nearPlane) + ";");
                    sb.AppendLine("    camera.farPlane = " + ExportUtils.ToFloatLiteral(entity.camera.farPlane) + ";");
                    sb.AppendLine("    camera.aspectRatio = " + ExportUtils.ToFloatLiteral(entity.camera.aspect) + ";");
                    sb.AppendLine("    camera.isActive = " + BoolLiteral(entity.camera.isActive) + ";");
                    sb.AppendLine("    m_registry.emplace<CameraComponent>(" + varName + ", camera);");
                }

                if (entity.rigidbody != null)
                {
                    sb.AppendLine("    RigidbodyComponent rigidbody{};");
                    sb.AppendLine("    rigidbody.isKinematic = " + BoolLiteral(entity.rigidbody.isKinematic) + ";");
                    sb.AppendLine("    rigidbody.useGravity = " + BoolLiteral(entity.rigidbody.useGravity) + ";");
                    sb.AppendLine("    rigidbody.maxLinearVelocity = " + ExportUtils.ToFloatLiteral(entity.rigidbody.maxLinearVelocity) + ";");
                    sb.AppendLine("    rigidbody.maxAngularVelocity = " + ExportUtils.ToFloatLiteral(entity.rigidbody.maxAngularVelocity) + ";");
                    sb.AppendLine("    rigidbody.centerOfMass = " + ToVec3Literal(entity.rigidbody.centerOfMass, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    rigidbody.linearVelocity = " + ToVec3Literal(entity.rigidbody.linearVelocity, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    rigidbody.angularVelocity = " + ToVec3Literal(entity.rigidbody.angularVelocity, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    m_registry.emplace<RigidbodyComponent>(" + varName + ", rigidbody);");
                }

                if (entity.boxCollider != null)
                {
                    sb.AppendLine("    BoxColliderComponent collider{};");
                    sb.AppendLine("    collider.size = " + ToVec3Literal(entity.boxCollider.size, 1.0f, 1.0f, 1.0f) + ";");
                    sb.AppendLine("    collider.offset = " + ToVec3Literal(entity.boxCollider.offset, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    collider.isTrigger = " + BoolLiteral(entity.boxCollider.isTrigger) + ";");
                    sb.AppendLine("    m_registry.emplace<BoxColliderComponent>(" + varName + ", collider);");
                }

                if (entity.sphereCollider != null)
                {
                    sb.AppendLine("    SphereColliderComponent collider{};");
                    sb.AppendLine("    collider.radius = " + ExportUtils.ToFloatLiteral(entity.sphereCollider.radius) + ";");
                    sb.AppendLine("    collider.offset = " + ToVec3Literal(entity.sphereCollider.offset, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    collider.isTrigger = " + BoolLiteral(entity.sphereCollider.isTrigger) + ";");
                    sb.AppendLine("    m_registry.emplace<SphereColliderComponent>(" + varName + ", collider);");
                }

                if (entity.capsuleCollider != null)
                {
                    sb.AppendLine("    CapsuleColliderComponent collider{};");
                    sb.AppendLine("    collider.radius = " + ExportUtils.ToFloatLiteral(entity.capsuleCollider.radius) + ";");
                    sb.AppendLine("    collider.height = " + ExportUtils.ToFloatLiteral(entity.capsuleCollider.height) + ";");
                    sb.AppendLine("    collider.offset = " + ToVec3Literal(entity.capsuleCollider.offset, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    collider.isTrigger = " + BoolLiteral(entity.capsuleCollider.isTrigger) + ";");
                    sb.AppendLine("    m_registry.emplace<CapsuleColliderComponent>(" + varName + ", collider);");
                }

                if (entity.meshCollider != null)
                {
                    sb.AppendLine("    MeshColliderComponent collider{};");
                    sb.AppendLine("    collider.meshId = " + GetMeshVar(meshVars, entity.meshCollider.meshAssetRelativePath) + ";");
                    sb.AppendLine("    collider.scale = " + ToVec3Literal(entity.meshCollider.scale, 1.0f, 1.0f, 1.0f) + ";");
                    sb.AppendLine("    collider.offset = " + ToVec3Literal(entity.meshCollider.offset, 0.0f, 0.0f, 0.0f) + ";");
                    sb.AppendLine("    collider.isTrigger = " + BoolLiteral(entity.meshCollider.isTrigger) + ";");
                    sb.AppendLine("    m_registry.emplace<MeshColliderComponent>(" + varName + ", collider);");
                }

                if (entity.terrain != null)
                {
                    sb.AppendLine("    {");
                    sb.AppendLine("        std::vector<float> heights;");
                    sb.AppendLine("        int hmWidth = 0;");
                    sb.AppendLine("        int hmHeight = 0;");
                    sb.AppendLine("        if (SceneExportRuntime::TryBuildHeightData(m_exportRoot, \"" + ExportUtils.EscapeCppString(entity.terrain.heightmapTexture ?? "") + "\", heights, hmWidth, hmHeight))");
                    sb.AppendLine("        {");
                    sb.AppendLine("            Mesh terrainMesh{};");
                    sb.AppendLine("            MeshFactory::CreateTerrain(terrainMesh, heights, hmWidth, hmHeight, " + ToVec3Literal(entity.terrain.size, 1.0f, 1.0f, 1.0f) + ");");
                    sb.AppendLine("            TerrainMaterial terrainMaterial{};");
                    sb.AppendLine("            terrainMaterial.featureFlags = TerrainMaterialFlags::ReceiveShadows;");

                    var layers = entity.terrain.layers ?? new List<TerrainLayerExportData>();
                    var usedLayerCount = Math.Min(layers.Count, 4);
                    if (usedLayerCount > 0)
                    {
                        var firstLayer = layers[0];
                        var specular = ((GetFloat(firstLayer.specularColor, 0, 0.5f)
                                        + GetFloat(firstLayer.specularColor, 1, 0.5f)
                                        + GetFloat(firstLayer.specularColor, 2, 0.5f)) / 3.0f);
                        var shininess = 4.0f + (Math.Max(0.0f, firstLayer.smoothness) * 124.0f);
                        var reflectivity = Math.Max(0.0f, firstLayer.metallic) * 0.25f;

                        sb.AppendLine("            terrainMaterial.specularStrength = " + ExportUtils.ToFloatLiteral(specular) + ";");
                        sb.AppendLine("            terrainMaterial.shininess = " + ExportUtils.ToFloatLiteral(shininess) + ";");
                        sb.AppendLine("            terrainMaterial.reflectivity = " + ExportUtils.ToFloatLiteral(reflectivity) + ";");
                    }
                    if (usedLayerCount > 1)
                        sb.AppendLine("            terrainMaterial.layer1Start = " + ExportUtils.ToFloatLiteral(1.0f / usedLayerCount) + ";");
                    if (usedLayerCount > 2)
                        sb.AppendLine("            terrainMaterial.layer2Start = " + ExportUtils.ToFloatLiteral(2.0f / usedLayerCount) + ";");
                    if (usedLayerCount > 3)
                        sb.AppendLine("            terrainMaterial.layer3Start = " + ExportUtils.ToFloatLiteral(3.0f / usedLayerCount) + ";");

                    for (var l = 0; l < layers.Count && l < 4; l++)
                    {
                        var layer = layers[l];
                        var albedoVar = GetTextureVar(textureVars, layer.albedoTexture);
                        var normalVar = GetTextureVar(textureVars, layer.normalTexture);

                        sb.AppendLine("            if (" + albedoVar + " != ResourceManager::INVALID_ID)");
                        sb.AppendLine("            {");
                        sb.AppendLine("                terrainMaterial.layer" + l + "DiffuseMap = " + albedoVar + ";");
                        sb.AppendLine("                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer" + l + "DiffuseMap;");
                        sb.AppendLine("            }");
                        sb.AppendLine("            if (" + normalVar + " != ResourceManager::INVALID_ID)");
                        sb.AppendLine("            {");
                        sb.AppendLine("                terrainMaterial.layer" + l + "NormalMap = " + normalVar + ";");
                        sb.AppendLine("                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseLayer" + l + "NormalMap;");
                        sb.AppendLine("            }");

                        if (l == 0)
                        {
                            sb.AppendLine("            terrainMaterial.uvTilingX = " + ExportUtils.ToFloatLiteral(GetFloat(layer.tileSize, 0, 1.0f)) + ";");
                            sb.AppendLine("            terrainMaterial.uvTilingY = " + ExportUtils.ToFloatLiteral(GetFloat(layer.tileSize, 1, 1.0f)) + ";");
                        }
                    }

                    var weightVar = GetTextureVar(textureVars, GetTerrainBlendMapPath(entity.terrain));

                    sb.AppendLine("            if (" + weightVar + " != ResourceManager::INVALID_ID)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                terrainMaterial.weightMap = " + weightVar + ";");
                    sb.AppendLine("                terrainMaterial.featureFlags |= TerrainMaterialFlags::UseWeightMap;");
                    sb.AppendLine("            }");

                    sb.AppendLine("            TerrainComponent terrainComp{};");
                    sb.AppendLine("            terrainComp.meshId = rm.RegisterMesh(std::move(terrainMesh));");
                    sb.AppendLine("            terrainComp.terrainMaterialId = rm.RegisterTerrainMaterial(std::move(terrainMaterial));");
                    sb.AppendLine("            terrainComp.visible = " + BoolLiteral(entity.terrain.enabled) + ";");
                    sb.AppendLine("            terrainComp.castShadows = true;");
                    sb.AppendLine("            terrainComp.receiveShadows = true;");
                    sb.AppendLine("            terrainComp.isStatic = " + BoolLiteral(entity.isStatic) + ";");
                    sb.AppendLine("            m_registry.emplace<TerrainComponent>(" + varName + ", terrainComp);");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                }

                if (entity.audioSource != null)
                {
                    sb.AppendLine("    AudioSourceComponent audio{};");
                    sb.AppendLine("    audio.audioId = ResourceManager::INVALID_ID;");
                    sb.AppendLine("    audio.volume = " + ExportUtils.ToFloatLiteral(entity.audioSource.volume) + ";");
                    sb.AppendLine("    audio.pitch = " + ExportUtils.ToFloatLiteral(entity.audioSource.pitch) + ";");
                    sb.AppendLine("    audio.loop = " + BoolLiteral(entity.audioSource.loop) + ";");
                    sb.AppendLine("    m_registry.emplace<AudioSourceComponent>(" + varName + ", audio);");
                }

                if (entity.customComponents != null)
                {
                    foreach (var custom in entity.customComponents)
                    {
                        if (string.IsNullOrWhiteSpace(custom.generatedType))
                            continue;

                        var customVar = "custom_" + ExportUtils.ToSnakeCase(custom.generatedType) + "_" + i.ToString(CultureInfo.InvariantCulture);
                        sb.AppendLine("    " + custom.generatedType + " " + customVar + "{};");
                        if (custom.fields != null)
                        {
                            foreach (var field in custom.fields)
                            {
                                var fieldName = ExportUtils.SanitizeIdentifier(field.name, "field");
                                sb.AppendLine("    " + customVar + "." + fieldName + " = " + field.valueCpp + ";");
                            }
                        }
                        sb.AppendLine("    m_registry.emplace<" + custom.generatedType + ">(" + varName + ", " + customVar + ");");
                    }
                }

                sb.AppendLine("    // ----- END ENTITY: " + entityStableId + " -----");
                sb.AppendLine();
            }

            foreach (var entity in entities)
            {
                if (string.IsNullOrWhiteSpace(entity.parentStableId))
                    continue;

                sb.AppendLine("    {");
                sb.AppendLine("        auto childIt = entityMap.find(\"" + ExportUtils.EscapeCppString(entity.stableId) + "\");");
                sb.AppendLine("        auto parentIt = entityMap.find(\"" + ExportUtils.EscapeCppString(entity.parentStableId) + "\");");
                sb.AppendLine("        if (childIt != entityMap.end() && parentIt != entityMap.end())");
                sb.AppendLine("            Hierarchy::AttachChild(m_registry, parentIt->second, childIt->second);");
                sb.AppendLine("    }");
            }

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
        }

        private static void WriteUnload(StringBuilder sb, string className)
        {
            sb.AppendLine("void " + className + "::Unload()");
            sb.AppendLine("{");
            sb.AppendLine("    m_registry.clear();");
            sb.AppendLine("    m_runtimeCameraEntity = entt::null;");
            sb.AppendLine("    m_freeCameraYaw = 0.0f;");
            sb.AppendLine("    m_freeCameraPitch = 0.0f;");
            sb.AppendLine("    m_freeCameraMouseLook = false;");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        private static void WriteUpdate(StringBuilder sb, string className)
        {
            sb.AppendLine("void " + className + "::Update(const GameTime& gameTime)");
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
        }

        private static void WriteOnEvent(StringBuilder sb, string className)
        {
            sb.AppendLine("void " + className + "::OnEvent(const GameTime& gameTime, const SDL_Event& event)");
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
        }

        private static void WriteOnGUI(StringBuilder sb, string className)
        {
            sb.AppendLine("void " + className + "::OnGUI(const GameTime& gameTime)");
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
            sb.AppendLine();
        }

        private static void AppendTextureAssignment(StringBuilder sb, string matVar, string setter, string textureVar)
        {
            sb.AppendLine("    " + matVar + "." + setter + "(" + textureVar + ");");
        }

        private static string GetTextureVar(Dictionary<string, string> textureVars, string texturePath)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return "ResourceManager::INVALID_ID";
            return textureVars.TryGetValue(texturePath, out var value) ? value : "ResourceManager::INVALID_ID";
        }

        private static string GetMeshVar(Dictionary<string, string> meshVars, string meshPath)
        {
            if (string.IsNullOrWhiteSpace(meshPath))
                return "ResourceManager::INVALID_ID";
            return meshVars.TryGetValue(meshPath, out var value) ? value : "ResourceManager::INVALID_ID";
        }

        private static string GetTerrainBlendMapPath(TerrainExportData terrain)
        {
            if (terrain == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(terrain.splatmapTexture))
                return terrain.splatmapTexture;

            return terrain.weightmapTexture ?? string.Empty;
        }

        private static string BoolLiteral(bool value)
        {
            return value ? "true" : "false";
        }

        private static float GetFloat(float[] data, int index, float fallback)
        {
            if (data == null || index < 0 || index >= data.Length)
                return fallback;
            return data[index];
        }

        private static string ToVec3Literal(float[] data, float fx, float fy, float fz)
        {
            var x = GetFloat(data, 0, fx);
            var y = GetFloat(data, 1, fy);
            var z = GetFloat(data, 2, fz);
            return "glm::vec3(" + ExportUtils.ToFloatLiteral(x) + ", " + ExportUtils.ToFloatLiteral(y) + ", " + ExportUtils.ToFloatLiteral(z) + ")";
        }

        private static string ToVec4Literal(float[] data, float fx, float fy, float fz, float fw)
        {
            var x = GetFloat(data, 0, fx);
            var y = GetFloat(data, 1, fy);
            var z = GetFloat(data, 2, fz);
            var w = GetFloat(data, 3, fw);
            return "glm::vec4(" + ExportUtils.ToFloatLiteral(x) + ", " + ExportUtils.ToFloatLiteral(y) + ", " + ExportUtils.ToFloatLiteral(z) + ", " + ExportUtils.ToFloatLiteral(w) + ")";
        }

        private static string ToQuatLiteral(float[] data)
        {
            var x = GetFloat(data, 0, 0.0f);
            var y = GetFloat(data, 1, 0.0f);
            var z = GetFloat(data, 2, 0.0f);
            var w = GetFloat(data, 3, 1.0f);
            return "glm::quat(" + ExportUtils.ToFloatLiteral(w) + ", " + ExportUtils.ToFloatLiteral(x) + ", " + ExportUtils.ToFloatLiteral(y) + ", " + ExportUtils.ToFloatLiteral(z) + ")";
        }

        private static bool HasCubemapSkybox(SceneExportData sceneData)
        {
            return sceneData != null &&
                   sceneData.skybox != null &&
                   sceneData.skybox.enabled &&
                   sceneData.skybox.cubemapFacePaths != null &&
                   sceneData.skybox.cubemapFacePaths.Count == 6 &&
                   sceneData.skybox.cubemapFacePaths.All(p => !string.IsNullOrWhiteSpace(p));
        }

        private static string GetSkyboxFace(SceneExportData sceneData, int index)
        {
            if (!HasCubemapSkybox(sceneData) || index < 0 || index >= sceneData.skybox.cubemapFacePaths.Count)
                return string.Empty;

            return sceneData.skybox.cubemapFacePaths[index];
        }
    }
}
#endif
