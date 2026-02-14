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
    internal sealed class CustomComponentGenerator
    {
        private readonly Dictionary<string, CustomComponentSchemaData> m_schemas =
            new Dictionary<string, CustomComponentSchemaData>(StringComparer.Ordinal);

        public IReadOnlyCollection<CustomComponentSchemaData> Schemas => m_schemas.Values;

        public void EnsureSchema(CustomComponentSchemaData schema)
        {
            if (schema == null || string.IsNullOrWhiteSpace(schema.sourceType))
                return;

            if (!m_schemas.TryGetValue(schema.sourceType, out var existing))
            {
                m_schemas[schema.sourceType] = schema;
                return;
            }

            foreach (var field in schema.fields)
            {
                if (existing.fields.All(f => f.name != field.name))
                    existing.fields.Add(field);
            }
        }

        public void Collect(MonoBehaviour behaviour, EntityExportData entity, ExportReport report)
        {
            if (behaviour == null || entity == null)
                return;

            var sourceType = behaviour.GetType().FullName ?? behaviour.GetType().Name;
            var generatedType = "Unity" + ExportUtils.SanitizeIdentifier(behaviour.GetType().Name) + "Component";
            var headerName = "unity_" + ExportUtils.ToSnakeCase(behaviour.GetType().Name) + "_component.hpp";

            if (!m_schemas.TryGetValue(sourceType, out var schema))
            {
                schema = new CustomComponentSchemaData
                {
                    sourceType = sourceType,
                    generatedType = generatedType,
                    headerFileName = headerName,
                    fields = new List<CustomFieldSchemaData>()
                };
                m_schemas[sourceType] = schema;
            }

            var instance = new CustomComponentInstanceData
            {
                sourceType = sourceType,
                generatedType = generatedType,
                fields = new List<CustomFieldValueData>()
            };

            var serialized = new SerializedObject(behaviour);
            var iterator = serialized.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (iterator.name == "m_Script" || iterator.depth != 0)
                    continue;

                if (TryBuildField(iterator, out var field))
                {
                    instance.fields.Add(field);

                    if (schema.fields.All(f => f.name != field.name))
                    {
                        schema.fields.Add(new CustomFieldSchemaData
                        {
                            name = field.name,
                            cppType = field.cppType,
                            defaultValueCpp = field.valueCpp,
                            serializedPropertyType = field.serializedPropertyType
                        });
                    }
                }
                else
                {
                    var warning = string.Format(CultureInfo.InvariantCulture,
                        "Custom component field skipped: {0}.{1} ({2})",
                        sourceType,
                        iterator.name,
                        iterator.propertyType);
                    if (!report.warnings.Contains(warning))
                        report.warnings.Add(warning);
                }
            }

            if (instance.fields.Count > 0)
                entity.customComponents.Add(instance);
        }

        public List<string> WriteHeaders(string bundleRoot, ExportManifest manifest)
        {
            var generated = new List<string>();
            var generatedDir = Path.Combine(bundleRoot, "game", "components", "generated");
            Directory.CreateDirectory(generatedDir);

            var sortedSchemas = m_schemas.Values.OrderBy(s => s.generatedType, StringComparer.Ordinal).ToList();
            foreach (var schema in sortedSchemas)
            {
                var path = Path.Combine(generatedDir, schema.headerFileName);
                File.WriteAllText(path, BuildHeader(schema));
                generated.Add(ExportUtils.NormalizeRelativePath(Path.Combine("game", "components", "generated", schema.headerFileName)));
            }

            var aggregatorPath = Path.Combine(bundleRoot, "game", "components", "generated_components.hpp");
            File.WriteAllText(aggregatorPath, BuildAggregator(sortedSchemas));
            generated.Add("game/components/generated_components.hpp");

            manifest.generatedComponentHeaders.AddRange(generated);
            return generated;
        }

        private static string BuildHeader(CustomComponentSchemaData schema)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <glm/glm.hpp>");
            sb.AppendLine("#include <string>");
            sb.AppendLine("#include <vector>");
            sb.AppendLine();
            sb.AppendLine("struct " + schema.generatedType);
            sb.AppendLine("{");
            foreach (var field in schema.fields.OrderBy(f => f.name, StringComparer.Ordinal))
            {
                sb.AppendLine("    " + field.cppType + " " + ExportUtils.SanitizeIdentifier(field.name) + " = " + field.defaultValueCpp + ";");
            }
            sb.AppendLine("};");
            return sb.ToString();
        }

        private static string BuildAggregator(IEnumerable<CustomComponentSchemaData> schemas)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            foreach (var schema in schemas)
            {
                sb.AppendLine("#include \"generated/" + schema.headerFileName + "\"");
            }
            return sb.ToString();
        }

        private static bool TryBuildField(SerializedProperty property, out CustomFieldValueData field)
        {
            field = null;
            if (property == null)
                return false;

            var name = ExportUtils.SanitizeIdentifier(property.name, "field");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "bool",
                        valueCpp = property.boolValue ? "true" : "false",
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Integer:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "int",
                        valueCpp = property.intValue.ToString(CultureInfo.InvariantCulture),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Float:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "float",
                        valueCpp = ExportUtils.ToFloatLiteral(property.floatValue),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.String:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "std::string",
                        valueCpp = "\"" + ExportUtils.EscapeCppString(property.stringValue) + "\"",
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "glm::vec4",
                        valueCpp = ExportUtils.ToVec4Literal(new Vector4(c.r, c.g, c.b, c.a)),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Vector2:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "glm::vec2",
                        valueCpp = ExportUtils.ToVec2Literal(property.vector2Value),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Vector3:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "glm::vec3",
                        valueCpp = ExportUtils.ToVec3Literal(property.vector3Value),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Vector4:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "glm::vec4",
                        valueCpp = ExportUtils.ToVec4Literal(property.vector4Value),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.Enum:
                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "int",
                        valueCpp = property.enumValueIndex.ToString(CultureInfo.InvariantCulture),
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                case SerializedPropertyType.ObjectReference:
                    var assetPath = string.Empty;
                    if (property.objectReferenceValue != null)
                        assetPath = AssetDatabase.GetAssetPath(property.objectReferenceValue);

                    field = new CustomFieldValueData
                    {
                        name = name,
                        cppType = "std::string",
                        valueCpp = "\"" + ExportUtils.EscapeCppString(assetPath ?? string.Empty) + "\"",
                        serializedPropertyType = property.propertyType.ToString()
                    };
                    return true;

                default:
                    if (property.isArray && property.propertyType == SerializedPropertyType.Generic)
                        return TryBuildArrayField(property, out field);
                    return false;
            }
        }

        private static bool TryBuildArrayField(SerializedProperty property, out CustomFieldValueData field)
        {
            field = null;
            if (!property.isArray)
                return false;

            var values = new List<string>();
            string innerType = null;
            for (var i = 0; i < property.arraySize; i++)
            {
                var item = property.GetArrayElementAtIndex(i);
                if (item == null)
                    continue;

                if (!TryBuildField(item, out var itemField))
                    return false;

                innerType = itemField.cppType;
                values.Add(itemField.valueCpp);
            }

            if (string.IsNullOrWhiteSpace(innerType))
                innerType = "int";

            field = new CustomFieldValueData
            {
                name = ExportUtils.SanitizeIdentifier(property.name, "array_field"),
                cppType = "std::vector<" + innerType + ">",
                valueCpp = "{" + string.Join(", ", values) + "}",
                serializedPropertyType = property.propertyType.ToString()
            };
            return true;
        }
    }
}
#endif
