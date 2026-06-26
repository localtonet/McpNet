using System.Collections.Generic;
using System.Linq;
using McpNet.Core.Capabilities;

namespace McpNet.Gateway.Upstream.Rest
{
    /// <summary>
    /// Converts parsed <see cref="RestOperation"/>s into MCP <see cref="McpTool"/> definitions.
    /// The generated <c>inputSchema</c> mirrors the operation's flattened parameters so MCP
    /// clients (and the gateway's own argument validator) see proper types and descriptions.
    /// </summary>
    public static class RestToolBuilder
    {
        public static McpTool BuildTool(RestOperation op)
        {
            var properties = new Dictionary<string, McpSchemaProperty>();
            var required = new List<string>();

            foreach (var p in op.Parameters)
            {
                // Last writer wins on the rare name clash; parameters were de-duplicated upstream.
                var type = NormalizeType(p.SchemaType);
                var prop = new McpSchemaProperty
                {
                    Type = type,
                    Description = BuildParamDescription(p),
                    Enum = p.EnumValues
                };
                // JSON Schema requires array properties to declare an element schema; strict MCP
                // clients (e.g. VS Code) reject the whole tool otherwise.
                if (type == "array")
                    prop.Items = BuildArrayItems(p.ItemSchemaType);
                properties[p.Name] = prop;
                if (p.Required && !required.Contains(p.Name))
                    required.Add(p.Name);
            }

            return new McpTool
            {
                Name = op.ToolName,
                Description = BuildToolDescription(op),
                InputSchema = new McpToolInputSchema
                {
                    Type = "object",
                    Properties = properties.Count > 0 ? properties : null,
                    Required = required.Count > 0 ? required : null
                }
            };
        }

        private static string NormalizeType(string schemaType) => schemaType switch
        {
            "integer" or "number" or "boolean" or "object" or "array" or "string" => schemaType,
            _ => "string"
        };

        /// <summary>
        /// Builds a valid element schema for an array property. When the element is itself an array,
        /// a nested string-element schema is supplied so the result is always JSON-Schema valid.
        /// </summary>
        private static McpSchemaProperty BuildArrayItems(string? itemSchemaType)
        {
            var elementType = NormalizeType(itemSchemaType ?? "string");
            var items = new McpSchemaProperty { Type = elementType };
            if (elementType == "array")
                items.Items = new McpSchemaProperty { Type = "string" };
            return items;
        }

        private static string BuildToolDescription(RestOperation op)
        {
            // Compose the richest description available, always prefixed with the HTTP signature
            // so the model can reason about side effects (GET vs DELETE, etc.).
            var text = op.Summary;
            if (string.IsNullOrWhiteSpace(text)) text = op.Description;
            var signature = $"{op.Method} {op.PathTemplate}";
            return string.IsNullOrWhiteSpace(text) ? signature : $"{text} ({signature})";
        }

        private static string? BuildParamDescription(RestParameter p)
        {
            var where = p.Location switch
            {
                RestParameterLocation.Path => "path",
                RestParameterLocation.Query => "query",
                RestParameterLocation.Header => "header",
                _ => "body"
            };
            return string.IsNullOrWhiteSpace(p.Description)
                ? $"[{where}]"
                : $"[{where}] {p.Description}";
        }

        public static List<McpTool> BuildTools(IEnumerable<RestOperation> ops)
            => ops.Select(BuildTool).ToList();
    }
}
