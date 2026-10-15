namespace OneYaml.Compat.Tests;

/// <summary>
/// Locates the shared conformance data from the environment. A variable that is unset means the data is not
/// wanted and its tests skip; a variable that names a missing directory, or a data set that turns out empty,
/// fails the test, so a wrong path can never pass as a skip.
/// </summary>
internal static class KitData
{
    internal const string SuiteVariable = "ONEYAML_YTS_DIR";
    internal const string KitVariable = "ONEYAML_KIT";
    internal const string WorkloadsVariable = "ONEYAML_WORKLOADS";

    /// <summary>The directory a variable names, or null when it is unset.</summary>
    internal static string? Directory(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return System.IO.Directory.Exists(path) ? path : throw new InvalidOperationException($"{variable} names a directory that does not exist: {path}");
    }

    /// <summary>
    /// The workload directories (each holds input.yaml): the prepared set ONEYAML_WORKLOADS names, laid out as the kit
    /// writes it (a workloads folder with one directory per workload), else the kit's real-world files.
    /// </summary>
    internal static IEnumerable<string> Workloads()
    {
        string root;
        if (Directory(WorkloadsVariable) is { } prepared)
        {
            root = Path.Combine(prepared, "workloads");
        }
        else if (Directory(KitVariable) is { } kit)
        {
            root = Path.Combine(kit, "bench", "workloads", "real");
        }
        else
        {
            return [];
        }

        if (!System.IO.Directory.Exists(root))
        {
            throw new InvalidOperationException($"the workload directory {root} does not exist");
        }

        return System.IO.Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "input.yaml"))).OrderBy(d => d, StringComparer.Ordinal);
    }

    /// <summary>The input of one named workload.</summary>
    internal static string WorkloadText(string name)
    {
        string dir = Workloads().FirstOrDefault(d => Path.GetFileName(d) == name)
            ?? throw new InvalidOperationException($"workload {name} was not found");
        return File.ReadAllText(Path.Combine(dir, "input.yaml"));
    }

    /// <summary>
    /// Wraps ids for a Theory. With none of the variables set there is no data and a lone empty id lets the test report
    /// a skip; with one set, an empty data set is an error.
    /// </summary>
    internal static IEnumerable<object[]> Rows(IEnumerable<string> ids, params string[] variables)
    {
        bool any = false;
        foreach (string id in ids)
        {
            any = true;
            yield return [id];
        }

        if (any)
        {
            yield break;
        }

        foreach (string variable in variables)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                throw new InvalidOperationException($"{variable} is set but no test data was found under it");
            }
        }

        yield return [string.Empty];
    }

    /// <summary>The ids of the suite's valid cases, "ID" or "ID/NN".</summary>
    internal static IEnumerable<string> ValidSuiteIds()
    {
        string? dir = Directory(SuiteVariable);
        if (dir is null)
        {
            yield break;
        }

        foreach (string caseDir in System.IO.Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(caseDir, "in.yaml")))
            {
                if (!File.Exists(Path.Combine(caseDir, "error")))
                {
                    yield return Path.GetFileName(caseDir);
                }

                continue;
            }

            foreach (string sub in System.IO.Directory.EnumerateDirectories(caseDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(sub, "in.yaml")) && !File.Exists(Path.Combine(sub, "error")))
                {
                    yield return Path.GetFileName(caseDir) + "/" + Path.GetFileName(sub);
                }
            }
        }
    }
}
