using System.Diagnostics;
using System.Text;

namespace OneYaml.PublicBoundary;

/// <summary>Runs git and returns its raw output.</summary>
internal static class Git
{
    public static byte[] Run(string dir, IEnumerable<string> args, byte[]? stdin = null)
    {
        (int code, byte[] output, string error) = Execute(dir, args, stdin);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {error.Trim()}");
        }

        return output;
    }

    public static string Text(string dir, params string[] args) =>
        Encoding.UTF8.GetString(Run(dir, args));

    /// <summary>True when git succeeds; used to probe whether a revision exists.</summary>
    public static bool Succeeds(string dir, params string[] args) => Execute(dir, args, null).Code == 0;

    public static List<string> SplitZ(byte[] data)
    {
        var result = new List<string>();
        foreach (string s in Encoding.UTF8.GetString(data).Split('\0'))
        {
            if (s.Length > 0)
            {
                result.Add(s);
            }
        }

        return result;
    }

    private static (int Code, byte[] Output, string Error) Execute(string dir, IEnumerable<string> args, byte[]? stdin)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("cannot start git");
        // Feed stdin and drain both outputs concurrently so no pipe fills up and blocks git.
        Task writer = Task.CompletedTask;
        if (stdin is not null)
        {
            writer = Task.Run(() =>
            {
                using Stream input = process.StandardInput.BaseStream;
                input.Write(stdin);
            });
        }

        Task<string> error = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        writer.Wait();
        return (process.ExitCode, buffer.ToArray(), error.Result);
    }
}
