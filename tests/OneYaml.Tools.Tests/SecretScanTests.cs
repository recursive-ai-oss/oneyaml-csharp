using System.Text;
using OneYaml.PublicBoundary;
using Xunit;

namespace OneYaml.Tools.Tests;

// Planted secrets are assembled from fragments, so this file passes the scan it tests.
public class SecretScanTests
{
    private static List<string> Scan(string s) => SecretScanCheck.ScanBlob(Encoding.UTF8.GetBytes(s));

    public static TheoryData<string, string> Secrets => new()
    {
        { "api key (sk- prefix)", "sk-" + "ant-" + new string('a', 30) },
        { "aws access key id", "AK" + "IA" + "ABCDEFGHIJKLMNOP" },
        { "github token", "gh" + "p_" + new string('x', 36) },
        { "private key", "-----BEGIN " + "RSA PRIVATE KEY-----" },
        { "nuget api key", "oy2" + new string('a', 43) },
        { "secret assignment", "SERVICE_API_" + "KEY=" + new string('z', 20) },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Planted_secret_is_found(string kind, string content)
    {
        Assert.Contains(kind, Scan("x = " + content + "\n"));
    }

    [Fact]
    public void Elf_header_is_found()
    {
        Assert.Contains("ELF binary or core dump", SecretScanCheck.ScanBlob([0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1]));
    }

    [Fact]
    public void Oversized_blob_is_found()
    {
        Assert.Single(SecretScanCheck.ScanBlob(new byte[SecretScanCheck.MaxBytes + 1]));
    }

    [Fact]
    public void Ordinary_text_passes()
    {
        Assert.Empty(Scan("namespace OneYaml;\npublic class A { }\nPASSWORD_POLICY = none\n"));
    }
}
