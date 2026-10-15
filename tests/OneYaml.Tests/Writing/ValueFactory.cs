using System.Numerics;
using OneYaml.Tests.Emitting;
using OneYaml.Tests.Generated;

namespace OneYaml.Tests.Writing;

/// <summary>Random values over the test models, from a seed so a failure can be replayed.</summary>
internal sealed class ValueFactory
{
    private readonly Random _r;
    private readonly HostileStrings _strings;

    public ValueFactory(int seed)
    {
        _r = new Random(seed);
        _strings = new HostileStrings(seed + 1);
    }

    public string Text(bool allowLong = false) => _strings.Next(allowLong);

    public string? MaybeText() => _r.Next(4) == 0 ? null : Text();

    public long Long() => _r.Next(6) switch
    {
        0 => _r.Next(-5, 6),
        1 => _r.Next(),
        2 => -_r.NextInt64(),
        3 => long.MaxValue - _r.Next(3),
        4 => long.MinValue + _r.Next(3),
        _ => _r.NextInt64(long.MinValue, long.MaxValue),
    };

    public double Double() => _r.Next(10) switch
    {
        0 => double.NaN,
        1 => _r.Next(2) == 0 ? double.PositiveInfinity : double.NegativeInfinity,
        2 => -0.0,
        3 => _r.Next(-1000, 1000),
        4 => _r.Next(-1000, 1000) / 8.0,
        5 => BitConverter.Int64BitsToDouble(_r.NextInt64()) is var d && !double.IsNaN(d) ? d : 1.0,
        6 => Math.Pow(10, _r.Next(-320, 308)),
        7 => double.Epsilon * _r.Next(1, 100),
        8 => _r.NextDouble() * 1e6,
        _ => _r.NextDouble(),
    };

    public float Single() => _r.Next(6) switch
    {
        0 => float.NaN,
        1 => float.NegativeInfinity,
        2 => _r.Next(-100, 100),
        3 => (float)(_r.NextDouble() * 1000),
        4 => MathF.Pow(10, _r.Next(-44, 38)),
        _ => BitConverter.Int32BitsToSingle(_r.Next()) is var f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : 0.5f,
    };

    // Always beyond the range of a long, which is where a load into object returns a BigInteger.
    public BigInteger Big()
    {
        var bytes = new byte[_r.Next(9, 40)];
        _r.NextBytes(bytes);
        bytes[^1] = _r.Next(2) == 0 ? (byte)_r.Next(1, 0x7F) : (byte)_r.Next(0x81, 0xFF);
        return new BigInteger(bytes);
    }

    public decimal Decimal()
    {
        var parts = new int[] { _r.Next(), _r.Next(), _r.Next(), 0 };
        return new decimal(parts[0], parts[1], parts[2], _r.Next(2) == 0, (byte)_r.Next(0, 10));
    }

    // ---- generic graphs ----

    public object? Graph(int depth = 0)
    {
        int kind = _r.Next(depth > 4 ? 8 : 11);
        switch (kind)
        {
            case 0: return null;
            case 1: return _r.Next(2) == 0;
            case 2:
            case 3: return Long();
            case 4: return Double();
            case 5:
            case 6:
            case 7: return Text(allowLong: depth == 0 && _r.Next(8) == 0);
            case 8: return Big();
            case 9:
                {
                    var list = new List<object?>();
                    int n = _r.Next(0, 6);
                    for (int i = 0; i < n; i++)
                    {
                        list.Add(Graph(depth + 1));
                    }

                    return list;
                }

            default:
                {
                    var map = new Dictionary<string, object?>();
                    int n = _r.Next(0, 6);
                    for (int i = 0; i < n; i++)
                    {
                        map[Key()] = Graph(depth + 1);
                    }

                    return map;
                }
        }
    }

    private string Key() => _r.Next(5) switch
    {
        0 => Text(allowLong: _r.Next(12) == 0),
        1 => _r.Next(0, 20).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "k" + _r.Next(0, 30),
    };

    // ---- typed values ----

    public Person Person() => new()
    {
        Name = Text(),
        Age = _r.Next(-5, 120),
        Nickname = MaybeText(),
        Tags = [.. Enumerable.Range(0, _r.Next(0, 4)).Select(_ => Text())],
    };

    public Node Node(int depth = 0) => new()
    {
        Name = Text(),
        Children = [.. Enumerable.Range(0, depth > 3 ? 0 : _r.Next(0, 3)).Select(_ => Node(depth + 1))],
    };

    public Holder Holder()
    {
        var holder = new Holder { Value = Graph() };
        int n = _r.Next(0, 4);
        for (int i = 0; i < n; i++)
        {
            holder.Bag["b" + i] = Graph(1);
        }

        return holder;
    }

