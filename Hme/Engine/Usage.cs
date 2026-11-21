using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shft.Hme;

/// <summary>
/// Calls spent on Tuya's free plan (about 26,000 a month), as core.py counted them in uso.json, and the pause after
/// the quota runs out: reads wait an hour at a time, commands still try.
/// </summary>
public sealed class Usage
{
    public const int QuotaMonth = 26000;

    readonly string _path;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    string _day = "", _month = "";
    DateTime _saved = DateTime.MinValue, _quotaUntil = DateTime.MinValue;

    public Usage(string folder, Func<DateTime>? now = null)
    {
        _path = Path.Combine(folder, "uso.json");
        _now = now ?? (() => DateTime.Now);
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject o)
            {
                _day = HmeConfig.Str(o["dia"]);
                _month = HmeConfig.Str(o["mes"]);
                Today = HmeConfig.Int(o["hoje"], 0);
                ThisMonth = HmeConfig.Int(o["no_mes"], 0);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    public int Today { get; private set; }
    public int ThisMonth { get; private set; }
    public bool QuotaOut => _quotaUntil > _now();
    public string QuotaMessage { get; private set; } = "";
    public DateTime Now => _now();

    public void Count()
    {
        lock (_gate)
        {
            var now = _now();
            string day = now.ToString("yyyy-MM-dd"), month = now.ToString("yyyy-MM");
            if (_day != day) (_day, Today) = (day, 0);
            if (_month != month) (_month, ThisMonth) = (month, 0);
            Today++;
            ThisMonth++;
            if ((now - _saved).TotalSeconds >= 300) Save(now);
        }
    }

    public void MarkQuota(string message)
    {
        _quotaUntil = _now().AddSeconds(3600);
        QuotaMessage = message;
    }

    public void ClearQuota() => _quotaUntil = DateTime.MinValue;

    public string Text() =>
        $"Chamadas à nuvem Tuya: hoje {Today} · este mês {ThisMonth} de ~26.000 ({(int)Math.Round(ThisMonth * 100.0 / QuotaMonth, MidpointRounding.ToEven)}%)";

    public void Flush()
    {
        lock (_gate) Save(_now());
    }

    void Save(DateTime now)
    {
        _saved = now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, new JsonObject { ["dia"] = _day, ["hoje"] = Today, ["mes"] = _month, ["no_mes"] = ThisMonth }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
