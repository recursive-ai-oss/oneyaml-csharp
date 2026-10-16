using Xunit;

namespace OneYaml.Tests;

// A mapping used as the source of a merge key is still a mapping: a key twice in it is an error, whether the key
// binds to a member, goes to a dictionary or is already decided by an explicit key or an earlier source.
public class MergeSourceDuplicateTests
{
    private static readonly YamlSerializerOptions Merging = new() { MergeKeys = true };

    public static IEnumerable<object[]> Documents() =>
    [
        ["base: &b {name: Ada, name: Bob}\nout:\n  <<: *b\n", 1],
        ["base: &b {name: Ada, age: 1, name: Bob}\nout:\n  age: 5\n  <<: *b\n", 1],
        ["base: &a {name: Ada}\nother: &b {age: 1, age: 2}\nout:\n  <<: [*a, *b]\n", 1],
        ["base: &a {name: Ada}\nother: &b {name: Cy, name: Di}\nout:\n  <<: [*a, *b]\n", 1],
        ["out:\n  <<: {name: Ada, name: Bob}\n", 1],
    ];

    [Theory, MemberData(nameof(Documents))]
    public void A_repeated_key_in_a_merge_source_is_refused_for_a_class(string yaml, int unused)
    {
        Assert.Equal(1, unused);
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder<Person>>(yaml, Merging));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
        Assert.True(ex.Line > 0);
    }

    [Fact]
    public void A_repeated_key_in_a_merge_source_is_refused_for_a_constructor_bound_type()
    {
        const string yaml = "base: &b {replicas: 1, replicas: 2}\nout:\n  <<: *b\n  script: x\n";
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Holder<Config>>(yaml, Merging));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
    }

    [Fact]
    public void A_repeated_key_in_a_merge_source_is_refused_for_a_dictionary_even_when_the_entry_is_taken()
    {
        const string yaml = "out:\n  x: 5\n  <<: {x: 1, x: 2}\n";
        YamlException ex = Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(yaml, Merging));
        Assert.Equal(YamlErrorCategory.DuplicateKey, ex.Category);
    }

    [Fact]
    public void A_repeated_key_in_a_merge_source_is_refused_by_generic_load_and_the_document()
    {
        const string yaml = "base: &b {x: 1, x: 2}\nout:\n  <<: *b\n";

        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlSerializer.Deserialize<object>(yaml, Merging)).Category);
        Assert.Equal(YamlErrorCategory.DuplicateKey, Assert.Throws<YamlException>(() => YamlDocument.Parse(yaml)).Category);
    }

    [Fact]
    public void The_same_key_in_different_sources_still_lets_the_first_win()
    {
        const string yaml = "a: &a {name: Ada, age: 1}\nb: &b {name: Bob, age: 2}\nout:\n  <<: [*a, *b]\n  age: 9\n";

        Holder<Person> h = YamlSerializer.Deserialize<Holder<Person>>(yaml, Merging)!;

        Assert.Equal("Ada", h.Out.Name);
        Assert.Equal(9, h.Out.Age);
    }

    [Fact]
    public void A_merge_source_with_a_merge_key_and_its_own_keys_is_not_a_repeat()
    {
        const string yaml = "a: &a {name: Ada}\nb: &b {<<: *a, age: 3}\nout:\n  <<: *b\n  nickname: x\n";

        Holder<Person> h = YamlSerializer.Deserialize<Holder<Person>>(yaml, Merging)!;

        Assert.Equal("Ada", h.Out.Name);
        Assert.Equal(3, h.Out.Age);
        Assert.Equal("x", h.Out.Nickname);
    }

    public sealed class Holder<T>
    {
        public T Out { get; set; } = default!;
    }
}
