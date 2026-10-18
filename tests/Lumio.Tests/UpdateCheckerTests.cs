using Lumio.Core.Updates;
using Xunit;

namespace Lumio.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.3.4", 1, 3, 4)]
    [InlineData("1.4.0", 1, 4, 0)]
    [InlineData("v2.0", 2, 0, 0)]
    [InlineData("v1.5.0-beta", 1, 5, 0)]
    [InlineData("v3", 3, 0, 0)]
    public void TryParseVersion_ReadsReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    public void TryParseVersion_RejectsOtherTags(string tag)
    {
        Assert.False(UpdateChecker.TryParseVersion(tag, out _));
    }

    [Fact]
    public void CurrentVersion_IsComparableWithReleaseTags()
    {
        UpdateChecker.TryParseVersion("v0.0.1", out var old);
        Assert.True(UpdateChecker.CurrentVersion > old);
        Assert.Equal(3, UpdateChecker.CurrentVersion.ToString(3).Split('.').Length);
    }
}
