using System.Text;
using Xunit;
using Cs = global::OneYaml.Compat.SharpYaml;
using Rs = global::SharpYaml;

namespace OneYaml.Compat.Tests.SharpYamlCompat;

// A quoted number is read by the compat layer through a retry; the retry must not build an error that grows with
// the size of the text read so far.
public class SharpYamlErrorCostTests
{
    private static string QuotedNumbers(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append("- \"").Append(i).Append("\"\n");
        }

        return sb.ToString();
    }

    private static long Allocated(string yaml)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        List<int>? list = Cs.YamlSerializer.Deserialize<List<int>>(yaml);
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(list);
        return used;
    }

    [Fact]
    public void Quoted_numbers_cost_grows_in_step_with_the_input()
    {
        string small = QuotedNumbers(1_000);
        string large = QuotedNumbers(4_000);
        Allocated(small);
        long a = Allocated(small);
        long b = Allocated(large);

        // Four times the items: about four times the allocation. A retry that re-reads the text for its error
        // position would make it about sixteen times.
        Assert.True(b < a * 6, $"1,000 items allocated {a:N0} bytes and 4,000 allocated {b:N0}");
    }

    [ThreadStatic]
    private static int t_thrown;

    // Exceptions thrown on this thread while the work runs. A quoted number that goes to the engine first and is caught
    // afterwards throws once per item, and each one costs time in step with the text read so far; counting the throws
    // does not depend on how fast the machine is.
    private static int Thrown(Action work)
    {
        static void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) => t_thrown++;
        t_thrown = 0;
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            work();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        return t_thrown;
    }

    [Fact]
    public void Quoted_numbers_are_read_without_an_exception_per_item()
    {
        string yaml = QuotedNumbers(2_000);
        Allocated(QuotedNumbers(10));

        int thrown = Thrown(() => Assert.Equal(2_000, Cs.YamlSerializer.Deserialize<List<int>>(yaml)!.Count));

        Assert.True(thrown < 20, $"reading 2,000 quoted numbers threw {thrown} exceptions");
    }

    [Fact]
    public void Quoted_numbers_read_the_same_values_as_the_real_library()
    {
        string yaml = QuotedNumbers(50) + "- 7\n- \"+8\"\n- \"-9\"\n";
        Assert.Equal(Rs.YamlSerializer.Deserialize<List<int>>(yaml), Cs.YamlSerializer.Deserialize<List<int>>(yaml));
        Assert.Equal(Rs.YamlSerializer.Deserialize<List<double>>("[\"1.5\", \"2e3\", 4]"), Cs.YamlSerializer.Deserialize<List<double>>("[\"1.5\", \"2e3\", 4]"));
    }
}
