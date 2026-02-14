#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Demonixis.UnityJSONSceneExporter
{
    internal enum AssetKind
    {
        Model,
        Texture,
        Audio,
        Terrain,
        Data
    }

    internal sealed class AssetExportDatabase
    {
        private readonly string m_bundleRoot;
        private readonly ExportManifest m_manifest;
        private readonly ExportReport m_report;

        private readonly Dictionary<string, string> m_sourceToRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> m_hashToRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> m_usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> TextureExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".exr", ".hdr", ".psd"
        };

        private static readonly HashSet<string> ForcePngTextureExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".tif", ".tiff", ".psd"
        };

        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac"
        };

        private static readonly HashSet<string> ModelExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".obj", ".dae", ".gltf", ".glb", ".blend", ".3ds", ".stl"
        };

        public AssetExportDatabase(string bundleRoot, ExportManifest manifest, ExportReport report)
        {
            m_bundleRoot = bundleRoot;
            m_manifest = manifest;
            m_report = report;
        }

        public string ExportAssetPath(string assetPath, AssetKind kind)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return string.Empty;

            assetPath = ExportUtils.NormalizeRelativePath(assetPath);
            if (m_sourceToRelativePath.TryGetValue(assetPath, out var cached))
                return cached;

            var absolutePath = GetAbsoluteAssetPath(assetPath);
            if (!File.Exists(absolutePath))
            {
                AddWarning("Missing asset file: " + assetPath);
                return string.Empty;
            }

            if (!IsRuntimeUsableAssetPath(assetPath))
            {
                AddWarning("Skipping non-runtime asset file: " + assetPath);
                return string.Empty;
            }

            if (kind == AssetKind.Texture && ShouldTranscodeTextureToPng(assetPath))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
                if (texture != null)
                {
                    var pngBytes = EncodeTextureToPng(texture);
                    if (pngBytes != null && pngBytes.Length > 0)
                    {
                        var logicalSubPathPng = assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                            ? assetPath.Substring("Assets/".Length)
                            : Path.GetFileName(assetPath);
                        logicalSubPathPng = Path.ChangeExtension(logicalSubPathPng, ".png");
                        return RegisterBytes(assetPath, pngBytes, kind, logicalSubPathPng);
                    }
                }

                AddWarning("Failed to transcode texture to PNG, keeping original: " + assetPath);
            }

            var bytes = File.ReadAllBytes(absolutePath);
            var logicalSubPath = assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? assetPath.Substring("Assets/".Length)
                : Path.GetFileName(assetPath);

            return RegisterBytes(assetPath, bytes, kind, logicalSubPath);
        }

        public string ExportObject(UnityEngine.Object obj, AssetKind kindHint, string fallbackName)
        {
            if (obj == null)
                return string.Empty;

            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrWhiteSpace(assetPath))
                return ExportAssetPath(assetPath, kindHint);

            if (obj is Texture texture)
            {
                var png = EncodeTextureToPng(texture);
                if (png == null || png.Length == 0)
                {
                    AddWarning("Unable to encode texture to PNG: " + obj.name);
                    return string.Empty;
                }

                var safeName = ExportUtils.ToSnakeCase(fallbackName, "texture");
                return RegisterBytes("generated:texture:" + obj.GetInstanceID().ToString(CultureInfo.InvariantCulture), png,
                                     AssetKind.Texture, "generated/" + safeName + ".png");
            }

            AddWarning("Asset object has no export path and unsupported fallback: " + obj.name);
            return string.Empty;
        }

        public string ExportGeneratedTexture(Texture2D texture, string key, string fileNameWithoutExtension, AssetKind kind)
        {
            if (texture == null)
                return string.Empty;

            var bytes = texture.EncodeToPNG();
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            var safeName = ExportUtils.ToSnakeCase(fileNameWithoutExtension, "generated_tex");
            return RegisterBytes("generated:texture:" + key, bytes, kind, "generated/" + safeName + ".png");
        }

        public string ExportBakedMeshObj(Mesh mesh, string key, string fileNameWithoutExtension)
        {
            if (mesh == null)
                return string.Empty;

            var objBytes = Encoding.UTF8.GetBytes(BuildObj(mesh));
            var safeName = ExportUtils.ToSnakeCase(fileNameWithoutExtension, "generated_mesh");
            return RegisterBytes("generated:mesh:" + key, objBytes, AssetKind.Model, "generated/" + safeName + ".obj");
        }

        public void ExportDiscoveredAssets(IEnumerable<string> assetPaths)
        {
            if (assetPaths == null)
                return;

            foreach (var rawPath in assetPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                    continue;

                var normalized = ExportUtils.NormalizeRelativePath(rawPath);
                var kind = ClassifyByPath(normalized);
                if (!kind.HasValue)
                    continue;

                ExportAssetPath(normalized, kind.Value);
            }
        }

        public static AssetKind? ClassifyByPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var ext = Path.GetExtension(assetPath);
            if (string.IsNullOrWhiteSpace(ext))
                return null;

            if (TextureExtensions.Contains(ext))
                return AssetKind.Texture;
            if (AudioExtensions.Contains(ext))
                return AssetKind.Audio;
            if (ModelExtensions.Contains(ext))
                return AssetKind.Model;

            return null;
        }

        private static bool IsRuntimeUsableAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return false;

            var ext = Path.GetExtension(assetPath);
            if (string.IsNullOrWhiteSpace(ext))
                return false;

            if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase))
                return false;
            if (ext.Equals(".terrainlayer", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static bool ShouldTranscodeTextureToPng(string assetPath)
        {
            var ext = Path.GetExtension(assetPath);
            if (string.IsNullOrWhiteSpace(ext))
                return false;

            return ForcePngTextureExtensions.Contains(ext);
        }

        private static string GetKindFolder(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Model:
                    return "models";
                case AssetKind.Texture:
                    return "textures";
                case AssetKind.Audio:
                    return "audio";
                case AssetKind.Terrain:
                    return "terrains";
                default:
                    return "data";
            }
        }

        private string RegisterBytes(string sourceKey, byte[] bytes, AssetKind kind, string suggestedRelativeSubPath)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(sourceKey) && m_sourceToRelativePath.TryGetValue(sourceKey, out var cached))
                return cached;

            var hash = ExportUtils.ComputeSha256(bytes);
            if (m_hashToRelativePath.TryGetValue(hash, out var hashCached))
            {
                if (!string.IsNullOrWhiteSpace(sourceKey))
                    m_sourceToRelativePath[sourceKey] = hashCached;
                return hashCached;
            }

            var kindFolder = GetKindFolder(kind);
            var cleanSubPath = ExportUtils.NormalizeRelativePath(suggestedRelativeSubPath ?? string.Empty).TrimStart('/');
            if (string.IsNullOrWhiteSpace(cleanSubPath))
                cleanSubPath = "generated/asset.bin";

            cleanSubPath = cleanSubPath.Replace("..", "_");
            var relativePath = ExportUtils.NormalizeRelativePath(Path.Combine("assets", kindFolder, cleanSubPath));

            if (m_usedRelativePaths.Contains(relativePath))
            {
                var dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
                var file = Path.GetFileNameWithoutExtension(relativePath);
                var ext = Path.GetExtension(relativePath);
                relativePath = string.Format(CultureInfo.InvariantCulture, "{0}/{1}_{2}{3}", dir, file, hash.Substring(0, 8), ext);
            }

            var absolute = Path.Combine(m_bundleRoot, relativePath);
            var absoluteDir = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrWhiteSpace(absoluteDir) && !Directory.Exists(absoluteDir))
                Directory.CreateDirectory(absoluteDir);

            File.WriteAllBytes(absolute, bytes);

            m_usedRelativePaths.Add(relativePath);
            m_hashToRelativePath[hash] = relativePath;
            if (!string.IsNullOrWhiteSpace(sourceKey))
                m_sourceToRelativePath[sourceKey] = relativePath;

            m_manifest.assets.Add(new AssetManifestEntry
            {
                kind = kind.ToString().ToLowerInvariant(),
                source = sourceKey,
                relativePath = relativePath,
                sha256 = hash,
                byteSize = bytes.LongLength
            });

            switch (kind)
            {
                case AssetKind.Texture:
                    m_report.stats.textureCount++;
                    break;
                case AssetKind.Model:
                    m_report.stats.modelAssetCount++;
                    break;
                case AssetKind.Audio:
                    m_report.stats.audioAssetCount++;
                    break;
                case AssetKind.Terrain:
                    m_report.stats.terrainAssetCount++;
                    break;
            }

            m_report.stats.totalAssetBytes += bytes.LongLength;
            return relativePath;
        }

        private string GetAbsoluteAssetPath(string assetPath)
        {
            var projectRoot = Directory.GetCurrentDirectory();
            return Path.Combine(projectRoot, assetPath);
        }

        private byte[] EncodeTextureToPng(Texture texture)
        {
            if (texture == null)
                return null;

            var source = texture as Texture2D;
            if (source != null)
            {
                try
                {
                    return source.EncodeToPNG();
                }
                catch
                {
                    // Continue with RenderTexture fallback.
                }
            }

            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;

            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            read.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            var bytes = read.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(read);
            return bytes;
        }

        private static string BuildObj(Mesh mesh)
        {
            var sb = new StringBuilder(mesh.vertexCount * 48);
            sb.AppendLine("# Unity mesh export");
            sb.AppendLine("# Pre-converted for CyberEngine Assimp flags (MakeLeftHanded + FlipWindingOrder + FlipUVs)");

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;

            for (var i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                // Unity mesh is left-handed. Export mirrored Z so importer conversion restores original orientation.
                sb.AppendFormat(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", v.x, v.y, -v.z);
            }

            if (uvs != null && uvs.Length == vertices.Length)
            {
                for (var i = 0; i < uvs.Length; i++)
                {
                    var uv = uvs[i];
                    sb.AppendFormat(CultureInfo.InvariantCulture, "vt {0} {1}\n", uv.x, 1.0f - uv.y);
                }
            }

            if (normals != null && normals.Length == vertices.Length)
            {
                for (var i = 0; i < normals.Length; i++)
                {
                    var n = normals[i];
                    sb.AppendFormat(CultureInfo.InvariantCulture, "vn {0} {1} {2}\n", n.x, n.y, -n.z);
                }
            }

            var hasUv = uvs != null && uvs.Length == vertices.Length;
            var hasNormals = normals != null && normals.Length == vertices.Length;

            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var triangles = mesh.GetTriangles(sub);
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var a = triangles[i] + 1;
                    var b = triangles[i + 1] + 1;
                    var c = triangles[i + 2] + 1;

                    // Reverse order so importer FlipWindingOrder restores original winding.
                    if (hasUv && hasNormals)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n", a, c, b);
                    else if (hasUv)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "f {0}/{0} {1}/{1} {2}/{2}\n", a, c, b);
                    else
                        sb.AppendFormat(CultureInfo.InvariantCulture, "f {0} {1} {2}\n", a, c, b);
                }
            }

            return sb.ToString();
        }

        private void AddWarning(string message)
        {
            if (!m_report.warnings.Contains(message))
                m_report.warnings.Add(message);
        }
    }
}
#endif
