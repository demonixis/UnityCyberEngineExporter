using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Demonixis.UnityJSONSceneExporter
{
    internal static class ExportUtils
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static string SanitizeIdentifier(string value, string fallback = "item")
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            if (sb.Length == 0)
                sb.Append(fallback);

            if (char.IsDigit(sb[0]))
                sb.Insert(0, '_');

            return sb.ToString();
        }

        public static string ToSnakeCase(string value, string fallback = "item")
        {
            var clean = SanitizeIdentifier(value, fallback);
            var sb = new StringBuilder(clean.Length + 8);
            for (var i = 0; i < clean.Length; i++)
            {
                var c = clean[i];
                if (char.IsUpper(c) && i > 0 && clean[i - 1] != '_')
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        public static string ToFloatLiteral(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return "0.0f";

            return value.ToString("0.0######", Invariant) + "f";
        }

        public static string ToVec2Literal(Vector2 value)
        {
            return string.Format(Invariant, "glm::vec2({0}, {1})", ToFloatLiteral(value.x), ToFloatLiteral(value.y));
        }

        public static string ToVec3Literal(Vector3 value)
        {
            return string.Format(Invariant, "glm::vec3({0}, {1}, {2})", ToFloatLiteral(value.x), ToFloatLiteral(value.y), ToFloatLiteral(value.z));
        }

        public static string ToVec4Literal(Vector4 value)
        {
            return string.Format(Invariant, "glm::vec4({0}, {1}, {2}, {3})", ToFloatLiteral(value.x), ToFloatLiteral(value.y), ToFloatLiteral(value.z), ToFloatLiteral(value.w));
        }

        public static string ToQuatLiteral(Quaternion value)
        {
            // Unity quaternion is (x,y,z,w), glm::quat ctor order is (w,x,y,z).
            return string.Format(Invariant, "glm::quat({0}, {1}, {2}, {3})", ToFloatLiteral(value.w), ToFloatLiteral(value.x), ToFloatLiteral(value.y), ToFloatLiteral(value.z));
        }

        public static float[] ToFloat2(Vector2 v)
        {
            return new[] { v.x, v.y };
        }

        public static float[] ToFloat3(Vector3 v)
        {
            return new[] { v.x, v.y, v.z };
        }

        public static float[] ToFloat4(Vector4 v)
        {
            return new[] { v.x, v.y, v.z, v.w };
        }

        public static float[] ToFloat4(Color c)
        {
            return new[] { c.r, c.g, c.b, c.a };
        }

        public static float[] ToFloat3(Color c)
        {
            return new[] { c.r, c.g, c.b };
        }

        public static string GetTransformPath(Transform tr)
        {
            if (tr == null)
                return string.Empty;

            var sb = new StringBuilder(tr.name);
            while (tr.parent != null)
            {
                tr = tr.parent;
                sb.Insert(0, '/');
                sb.Insert(0, tr.name);
            }

            return sb.ToString();
        }

        public static string BuildStableId(GameObject go)
        {
            if (go == null)
                return Guid.NewGuid().ToString("N");

            var path = GetTransformPath(go.transform);
            var raw = path;

#if UNITY_EDITOR
            var global = GlobalObjectId.GetGlobalObjectIdSlow(go);
            raw = global.ToString() + "|" + path;
#endif

            return "go_" + ComputeSha1(raw).Substring(0, 16);
        }

        public static string ComputeSha1(string text)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
                var hash = sha1.ComputeHash(bytes);
                return ToHex(hash);
            }
        }

        public static string ComputeSha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                return ToHex(hash);
            }
        }

        public static string ComputeSha256File(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return ComputeSha256(bytes);
        }

        public static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        public static string EscapeCppString(string value)
        {
            if (value == null)
                return "";

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2", Invariant));
            return sb.ToString();
        }
    }
}
