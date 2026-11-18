using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public class PrivacyTests
{
    [Theory]
    [InlineData(@"report saved: C:\Users\Ivan Petrov\Desktop\r.zip", @"report saved: C:\Users\<user>\Desktop\r.zip")]
    [InlineData(@"""ClientLogPath"": ""D:/users/ivan/Games/Client.txt""", @"""ClientLogPath"": ""D:/users/<user>/Games/Client.txt""")]
    [InlineData(@"at X() in C:\Users\ivan\src\A.cs:line 5", @"at X() in C:\Users\<user>\src\A.cs:line 5")]
    [InlineData(@"C:\Users\ivan\AppData", @"C:\Users\<user>\AppData")]
    [InlineData(@"E:\Steam\steamapps\common\Path of Exile\logs\Client.txt", @"E:\Steam\steamapps\common\Path of Exile\logs\Client.txt")]
    [InlineData("UserAgent FreshExile/0.1, Users online", "UserAgent FreshExile/0.1, Users online")]
    public void Hides_the_windows_user_folder_only(string input, string expected) =>
        Assert.Equal(expected, Privacy.Scrub(input));

    [Theory]
    [InlineData(@"C:\Me\Programs\Steam\steamapps\common\Path of Exile\logs\Client.txt", @"…\Path of Exile\logs\Client.txt")]
    [InlineData("D:/Games/PoE/logs/LatestClient.txt", @"…\PoE\logs\LatestClient.txt")]
    [InlineData(@"logs\Client.txt", @"logs\Client.txt")]
    [InlineData("", "")]
    public void Log_path_keeps_only_the_game_folder(string input, string expected) =>
        Assert.Equal(expected, Privacy.ShortPath(input));

    [Fact]
    public void Already_scrubbed_text_stays_the_same() =>
        Assert.Equal(@"C:\Users\<user>\x", Privacy.Scrub(Privacy.Scrub(@"C:\Users\ivan\x")));

    [Fact]
    public void Hides_account_with_and_without_tag_and_character()
    {
        var s = Privacy.Scrub("sync Ivan#1234 / ivan: TestRanger (Ranger) lvl 38, TestRangers stays",
            account: "Ivan#1234", character: "TestRanger");
        Assert.Equal("sync (account) / (account): (character) (Ranger) lvl 38, TestRangers stays", s);
    }

    [Fact]
    public void Short_or_empty_names_are_left_alone()
    {
        Assert.Equal("Al went to Al's town", Privacy.Scrub("Al went to Al's town", account: "Al", character: ""));
        Assert.Equal("", Privacy.Scrub(""));
    }
}
