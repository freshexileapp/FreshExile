using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FreshExile.Core;

/// <summary>
/// The interface's two languages. Russian text in the code is the key, its English lives in Data/en.json; a line without
/// a translation stays Russian. An interpolated string becomes its own key: <c>T($"Ур. {level}")</c> looks up
/// "Ур. {0}" and fills the hole in. The language is chosen once at startup (a switch restarts the app).
/// </summary>
public static class Loc
{
    public const string Ru = "ru", En = "en";

    static readonly Lazy<Dictionary<string, string>> English = new(Load);

    public static string Lang { get; private set; } = Ru;

    public static bool IsEnglish => Lang == En;

    public static void Use(string? lang) => Lang = lang == En ? En : Ru;

    /// <summary>The first start: Russian for a Russian, Ukrainian, Belarusian or Kazakh Windows, English for the rest.</summary>
    public static string FromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? Ru : En;

    public static string T(string ru) => IsEnglish && English.Value.TryGetValue(ru, out var en) ? en : ru;

    public static string T(LocString s) => s.Text();

    /// <summary>The English dictionary, for the test that every key has its line.</summary>
    public static IReadOnlyDictionary<string, string> Dictionary => English.Value;

    static Dictionary<string, string> Load()
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream("en.json");
        return s == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
    }

    internal static string Lookup(string key) => IsEnglish && English.Value.TryGetValue(key, out var en) ? en : key;
}

/// <summary>Builds "Ур. {0}" plus its values out of <c>$"Ур. {level}"</c>, so the template is the dictionary key.</summary>
[InterpolatedStringHandler]
public ref struct LocString
{
    readonly StringBuilder _key;
    readonly List<object?> _args;

    public LocString(int literalLength, int formattedCount)
    {
        _key = new StringBuilder(literalLength + formattedCount * 4);
        _args = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string s) => _key.Append(s.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment, string? format = null)
    {
        _key.Append('{').Append(_args.Count);
        if (alignment != 0) _key.Append(',').Append(alignment);
        if (format != null) _key.Append(':').Append(format);
        _key.Append('}');
        _args.Add(value);
    }

    public readonly string Text() => string.Format(CultureInfo.CurrentCulture, Loc.Lookup(_key.ToString()), _args.ToArray());

    /// <summary>The dictionary key this string looks up ("Ур. {0}").</summary>
    public readonly string Key => _key.ToString();
}
