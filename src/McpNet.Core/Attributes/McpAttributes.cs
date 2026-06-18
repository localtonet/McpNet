using System;

namespace McpNet.Core.Attributes
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? Description { get; set; }

        public McpToolAttribute() { }
        public McpToolAttribute(string description) { Description = description; }
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class McpParameterAttribute : Attribute
    {
        public string? Description { get; set; }
        public bool Required { get; set; } = true;
        public string? EnumValues { get; set; }

        public McpParameterAttribute() { }
        public McpParameterAttribute(string description) { Description = description; }
    }
}
