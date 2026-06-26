using System;
using System.Collections.Generic;
using System.Linq;
using McpNet.Gateway.Models;
using McpNet.Gateway.Upstream.Rest;
using Xunit;

namespace McpNet.Tests
{
    public class OpenApiSpecParserTests
    {
        private const string PetstoreV3 = @"{
          ""openapi"": ""3.0.1"",
          ""servers"": [{ ""url"": ""https://api.example.com/v1"" }],
          ""paths"": {
            ""/pets"": {
              ""get"": {
                ""operationId"": ""listPets"",
                ""summary"": ""List all pets"",
                ""parameters"": [
                  { ""name"": ""limit"", ""in"": ""query"", ""required"": false, ""schema"": { ""type"": ""integer"" } },
                  { ""name"": ""status"", ""in"": ""query"", ""schema"": { ""type"": ""string"", ""enum"": [""available"",""sold""] } }
                ]
              },
              ""post"": {
                ""operationId"": ""createPet"",
                ""summary"": ""Create a pet"",
                ""requestBody"": {
                  ""required"": true,
                  ""content"": {
                    ""application/json"": {
                      ""schema"": {
                        ""type"": ""object"",
                        ""required"": [""name""],
                        ""properties"": {
                          ""name"": { ""type"": ""string"", ""description"": ""Pet name"" },
                          ""tag"": { ""type"": ""string"" }
                        }
                      }
                    }
                  }
                }
              }
            },
            ""/pets/{petId}"": {
              ""get"": {
                ""operationId"": ""getPet"",
                ""parameters"": [
                  { ""name"": ""petId"", ""in"": ""path"", ""required"": true, ""schema"": { ""type"": ""string"" } }
                ]
              }
            }
          }
        }";

        [Fact]
        public void Parse_V3_ExtractsOperationsAndBaseUrl()
        {
            var model = OpenApiSpecParser.Parse(PetstoreV3, new RestApiConfig());

            Assert.Equal("https://api.example.com/v1", model.BaseUrl);
            Assert.Equal(3, model.Operations.Count);
            Assert.Contains(model.Operations, o => o.ToolName == "listPets" && o.Method == "GET");
            Assert.Contains(model.Operations, o => o.ToolName == "createPet" && o.Method == "POST");
        }

        [Fact]
        public void Parse_V3_QueryParametersBecomeArgumentsWithEnum()
        {
            var model = OpenApiSpecParser.Parse(PetstoreV3, new RestApiConfig());
            var listPets = model.Operations.Single(o => o.ToolName == "listPets");

            var status = listPets.Parameters.Single(p => p.Name == "status");
            Assert.Equal(RestParameterLocation.Query, status.Location);
            Assert.NotNull(status.EnumValues);
            Assert.Contains("available", status.EnumValues!);

            var limit = listPets.Parameters.Single(p => p.Name == "limit");
            Assert.Equal("integer", limit.SchemaType);
        }

        [Fact]
        public void Parse_V3_PathParameterIsRequired()
        {
            var model = OpenApiSpecParser.Parse(PetstoreV3, new RestApiConfig());
            var getPet = model.Operations.Single(o => o.ToolName == "getPet");

            var petId = getPet.Parameters.Single(p => p.Name == "petId");
            Assert.Equal(RestParameterLocation.Path, petId.Location);
            Assert.True(petId.Required);
        }

        [Fact]
        public void Parse_V3_RequestBodyFieldsFlattenedWithRequired()
        {
            var model = OpenApiSpecParser.Parse(PetstoreV3, new RestApiConfig());
            var createPet = model.Operations.Single(o => o.ToolName == "createPet");

            Assert.True(createPet.HasBody);
            var name = createPet.Parameters.Single(p => p.Name == "name");
            Assert.Equal(RestParameterLocation.Body, name.Location);
            Assert.True(name.Required);

            var tag = createPet.Parameters.Single(p => p.Name == "tag");
            Assert.False(tag.Required);
        }

        [Fact]
        public void Parse_IncludeMethods_FiltersOutOtherVerbs()
        {
            var config = new RestApiConfig { IncludeMethods = new List<string> { "get" } };
            var model = OpenApiSpecParser.Parse(PetstoreV3, config);

            Assert.All(model.Operations, o => Assert.Equal("GET", o.Method));
            Assert.DoesNotContain(model.Operations, o => o.ToolName == "createPet");
        }

        [Fact]
        public void Parse_ExcludeOperations_RemovesMatching()
        {
            var config = new RestApiConfig { ExcludeOperations = new List<string> { "createPet" } };
            var model = OpenApiSpecParser.Parse(PetstoreV3, config);

            Assert.DoesNotContain(model.Operations, o => o.ToolName == "createPet");
            Assert.Contains(model.Operations, o => o.ToolName == "listPets");
        }

        [Fact]
        public void Parse_MaxTools_LimitsCount()
        {
            var config = new RestApiConfig { MaxTools = 1 };
            var model = OpenApiSpecParser.Parse(PetstoreV3, config);

            Assert.Single(model.Operations);
        }

        [Fact]
        public void Parse_ResolvesLocalRefs()
        {
            const string spec = @"{
              ""openapi"": ""3.0.0"",
              ""servers"": [{ ""url"": ""https://x.test"" }],
              ""paths"": {
                ""/things"": {
                  ""post"": {
                    ""operationId"": ""addThing"",
                    ""requestBody"": { ""content"": { ""application/json"": {
                      ""schema"": { ""$ref"": ""#/components/schemas/Thing"" } } } }
                  }
                }
              },
              ""components"": { ""schemas"": {
                ""Thing"": { ""type"": ""object"", ""required"": [""id""],
                  ""properties"": { ""id"": { ""type"": ""string"" }, ""qty"": { ""type"": ""integer"" } } }
              } }
            }";

            var model = OpenApiSpecParser.Parse(spec, new RestApiConfig());
            var op = model.Operations.Single();
            Assert.Contains(op.Parameters, p => p.Name == "id" && p.Required);
            Assert.Contains(op.Parameters, p => p.Name == "qty" && p.SchemaType == "integer");
        }

        [Fact]
        public void Parse_Swagger2_DerivesBaseUrlAndBodyParam()
        {
            const string spec = @"{
              ""swagger"": ""2.0"",
              ""host"": ""api.legacy.test"",
              ""basePath"": ""/api"",
              ""schemes"": [""https""],
              ""paths"": {
                ""/users"": {
                  ""post"": {
                    ""operationId"": ""addUser"",
                    ""parameters"": [
                      { ""name"": ""body"", ""in"": ""body"", ""required"": true,
                        ""schema"": { ""type"": ""object"", ""required"": [""email""],
                          ""properties"": { ""email"": { ""type"": ""string"" } } } }
                    ]
                  }
                }
              }
            }";

            var model = OpenApiSpecParser.Parse(spec, new RestApiConfig());
            Assert.Equal("https://api.legacy.test/api", model.BaseUrl);

            var addUser = model.Operations.Single();
            Assert.True(addUser.HasBody);
            Assert.Contains(addUser.Parameters, p => p.Name == "email" && p.Location == RestParameterLocation.Body && p.Required);
        }

        [Fact]
        public void BuildTool_ProducesInputSchemaWithRequiredAndEnum()
        {
            var model = OpenApiSpecParser.Parse(PetstoreV3, new RestApiConfig());
            var listPets = model.Operations.Single(o => o.ToolName == "listPets");

            var tool = RestToolBuilder.BuildTool(listPets);
            Assert.Equal("listPets", tool.Name);
            Assert.NotNull(tool.InputSchema.Properties);
            Assert.True(tool.InputSchema.Properties!.ContainsKey("status"));
            Assert.NotNull(tool.InputSchema.Properties["status"].Enum);
        }

        [Fact]
        public void BuildTool_ArrayParameter_EmitsItemsSchema()
        {
            const string spec = @"{
              ""openapi"": ""3.0.1"",
              ""servers"": [{ ""url"": ""https://api.example.com"" }],
              ""paths"": {
                ""/sso"": {
                  ""post"": {
                    ""operationId"": ""saveSso"",
                    ""requestBody"": {
                      ""required"": true,
                      ""content"": {
                        ""application/json"": {
                          ""schema"": {
                            ""type"": ""object"",
                            ""properties"": {
                              ""endpoints"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                              ""ports"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
                              ""bare"": { ""type"": ""array"" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }";

            var model = OpenApiSpecParser.Parse(spec, new RestApiConfig());
            var op = model.Operations.Single(o => o.ToolName == "saveSso");
            var tool = RestToolBuilder.BuildTool(op);

            var endpoints = tool.InputSchema.Properties!["endpoints"];
            Assert.Equal("array", endpoints.Type);
            Assert.NotNull(endpoints.Items);
            Assert.Equal("string", endpoints.Items!.Type);

            var ports = tool.InputSchema.Properties!["ports"];
            Assert.Equal("array", ports.Type);
            Assert.Equal("integer", ports.Items!.Type);

            // An array with no declared items must still get a valid element schema.
            var bare = tool.InputSchema.Properties!["bare"];
            Assert.Equal("array", bare.Type);
            Assert.NotNull(bare.Items);
            Assert.Equal("string", bare.Items!.Type);
        }
    }
}
