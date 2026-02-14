using System;
using System.Collections.Generic;

namespace Demonixis.UnityJSONSceneExporter
{
    [Serializable]
    public class ExportManifest
    {
        public string schemaVersion = "1.2.0";
        public string projectName;
        public string generatedAtUtc;
        public ExportOptionsSnapshot options;
        public List<SceneManifestEntry> scenes = new List<SceneManifestEntry>();
        public List<AssetManifestEntry> assets = new List<AssetManifestEntry>();
        public string gameDataPath;
        public List<string> generatedComponentHeaders = new List<string>();
        public GeneratedProjectManifestEntry generatedProject;
        public ComponentAuditSummary componentAudit;
    }

    [Serializable]
    public class ExportOptionsSnapshot
    {
        public string outputRoot;
        public string assetScope;
        public string sceneSelectionMode;
        public bool generateCpp;
        public bool generateJson;
        public bool generateCppProject;
        public bool convertSceneToCpp;
        public string cyberEngineRootPath;
        public string generatedProjectName;
        public string baseSceneClass;
        public bool failOnError;
        public bool cleanOutput;
    }

    [Serializable]
    public class SceneManifestEntry
    {
        public string sceneName;
        public string sceneAssetPath;
        public string sceneJsonPath;
        public string sceneHeaderPath;
        public string sceneCppPath;
        public int entityCount;
        public int customComponentCount;
        public int warningCount;
    }

    [Serializable]
    public class AssetManifestEntry
    {
        public string kind;
        public string source;
        public string relativePath;
        public string sha256;
        public long byteSize;
    }

    [Serializable]
    public class GeneratedProjectManifestEntry
    {
        public string rootPath;
        public string cmakePath;
        public string mainPath;
        public string sceneRegistryHeaderPath;
        public string sceneRegistryCppPath;
        public string readmePath;
        public string cyberEngineLinkPath;
        public bool cyberEngineLinkCreated;
        public string defaultSceneName;
        public string sceneLoadingMode;
        public string jsonSceneLoaderHeaderPath;
        public string jsonSceneLoaderCppPath;
        public string jsonRuntimeSceneHeaderPath;
        public string jsonRuntimeSceneCppPath;
    }

    [Serializable]
    public class ComponentAuditSummary
    {
        public int totalComponentTypeCount;
        public int unsupportedBuiltinTypeCount;
        public int unsupportedBuiltinInstanceCount;
        public List<ComponentAuditTopEntry> topMissing = new List<ComponentAuditTopEntry>();
    }

    [Serializable]
    public class ComponentAuditTopEntry
    {
        public string typeName;
        public int usageCount;
        public float impactWeight;
        public float score;
    }

    [Serializable]
    public class ComponentAuditReport
    {
        public string schemaVersion = "1.0.0";
        public string generatedAtUtc;
        public ComponentAuditSummary summary = new ComponentAuditSummary();
        public List<ComponentAuditEntry> components = new List<ComponentAuditEntry>();
        public List<ComponentStructuralGapEntry> structuralGaps = new List<ComponentStructuralGapEntry>();
    }

    [Serializable]
    public class ComponentAuditEntry
    {
        public string typeName;
        public string classification;
        public bool isBuiltin;
        public bool isMonoBehaviour;
        public int usageCount;
        public float impactWeight;
        public float score;
        public List<ComponentSceneCountEntry> scenes = new List<ComponentSceneCountEntry>();
        public List<string> examples = new List<string>();
    }

    [Serializable]
    public class ComponentSceneCountEntry
    {
        public string sceneName;
        public int count;
    }

    [Serializable]
    public class ComponentStructuralGapEntry
    {
        public string key;
        public string title;
        public bool detected;
        public int relatedComponentCount;
        public List<string> relatedTypes = new List<string>();
    }

    [Serializable]
    public class SceneExportData
    {
        public string schemaVersion = "1.0.0";
        public string sceneName;
        public string sceneAssetPath;
        public string generatedAtUtc;
        public RenderSettingsExportData renderSettings;
        public SkyboxExportData skybox;
        public List<EntityExportData> entities = new List<EntityExportData>();
        public List<string> warnings = new List<string>();
    }

    [Serializable]
    public class GameExportData
    {
        public string schemaVersion = "1.0.0";
        public string projectName;
        public string generatedAtUtc;
        public string defaultSceneName;
        public List<GameSceneEntry> scenes = new List<GameSceneEntry>();
    }

    [Serializable]
    public class GameSceneEntry
    {
        public string sceneName;
        public string sceneJsonPath;
        public string sceneHeaderPath;
        public string sceneCppPath;
    }

    [Serializable]
    public class EntityExportData
    {
        public string stableId;
        public string parentStableId;
        public string name;
        public string tag;
        public bool isStatic;
        public bool isActive;
        public float[] localPosition;
        public float[] localRotation;
        public float[] localScale;

        public ModelExportData model;
        public DirectionalLightExportData directionalLight;
        public PointLightExportData pointLight;
        public SpotLightExportData spotLight;
        public ReflectionProbeExportData reflectionProbe;
        public CameraExportData camera;
        public RigidbodyExportData rigidbody;
        public BoxColliderExportData boxCollider;
        public SphereColliderExportData sphereCollider;
        public CapsuleColliderExportData capsuleCollider;
        public MeshColliderExportData meshCollider;
        public TerrainExportData terrain;
        public AudioSourceExportData audioSource;

