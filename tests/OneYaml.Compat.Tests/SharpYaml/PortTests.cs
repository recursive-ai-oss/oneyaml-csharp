// The code below is the quick start of the SharpYaml 3 package as its users wrote it. The only change is the using
// directive: `using SharpYaml;` became `using OneYaml.Compat.SharpYaml;` and `using SharpYaml.Serialization;` became
// `using OneYaml.Compat.SharpYaml.Serialization;`. It is in a namespace of its own, as a program's code is.
using System.Text.Json;
using OneYaml.Compat.SharpYaml;
using OneYaml.Compat.SharpYaml.Serialization;
using Xunit;

namespace PortedFromSharpYaml;

public class Person
{
    public string Name { get; set; } = "";

    public int Age { get; set; }
}

public class MyConfig
{
    public string? Title { get; set; }

    public int? Retries { get; set; }

    public List<string> Hosts { get; set; } = [];

    [YamlPropertyName("log-level")]
    public string LogLevel { get; set; } = "info";

    [YamlIgnore]
    public string Secret { get; set; } = "";
}

// Declared the way SharpYaml's generator wants it. The package has no generator, so the class only has to compile.
[YamlSerializable(typeof(MyConfig))]
#pragma warning disable CA1852 // The declaration is the README's, unchanged.
internal partial class MyYamlContext : YamlSerializerContext
{
}

public class QuickStartPortTests
{
    [Fact]
    public void Serialize_and_deserialize()
    {
        // Serialize
        var yaml = YamlSerializer.Serialize(new { Name = "Ada", Age = 37 });

        // Deserialize
        var person = YamlSerializer.Deserialize<Person>(yaml);

        Assert.Equal("Name: Ada\nAge: 37\n", yaml);
        Assert.Equal("Ada", person!.Name);
        Assert.Equal(37, person.Age);
    }

    [Fact]
    public void Options()
    {
        var config = new MyConfig { Title = "t", Retries = null, Hosts = ["a", "b"], Secret = "s" };

        var options = new YamlSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            IndentSize = 4,
            DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
        };

        var yaml = YamlSerializer.Serialize(config, options);
        var model = YamlSerializer.Deserialize<MyConfig>(yaml, options);

        Assert.Equal("title: t\nhosts:\n    - a\n    - b\nlog-level: info\n", yaml);
        Assert.Equal(["a", "b"], model!.Hosts);
        Assert.Null(model.Retries);
        Assert.Equal("", model.Secret);
    }

    [Fact]
    public void Defaults_keep_the_member_names()
    {
        // "By default, PropertyNamingPolicy is null, meaning CLR member names are used as-is."
        var yaml = YamlSerializer.Serialize(new MyConfig { Title = "t" });
        Assert.StartsWith("Title: t\n", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_carry_a_position()
    {
        var error = Assert.Throws<SyntaxErrorException>(() => YamlSerializer.Deserialize<Person>("Name: a: b"));
        Assert.Equal(0, error.Start.Line);
        Assert.Equal(error.Start.Index, error.Start.Column);
        Assert.IsAssignableFrom<YamlException>(error);
    }

    [Fact]
    public void Stream_and_text_reader_and_writer()
    {
        using var stream = new MemoryStream();
        YamlSerializer.Serialize(stream, new Person { Name = "Bo", Age = 2 });
        stream.Position = 0;
        var back = YamlSerializer.Deserialize<Person>(stream);
        Assert.Equal("Bo", back!.Name);

        var writer = new StringWriter();
        YamlSerializer.Serialize(writer, new Person { Name = "Cy", Age = 3 });
        var again = YamlSerializer.Deserialize<Person>(new StringReader(writer.ToString()));
        Assert.Equal(3, again!.Age);

        Assert.True(YamlSerializer.TryDeserialize<Person>("Name: Di\nAge: 4\n", out var di));
        Assert.Equal("Di", di!.Name);
        Assert.False(YamlSerializer.TryDeserialize<Person>("Name: [", out _));
    }
}
