using System.Text.RegularExpressions;

namespace FreshExile.Core;

/// <summary>
/// Strips personal bits from text that leaves this PC in a report: Windows user folders in paths,
/// the PoE account and character names.
/// </summary>
public static partial class Privacy
{
    public const string UserMark = "<user>";
    public const string AccountMark = "(account)";
    public const string CharacterMark = "(character)";

    // C:\Users\Name\..., D:/Users/Name/..., \\?\C:\Users\Name: keep the path shape, hide the folder name
    [GeneratedRegex(@"(?i)(\b[A-Z]:[\\/]+Users[\\/]+)(?!<user>)[^\\/\r\n""'<>|:*?]+")]
    private static partial Regex UsersFolder();

    public static string Scrub(string text, string? account = null, string? character = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = UsersFolder().Replace(text, "$1" + UserMark);

        if (!string.IsNullOrWhiteSpace(account))
        {
            var a = account.Trim();
            text = ReplaceWord(text, a, AccountMark);
            // "Name#1234": the name alone shows up too (profile URLs, logs)
            var hash = a.IndexOf('#');
            if (hash > 0) text = ReplaceWord(text, a[..hash], AccountMark);
        }
        if (!string.IsNullOrWhiteSpace(character)) text = ReplaceWord(text, character.Trim(), CharacterMark);
        return text;
    }

    /// <summary>"C:\Me\Games\Path of Exile\logs\Client.txt" → "…\Path of Exile\logs\Client.txt": folder names above the game can be personal.</summary>
    public static string ShortPath(string? path, int keep = 3)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= keep ? path : "…\\" + string.Join("\\", parts[^keep..]);
    }

    static string ReplaceWord(string text, string word, string mark) =>
        word.Length < 3 ? text : Regex.Replace(text, $@"(?<![\w]){Regex.Escape(word)}(?![\w])", mark, RegexOptions.IgnoreCase);
}
