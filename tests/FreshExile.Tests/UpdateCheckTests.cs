using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class UpdateCheckTests
{
    const string Latest = """
        {"tag_name":"v0.3.9","html_url":"https://github.com/freshexileapp/FreshExile/releases/tag/v0.3.9","draft":false,"prerelease":false,
         "assets":[{"name":"LICENSE.txt","browser_download_url":"https://github.com/x/LICENSE.txt"},
                   {"name":"FreshExile.zip","browser_download_url":"https://github.com/freshexileapp/FreshExile/releases/download/v0.3.9/FreshExile.zip"}]}
        """;

    [Fact]
    public void The_latest_release_gives_its_version_and_archive()
    {
        var r = UpdateCheck.Parse(Latest)!;
        Assert.Equal(new Version(0, 3, 9), r.Version);
        Assert.EndsWith("/v0.3.9/FreshExile.zip", r.ZipUrl);
        Assert.True(UpdateCheck.IsNewer(r, new Version(0, 3, 8, 0)));
        Assert.False(UpdateCheck.IsNewer(r, new Version(0, 3, 9, 0)));
        Assert.False(UpdateCheck.IsNewer(r, new Version(0, 4, 0)));
    }

    [Fact]
    public void A_release_without_the_archive_or_a_version_tag_is_no_update()
    {
        Assert.Null(UpdateCheck.Parse("""{"tag_name":"v0.3.9","assets":[]}"""));
        Assert.Null(UpdateCheck.Parse("""{"tag_name":"latest","assets":[{"name":"FreshExile.zip","browser_download_url":"https://x/FreshExile.zip"}]}"""));
        Assert.Null(UpdateCheck.Parse("""{"tag_name":"v0.4.0","prerelease":true,"assets":[{"name":"FreshExile.zip","browser_download_url":"https://x/FreshExile.zip"}]}"""));
    }
}
