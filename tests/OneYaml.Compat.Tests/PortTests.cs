// Snippets in the style of the YamlDotNet documentation. Apart from the using lines, which name the compat
// namespaces instead of YamlDotNet's, nothing here would change when moving a project over.
using OneYaml.Compat.YamlDotNet.Core;
using OneYaml.Compat.YamlDotNet.Core.Events;
using OneYaml.Compat.YamlDotNet.RepresentationModel;
using OneYaml.Compat.YamlDotNet.Serialization;
using OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;
using Xunit;

namespace PortTests;

public class Order
{
    public string? Receipt { get; set; }

    public DateTime Date { get; set; }

    public Customer? Customer { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    [YamlMember(Alias = "ship-to")]
    public string? ShipTo { get; set; }

    [YamlIgnore]
    public string? Secret { get; set; }
}

public class Customer
{
    public string? Given { get; set; }

    public string? Family { get; set; }
}

public class OrderItem
{
    public string? PartNo { get; set; }

    public decimal Price { get; set; }

    public int Quantity { get; set; }
}

public sealed class Point
{
    public int X { get; set; }

    public int Y { get; set; }
}

public sealed class PointConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Point);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        string[] parts = scalar.Value.Split(',');
        return new Point { X = int.Parse(parts[0]), Y = int.Parse(parts[1]) };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var p = (Point)value!;
        emitter.Emit(new Scalar(null, null, $"{p.X},{p.Y}", ScalarStyle.Any, true, false));
    }
}

public class Holder
{
    public Point? At { get; set; }
}

public class ReadmeSnippets
{
    private const string Document = """
        receipt:    Oz-Ware Purchase Invoice
        date:       2007-08-06
        customer:
            given:   Dorothy
            family:  Gale
        items:
            - part_no:   A4786
              price:     1.47
              quantity:  4
            - part_no:   E1628
              price:     100.27
              quantity:  1
        ship-to: Kansas
        """;

    [Fact]
    public void Deserialize_with_a_naming_convention()
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var order = deserializer.Deserialize<Order>(Document);

        Assert.Equal("Oz-Ware Purchase Invoice", order.Receipt);
        Assert.Equal("Gale", order.Customer!.Family);
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(100.27m, order.Items[1].Price);
        Assert.Equal("Kansas", order.ShipTo);
    }

    [Fact]
    public void Serialize_with_a_naming_convention()
    {
        var order = new Order
        {
            Receipt = "Oz",
            Customer = new Customer { Given = "Dorothy", Family = "Gale" },
            Items = [new OrderItem { PartNo = "A4786", Price = 1.47m, Quantity = 4 }],
            Secret = "hidden",
        };

        ISerializer serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        string yaml = serializer.Serialize(order);

        Assert.Contains("receipt: Oz", yaml, StringComparison.Ordinal);
        Assert.Contains("partNo: A4786", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_through_the_serializer_pair()
    {
        var serializer = new SerializerBuilder().Build();
        var deserializer = new DeserializerBuilder().Build();
        var original = new Dictionary<string, List<int>> { ["a"] = [1, 2], ["b"] = [3] };

        var copy = deserializer.Deserialize<Dictionary<string, List<int>>>(serializer.Serialize(original));

        Assert.Equal(original["a"], copy["a"]);
        Assert.Equal(original["b"], copy["b"]);
    }

    [Fact]
    public void Untyped_values_come_back_as_dictionaries_and_lists()
    {
        var value = new Deserializer().Deserialize<Dictionary<object, object>>("a: 1\nb: [x, y]\n");

        Assert.Equal("1", value["a"]);
        Assert.Equal(2, ((List<object>)value["b"]).Count);
    }

    [Fact]
    public void Custom_type_converter()
    {
        var deserializer = new DeserializerBuilder().WithTypeConverter(new PointConverter()).Build();
        var serializer = new SerializerBuilder().WithTypeConverter(new PointConverter()).Build();

        var holder = deserializer.Deserialize<Holder>("At: 3,4\n");
        Assert.Equal(4, holder.At!.Y);
        Assert.Equal("At: 3,4", serializer.Serialize(holder).Trim());
    }

    [Fact]
    public void Representation_model_stream()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader("""
            - name: one
              tags: [a, b]
            - name: two
            """));

        var root = (YamlSequenceNode)yaml.Documents[0].RootNode;
        var names = new List<string>();
        foreach (YamlMappingNode item in root.Children)
        {
            names.Add(((YamlScalarNode)item.Children[new YamlScalarNode("name")]).Value!);
        }

        Assert.Equal(["one", "two"], names);
        Assert.Equal(11, yaml.Documents[0].AllNodes.Count());
    }

    [Fact]
    public void Syntax_errors_carry_a_position()
    {
        var ex = Assert.ThrowsAny<YamlException>(() => new Deserializer().Deserialize<object>("a: [1, 2"));
        Assert.True(ex.Start.Line >= 1);
    }

    [Fact]
    public void Ignore_unmatched_properties()
    {
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        var customer = deserializer.Deserialize<Customer>("Given: Dorothy\nunknown: 1\n");
        Assert.Equal("Dorothy", customer.Given);
    }
}
