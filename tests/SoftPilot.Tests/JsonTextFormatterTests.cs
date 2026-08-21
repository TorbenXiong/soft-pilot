using System.Text.Json;
using SoftPilot.Application;

namespace SoftPilot.Tests;

[TestClass]
public sealed class JsonTextFormatterTests
{
    [TestMethod]
    public void Beautify_FormatsNestedJsonAndKeepsUnicodeReadable()
    {
        var actual = JsonTextFormatter.Beautify("{\"name\":\"工具箱\",\"items\":[1,true,null]}");

        StringAssert.Contains(actual, "\"name\": \"工具箱\"");
        StringAssert.Contains(actual, "\"items\": [");
        StringAssert.Contains(actual, "    1");
    }

    [TestMethod]
    public void Minify_RemovesInsignificantWhitespace()
    {
        var actual = JsonTextFormatter.Minify("""
            {
              "name": "SoftPilot",
              "enabled": true
            }
            """);

        Assert.AreEqual("{\"name\":\"SoftPilot\",\"enabled\":true}", actual);
    }

    [TestMethod]
    public void Beautify_AcceptsScalarJson()
    {
        Assert.AreEqual("42", JsonTextFormatter.Beautify("42"));
        Assert.AreEqual("\"text\"", JsonTextFormatter.Beautify("\"text\""));
    }

    [TestMethod]
    public void Beautify_RejectsInvalidJsonWithPosition()
    {
        var exception = Assert.Throws<JsonException>(() => JsonTextFormatter.Beautify("{\n  \"name\":\n}"));

        Assert.IsNotNull(exception.LineNumber);
        Assert.IsNotNull(exception.BytePositionInLine);
    }

    [TestMethod]
    public void Beautify_RejectsEmptyText()
    {
        Assert.Throws<JsonException>(() => JsonTextFormatter.Beautify("   "));
    }
}