    public Scalars Scalars() => new()
    {
        S8 = (sbyte)_r.Next(sbyte.MinValue, sbyte.MaxValue + 1),
        U8 = (byte)_r.Next(0, 256),
        S16 = (short)_r.Next(short.MinValue, short.MaxValue + 1),
        U16 = (ushort)_r.Next(0, ushort.MaxValue + 1),
        S32 = _r.Next(int.MinValue, int.MaxValue),
        U32 = (uint)_r.NextInt64(0, uint.MaxValue + 1L),
        S64 = Long(),
        U64 = (ulong)_r.NextInt64() * 2 + (ulong)_r.Next(2),
        Big = _r.Next(2) == 0 ? Big() : _r.Next(-100, 100),
        Dec = Decimal(),
        Dbl = Double(),
        Flt = Single(),
        Flag = _r.Next(2) == 0,
        Ch = Letter(),
        Id = Guid.NewGuid() is var g && _r.Next(2) == 0 ? g : new Guid(Enumerable.Range(0, 16).Select(_ => (byte)_r.Next(256)).ToArray()),
        When = new DateTime(_r.NextInt64(DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), _r.Next(2) == 0 ? DateTimeKind.Utc : DateTimeKind.Unspecified),
        At = new DateTimeOffset(_r.NextInt64(DateTime.MinValue.Ticks + TimeSpan.TicksPerDay, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay), TimeSpan.FromMinutes(_r.Next(-14 * 60, 14 * 60 + 1))),
        Day = DateOnly.FromDayNumber(_r.Next(0, DateOnly.MaxValue.DayNumber)),
        Clock = new TimeOnly(_r.NextInt64(0, TimeOnly.MaxValue.Ticks)),
        Span = new TimeSpan(_r.NextInt64(TimeSpan.MinValue.Ticks, TimeSpan.MaxValue.Ticks)),
        Link = _r.Next(3) == 0 ? null : new Uri("https://host" + _r.Next(100) + ".example/p/" + _r.Next(1000) + "?q=" + _r.Next(9)),
        Bytes = _r.Next(3) == 0 ? null : RandomBytes(_r.Next(0, 40)),
        Color = (Color)_r.Next(-1, 5),
        Maybe = _r.Next(3) == 0 ? null : _r.Next(),
        Perm = (Perm)_r.Next(0, 8),
    };

    private char Letter()
    {
        char c;
        do
        {
            c = (char)_r.Next(1, 0xFFFF);
        }
        while (char.IsSurrogate(c));
        return c;
    }

    private byte[] RandomBytes(int n)
    {
        var bytes = new byte[n];
        _r.NextBytes(bytes);
        return bytes;
    }

    public Dog Dog() => new()
    {
        Name = Text(),
        GoodBoy = _r.Next(2) == 0,
        Weight = _r.Next(100),
        Legs = _r.Next(0, 5),
        Kept = MaybeText(),
    };

    public Defaults Defaults() => new(
        Text(),
        (Color)_r.Next(0, 3),
        Double(),
        Long(),
        Letter(),
        _r.Next(2) == 0,
        _r.Next(2) == 0 ? null : _r.Next(),
        new Guid(Enumerable.Range(0, 16).Select(_ => (byte)_r.Next(256)).ToArray()),
        (byte)_r.Next(256),
        MaybeText());

    public InitOnly InitOnly() => new()
    {
        Kind = Text(),
        Count = _r.Next(),
        Tags = [.. Enumerable.Range(0, _r.Next(0, 4)).Select(_ => Text())],
        Note = MaybeText(),
    };

    public Page<Person> Page() => new()
    {
        Number = _r.Next(),
        Items = [.. Enumerable.Range(0, _r.Next(0, 4)).Select(_ => Person())],
    };

    public Dictionary<string, List<Person>> Groups()
    {
        var groups = new Dictionary<string, List<Person>>();
        int n = _r.Next(0, 4);
        for (int i = 0; i < n; i++)
        {
            groups[Key()] = [.. Enumerable.Range(0, _r.Next(0, 3)).Select(_ => Person())];
        }

        return groups;
    }

    public Interfaces Interfaces() => new()
    {
        Numbers = Enumerable.Range(0, _r.Next(0, 4)).Select(_ => _r.Next()).ToList(),
        Names = new HashSet<string>(Enumerable.Range(0, _r.Next(0, 4)).Select(_ => Text())),
        Unique = new HashSet<int>(Enumerable.Range(0, _r.Next(0, 4)).Select(_ => _r.Next())),
        Items = Enumerable.Range(0, _r.Next(0, 3)).Select(_ => Text()).ToList(),
        People = [.. Enumerable.Range(0, _r.Next(0, 3)).Select(_ => Person())],
        Words = [.. Enumerable.Range(0, _r.Next(0, 3)).Select(_ => Text())],
        Counts = Counts(),
        Groups = Groups(),
        Colors = [.. Enumerable.Range(0, _r.Next(0, 3)).Select(_ => (Color)_r.Next(0, 3))],
        MaybePerm = _r.Next(2) == 0 ? null : (Perm)_r.Next(0, 4),
        MaybeCoord = _r.Next(2) == 0 ? null : new Coord(_r.Next(), _r.Next()),
        Grid = [.. Enumerable.Range(0, _r.Next(0, 3)).Select(_ => Enumerable.Range(0, _r.Next(0, 3)).Select(_ => _r.Next()).ToArray())],
        ByColor = new Dictionary<Color, int> { [Color.Red] = _r.Next(), [Color.Blue] = _r.Next() },
    };

    private Counts Counts()
    {
        var counts = new Counts();
        int n = _r.Next(0, 4);
        for (int i = 0; i < n; i++)
        {
            counts[Key()] = _r.Next();
        }

        return counts;
    }
}
