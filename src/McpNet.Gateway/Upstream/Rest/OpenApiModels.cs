using System.Collections.Generic;

namespace McpNet.Gateway.Upstream.Rest
{
    /// <summary>
    /// Where an operation parameter is carried in the HTTP request.
    /// </summary>
    public enum RestParameterLocation
    {
        Path,
        Query,
        Header,
        /// <summary>A top-level field of the JSON request body.</summary>
        Body
    }

    /// <summary>
    /// A single input to a REST operation, projected as one MCP tool argument.
    /// </summary>
    public sealed class RestParameter
    {
        public string Name { get; set; } = string.Empty;
        public RestParameterLocation Location { get; set; }
        public bool Required { get; set; }
        public string? Description { get; set; }

        /// <summary>JSON Schema type: string | number | integer | boolean | object | array.</summary>
        public string SchemaType { get; set; } = "string";

        /// <summary>
        /// Element type when <see cref="SchemaType"/> is <c>array</c> (e.g. <c>string</c>).
        /// Defaults to <c>string</c> when the document does not declare one.
        /// </summary>
        public string? ItemSchemaType { get; set; }

        /// <summary>Allowed values, when the schema declares an enum (string-projected).</summary>
        public List<string>? EnumValues { get; set; }
    }

    /// <summary>
    /// A single REST operation (one HTTP method on one path) parsed from an OpenAPI document.
    /// </summary>
    public sealed class RestOperation
    {
        /// <summary>HTTP method in upper case (GET, POST, …).</summary>
        public string Method { get; set; } = "GET";

        /// <summary>Path template relative to the base URL, e.g. <c>/users/{id}</c>.</summary>
        public string PathTemplate { get; set; } = "/";

        /// <summary>The MCP tool's local name (sanitized, unique within the document).</summary>
        public string ToolName { get; set; } = string.Empty;

        public string? Summary { get; set; }
        public string? Description { get; set; }

        /// <summary>All inputs (path/query/header/body) flattened into tool arguments.</summary>
        public List<RestParameter> Parameters { get; } = new List<RestParameter>();

        /// <summary>True when the operation accepts a JSON request body.</summary>
        public bool HasBody { get; set; }

        /// <summary>
        /// When the request body is a non-object schema (e.g. a raw array or scalar), the body is
        /// taken from a single tool argument with this name instead of being assembled from fields.
        /// </summary>
        public string? RawBodyArgName { get; set; }

        /// <summary>Media type to send the request body as (defaults to application/json).</summary>
        public string BodyContentType { get; set; } = "application/json";
    }

    /// <summary>
    /// The result of parsing an OpenAPI / Swagger document.
    /// </summary>
    public sealed class RestApiModel
    {
        /// <summary>Base URL derived from the document (may be overridden by config).</summary>
        public string? BaseUrl { get; set; }

        public List<RestOperation> Operations { get; } = new List<RestOperation>();
    }
}
