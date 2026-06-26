using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Upstream.Rest
{
    /// <summary>
    /// Parses an OpenAPI 3.x or Swagger 2.0 JSON document into a flat list of
    /// <see cref="RestOperation"/> that the gateway can virtualize as MCP tools.
    /// The parser is self-contained (System.Text.Json only) and resolves local
    /// <c>$ref</c> references inside the document.
    /// </summary>
    public sealed class OpenApiSpecParser
    {
        private static readonly string[] HttpMethods =
            { "get", "post", "put", "patch", "delete", "head", "options" };

        private readonly JsonElement _root;
        private readonly bool _isSwagger2;

        private OpenApiSpecParser(JsonElement root, bool isSwagger2)
        {
            _root = root;
            _isSwagger2 = isSwagger2;
        }

        /// <summary>
        /// Parses <paramref name="documentJson"/> and applies the include/exclude/method/max-tools
        /// filters from <paramref name="config"/>.
        /// </summary>
        /// <param name="documentJson">The raw OpenAPI / Swagger JSON document.</param>
        /// <param name="config">Filtering and base-URL configuration.</param>
        /// <param name="specBaseUri">
        /// The URL the document was fetched from, used to resolve a relative server URL.
        /// </param>
        public static RestApiModel Parse(string documentJson, RestApiConfig config, Uri? specBaseUri = null)
        {
            if (string.IsNullOrWhiteSpace(documentJson))
                throw new InvalidOperationException("OpenAPI document is empty.");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(documentJson); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "OpenAPI document is not valid JSON. YAML documents are not supported; " +
                    "convert the spec to JSON first. " + ex.Message);
            }

            using (doc)
            {
                var root = doc.RootElement.Clone();
                var isSwagger2 = root.TryGetProperty("swagger", out var sw) &&
                                 sw.ValueKind == JsonValueKind.String &&
                                 sw.GetString()!.StartsWith("2.", StringComparison.Ordinal);

                var parser = new OpenApiSpecParser(root, isSwagger2);
                return parser.Build(config, specBaseUri);
            }
        }

        private RestApiModel Build(RestApiConfig config, Uri? specBaseUri)
        {
            var model = new RestApiModel
            {
                BaseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
                    ? config.BaseUrl!.TrimEnd('/')
                    : ResolveBaseUrl(specBaseUri)
            };

            if (!_root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                return model;

            var methodFilter = config.IncludeMethods
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pathProp in paths.EnumerateObject())
            {
                var pathTemplate = pathProp.Name;
                var pathItem = pathProp.Value;
                if (pathItem.ValueKind != JsonValueKind.Object) continue;

                // Path-level parameters apply to every operation on the path.
                var sharedParams = ReadParameterArray(pathItem, "parameters");

                foreach (var method in HttpMethods)
                {
                    if (methodFilter.Count > 0 && !methodFilter.Contains(method)) continue;
                    if (!pathItem.TryGetProperty(method, out var opEl) || opEl.ValueKind != JsonValueKind.Object)
                        continue;

                    var operation = BuildOperation(method, pathTemplate, opEl, sharedParams, usedNames);
                    if (operation == null) continue;

                    if (!PassesOperationFilter(operation, config)) continue;

                    model.Operations.Add(operation);
                    if (config.MaxTools > 0 && model.Operations.Count >= config.MaxTools)
                        return model;
                }
            }

            return model;
        }

        private static bool PassesOperationFilter(RestOperation op, RestApiConfig config)
        {
            // "METHOD /path" and operationId are both matchable identifiers.
            string Identifier() => $"{op.Method} {op.PathTemplate}";
            bool Matches(string token) =>
                op.ToolName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                Identifier().Contains(token, StringComparison.OrdinalIgnoreCase);

            if (config.IncludeOperations.Count > 0 &&
                !config.IncludeOperations.Any(t => !string.IsNullOrWhiteSpace(t) && Matches(t)))
                return false;

            if (config.ExcludeOperations.Count > 0 &&
                config.ExcludeOperations.Any(t => !string.IsNullOrWhiteSpace(t) && Matches(t)))
                return false;

            return true;
        }

        private RestOperation? BuildOperation(
            string method, string pathTemplate, JsonElement opEl,
            List<JsonElement> sharedParams, HashSet<string> usedNames)
        {
            var op = new RestOperation
            {
                Method = method.ToUpperInvariant(),
                PathTemplate = pathTemplate,
                Summary = GetString(opEl, "summary"),
                Description = GetString(opEl, "description")
            };

            op.ToolName = MakeUniqueToolName(opEl, method, pathTemplate, usedNames);

            // Merge path-level and operation-level parameters (operation wins on name+in clash).
            var paramElements = new List<JsonElement>(sharedParams);
            paramElements.AddRange(ReadParameterArray(opEl, "parameters"));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paramElements)
            {
                var resolved = ResolveRef(raw);
                var name = GetString(resolved, "name");
                var location = GetString(resolved, "in");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(location)) continue;

                // Swagger 2.0 body parameter: schema lives on the parameter itself.
                if (_isSwagger2 && string.Equals(location, "body", StringComparison.OrdinalIgnoreCase))
                {
                    if (resolved.TryGetProperty("schema", out var bodySchema))
                        ApplyRequestBody(op, ResolveRef(bodySchema), GetBool(resolved, "required"));
                    continue;
                }

                if (!seen.Add(name + "|" + location)) continue;

                var loc = ParseLocation(location);
                if (loc == null) continue; // ignore cookie / formData etc.

                op.Parameters.Add(new RestParameter
                {
                    Name = name!,
                    Location = loc.Value,
                    Required = GetBool(resolved, "required") ||
                               loc.Value == RestParameterLocation.Path, // path params are always required
                    Description = GetString(resolved, "description"),
                    SchemaType = ReadParameterType(resolved),
                    ItemSchemaType = ReadItemType(GetSchemaForParameter(resolved)),
                    EnumValues = ReadEnum(GetSchemaForParameter(resolved))
                });
            }

            // OpenAPI 3.x request body.
            if (!_isSwagger2 && opEl.TryGetProperty("requestBody", out var rbEl))
            {
                var rb = ResolveRef(rbEl);
                var required = GetBool(rb, "required");
                var (contentType, schema) = PickJsonContent(rb);
                if (schema.HasValue)
                {
                    op.BodyContentType = contentType;
                    ApplyRequestBody(op, ResolveRef(schema.Value), required);
                }
            }

            return op;
        }

        /// <summary>
        /// Flattens a request-body schema into tool arguments. Object schemas contribute one
        /// argument per top-level property; non-object schemas become a single "body" argument.
        /// </summary>
        private void ApplyRequestBody(RestOperation op, JsonElement schema, bool bodyRequired)
        {
            op.HasBody = true;

            var type = ReadType(schema);
            if (type == "object" || schema.TryGetProperty("properties", out _))
            {
                var requiredFields = ReadStringArray(schema, "required");
                if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        var propSchema = ResolveRef(prop.Value);
                        // Avoid clobbering an existing path/query/header argument of the same name.
                        if (op.Parameters.Any(p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        op.Parameters.Add(new RestParameter
                        {
                            Name = prop.Name,
                            Location = RestParameterLocation.Body,
                            Required = requiredFields.Contains(prop.Name),
                            Description = GetString(propSchema, "description"),
                            SchemaType = ReadType(propSchema),
                            ItemSchemaType = ReadItemType(propSchema),
                            EnumValues = ReadEnum(propSchema)
                        });
                    }
                    return;
                }
            }

            // Non-object (array/scalar) body → single passthrough argument.
            op.RawBodyArgName = "body";
            op.Parameters.Add(new RestParameter
            {
                Name = "body",
                Location = RestParameterLocation.Body,
                Required = bodyRequired,
                Description = "Raw request body.",
                SchemaType = string.IsNullOrEmpty(type) ? "object" : type,
                ItemSchemaType = ReadItemType(schema)
            });
        }

        // ── Base URL resolution ───────────────────────────────────────────────

        private string? ResolveBaseUrl(Uri? specBaseUri)
        {
            if (_isSwagger2)
            {
                var host = GetString(_root, "host");
                var basePath = GetString(_root, "basePath") ?? string.Empty;
                var scheme = "https";
                if (_root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array)
                {
                    var first = schemes.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String) scheme = first.GetString()!;
                }
                if (!string.IsNullOrEmpty(host))
                    return $"{scheme}://{host}{basePath}".TrimEnd('/');
                // Fall back to the document's own origin.
                return specBaseUri != null ? $"{specBaseUri.Scheme}://{specBaseUri.Authority}{basePath}".TrimEnd('/') : null;
            }

            // OpenAPI 3.x
            if (_root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            {
                var first = servers.EnumerateArray().FirstOrDefault();
                var url = GetString(first, "url");
                if (!string.IsNullOrEmpty(url))
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
                        return abs.ToString().TrimEnd('/');
                    // Relative server URL → resolve against the spec's own location.
                    if (specBaseUri != null && Uri.TryCreate(specBaseUri, url, out var combined))
                        return combined.ToString().TrimEnd('/');
                }
            }

            return specBaseUri != null ? $"{specBaseUri.Scheme}://{specBaseUri.Authority}" : null;
        }

        // ── $ref resolution ───────────────────────────────────────────────────

        /// <summary>Follows a local <c>$ref</c> chain (e.g. <c>#/components/schemas/X</c>).</summary>
        private JsonElement ResolveRef(JsonElement element)
        {
            var guard = 0;
            while (element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty("$ref", out var refEl) &&
                   refEl.ValueKind == JsonValueKind.String &&
                   guard++ < 32)
            {
                var pointer = refEl.GetString()!;
                if (!pointer.StartsWith("#/", StringComparison.Ordinal))
                    break; // external refs are unsupported; return as-is

                var resolved = ResolvePointer(pointer.Substring(2));
                if (resolved == null) break;
                element = resolved.Value;
            }
            return element;
        }

        private JsonElement? ResolvePointer(string pointer)
        {
            var current = _root;
            foreach (var rawSegment in pointer.Split('/'))
            {
                var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment, out var next))
                    return null;
                current = next;
            }
            return current;
        }

        // ── Small JSON helpers ────────────────────────────────────────────────

        private List<JsonElement> ReadParameterArray(JsonElement parent, string name)
        {
            var list = new List<JsonElement>();
            if (parent.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    list.Add(item);
            return list;
        }

        private (string ContentType, JsonElement? Schema) PickJsonContent(JsonElement requestBody)
        {
            if (!requestBody.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
                return ("application/json", null);

            // Prefer application/json, otherwise the first declared media type.
            foreach (var media in content.EnumerateObject())
            {
                if (media.Name.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                    media.Value.TryGetProperty("schema", out var jsonSchema))
                    return (media.Name, jsonSchema);
            }
            var firstMedia = content.EnumerateObject().FirstOrDefault();
            if (firstMedia.Value.ValueKind == JsonValueKind.Object &&
                firstMedia.Value.TryGetProperty("schema", out var schema))
                return (firstMedia.Name, schema);

            return ("application/json", null);
        }

        private JsonElement GetSchemaForParameter(JsonElement param)
        {
            // OpenAPI 3.x: schema is nested; Swagger 2.0: type/enum live on the parameter.
            if (param.TryGetProperty("schema", out var schema))
                return ResolveRef(schema);
            return param;
        }

        private string ReadParameterType(JsonElement param)
            => ReadType(GetSchemaForParameter(param));

        /// <summary>
        /// Returns the element type of an <c>array</c> schema (from <c>items.type</c>), or null when
        /// the schema is not an array. Defaults to <c>string</c> when items declare no concrete type.
        /// </summary>
        private string? ReadItemType(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return null;
            if (!string.Equals(ReadType(schema), "array", StringComparison.Ordinal)) return null;
            if (schema.TryGetProperty("items", out var items))
            {
                var resolved = ResolveRef(items);
                var t = ReadType(resolved);
                return string.IsNullOrEmpty(t) ? "string" : t;
            }
            return "string";
        }

        private string ReadType(JsonElement schema)
        {
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString()!;
                // OpenAPI 3.1 allows type arrays; pick the first non-null entry.
                if (schema.TryGetProperty("type", out var ta) && ta.ValueKind == JsonValueKind.Array)
                {
                    var first = ta.EnumerateArray()
                        .FirstOrDefault(e => e.ValueKind == JsonValueKind.String && e.GetString() != "null");
                    if (first.ValueKind == JsonValueKind.String) return first.GetString()!;
                }
                if (schema.TryGetProperty("properties", out _)) return "object";
                if (schema.TryGetProperty("items", out _)) return "array";
            }
            return "string";
        }

        private List<string>? ReadEnum(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object ||
                !schema.TryGetProperty("enum", out var en) || en.ValueKind != JsonValueKind.Array)
                return null;

            var values = new List<string>();
            foreach (var item in en.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                if (!string.IsNullOrEmpty(s)) values.Add(s!);
            }
            return values.Count > 0 ? values : null;
        }

        private HashSet<string> ReadStringArray(JsonElement parent, string name)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (parent.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        set.Add(item.GetString()!);
            return set;
        }

        private static RestParameterLocation? ParseLocation(string? location) => location?.ToLowerInvariant() switch
        {
            "path" => RestParameterLocation.Path,
            "query" => RestParameterLocation.Query,
            "header" => RestParameterLocation.Header,
            _ => null
        };

        private static string? GetString(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object &&
               el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static bool GetBool(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object &&
               el.TryGetProperty(name, out var v) &&
               (v.ValueKind == JsonValueKind.True ||
                (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

        // ── Tool-name generation ──────────────────────────────────────────────

        private string MakeUniqueToolName(JsonElement opEl, string method, string pathTemplate, HashSet<string> usedNames)
        {
            var baseName = GetString(opEl, "operationId");
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = method + "_" + pathTemplate;

            var sanitized = Sanitize(baseName!);
            if (string.IsNullOrEmpty(sanitized)) sanitized = method.ToLowerInvariant();

            var candidate = sanitized;
            var suffix = 2;
            while (!usedNames.Add(candidate))
                candidate = sanitized + "_" + suffix++.ToString(CultureInfo.InvariantCulture);
            return candidate;
        }

        private static string Sanitize(string value)
        {
            var sb = new StringBuilder(value.Length);
            var lastUnderscore = false;
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    lastUnderscore = false;
                }
                else if (!lastUnderscore)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }
            return sb.ToString().Trim('_');
        }
    }
}
