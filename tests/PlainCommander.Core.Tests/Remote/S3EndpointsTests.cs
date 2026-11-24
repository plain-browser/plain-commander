using AwesomeAssertions;
using Xunit;
using PlainCommander.Core.Remote;

namespace PlainCommander.Core.Tests.Remote;

public class S3EndpointsTests
{
    [Theory]
    [InlineData("s3.eu-central-1.amazonaws.com", S3Provider.Aws)]
    [InlineData("S3.Amazonaws.Com", S3Provider.Aws)]
    [InlineData("s3.cn-north-1.amazonaws.com.cn", S3Provider.Aws)]
    [InlineData("0123abcd.r2.cloudflarestorage.com", S3Provider.CloudflareR2)]
    [InlineData("s3.eu-central-003.backblazeb2.com", S3Provider.BackblazeB2)]
    [InlineData("s3.eu-central-1.wasabisys.com", S3Provider.Wasabi)]
    [InlineData("minio.example.com", S3Provider.Other)]
    [InlineData("192.168.1.10", S3Provider.Other)]
    [InlineData("", S3Provider.Other)]
    public void The_service_is_told_from_the_host_name(string host, S3Provider expected) =>
        S3Endpoints.FromHost(host).Should().Be(expected);

    [Theory]
    [InlineData("s3.eu-central-1.amazonaws.com", "eu-central-1")]
    [InlineData("s3-us-west-2.amazonaws.com", "us-west-2")]
    [InlineData("s3.amazonaws.com", "us-east-1")]
    [InlineData("s3.cn-north-1.amazonaws.com.cn", "cn-north-1")]
    [InlineData("s3.us-west-004.backblazeb2.com", "us-west-004")]
    [InlineData("s3.ap-northeast-1.wasabisys.com", "ap-northeast-1")]
    [InlineData("s3.wasabisys.com", "us-east-1")]
    [InlineData("0123abcd.r2.cloudflarestorage.com", "auto")]
    [InlineData("eu-central-1.minio.example.com", "us-east-1")]
    public void The_region_is_read_from_the_addresses_of_the_known_services(string host, string expected) =>
        S3Endpoints.RegionOf(host).Should().Be(expected);

    [Fact]
    public void A_region_set_on_the_site_wins_over_the_host_name()
    {
        S3RemoteClient.RegionOf(new RemoteSite { Host = "s3.eu-central-1.amazonaws.com", Region = " eu-west-1 " }).Should().Be("eu-west-1");
        S3RemoteClient.RegionOf(new RemoteSite { Host = "s3.eu-central-1.amazonaws.com" }).Should().Be("eu-central-1");
        S3RemoteClient.IsAws(new RemoteSite { Host = "s3.eu-central-1.amazonaws.com" }).Should().BeTrue();
        S3RemoteClient.IsAws(new RemoteSite { Host = "s3.eu-central-1.wasabisys.com" }).Should().BeFalse();
    }

    [Theory]
    [InlineData("eu-central-1", "s3.eu-central-1.amazonaws.com")]
    [InlineData(" EU-West-1 ", "s3.eu-west-1.amazonaws.com")]
    [InlineData("", "s3.amazonaws.com")]
    [InlineData("cn-north-1", "s3.cn-north-1.amazonaws.com.cn")]
    public void An_AWS_address_is_made_of_the_region(string region, string expected)
    {
        S3Endpoints.AwsHost(region).Should().Be(expected);
        S3Endpoints.FromHost(expected).Should().Be(S3Provider.Aws);
    }

    [Theory]
    [InlineData("minio.example.com", "minio.example.com", 0, null)]
    [InlineData(" https://minio.example.com/ ", "minio.example.com", 0, false)]
    [InlineData("http://192.168.1.10:9000", "192.168.1.10", 9000, true)]
    [InlineData("HTTPS://s3.example.com:8443/bucket/key", "s3.example.com", 8443, false)]
    [InlineData("nas:9000", "nas", 9000, null)]
    [InlineData("nas:0", "nas:0", 0, null)]
    [InlineData("", "", 0, null)]
    public void A_pasted_endpoint_is_taken_apart(string text, string host, int port, bool? http) =>
        S3Endpoints.ParseEndpoint(text).Should().Be((host, port, http));

    [Fact]
    public void A_site_saved_before_the_service_was_chosen_reads_it_from_the_host()
    {
        S3Endpoints.ProviderOf(new RemoteSite { Host = "s3.eu-central-1.amazonaws.com" }).Should().Be(S3Provider.Aws);
        S3Endpoints.ProviderOf(new RemoteSite { Host = "s3.eu-central-1.amazonaws.com", Provider = S3Provider.Other }).Should().Be(S3Provider.Other);
    }
}
