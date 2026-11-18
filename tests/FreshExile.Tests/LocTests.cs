using System.Text;
using System.Text.RegularExpressions;
using FreshExile.Core;
using Xunit;

namespace FreshExile.Tests;

public partial class LocTests
{
    static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "FreshExile.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("FreshExile.slnx");
        }
    }

    [Fact]
    public void InterpolationBecomesTheKey()
    {
        var level = 12;
        var s = $"Ур. {level}: {3.5:0.0}";
        LocString h = $"Ур. {level}: {3.5:0.0} {{x}}";
        Assert.Equal("Ур. {0}: {1:0.0} {{x}}", h.Key);
        Assert.Equal(s, Loc.T($"Ур. {level}: {3.5:0.0}"));
    }

    [Fact]
    public void RussianStaysRussianWithoutEnglish()
    {
        Assert.Equal(Loc.Ru, Loc.Lang);
        Assert.Equal("Строка, которой нет в словаре", Loc.T("Строка, которой нет в словаре"));
    }

    /// <summary>Every Loc.T("…") / Loc.T($"…") in C# and {l:T '…'} in XAML has its English line in Data/en.json.</summary>
    [Fact]
    public void EveryInterfaceLineHasEnglish()
    {
        var src = Path.Combine(RepoRoot, "src");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput))
            foreach (var k in CsKeys(File.ReadAllText(f))) keys.Add(k);
        foreach (var f in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(NotBuildOutput))
            foreach (Match m in XamlRx().Matches(File.ReadAllText(f)))
                keys.Add(System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1")));

        var missing = keys.Where(k => !Loc.Dictionary.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "No English for:\n" + string.Join("\n", missing));
        var unused = Loc.Dictionary.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, "English nobody uses:\n" + string.Join("\n", unused));
    }

    static bool NotBuildOutput(string f) => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    [GeneratedRegex(@"\{l:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlRx();

    /// <summary>The literal right after each "Loc.T(", as its key: holes of $"…" become {0}, {1:fmt}…</summary>
    static IEnumerable<string> CsKeys(string code)
    {
        for (var i = code.IndexOf("Loc.T(", StringComparison.Ordinal); i >= 0; i = code.IndexOf("Loc.T(", i + 1, StringComparison.Ordinal))
        {
            var p = i + 6;
            while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
            var interp = p < code.Length && code[p] == '$';
            if (interp) p++;
            if (p >= code.Length || code[p] != '"') continue;   // Loc.T(variable): checked where the literal is
            yield return ReadLiteral(code, p + 1, interp);
        }
    }

    static string ReadLiteral(string code, int p, bool interp)
    {
        var key = new StringBuilder();
        var n = 0;
        while (code[p] != '"')
        {
            var c = code[p];
            if (c == '\\') { key.Append(Unescape(code[p + 1])); p += 2; continue; }
            if (interp && c == '{' && code[p + 1] == '{') { key.Append("{{"); p += 2; continue; }
            if (interp && c == '}' && code[p + 1] == '}') { key.Append("}}"); p += 2; continue; }
            if (interp && c == '{')
            {
                // a hole: skip the expression (nested parens, strings), keep ",align" and ":format"
                p++;
                int depth = 0, start = p, colon = -1, comma = -1;
                for (; ; p++)
                {
                    var d = code[p];
                    if (d == '"') { p = SkipString(code, p); continue; }
                    if (d == '\'') { p = code.IndexOf('\'', p + (code[p + 1] == '\\' ? 3 : 2)); continue; }
                    if (d is '(' or '[' or '{') depth++;
                    else if (d is ')' or ']') depth--;
                    else if (d == '}' && depth-- == 0) break;
                    else if (d == ':' && depth == 0 && colon < 0) colon = p;
                    else if (d == ',' && depth == 0 && colon < 0 && comma < 0) comma = p;
                }
                key.Append('{').Append(n++);
                if (comma >= 0) key.Append(',').Append(code[(comma + 1)..(colon >= 0 ? colon : p)].Trim());
                if (colon >= 0) key.Append(':').Append(code[(colon + 1)..p]);
                key.Append('}');
                p++;
                continue;
            }
            key.Append(c);
            p++;
        }
        return key.ToString();
    }

    static int SkipString(string code, int p)
    {
        var interp = p > 0 && code[p - 1] == '$';
        p++;
        while (code[p] != '"')
        {
            if (code[p] == '\\') p++;
            else if (interp && code[p] == '{' && code[p + 1] != '{')
            {
                var depth = 0;
                for (p++; ; p++)
                {
                    if (code[p] == '"') { p = SkipString(code, p); continue; }
                    if (code[p] == '{') depth++;
                    else if (code[p] == '}' && depth-- == 0) break;
                }
            }
            p++;
        }
        return p + 1;
    }

    static char Unescape(char c) => c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => c };
}
