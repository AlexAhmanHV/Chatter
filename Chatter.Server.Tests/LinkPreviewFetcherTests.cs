using System.Net;
using Chatter.Server.Services;
using Xunit;

namespace Chatter.Server.Tests;

// Covers the parts of LinkPreviewFetcher that don't require a real network call: the SSRF guard
// (IsPublicAddress) and the HTML meta-tag extraction. The actual FetchAsync/GetOrFetchAsync path
// does real DNS + HTTP and is exercised manually rather than in this offline suite.
public class LinkPreviewFetcherTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("10.0.0.5")]       // 10.0.0.0/8
    [InlineData("172.16.0.1")]     // 172.16.0.0/12
    [InlineData("172.31.255.255")] // 172.16.0.0/12, top of range
    [InlineData("192.168.1.1")]    // 192.168.0.0/16
    [InlineData("169.254.169.254")] // link-local / cloud metadata endpoint
    [InlineData("0.0.0.0")]
    public void IsPublicAddress_PrivateOrReservedIPv4_ReturnsFalse(string ip)
    {
        Assert.False(LinkPreviewFetcher.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]  // just outside the 172.16.0.0/12 range
    [InlineData("192.169.0.1")] // just outside the 192.168.0.0/16 range
    public void IsPublicAddress_PublicIPv4_ReturnsTrue(string ip)
    {
        Assert.True(LinkPreviewFetcher.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPublicAddress_IPv6Loopback_ReturnsFalse()
    {
        Assert.False(LinkPreviewFetcher.IsPublicAddress(IPAddress.Parse("::1")));
    }

    [Fact]
    public void IsPublicAddress_IPv6UniqueLocal_ReturnsFalse()
    {
        Assert.False(LinkPreviewFetcher.IsPublicAddress(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public void IsPublicAddress_IPv6Public_ReturnsTrue()
    {
        Assert.True(LinkPreviewFetcher.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111"))); // Cloudflare DNS
    }

    [Fact]
    public void ExtractMetaContent_PropertyBeforeContent_FindsIt()
    {
        var html = "<html><head><meta property=\"og:title\" content=\"Hello World\"></head></html>";
        Assert.Equal("Hello World", LinkPreviewFetcher.ExtractMetaContent(html, "og:title"));
    }

    [Fact]
    public void ExtractMetaContent_ContentBeforeProperty_FindsIt()
    {
        var html = "<html><head><meta content=\"Hello World\" property=\"og:title\"></head></html>";
        Assert.Equal("Hello World", LinkPreviewFetcher.ExtractMetaContent(html, "og:title"));
    }

    [Fact]
    public void ExtractMetaContent_UsingNameAttribute_FindsIt()
    {
        var html = "<meta name=\"description\" content=\"A page about things\">";
        Assert.Equal("A page about things", LinkPreviewFetcher.ExtractMetaContent(html, "description"));
    }

    [Fact]
    public void ExtractMetaContent_HtmlEntitiesInContent_AreDecoded()
    {
        var html = "<meta property=\"og:title\" content=\"Fish &amp; Chips\">";
        Assert.Equal("Fish & Chips", LinkPreviewFetcher.ExtractMetaContent(html, "og:title"));
    }

    [Fact]
    public void ExtractMetaContent_NotPresent_ReturnsNull()
    {
        var html = "<html><head><title>No meta here</title></head></html>";
        Assert.Null(LinkPreviewFetcher.ExtractMetaContent(html, "og:title"));
    }

    [Fact]
    public void ExtractTitleTag_ReturnsTrimmedText()
    {
        var html = "<html><head><title>  My Page  </title></head></html>";
        Assert.Equal("My Page", LinkPreviewFetcher.ExtractTitleTag(html));
    }
}
