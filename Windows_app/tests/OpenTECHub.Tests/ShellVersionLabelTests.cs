using Xunit;

namespace OpenTECHub.Tests;

/// <summary>
/// The header carries the version; the full MinVer string, commit hash and all, is a tooltip.
/// Print 49 shows the long form taking visible width away from the connection chip.
/// </summary>
public sealed class ShellVersionLabelTests
{
    [Theory]
    [InlineData("0.26.2-dev.14+23ce95a8f1", "0.26.2-dev")]
    [InlineData("0.26.2-dev.14", "0.26.2-dev")]
    [InlineData("0.26.2+23ce95a8f1", "0.26.2")]
    [InlineData("0.26.2", "0.26.2")]
    [InlineData("Dev", "Dev")]
    [InlineData("", "")]
    public void The_header_shows_the_version_without_its_build_metadata(string full, string expected)
        => Assert.Equal(expected, OpenTECHub.ViewModels.ShellViewModel.ShortenVersion(full));
}
