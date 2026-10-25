using System.Net;
using Kirimdong.Core;

namespace Kirimdong.Core.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("foto.jpg", "foto.jpg")]
    [InlineData("Liburan/pantai/1.jpg", "Liburan|pantai|1.jpg")]
    [InlineData("Liburan\\pantai\\1.jpg", "Liburan|pantai|1.jpg")]
    [InlineData("../../Windows/system.ini", "Windows|system.ini")]
    [InlineData("/etc/passwd", "etc|passwd")]
    [InlineData("C:\\Users\\x.txt", "C_|Users|x.txt")]
    [InlineData("a/./b/../c.txt", "a|b|c.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("name. ", "name")]
    [InlineData("tanya?.txt", "tanya_.txt")]
    public void SanitizeKeepsOnlySafeSegments(string input, string expected)
    {
        Assert.Equal(expected.Split('|'), PathSafety.SanitizeRelative(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("/")]
    public void SanitizeRejectsPathsWithNothingLeft(string input)
    {
        Assert.Null(PathSafety.SanitizeRelative(input));
    }

    [Fact]
    public void ResolveStaysInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "kd-root");
        var resolved = PathSafety.Resolve(root, "../../../evil.txt");
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "evil.txt"), resolved);
    }

    [Fact]
    public void MakeUniqueCountsUp()
    {
        var taken = new HashSet<string> { @"C:\d\foto.jpg", @"C:\d\foto (1).jpg" };
        Assert.Equal(@"C:\d\foto (2).jpg", PathSafety.MakeUnique(@"C:\d\foto.jpg", taken.Contains));
        Assert.Equal(@"C:\d\lain.jpg", PathSafety.MakeUnique(@"C:\d\lain.jpg", taken.Contains));
    }
}

public class ProtocolTests
{
    [Fact]
    public void AnnouncementRoundTrips()
    {
        var a = new Announcement(Protocol.AppTag, Protocol.Version, Announcement.KindHello, "id-1", "Laptop Budi", 47801);
        var decoded = Protocol.DecodeAnnouncement(Protocol.EncodeAnnouncement(a));
        Assert.Equal(a, decoded);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"app\":\"other\",\"v\":1,\"kind\":\"hi\",\"id\":\"x\",\"name\":\"n\",\"port\":1}")]
    [InlineData("{\"app\":\"kirimdong\",\"v\":99,\"kind\":\"hi\",\"id\":\"x\",\"name\":\"n\",\"port\":1}")]
    [InlineData("{\"app\":\"kirimdong\",\"v\":1,\"kind\":\"hi\",\"id\":\"\",\"name\":\"n\",\"port\":1}")]
    [InlineData("{\"app\":\"kirimdong\",\"v\":1,\"kind\":\"hi\",\"id\":\"x\",\"name\":\"n\",\"port\":70000}")]
    public void ForeignOrBrokenDatagramsAreIgnored(string json)
    {
        Assert.Null(Protocol.DecodeAnnouncement(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void NamesFromTheNetworkAreCleaned()
    {
        Assert.Equal("PC tanpa nama", Protocol.CleanName("  "));
        Assert.Equal("ab", Protocol.CleanName("a\u0007b"));
        Assert.Equal(40, Protocol.CleanName(new string('x', 100)).Length);
    }

    [Theory]
    [InlineData("192.168.1.23", "255.255.255.0", "192.168.1.255")]
    [InlineData("10.4.7.9", "255.255.0.0", "10.4.255.255")]
    public void DirectedBroadcastFromMask(string ip, string mask, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), DiscoveryService.DirectedBroadcast(IPAddress.Parse(ip), IPAddress.Parse(mask)));
    }

    [Fact]
    public void SummaryDescribesTheParcel()
    {
        Assert.Equal("foto.jpg", Transfer.Describe(new[] { "foto.jpg" }, 1));
        Assert.Equal("Liburan (24 file)", Transfer.Describe(new[] { "Liburan" }, 24));
        Assert.Equal("a.txt + 2 lainnya (3 file)", Transfer.Describe(new[] { "a.txt", "b.txt", "c.txt" }, 3));
    }
}
