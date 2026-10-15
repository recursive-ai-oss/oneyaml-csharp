using Cm = global::OneYaml.Compat.YamlDotNet.Serialization;
using Rl = global::YamlDotNet.Serialization;

namespace OneYaml.Compat.Tests;

// The two libraries with the same builder calls, so a test says what to configure once.
internal static class Both
{
    internal static (Rl.IDeserializer Real, Cm.IDeserializer Compat) Deserializers(
        Func<Rl.DeserializerBuilder, Rl.DeserializerBuilder>? real = null,
        Func<Cm.DeserializerBuilder, Cm.DeserializerBuilder>? compat = null)
    {
        var r = new Rl.DeserializerBuilder();
        var c = new Cm.DeserializerBuilder();
        return ((real is null ? r : real(r)).Build(), (compat is null ? c : compat(c)).Build());
    }

    internal static (Rl.ISerializer Real, Cm.ISerializer Compat) Serializers(
        Func<Rl.SerializerBuilder, Rl.SerializerBuilder>? real = null,
        Func<Cm.SerializerBuilder, Cm.SerializerBuilder>? compat = null)
    {
        var r = new Rl.SerializerBuilder();
        var c = new Cm.SerializerBuilder();
        return ((real is null ? r : real(r)).Build(), (compat is null ? c : compat(c)).Build());
    }
}
