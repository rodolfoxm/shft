using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shft.Hme;

/// <summary>
/// The last device list that worked (aparelhos.json), shown when the cloud fails so the panel is never empty. Only
/// what drawing needs: no local keys, IP or location.
/// </summary>
public sealed class SavedList
{
    static readonly string[] Fields = ["id", "name", "category", "online", "status", "owner_id"];

    readonly string _path;
    readonly object _gate = new();
    readonly JsonObject _data = new() { ["tuya"] = new JsonArray(), ["positivo"] = new JsonArray() };

    public SavedList(string folder)
    {
        _path = Path.Combine(folder, "aparelhos.json");
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject o)
                foreach (var key in new[] { "tuya", "positivo" })
                    if (o[key] is JsonArray a) _data[key] = a.DeepClone();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    public List<JsonObject> Get(string source)
    {
        lock (_gate) return (_data[source] as JsonArray ?? new()).OfType<JsonObject>().Select(d => (JsonObject)d.DeepClone()).ToList();
    }

    public void Put(string source, IEnumerable<JsonObject> devs)
    {
        var clean = new JsonArray();
        foreach (var d in devs.Where(d => HmeConfig.Str(d["id"]).Length > 0))
        {
            var o = new JsonObject();
            foreach (var f in Fields)
                if (d.ContainsKey(f)) o[f] = d[f]?.DeepClone();
            clean.Add(o);
        }
        lock (_gate)
        {
            if (JsonNode.DeepEquals(clean, _data[source])) return;
            _data[source] = clean;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, _data.ToJsonString());
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
