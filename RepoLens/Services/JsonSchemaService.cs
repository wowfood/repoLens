using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevContext.Configuration;
using DevContext.Core;

namespace DevContext.Services;

internal static class JsonSchemaService
{
    private const string Draft = "https://json-schema.org/draft/2020-12/schema";

    private static readonly IReadOnlyDictionary<string, Type> DocumentTypes =
        new SortedDictionary<string, Type>(StringComparer.Ordinal)
        {
            ["analysis"] = typeof(AnalysisSnapshot),
            ["baseline-manifest"] = typeof(BaselineManifest),
            ["build"] = typeof(BuildSnapshot),
            ["configuration"] = typeof(DevContextConfig),
            ["dependencies"] = typeof(DependencyIndex),
            ["git"] = typeof(GitSnapshot),
            ["projects"] = typeof(RepositoryIndex),
            ["symbols"] = typeof(SymbolIndex),
            ["tests"] = typeof(TestSnapshot),
            ["trend-point"] = typeof(RepositoryTrendPoint),
            ["verification"] = typeof(VerificationReport)
        };

    public static IReadOnlyList<string> Documents => DocumentTypes.Keys.ToArray();

    public static JsonObject Build(string? document)
    {
        if (!string.IsNullOrWhiteSpace(document))
        {
            var normalized = document.Trim().ToLowerInvariant();
            if (!DocumentTypes.TryGetValue(normalized, out var type))
            {
                throw new InvalidOperationException(
                    $"Unknown schema document '{document}'. Available documents: {string.Join(", ", Documents)}.");
            }

            return new Generator(normalized).Build(type);
        }

        var definitions = new JsonObject();
        foreach (var (name, type) in DocumentTypes)
        {
            definitions[name] = new Generator(name).Build(type);
        }

        return new JsonObject
        {
            ["$schema"] = Draft,
            ["$id"] = $"urn:repolens:schema:v{SchemaVersions.Current}:catalog",
            ["title"] = "RepoLens persisted document schemas",
            ["description"] =
                $"Current schema {SchemaVersions.Current}; readable persisted schemas " +
                $"{SchemaVersions.MinimumReadable}-{SchemaVersions.Current}.",
            ["$defs"] = definitions
        };
    }

    private sealed class Generator(string document)
    {
        private readonly SortedDictionary<string, JsonObject> _definitions = new(StringComparer.Ordinal);
        private readonly NullabilityInfoContext _nullability = new();

        public JsonObject Build(Type rootType)
        {
            var root = SchemaFor(rootType);
            ConstrainVersion(rootType);
            root["$schema"] = Draft;
            root["$id"] = $"urn:repolens:schema:v{SchemaVersions.Current}:{document}";
            root["title"] = $"RepoLens {document}";
            if (_definitions.Count > 0)
            {
                var definitions = new JsonObject();
                foreach (var (name, schema) in _definitions)
                {
                    definitions[name] = schema;
                }

                root["$defs"] = definitions;
            }

            return root;
        }

        private void ConstrainVersion(Type rootType)
        {
            var definition = _definitions[DefinitionName(rootType)];
            var properties = definition["properties"]?.AsObject();
            if (rootType == typeof(DevContextConfig))
            {
                properties!["version"]!.AsObject()["const"] = ConfigLoader.CurrentVersion;
            }
            else if (rootType.GetProperty(nameof(BaselineManifest.SchemaVersion)) is not null)
            {
                properties!["schemaVersion"]!.AsObject()["const"] = SchemaVersions.Current;
            }
        }

        private JsonObject SchemaFor(Type type)
        {
            var nullableValueType = Nullable.GetUnderlyingType(type);
            if (nullableValueType is not null)
            {
                return AllowNull(SchemaFor(nullableValueType));
            }

            if (type == typeof(string) || type == typeof(char))
            {
                return new JsonObject { ["type"] = "string" };
            }

            if (type == typeof(bool))
            {
                return new JsonObject { ["type"] = "boolean" };
            }

            if (type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong))
            {
                return new JsonObject { ["type"] = "integer" };
            }

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                return new JsonObject { ["type"] = "number" };
            }

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            }

            if (type == typeof(DateOnly))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "date" };
            }

            if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
            {
                return new JsonObject { ["type"] = "string" };
            }

            if (type == typeof(Guid))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            }

            if (type.IsEnum)
            {
                return new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Enum.GetNames(type)
                        .Select(name => (JsonNode?)JsonValue.Create(name))
                        .ToArray())
                };
            }

            if (type == typeof(object) || type.FullName is "System.Text.Json.JsonElement")
            {
                return [];
            }

            if (TryDictionaryValue(type, out var valueType))
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = SchemaFor(valueType)
                };
            }

            if (TryEnumerableElement(type, out var elementType))
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = SchemaFor(elementType)
                };
            }

            var name = DefinitionName(type);
            if (!_definitions.ContainsKey(name))
            {
                _definitions[name] = [];
                _definitions[name] = BuildObject(type);
            }

            return new JsonObject { ["$ref"] = $"#/$defs/{name}" };
        }

        private JsonObject BuildObject(Type type)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.GetMethod is not null
                                            && property.GetIndexParameters().Length == 0
                                            && property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition
                                            != JsonIgnoreCondition.Always)
                         .OrderBy(PropertyName, StringComparer.Ordinal))
            {
                var propertyName = PropertyName(property);
                var propertySchema = SchemaFor(property.PropertyType);
                if (IsNullable(property))
                {
                    propertySchema = AllowNull(propertySchema);
                }

                properties[propertyName] = propertySchema;
                required.Add(propertyName);
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = required
            };
        }

        private bool IsNullable(PropertyInfo property) =>
            Nullable.GetUnderlyingType(property.PropertyType) is not null
            || !property.PropertyType.IsValueType
            && _nullability.Create(property).ReadState == NullabilityState.Nullable;

        private static JsonObject AllowNull(JsonObject schema) => new()
        {
            ["anyOf"] = new JsonArray(
                schema,
                new JsonObject { ["type"] = "null" })
        };

        private static string PropertyName(PropertyInfo property) =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

        private static string DefinitionName(Type type) =>
            type.IsGenericType
                ? type.Name.Split('`')[0] + string.Concat(type.GetGenericArguments().Select(DefinitionName))
                : type.Name.Replace('+', '.');

        private static bool TryEnumerableElement(Type type, out Type elementType)
        {
            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            var enumerable = type.GetInterfaces()
                .Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType
                                             && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            elementType = enumerable?.GetGenericArguments()[0] ?? typeof(object);
            return enumerable is not null;
        }

        private static bool TryDictionaryValue(Type type, out Type valueType)
        {
            var dictionary = type.GetInterfaces()
                .Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType
                                             && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                                             && candidate.GetGenericArguments()[0] == typeof(string));
            valueType = dictionary?.GetGenericArguments()[1] ?? typeof(object);
            return dictionary is not null;
        }
    }
}
