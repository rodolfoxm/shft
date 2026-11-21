using System.Globalization;
using System.Text;

namespace Shft.Hme;

/// <summary>core.py's rooms: the user's list first, then common Portuguese room words in device names.</summary>
public static class Rooms
{
    public const string Other = "Outros";

    static readonly (string Word, string Room)[] Words =
    [
        ("suite", "Suíte"), ("cozinha", "Cozinha"), ("sala", "Sala"), ("quarto", "Quarto"),
        ("banheiro", "Banheiro"), ("lavabo", "Banheiro"), ("escritorio", "Escritório"),
        ("studio", "Estúdio"), ("estudio", "Estúdio"), ("varanda", "Varanda"), ("sacada", "Varanda"),
        ("garagem", "Garagem"), ("lavanderia", "Lavanderia"), ("area externa", "Área externa"),
        ("quintal", "Área externa"), ("jardim", "Área externa"), ("corredor", "Corredor"),
    ];

    public static string Plain(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }

    public static string? Match(string text, IReadOnlyList<string> rooms)
    {
        var p = Plain(text);
        foreach (var r in rooms)
            if (Plain(r) is { Length: > 0 } pr && p.Contains(pr, StringComparison.Ordinal)) return r;
        foreach (var (word, room) in Words)
            if (p.Contains(word, StringComparison.Ordinal))
                return rooms.FirstOrDefault(r => Plain(r) == Plain(room)) ?? room;
        return null;
    }

    static List<string> List(HmeConfig cfg) => cfg.Rooms.Select(HmeConfig.Str).Where(r => r.Length > 0).ToList();

    public static string Of(Device d, HmeConfig cfg, IReadOnlyDictionary<string, string> smartLifeRooms)
    {
        var rooms = List(cfg);
        if (cfg.DeviceRoom.ContainsKey(d.Id))
            return HmeConfig.Str(cfg.DeviceRoom[d.Id]) is { Length: > 0 } chosen ? chosen : Other;
        if (smartLifeRooms.TryGetValue(d.Id, out var sl)) return Match(sl, rooms) ?? sl;
        return Match(d.Name, rooms) ?? Match(d.OriginalName, rooms) ?? Other;
    }

    public static List<string> Order(IEnumerable<Device> devs, HmeConfig cfg)
    {
        var order = List(cfg);
        var list = devs.ToList();
        foreach (var d in list)
            if (d.Room.Length > 0 && d.Room != Other && !order.Contains(d.Room)) order.Add(d.Room);
        if (list.Any(d => d.Room == Other)) order.Add(Other);
        return order;
    }
}
