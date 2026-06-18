using McpNet.Cli;
using Xunit;

namespace McpNet.Tests
{
    public class ArgParserTests
    {
        [Fact]
        public void Parse_Command_Only()
        {
            var parsed = ArgParser.Parse(new[] { "servers" });
            Assert.Equal("servers", parsed.Command);
            Assert.Empty(parsed.Positional);
            Assert.Empty(parsed.Flags);
        }

        [Fact]
        public void Parse_Flag_WithValue()
        {
            var parsed = ArgParser.Parse(new[] { "register", "--name", "context7" });
            Assert.Equal("register", parsed.Command);
            Assert.Equal("context7", parsed.Get("name"));
        }

        [Fact]
        public void Parse_BooleanFlag_NoValue()
        {
            var parsed = ArgParser.Parse(new[] { "tools", "--help" });
            Assert.Equal("tools", parsed.Command);
            Assert.True(parsed.Flags.ContainsKey("help"));
            Assert.Null(parsed.Get("help"));
        }

        [Fact]
        public void Parse_Positionals_AfterCommand()
        {
            var parsed = ArgParser.Parse(new[] { "group", "create", "myname" });
            Assert.Equal("group", parsed.Command);
            Assert.Equal(new[] { "create", "myname" }, parsed.Positional.ToArray());
        }

        [Fact]
        public void Parse_MixedFlagsAndPositionals()
        {
            var parsed = ArgParser.Parse(new[] { "register", "ctx", "--url", "http://x", "--enabled" });
            Assert.Equal("register", parsed.Command);
            Assert.Equal("ctx", parsed.Positional[0]);
            Assert.Equal("http://x", parsed.Get("url"));
            Assert.True(parsed.Flags.ContainsKey("enabled"));
        }

        [Fact]
        public void Parse_ShortFlag_IsBoolean()
        {
            var parsed = ArgParser.Parse(new[] { "tools", "-h" });
            Assert.True(parsed.Flags.ContainsKey("h"));
        }

        [Fact]
        public void Parse_Flag_IsCaseInsensitive()
        {
            var parsed = ArgParser.Parse(new[] { "register", "--Name", "x" });
            Assert.Equal("x", parsed.Get("name"));
        }

        [Fact]
        public void Parse_Empty_ReturnsNullCommand()
        {
            var parsed = ArgParser.Parse(new string[0]);
            Assert.Null(parsed.Command);
        }

        [Fact]
        public void Parse_FlagFollowedByFlag_FirstIsBoolean()
        {
            var parsed = ArgParser.Parse(new[] { "x", "--a", "--b", "val" });
            Assert.True(parsed.Flags.ContainsKey("a"));
            Assert.Null(parsed.Get("a"));
            Assert.Equal("val", parsed.Get("b"));
        }
    }
}
