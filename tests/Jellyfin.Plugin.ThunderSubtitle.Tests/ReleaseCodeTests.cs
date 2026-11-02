using Jellyfin.Plugin.ThunderSubtitle.Core;
using Xunit;

namespace Jellyfin.Plugin.ThunderSubtitle.Tests;

public class ReleaseCodeTests
{
    [Theory]
    [InlineData("ABF-363-U.strm", "ABF-363")]
    [InlineData("MIUM-1457.strm", "MIUM-1457")]
    [InlineData("FSDSS-272.strm", "FSDSS-272")]
    [InlineData("SSIS-706-C.4K.strm", "SSIS-706")]
    [InlineData("[ThZu.Cc]SSIS-706.1080p.strm", "SSIS-706")]
    [InlineData("hhd800.com@ABF-0363.mp4", "ABF-0363")]
    [InlineData("NIMA-086-U.strm", "NIMA-086")]
    [InlineData("ABF_363.mp4", "ABF-363")]
    [InlineData("MEYD-121-C_leak.mp4", "MEYD-121")]
    [InlineData("FC2-PPV-1234567.mp4", "FC2-PPV-1234567")]
    [InlineData("HEYZO-1234.mp4", "HEYZO-1234")]
    [InlineData("ABF-363-U", "ABF-363")]
    public void ExtractsCode(string fileName, string expected)
    {
        Assert.Equal(expected, ReleaseCode.FromText(Path.GetFileNameWithoutExtension(fileName)));
    }

    [Theory]
    [InlineData("ABF-0363", "ABF-363")]
    [InlineData("ABF-363", "ABF-363")]
    [InlineData("NIMA-086", "NIMA-86")]
    [InlineData("FC2-PPV-0012345", "FC2-PPV-12345")]
    public void BuildsComparisonKey(string code, string expected)
    {
        Assert.Equal(expected, ReleaseCode.CompareKey(code));
    }

    [Theory]
    [InlineData("NIMA-086", "NIMA-86")]
    [InlineData("ABF-0363", "ABF-363")]
    [InlineData("ABF-363", null)]
    [InlineData("SSIS-706", null)]
    public void OffersTheOtherSpelling(string code, string? expected)
    {
        Assert.Equal(expected, ReleaseCode.AlternativeSpelling(code));
    }

    [Theory]
    [InlineData("The Matrix 1999")]
    [InlineData("x264-1080p")]
    [InlineData("")]
    [InlineData(null)]
    public void ReturnsNullWhenNoCode(string? text)
    {
        Assert.Null(ReleaseCode.FromText(text));
    }

    [Fact]
    public void NormalizesFullWidthCharacters()
    {
        Assert.Equal("ABF-363", ReleaseCode.FromText("ＡＢＦ－３６３"));
    }
}
