#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Demonixis.UnityJSONSceneExporter
{
    internal sealed class SceneCollector
    {
        private const string ClassificationNativeMapped = "native_mapped";
        private const string ClassificationCustomStub = "custom_stub";
        private const string ClassificationIgnoredAuthoring = "ignored_authoring";
        private const string ClassificationUnsupportedBuiltin = "unsupported_builtin";
        private const int MaxComponentExamples = 5;

        private readonly AssetExportDatabase m_assets;
        private readonly CustomComponentGenerator m_customComponents;
        private readonly ExportReport m_report;
        private readonly Dictionary<string, ComponentUsageAccumulator> m_componentUsage =
            new Dictionary<string, ComponentUsageAccumulator>(StringComparer.Ordinal);
        private readonly HashSet<string> m_unsupportedBuiltinWarnings =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_nonMonoCustomWarnings =
            new HashSet<string>(StringComparer.Ordinal);

        public SceneCollector(AssetExportDatabase assets, CustomComponentGenerator customComponents, ExportReport report)
        {
            m_assets = assets;
            m_customComponents = customComponents;
            m_report = report;
        }

        public SceneExportData CollectScene(string scenePath)
        {
            var opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var sceneData = new SceneExportData
            {
                sceneName = opened.name,
                sceneAssetPath = scenePath,
                generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                renderSettings = new RenderSettingsExportData(),
                skybox = null,
                entities = new List<EntityExportData>(),
                warnings = new List<string>()
            };

            CollectRenderSettings(sceneData);
            CollectSkybox(sceneData);

            var roots = opened.GetRootGameObjects();
            foreach (var root in roots)
                CollectTransform(root.transform, null, sceneData, opened.name);

            sceneData.entities = sceneData.entities.OrderBy(e => e.stableId, StringComparer.Ordinal).ToList();
            return sceneData;
        }

        public static HashSet<string> DiscoverAssets(ExportOptions options)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (options == null || options.scenePaths == null)
                return set;

            if (options.assetScope == AssetScope.AllAssets)
            {
                var guids = AssetDatabase.FindAssets(string.Empty, new[] { "Assets" });
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrWhiteSpace(path) && !IsBakedOrTransientAsset(path))
                        set.Add(ExportUtils.NormalizeRelativePath(path));
                }
                return set;
            }

            foreach (var scenePath in options.scenePaths)
            {
                var dependencies = AssetDatabase.GetDependencies(scenePath, true);
                foreach (var dep in dependencies)
                {
                    if (!string.IsNullOrWhiteSpace(dep) && !IsBakedOrTransientAsset(dep))
                        set.Add(ExportUtils.NormalizeRelativePath(dep));
                }
            }

            return set;
        }

        private void CollectTransform(Transform tr, string parentStableId, SceneExportData sceneData, string sceneName)
        {
            var go = tr.gameObject;
            var stableId = ExportUtils.BuildStableId(go);

            var entity = new EntityExportData
            {
                stableId = stableId,
                parentStableId = parentStableId,
                name = go.name,
                tag = go.tag,
                isStatic = go.isStatic,
                isActive = go.activeSelf,
                localPosition = ExportUtils.ToFloat3(tr.localPosition),
                localRotation = ExportUtils.ToFloat4(new Vector4(tr.localRotation.x, tr.localRotation.y, tr.localRotation.z, tr.localRotation.w)),
                localScale = ExportUtils.ToFloat3(tr.localScale),
                customComponents = new List<CustomComponentInstanceData>()
            };

            CollectModel(go, stableId, sceneData, entity);
            CollectLight(go, sceneData, entity);
            CollectReflectionProbe(go, sceneData, entity);
            CollectCamera(go, entity);
            CollectRigidbody(go, entity);
            CollectColliders(go, stableId, sceneData, entity);
            CollectTerrain(go, stableId, sceneData, entity);
            CollectAudio(go, stableId, sceneData, entity);
            CollectCustomComponents(go, sceneData, entity, sceneName);

            sceneData.entities.Add(entity);

            for (var i = 0; i < tr.childCount; i++)
            {
                CollectTransform(tr.GetChild(i), stableId, sceneData, sceneName);
            }
        }

        private void CollectModel(GameObject go, string stableId, SceneExportData sceneData, EntityExportData entity)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            var filter = go.GetComponent<MeshFilter>();
            if (renderer == null || filter == null || filter.sharedMesh == null)
                return;

            var mesh = filter.sharedMesh;
            var modelData = new ModelExportData
            {
                enabled = renderer.enabled,
                castShadows = renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off,
                receiveShadows = renderer.receiveShadows,
                isStatic = go.isStatic,
                meshSource = AssetDatabase.GetAssetPath(mesh)
            };

            var meshPath = ResolveMeshPath(mesh, stableId, go.name + "_mesh", out var wasBaked);
            modelData.meshAssetRelativePath = meshPath;
            modelData.meshWasBaked = wasBaked;

            var materials = renderer.sharedMaterials;
            if (materials != null && materials.Length > 0)
            {
                if (materials.Length > 1)
                {
                    var warning = "MeshRenderer with multiple materials detected on " + ExportUtils.GetTransformPath(go.transform) +
                                  ". Only first material is mapped to ModelComponent.";
                    sceneData.warnings.Add(warning);
                    m_report.warnings.Add(warning);
                }

                modelData.material = BuildMaterial(materials[0], go.name, sceneData);
            }
            else
            {
                modelData.material = BuildMaterial(null, go.name, sceneData);
            }

            entity.model = modelData;
        }

        private string ResolveMeshPath(Mesh mesh, string stableId, string fallbackName, out bool wasBaked)
        {
            wasBaked = false;
            if (mesh == null)
                return string.Empty;

            var assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var ext = Path.GetExtension(assetPath);
                if (AssetExportDatabase.ClassifyByPath(assetPath) == AssetKind.Model && AssetDatabase.IsMainAsset(mesh))
                {
                    return m_assets.ExportAssetPath(assetPath, AssetKind.Model);
                }

                var warning = "Mesh sub-asset fallback to baked OBJ for " + mesh.name + " from " + assetPath;
                if (!m_report.warnings.Contains(warning))
                    m_report.warnings.Add(warning);
            }

            wasBaked = true;
            return m_assets.ExportBakedMeshObj(mesh, stableId, fallbackName);
        }

        private MaterialExportData BuildMaterial(Material material, string ownerName, SceneExportData sceneData)
        {
            var data = new MaterialExportData
            {
                stableId = string.Empty,
                name = "Default",
                baseColor = new[] { 1.0f, 1.0f, 1.0f, 1.0f },
                emissionColor = new[] { 0.0f, 0.0f, 0.0f, 0.0f },
                emissionIntensity = 0.0f,
                shininess = 32.0f,
                reflectivity = 0.0f,
                specularStrength = 0.5f,
                alphaCutoff = -1.0f,
                receiveShadows = true,
                doubleSided = false,
                transparent = false,
                uvScale = new[] { 1.0f, 1.0f },
                uvOffset = new[] { 0.0f, 0.0f }
            };

            if (material == null)
            {
                data.stableId = "mat_default";
                return data;
            }

            data.name = material.name;
            var hasBaseColor = material.HasProperty("_BaseColor") || material.HasProperty("_Color");
            data.baseColor = ExportUtils.ToFloat4(hasBaseColor ? material.TryGetColor("_BaseColor", "_Color") : Color.white);

            var mainTexProperty = material.TryGetTexturePropertyName("_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap");
            if (!string.IsNullOrWhiteSpace(mainTexProperty))
            {
                data.uvScale = ExportUtils.ToFloat2(material.GetTextureScale(mainTexProperty));
                data.uvOffset = ExportUtils.ToFloat2(material.GetTextureOffset(mainTexProperty));
            }
            else
            {
                data.uvScale = ExportUtils.ToFloat2(material.mainTextureScale);
                data.uvOffset = ExportUtils.ToFloat2(material.mainTextureOffset);
            }

            var glossiness = material.HasProperty("_Glossiness")
                ? material.GetFloat("_Glossiness")
                : material.TryGetFloat("_Smoothness");
            if (!material.HasProperty("_Glossiness") && !material.HasProperty("_Smoothness"))
                glossiness = 0.5f;
            data.shininess = Mathf.Lerp(4.0f, 128.0f, Mathf.Clamp01(glossiness));
            data.alphaCutoff = material.HasProperty("_Cutoff")
                ? material.GetFloat("_Cutoff")
                : (material.HasProperty("_AlphaCutoff") ? material.GetFloat("_AlphaCutoff") : -1.0f);

            data.transparent = material.renderQueue >= 3000 ||
                               (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f) ||
                               (material.HasProperty("_Mode") && material.GetFloat("_Mode") >= 2.0f);
            data.doubleSided = material.HasProperty("_Cull") && Mathf.Approximately(material.GetFloat("_Cull"), 0.0f);

            var emissionColor = material.HasProperty("_EmissionColor")
                ? material.GetColor("_EmissionColor")
                : material.TryGetColor("_EmissiveColor");
            data.emissionColor = ExportUtils.ToFloat4(emissionColor);
            data.emissionIntensity = Mathf.Max(emissionColor.maxColorComponent, 0.0f);

            var specColor = material.HasProperty("_SpecColor")
                ? material.GetColor("_SpecColor")
                : material.TryGetColor("_SpecularColor");
            if (!material.HasProperty("_SpecColor") && !material.HasProperty("_SpecularColor"))
                specColor = new Color(0.5f, 0.5f, 0.5f, 1.0f);
            data.specularStrength = (specColor.r + specColor.g + specColor.b) / 3.0f;

            var textures = new List<KeyValuePair<string, Texture>>
            {
                new KeyValuePair<string, Texture>("diffuse", material.TryGetTexture("_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap")),
                new KeyValuePair<string, Texture>("normal", material.TryGetTexture("_BumpMap", "_NormalMap", "_NormalTex")),
                new KeyValuePair<string, Texture>("specular", material.TryGetTexture("_SpecGlossMap", "_SpecularMap", "_SpecMap")),
                new KeyValuePair<string, Texture>("emissive", material.TryGetTexture("_EmissionMap", "_EmissiveMap")),
                new KeyValuePair<string, Texture>("ao", material.TryGetTexture("_OcclusionMap", "_AOTex")),
                new KeyValuePair<string, Texture>("metallic", material.TryGetTexture("_MetallicGlossMap", "_MetallicMap", "_MaskMap"))
            };

            foreach (var kv in textures)
            {
                if (kv.Value == null)
                    continue;

                var rel = m_assets.ExportObject(kv.Value, AssetKind.Texture, ownerName + "_" + kv.Key);
                switch (kv.Key)
                {
                    case "diffuse":
                        data.diffuseTexture = rel;
                        break;
                    case "normal":
                        data.normalTexture = rel;
                        break;
                    case "specular":
                        data.specularTexture = rel;
                        break;
                    case "emissive":
                        data.emissiveTexture = rel;
                        break;
                    case "ao":
                        data.aoTexture = rel;
                        break;
                    case "metallic":
                        data.metallicTexture = rel;
                        break;
                }
            }

            var idSeed = data.name + "|" + data.diffuseTexture + "|" + data.normalTexture + "|" + data.specularTexture + "|" + data.emissiveTexture;
            data.stableId = "mat_" + ExportUtils.ComputeSha1(idSeed).Substring(0, 16);
            return data;
        }

        private void CollectLight(GameObject go, SceneExportData sceneData, EntityExportData entity)
        {
            var light = go.GetComponent<Light>();
            if (light == null)
                return;

            if (light.type == LightType.Directional)
            {
                entity.directionalLight = new DirectionalLightExportData
                {
                    enabled = light.enabled,
                    color = ExportUtils.ToFloat3(light.color),
                    intensity = light.intensity,
                    castShadows = light.shadows != LightShadows.None
                };
            }
            else if (light.type == LightType.Point)
            {
                entity.pointLight = new PointLightExportData
                {
                    enabled = light.enabled,
                    color = ExportUtils.ToFloat3(light.color),
                    intensity = light.intensity,
                    radius = light.range,
                    castShadows = light.shadows != LightShadows.None
                };
            }
            else if (light.type == LightType.Spot)
            {
                entity.spotLight = new SpotLightExportData
                {
                    enabled = light.enabled,
                    color = ExportUtils.ToFloat3(light.color),
                    intensity = light.intensity,
                    range = light.range,
                    innerConeAngleDegrees = light.innerSpotAngle,
                    outerConeAngleDegrees = light.spotAngle,
                    castShadows = light.shadows != LightShadows.None
                };
            }
            else
            {
                var warning = "Unsupported light type on " + ExportUtils.GetTransformPath(go.transform) + ": " + light.type;
                sceneData.warnings.Add(warning);
                m_report.warnings.Add(warning);
            }
        }

        private void CollectRenderSettings(SceneExportData sceneData)
        {
            if (sceneData == null)
                return;

            sceneData.renderSettings = new RenderSettingsExportData
            {
                ambientLight = ExportUtils.ToFloat3(RenderSettings.ambientLight),
                ambientIntensity = RenderSettings.ambientIntensity,
                fogEnabled = RenderSettings.fog,
                fogColor = ExportUtils.ToFloat3(RenderSettings.fogColor),
                fogDensity = RenderSettings.fogDensity,
                fogStartDistance = RenderSettings.fogStartDistance,
                fogEndDistance = RenderSettings.fogEndDistance,
                fogMode = RenderSettings.fogMode.ToString(),
                reflectionIntensity = RenderSettings.reflectionIntensity,
                reflectionBounces = RenderSettings.reflectionBounces,
                defaultReflectionMode = RenderSettings.defaultReflectionMode.ToString()
            };
        }

        private void CollectSkybox(SceneExportData sceneData)
        {
            if (sceneData == null)
                return;

            var material = RenderSettings.skybox;
            if (material == null)
                return;

            var skybox = new SkyboxExportData
            {
                enabled = true,
                sourceType = "unknown",
                materialName = material.name,
                shaderName = material.shader != null ? material.shader.name : string.Empty,
                cubemapFacePaths = new List<string>()
            };

            if (TryCollectSixSidedSkybox(material, sceneData, skybox) || TryCollectCubemapSkybox(material, sceneData, skybox))
            {
                sceneData.skybox = skybox;
                return;
            }

            var panoramic = material.TryGetTexture("_MainTex");
            if (panoramic != null)
            {
                skybox.sourceType = "panoramic";
                skybox.panoramicTexture = m_assets.ExportObject(panoramic, AssetKind.Texture, sceneData.sceneName + "_skybox_panoramic");
                sceneData.skybox = skybox;

                var warning = "Skybox panoramic texture exported as data only on scene " + sceneData.sceneName +
                              ". Runtime cubemap conversion is not implemented yet.";
                sceneData.warnings.Add(warning);
                m_report.warnings.Add(warning);
                return;
            }

            var unsupportedWarning = "Skybox material exported without runtime mapping on scene " + sceneData.sceneName +
                                     " (" + skybox.shaderName + ").";
            sceneData.warnings.Add(unsupportedWarning);
            m_report.warnings.Add(unsupportedWarning);
            sceneData.skybox = skybox;
        }

        private bool TryCollectSixSidedSkybox(Material material, SceneExportData sceneData, SkyboxExportData skybox)
        {
            var right = material.TryGetTexture("_RightTex");
            var left = material.TryGetTexture("_LeftTex");
            var up = material.TryGetTexture("_UpTex");
            var down = material.TryGetTexture("_DownTex");
            var front = material.TryGetTexture("_FrontTex");
            var back = material.TryGetTexture("_BackTex");

            if (right == null || left == null || up == null || down == null || front == null || back == null)
                return false;

            skybox.sourceType = "six_sided";
            skybox.cubemapFacePaths = new List<string>
            {
                m_assets.ExportObject(right, AssetKind.Texture, sceneData.sceneName + "_skybox_east"),
                m_assets.ExportObject(left, AssetKind.Texture, sceneData.sceneName + "_skybox_west"),
                m_assets.ExportObject(up, AssetKind.Texture, sceneData.sceneName + "_skybox_up"),
                m_assets.ExportObject(down, AssetKind.Texture, sceneData.sceneName + "_skybox_down"),
                m_assets.ExportObject(front, AssetKind.Texture, sceneData.sceneName + "_skybox_north"),
                m_assets.ExportObject(back, AssetKind.Texture, sceneData.sceneName + "_skybox_south")
            };
            return skybox.cubemapFacePaths.All(p => !string.IsNullOrWhiteSpace(p));
        }

        private bool TryCollectCubemapSkybox(Material material, SceneExportData sceneData, SkyboxExportData skybox)
        {
            var cubemapTexture = material.TryGetTexture("_Tex", "_MainTex", "_Cube");
            if (!(cubemapTexture is Cubemap cubemap))
                return false;

            skybox.sourceType = "cubemap";
            var sceneKey = ExportUtils.ToSnakeCase(sceneData.sceneName, "scene");
            var facePaths = new List<string>(6);

            var faceEntries = new[]
            {
                new { face = CubemapFace.PositiveX, name = "east" },
                new { face = CubemapFace.NegativeX, name = "west" },
                new { face = CubemapFace.PositiveY, name = "up" },
                new { face = CubemapFace.NegativeY, name = "down" },
                new { face = CubemapFace.PositiveZ, name = "north" },
                new { face = CubemapFace.NegativeZ, name = "south" }
            };

            try
            {
                var size = cubemap.width;
                if (size <= 0)
                    return false;

                for (var i = 0; i < faceEntries.Length; i++)
                {
                    var entry = faceEntries[i];
                    var colors = cubemap.GetPixels(entry.face);
                    if (colors == null || colors.Length == 0)
                        return false;

                    var faceTexture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                    faceTexture.SetPixels(colors);
                    faceTexture.Apply();

                    var key = sceneKey + "_skybox_" + entry.name;
                    var rel = m_assets.ExportGeneratedTexture(faceTexture, key, key, AssetKind.Texture);
                    UnityEngine.Object.DestroyImmediate(faceTexture);

                    facePaths.Add(rel);
                }
            }
            catch (Exception ex)
            {
                var warning = "Failed to export cubemap skybox faces on scene " + sceneData.sceneName + ": " + ex.Message;
                sceneData.warnings.Add(warning);
                m_report.warnings.Add(warning);
                return false;
            }

            if (facePaths.Any(string.IsNullOrWhiteSpace))
                return false;

            skybox.cubemapFacePaths = facePaths;
            return true;
        }

        private void CollectReflectionProbe(GameObject go, SceneExportData sceneData, EntityExportData entity)
        {
            var probe = go.GetComponent<ReflectionProbe>();
            if (probe == null)
                return;

            var cubemapTexture = probe.customBakedTexture;
            if (cubemapTexture == null)
                cubemapTexture = probe.texture;

            var cubemapPath = string.Empty;
            var cubemapAssetPath = AssetDatabase.GetAssetPath(cubemapTexture);
            if (cubemapTexture != null && AssetDatabase.Contains(cubemapTexture) && !IsBakedOrTransientAsset(cubemapAssetPath))
            {
                cubemapPath = m_assets.ExportObject(cubemapTexture, AssetKind.Texture, go.name + "_reflection_probe");
            }

            if (!string.IsNullOrWhiteSpace(cubemapPath))
            {
                var warning = "ReflectionProbe exported as texture path on " + ExportUtils.GetTransformPath(go.transform) +
                              ". CyberEngine reflection probe import fallback may require custom cubemap handling.";
                sceneData.warnings.Add(warning);
                m_report.warnings.Add(warning);
            }

            entity.reflectionProbe = new ReflectionProbeExportData
            {
                enabled = probe.enabled,
                isBaked = probe.refreshMode != UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame,
                intensity = probe.intensity,
                size = ExportUtils.ToFloat3(probe.size),
                center = ExportUtils.ToFloat3(probe.center),
                cubemapPath = cubemapPath
            };
        }

        private static bool IsBakedOrTransientAsset(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return false;

            var normalized = ExportUtils.NormalizeRelativePath(assetPath);
            var fileName = Path.GetFileName(normalized);

            if (fileName.StartsWith("Lightmap-", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.StartsWith("ReflectionProbe-", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.Equals("LightingData.asset", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.Equals("LightProbes.asset", StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.IndexOf("/ProBuilder Data/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private static void CollectCamera(GameObject go, EntityExportData entity)
        {
            var camera = go.GetComponent<Camera>();
            if (camera == null)
                return;

            entity.camera = new CameraExportData
            {
                enabled = camera.enabled,
                fov = camera.fieldOfView,
                nearPlane = camera.nearClipPlane,
                farPlane = camera.farClipPlane,
                aspect = camera.aspect > 0.0f ? camera.aspect : (16.0f / 9.0f),
                isActive = camera.enabled && go.activeInHierarchy
            };
        }

        private static void CollectRigidbody(GameObject go, EntityExportData entity)
        {
            var body = go.GetComponent<Rigidbody>();
            if (body == null)
                return;

            entity.rigidbody = new RigidbodyExportData
            {
                isKinematic = body.isKinematic,
                useGravity = body.useGravity,
                maxLinearVelocity = body.maxLinearVelocity,
                maxAngularVelocity = body.maxAngularVelocity,
                centerOfMass = ExportUtils.ToFloat3(body.centerOfMass),
                linearVelocity = ExportUtils.ToFloat3(body.linearVelocity),
                angularVelocity = ExportUtils.ToFloat3(body.angularVelocity)
            };
        }

        private void CollectColliders(GameObject go, string stableId, SceneExportData sceneData, EntityExportData entity)
        {
            var colliders = go.GetComponents<Collider>();
            if (colliders == null || colliders.Length == 0)
                return;

            foreach (var collider in colliders)
            {
                if (collider is BoxCollider box && entity.boxCollider == null)
                {
                    entity.boxCollider = new BoxColliderExportData
                    {
                        enabled = box.enabled,
                        isTrigger = box.isTrigger,
                        size = ExportUtils.ToFloat3(box.size),
                        offset = ExportUtils.ToFloat3(box.center)
                    };
                    continue;
                }

                if (collider is SphereCollider sphere && entity.sphereCollider == null)
                {
                    entity.sphereCollider = new SphereColliderExportData
                    {
                        enabled = sphere.enabled,
                        isTrigger = sphere.isTrigger,
                        radius = sphere.radius,
                        offset = ExportUtils.ToFloat3(sphere.center)
                    };
                    continue;
                }

                if (collider is CapsuleCollider capsule && entity.capsuleCollider == null)
                {
                    if (capsule.direction != 1)
                    {
                        var warning = "CapsuleCollider direction != Y fallback on " + ExportUtils.GetTransformPath(go.transform);
                        sceneData.warnings.Add(warning);
                        m_report.warnings.Add(warning);
                    }

                    entity.capsuleCollider = new CapsuleColliderExportData
                    {
                        enabled = capsule.enabled,
                        isTrigger = capsule.isTrigger,
                        radius = capsule.radius,
                        height = capsule.height,
                        direction = capsule.direction,
                        offset = ExportUtils.ToFloat3(capsule.center)
                    };
                    continue;
                }

                if (collider is MeshCollider meshCollider && entity.meshCollider == null)
                {
                    var meshPath = ResolveMeshPath(meshCollider.sharedMesh, stableId + "_meshcol", go.name + "_mesh_collider", out var wasBaked);
                    entity.meshCollider = new MeshColliderExportData
                    {
                        enabled = meshCollider.enabled,
                        isTrigger = meshCollider.isTrigger,
                        scale = ExportUtils.ToFloat3(Vector3.one),
                        offset = ExportUtils.ToFloat3(Vector3.zero),
                        meshAssetRelativePath = meshPath,
                        meshWasBaked = wasBaked,
                        meshSource = meshCollider.sharedMesh != null ? AssetDatabase.GetAssetPath(meshCollider.sharedMesh) : string.Empty
                    };
                    continue;
                }
            }
        }

        private void CollectTerrain(GameObject go, string stableId, SceneExportData sceneData, EntityExportData entity)
        {
            var terrain = go.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null)
                return;

            var data = terrain.terrainData;
            var terrainData = new TerrainExportData
            {
                enabled = terrain.enabled,
                size = ExportUtils.ToFloat3(data.size),
                layers = new List<TerrainLayerExportData>()
            };

            var heightmapTexture = ExportTerrainHeightmap(data, stableId + "_heightmap");
            terrainData.heightmapTexture = heightmapTexture;
            terrainData.splatmapTexture = ExportTerrainSplatmap(data, stableId + "_splatmap");
            terrainData.weightmapTexture = terrainData.splatmapTexture;

            ValidateTerrainTexturePath(sceneData, go, "splatmap", terrainData.splatmapTexture);

            var layers = data.terrainLayers;
            if (layers != null)
            {
                if (layers.Length > 4)
                {
                    var warning = "Terrain on " + ExportUtils.GetTransformPath(go.transform) + " has " + layers.Length +
                                  " layers. CyberEngine V1 supports 4 layers. Extra layers are ignored.";
                    sceneData.warnings.Add(warning);
                    m_report.warnings.Add(warning);
                }

                for (var i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    if (layer == null)
                        continue;

                    var layerData = new TerrainLayerExportData
                    {
                        index = i,
                        name = layer.name,
                        metallic = layer.metallic,
                        smoothness = layer.smoothness,
                        specularColor = ExportUtils.ToFloat3(layer.specular),
                        tileOffset = ExportUtils.ToFloat2(layer.tileOffset),
                        tileSize = ExportUtils.ToFloat2(layer.tileSize),
                        albedoTexture = m_assets.ExportObject(layer.diffuseTexture, AssetKind.Texture, layer.name + "_albedo"),
                        normalTexture = m_assets.ExportObject(layer.normalMapTexture, AssetKind.Texture, layer.name + "_normal")
                    };

                    terrainData.layers.Add(layerData);
                    ValidateTerrainTexturePath(sceneData, go, "layer albedo", layerData.albedoTexture);
                    ValidateTerrainTexturePath(sceneData, go, "layer normal", layerData.normalTexture);
                }
            }

            entity.terrain = terrainData;
        }

        private string ExportTerrainHeightmap(TerrainData terrainData, string key)
        {
            if (terrainData == null)
                return string.Empty;

            var width = terrainData.heightmapResolution;
            var height = terrainData.heightmapResolution;
            var heights = terrainData.GetHeights(0, 0, width, height);

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var h = heights[y, x];
                    tex.SetPixel(x, y, new Color(h, h, h, 1.0f));
                }
            }
            tex.Apply();

            var rel = m_assets.ExportGeneratedTexture(tex, key, key, AssetKind.Terrain);
            UnityEngine.Object.DestroyImmediate(tex);
            return rel;
        }

        private string ExportTerrainSplatmap(TerrainData terrainData, string key)
        {
            if (terrainData == null)
                return string.Empty;

            if (terrainData.alphamapTextureCount > 0)
            {
                var alphaTexture = terrainData.GetAlphamapTexture(0);
                if (alphaTexture != null)
                {
                    var relFromSource = m_assets.ExportObject(alphaTexture, AssetKind.Terrain, key);
                    if (!string.IsNullOrWhiteSpace(relFromSource))
                        return relFromSource;
                }
            }

            var width = terrainData.alphamapWidth;
            var height = terrainData.alphamapHeight;
            var layers = terrainData.alphamapLayers;
            if (width <= 0 || height <= 0 || layers <= 0)
                return string.Empty;

            var alpha = terrainData.GetAlphamaps(0, 0, width, height);
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var r = layers > 0 ? alpha[y, x, 0] : 0.0f;
                    var g = layers > 1 ? alpha[y, x, 1] : 0.0f;
                    var b = layers > 2 ? alpha[y, x, 2] : 0.0f;
                    var a = layers > 3 ? alpha[y, x, 3] : 0.0f;
                    tex.SetPixel(x, y, new Color(r, g, b, a));
                }
            }
            tex.Apply();

            var rel = m_assets.ExportGeneratedTexture(tex, key, key, AssetKind.Terrain);
            UnityEngine.Object.DestroyImmediate(tex);
            return rel;
        }

        private static bool IsValidTerrainTextureExportPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            var normalized = ExportUtils.NormalizeRelativePath(relativePath);
            return normalized.StartsWith("assets/textures/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("assets/terrains/", StringComparison.OrdinalIgnoreCase);
        }

        private void ValidateTerrainTexturePath(SceneExportData sceneData, GameObject terrainObject, string textureRole, string relativePath)
        {
            if (sceneData == null || terrainObject == null)
                return;

            if (IsValidTerrainTextureExportPath(relativePath))
                return;

            var warning = "Terrain texture export invalid or missing (" + textureRole + ") on " +
                          ExportUtils.GetTransformPath(terrainObject.transform) + ".";
            sceneData.warnings.Add(warning);
            m_report.warnings.Add(warning);
        }

        private void CollectAudio(GameObject go, string stableId, SceneExportData sceneData, EntityExportData entity)
        {
            var source = go.GetComponent<AudioSource>();
            if (source == null)
                return;

            var clipPath = m_assets.ExportObject(source.clip, AssetKind.Audio, go.name + "_audio");
            entity.audioSource = new AudioSourceExportData
            {
                enabled = source.enabled,
                clipPath = clipPath,
                volume = source.volume,
                pitch = source.pitch,
                loop = source.loop,
                playOnAwake = source.playOnAwake,
                spatialize = source.spatialize
            };

            EnsureAudioStub(entity);
        }

        private void EnsureAudioStub(EntityExportData entity)
        {
            const string sourceType = "UnityEngine.AudioSource";
            const string generatedType = "UnityAudioSourceMetadataComponent";
            const string headerName = "unity_audio_source_metadata_component.hpp";

            m_customComponents.EnsureSchema(new CustomComponentSchemaData
            {
                sourceType = sourceType,
                generatedType = generatedType,
                headerFileName = headerName,
                fields = new List<CustomFieldSchemaData>
                {
                    new CustomFieldSchemaData { name = "clipPath", cppType = "std::string", defaultValueCpp = "\"\"", serializedPropertyType = "String" },
                    new CustomFieldSchemaData { name = "volume", cppType = "float", defaultValueCpp = "1.0f", serializedPropertyType = "Float" },
                    new CustomFieldSchemaData { name = "pitch", cppType = "float", defaultValueCpp = "1.0f", serializedPropertyType = "Float" },
                    new CustomFieldSchemaData { name = "loop", cppType = "bool", defaultValueCpp = "false", serializedPropertyType = "Boolean" },
                    new CustomFieldSchemaData { name = "playOnAwake", cppType = "bool", defaultValueCpp = "true", serializedPropertyType = "Boolean" },
                    new CustomFieldSchemaData { name = "spatialize", cppType = "bool", defaultValueCpp = "false", serializedPropertyType = "Boolean" }
                }
            });

            if (entity.audioSource == null)
                return;

            entity.customComponents.Add(new CustomComponentInstanceData
            {
                sourceType = sourceType,
                generatedType = generatedType,
                fields = new List<CustomFieldValueData>
                {
                    new CustomFieldValueData { name = "clipPath", cppType = "std::string", valueCpp = "\"" + ExportUtils.EscapeCppString(entity.audioSource.clipPath) + "\"", serializedPropertyType = "String" },
                    new CustomFieldValueData { name = "volume", cppType = "float", valueCpp = ExportUtils.ToFloatLiteral(entity.audioSource.volume), serializedPropertyType = "Float" },
                    new CustomFieldValueData { name = "pitch", cppType = "float", valueCpp = ExportUtils.ToFloatLiteral(entity.audioSource.pitch), serializedPropertyType = "Float" },
                    new CustomFieldValueData { name = "loop", cppType = "bool", valueCpp = entity.audioSource.loop ? "true" : "false", serializedPropertyType = "Boolean" },
                    new CustomFieldValueData { name = "playOnAwake", cppType = "bool", valueCpp = entity.audioSource.playOnAwake ? "true" : "false", serializedPropertyType = "Boolean" },
                    new CustomFieldValueData { name = "spatialize", cppType = "bool", valueCpp = entity.audioSource.spatialize ? "true" : "false", serializedPropertyType = "Boolean" }
                }
            });
        }

        public ComponentAuditReport BuildComponentAuditReport(string generatedAtUtc)
        {
            var auditReport = new ComponentAuditReport
            {
                generatedAtUtc = generatedAtUtc
            };

            foreach (var usage in m_componentUsage.Values.OrderBy(v => v.typeName, StringComparer.Ordinal))
            {
                var impactWeight = GetImpactWeight(usage.typeName, usage.classification);
                var score = usage.usageCount * impactWeight;
                var entry = new ComponentAuditEntry
                {
                    typeName = usage.typeName,
                    classification = usage.classification,
                    isBuiltin = usage.isBuiltin,
                    isMonoBehaviour = usage.isMonoBehaviour,
                    usageCount = usage.usageCount,
                    impactWeight = impactWeight,
                    score = score,
                    scenes = usage.sceneCounts
                        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => new ComponentSceneCountEntry { sceneName = kv.Key, count = kv.Value })
                        .ToList(),
                    examples = usage.examples.ToList()
                };

                auditReport.components.Add(entry);
            }

            var summary = new ComponentAuditSummary
            {
                totalComponentTypeCount = auditReport.components.Count,
                unsupportedBuiltinTypeCount = auditReport.components.Count(c => c.classification == ClassificationUnsupportedBuiltin),
                unsupportedBuiltinInstanceCount = auditReport.components
                    .Where(c => c.classification == ClassificationUnsupportedBuiltin)
                    .Sum(c => c.usageCount)
            };

            summary.topMissing = auditReport.components
                .Where(c => c.classification == ClassificationUnsupportedBuiltin)
                .OrderByDescending(c => c.score)
                .ThenByDescending(c => c.usageCount)
                .ThenBy(c => c.typeName, StringComparer.Ordinal)
                .Take(20)
                .Select(c => new ComponentAuditTopEntry
                {
                    typeName = c.typeName,
                    usageCount = c.usageCount,
                    impactWeight = c.impactWeight,
                    score = c.score
                })
                .ToList();

            auditReport.summary = summary;
            auditReport.structuralGaps = BuildStructuralGaps(auditReport.components);
            return auditReport;
        }

        private void CollectCustomComponents(GameObject go, SceneExportData sceneData, EntityExportData entity, string sceneName)
        {
            var components = go.GetComponents<Component>();
            var transformPath = ExportUtils.GetTransformPath(go.transform);

            foreach (var component in components)
            {
                if (component == null)
                {
                    var warning = "Missing script component on " + transformPath;
                    if (!m_report.warnings.Contains(warning))
                        m_report.warnings.Add(warning);
                    continue;
                }

                if (IsNativeMappedComponent(component))
                {
                    RecordComponentUsage(component, ClassificationNativeMapped, sceneName, transformPath);
                    continue;
                }

                if (component is SceneExporter || ShouldSkipCustomComponent(component))
                {
                    RecordComponentUsage(component, ClassificationIgnoredAuthoring, sceneName, transformPath);
                    continue;
                }

                if (IsBuiltinComponent(component.GetType()))
                {
                    RecordComponentUsage(component, ClassificationUnsupportedBuiltin, sceneName, transformPath);
                    var typeName = component.GetType().FullName ?? component.GetType().Name;
                    if (m_unsupportedBuiltinWarnings.Add(typeName))
                    {
                        var warning = "Unsupported built-in component: " + typeName + " on " + transformPath;
                        sceneData.warnings.Add(warning);
                        m_report.warnings.Add(warning);
                    }
                    continue;
                }

                RecordComponentUsage(component, ClassificationCustomStub, sceneName, transformPath);
                if (component is MonoBehaviour mono)
                {
                    m_customComponents.Collect(mono, entity, m_report);
                    continue;
                }

                var customTypeName = component.GetType().FullName ?? component.GetType().Name;
                if (m_nonMonoCustomWarnings.Add(customTypeName))
                {
                    var warning = "Custom non-MonoBehaviour component skipped: " + customTypeName + " on " + transformPath;
                    sceneData.warnings.Add(warning);
                    m_report.warnings.Add(warning);
                }
            }
        }

        private void RecordComponentUsage(Component component, string classification, string sceneName, string transformPath)
        {
            if (component == null)
                return;

            var type = component.GetType();
            var typeName = type.FullName ?? type.Name;

            if (!m_componentUsage.TryGetValue(typeName, out var usage))
            {
                usage = new ComponentUsageAccumulator
                {
                    typeName = typeName,
                    classification = classification,
                    isBuiltin = IsBuiltinComponent(type),
                    isMonoBehaviour = component is MonoBehaviour
                };
                m_componentUsage[typeName] = usage;
            }

            usage.classification = classification;
            usage.usageCount++;

            if (!usage.sceneCounts.TryGetValue(sceneName, out var count))
                count = 0;
            usage.sceneCounts[sceneName] = count + 1;

            if (usage.examples.Count < MaxComponentExamples && !usage.examples.Contains(transformPath))
                usage.examples.Add(transformPath);
        }

        private static float GetImpactWeight(string typeName, string classification)
        {
            if (classification != ClassificationUnsupportedBuiltin || string.IsNullOrWhiteSpace(typeName))
                return 0.0f;

            if (ContainsAny(typeName, "Animator", "Animation", "SkinnedMeshRenderer", "BlendShape", "Avatar"))
                return 10.0f;
            if (ContainsAny(typeName, "ParticleSystem", "VisualEffect", "VFX"))
                return 8.0f;
            if (ContainsAny(typeName, "CharacterController", "NavMeshAgent", "NavMesh"))
                return 7.0f;
            if (ContainsAny(typeName, "Canvas", "RectTransform", "TextMeshPro", "TMP_", "UnityEngine.UI", "Text"))
                return 6.0f;
            if (ContainsAny(typeName, "Joint", "WheelCollider", "Cloth"))
                return 5.0f;
            if (ContainsAny(typeName, "Sprite", "Tilemap"))
                return 4.0f;

            return 2.0f;
        }

        private static List<ComponentStructuralGapEntry> BuildStructuralGaps(List<ComponentAuditEntry> entries)
        {
            var missingEntries = entries.Where(e => e.classification == ClassificationUnsupportedBuiltin).ToList();
            var list = new List<ComponentStructuralGapEntry>
            {
                BuildStructuralGap("animation", "Animation (clips/state machine/skeleton/skinning/blend)", missingEntries,
                    "Animator", "Animation", "SkinnedMeshRenderer", "BlendShape", "Avatar"),
                BuildStructuralGap("vfx_particles", "VFX / Particles", missingEntries, "ParticleSystem", "VisualEffect", "VFX"),
                BuildStructuralGap("ui_runtime", "UI runtime (Canvas/RectTransform/Text)", missingEntries,
                    "Canvas", "RectTransform", "TextMeshPro", "TMP_", "UnityEngine.UI", "Text"),
                BuildStructuralGap("navigation_agents", "Navigation / Agents", missingEntries,
                    "NavMesh", "NavMeshAgent", "OffMeshLink"),
                BuildStructuralGap("advanced_physics", "Advanced physics controllers (joints/character/wheel)", missingEntries,
                    "Joint", "CharacterController", "WheelCollider", "Cloth"),
                BuildStructuralGap("stack_2d", "2D stack (sprites/tilemaps)", missingEntries,
                    "Sprite", "Tilemap", "CompositeCollider2D", "Rigidbody2D")
            };
            return list;
        }

        private static ComponentStructuralGapEntry BuildStructuralGap(string key, string title, List<ComponentAuditEntry> entries,
                                                                      params string[] keywords)
        {
            var related = entries
                .Where(e => ContainsAny(e.typeName, keywords))
                .OrderByDescending(e => e.usageCount)
                .ThenBy(e => e.typeName, StringComparer.Ordinal)
                .ToList();

            return new ComponentStructuralGapEntry
            {
                key = key,
                title = title,
                detected = related.Count > 0,
                relatedComponentCount = related.Sum(e => e.usageCount),
                relatedTypes = related.Select(e => e.typeName).Take(10).ToList()
            };
        }

        private static bool ContainsAny(string value, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(value) || keywords == null)
                return false;

            for (var i = 0; i < keywords.Length; i++)
            {
                var keyword = keywords[i];
                if (string.IsNullOrWhiteSpace(keyword))
                    continue;
                if (value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsBuiltinComponent(Type type)
        {
            if (type == null)
                return false;

            var ns = type.Namespace ?? string.Empty;
            var asmName = type.Assembly != null ? (type.Assembly.GetName().Name ?? string.Empty) : string.Empty;
            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("UnityEditor", StringComparison.Ordinal))
                return true;
            if (asmName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static bool IsNativeMappedComponent(Component component)
        {
            return component is Transform || component is MeshRenderer || component is MeshFilter || component is Light ||
                   component is ReflectionProbe || component is Camera || component is Collider || component is Rigidbody ||
                   component is Terrain || component is AudioSource;
        }

        private sealed class ComponentUsageAccumulator
        {
            public string typeName;
            public string classification;
            public bool isBuiltin;
            public bool isMonoBehaviour;
            public int usageCount;
            public Dictionary<string, int> sceneCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<string> examples = new List<string>();
        }

        private static bool ShouldSkipCustomComponent(Component component)
        {
            if (component == null)
                return true;

            var type = component.GetType();
            var ns = type.Namespace ?? string.Empty;
            var asmName = type.Assembly != null ? (type.Assembly.GetName().Name ?? string.Empty) : string.Empty;

            // ProBuilder components carry authoring/editor data and can be very large.
            // We export resulting meshes through MeshFilter/MeshRenderer, so these stubs are unnecessary.
            if (ns.StartsWith("UnityEngine.ProBuilder", StringComparison.Ordinal))
                return true;
            if (ns.StartsWith("UnityEditor.ProBuilder", StringComparison.Ordinal))
                return true;
            if (string.Equals(asmName, "Unity.ProBuilder", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(asmName, "UnityEditor.ProBuilder", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
#endif