        public List<CustomComponentInstanceData> customComponents = new List<CustomComponentInstanceData>();
    }

    [Serializable]
    public class ModelExportData
    {
        public bool enabled;
        public bool castShadows;
        public bool receiveShadows;
        public bool isStatic;
        public string meshAssetRelativePath;
        public string meshSource;
        public bool meshWasBaked;
        public MaterialExportData material;
    }

    [Serializable]
    public class MaterialExportData
    {
        public string stableId;
        public string name;
        public float[] baseColor;
        public float[] emissionColor;
        public float emissionIntensity;
        public float shininess;
        public float reflectivity;
        public float specularStrength;
        public float alphaCutoff;
        public bool receiveShadows;
        public bool doubleSided;
        public bool transparent;
        public float[] uvScale;
        public float[] uvOffset;
        public string diffuseTexture;
        public string normalTexture;
        public string specularTexture;
        public string emissiveTexture;
        public string aoTexture;
        public string metallicTexture;
    }

    [Serializable]
    public class DirectionalLightExportData
    {
        public bool enabled;
        public float[] color;
        public float intensity;
        public bool castShadows;
    }

    [Serializable]
    public class PointLightExportData
    {
        public bool enabled;
        public float[] color;
        public float intensity;
        public float radius;
        public bool castShadows;
    }

    [Serializable]
    public class SpotLightExportData
    {
        public bool enabled;
        public float[] color;
        public float intensity;
        public float range;
        public float innerConeAngleDegrees;
        public float outerConeAngleDegrees;
        public bool castShadows;
    }

    [Serializable]
    public class ReflectionProbeExportData
    {
        public bool enabled;
        public bool isBaked;
        public float intensity;
        public float[] size;
        public float[] center;
        public string cubemapPath;
    }

    [Serializable]
    public class CameraExportData
    {
        public bool enabled;
        public float fov;
        public float nearPlane;
        public float farPlane;
        public float aspect;
        public bool isActive;
    }

    [Serializable]
    public class RenderSettingsExportData
    {
        public float[] ambientLight;
        public float ambientIntensity;
        public bool fogEnabled;
        public float[] fogColor;
        public float fogDensity;
        public float fogStartDistance;
        public float fogEndDistance;
        public string fogMode;
        public float reflectionIntensity;
        public int reflectionBounces;
        public string defaultReflectionMode;
    }

    [Serializable]
    public class SkyboxExportData
    {
        public bool enabled;
        public string sourceType;
        public string materialName;
        public string shaderName;
        public string panoramicTexture;
        public List<string> cubemapFacePaths = new List<string>();
    }

    [Serializable]
    public class RigidbodyExportData
    {
        public bool isKinematic;
        public bool useGravity;
        public float maxLinearVelocity;
        public float maxAngularVelocity;
        public float[] centerOfMass;
        public float[] linearVelocity;
        public float[] angularVelocity;
    }

    [Serializable]
    public class BoxColliderExportData
    {
        public bool enabled;
        public bool isTrigger;
        public float[] size;
        public float[] offset;
    }

    [Serializable]
    public class SphereColliderExportData
    {
        public bool enabled;
        public bool isTrigger;
        public float radius;
        public float[] offset;
    }

    [Serializable]
    public class CapsuleColliderExportData
    {
        public bool enabled;
        public bool isTrigger;
        public float radius;
        public float height;
        public int direction;
        public float[] offset;
    }

    [Serializable]
    public class MeshColliderExportData
    {
        public bool enabled;
        public bool isTrigger;
        public float[] scale;
        public float[] offset;
        public string meshAssetRelativePath;
        public bool meshWasBaked;
        public string meshSource;
    }

    [Serializable]
    public class TerrainLayerExportData
    {
        public int index;
        public string name;
        public float metallic;
        public float smoothness;
        public float[] specularColor;
        public float[] tileOffset;
        public float[] tileSize;
        public string albedoTexture;
        public string normalTexture;
    }

    [Serializable]
    public class TerrainExportData
    {
        public bool enabled;
        public float[] size;
        public string heightmapTexture;
        public string splatmapTexture;
        public string weightmapTexture;
        public List<TerrainLayerExportData> layers = new List<TerrainLayerExportData>();
    }

    [Serializable]
    public class AudioSourceExportData
    {
        public bool enabled;
        public string clipPath;
        public float volume;
        public float pitch;
        public bool loop;
        public bool playOnAwake;
        public bool spatialize;
    }

    [Serializable]
    public class CustomComponentInstanceData
    {
        public string sourceType;
        public string generatedType;
        public List<CustomFieldValueData> fields = new List<CustomFieldValueData>();
    }

    [Serializable]
    public class CustomComponentSchemaData
    {
        public string sourceType;
        public string generatedType;
        public string headerFileName;
        public List<CustomFieldSchemaData> fields = new List<CustomFieldSchemaData>();
    }

    [Serializable]
    public class CustomFieldSchemaData
    {
        public string name;
        public string cppType;
        public string defaultValueCpp;
        public string serializedPropertyType;
    }

    [Serializable]
    public class CustomFieldValueData
    {
        public string name;
        public string cppType;
        public string valueCpp;
        public string serializedPropertyType;
    }
}
