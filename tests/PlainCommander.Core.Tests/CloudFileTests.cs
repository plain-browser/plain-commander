using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class CloudFileTests
{
    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint, false)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00400000, true)] // RECALL_ON_DATA_ACCESS: OneDrive "online only"
    [InlineData((FileAttributes)0x00040000, true)] // RECALL_ON_OPEN
    [InlineData(FileAttributes.Archive | (FileAttributes)0x00400000, true)]
    public void Placeholder_attributes_are_recognised(FileAttributes attributes, bool expected)
    {
        CloudFile.IsCloudOnly(attributes).Should().Be(expected);
    }

    [Fact]
    public void A_missing_file_is_not_reported_as_cloud_only()
    {
        CloudFile.IsCloudOnly(Path.Combine(Path.GetTempPath(), "pc-no-such-file-" + Guid.NewGuid().ToString("N"))).Should().BeFalse();
    }
}
