using System.Net;
using System.Text.Json;

namespace FreshExile.Core;

public sealed record CharacterSnapshot(string Name, int Level, string Class, IReadOnlyList<int> Nodes, IReadOnlySet<string> GemKeys)
{
    public bool HasGem(Gem g) => GemKeys.Contains(g.Key) || GemKeys.Contains(Gem.GemKey(g.Name + " Support"));
}

public sealed class PoeApiException(string message) : Exception(message);

/// <summary>Public character profile (pathofexile.com character-window). Works only for public profiles.</summary>
public sealed class PoeApi(HttpClient http)
{
    const string Base = "https://www.pathofexile.com/character-window/";

    public async Task<CharacterSnapshot> GetCharacterAsync(string account, string character, CancellationToken ct = default)
    {
        account = account.Trim();
        character = character.Trim();
        if (account.Length == 0 || character.Length == 0) throw new PoeApiException(Loc.T("Укажи аккаунт и персонажа"));

        using var passives = await PostAsync("get-passive-skills", account, character, ct);
        using var items = await PostAsync("get-items", account, character, ct);

        var nodes = passives.RootElement.TryGetProperty("hashes", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList()
            : [];

        var gems = new HashSet<string>();
        var root = items.RootElement;
        if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("socketedItems", out var socketed) && socketed.ValueKind == JsonValueKind.Array)
                    foreach (var s in socketed.EnumerateArray())
                        if (s.TryGetProperty("typeLine", out var tl) && tl.GetString() is { } name)
                            gems.Add(Gem.GemKey(name));

        string name1 = character, cls = "";
        var level = 0;
        if (root.TryGetProperty("character", out var c))
        {
            if (c.TryGetProperty("name", out var n)) name1 = n.GetString() ?? character;
            if (c.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number) level = l.GetInt32();
            if (c.TryGetProperty("class", out var k)) cls = k.GetString() ?? "";
        }
        return new CharacterSnapshot(name1, level, cls, nodes, gems);
    }

    async Task<JsonDocument> PostAsync(string endpoint, string account, string character, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["accountName"] = account,
            ["character"] = character,
            ["realm"] = "pc",
        });
        using var resp = await http.PostAsync(Base + endpoint, content, ct);
        switch (resp.StatusCode)
        {
            case HttpStatusCode.Forbidden:
                throw new PoeApiException(Loc.T("Профиль приватный. Открой вкладку Characters в настройках приватности на pathofexile.com"));
            case HttpStatusCode.NotFound:
                throw new PoeApiException(Loc.T("Аккаунт или персонаж не найден (аккаунт пишется с #1234)"));
            case HttpStatusCode.TooManyRequests:
                throw new PoeApiException(Loc.T("Слишком часто, сайт GGG просит подождать минуту"));
        }
        if (!resp.IsSuccessStatusCode) throw new PoeApiException(Loc.T($"pathofexile.com ответил {(int)resp.StatusCode}"));

        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
        {
            doc.Dispose();
            throw new PoeApiException(err.TryGetProperty("message", out var m) ? m.GetString() ?? Loc.T("Ошибка API") : Loc.T("Ошибка API"));
        }
        return doc;
    }
}
