using System.IO;
using System.Text.Json.Nodes;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

public sealed class HmeConfigTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-tests-" + Guid.NewGuid().ToString("N"));

    public HmeConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    string File_ => Path.Combine(_dir, "config.json");

    void Write(string json) => File.WriteAllText(File_, json);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var c = HmeConfig.Load(_dir);
        Assert.Equal("us", c.Region);
        Assert.Equal(8765, c.Port);
        Assert.Equal(17, (int)c.Ac["temp_min"]!);
        Assert.Equal(30, (int)c.Ac["temp_max"]!);
        Assert.Equal(new[] { "cool", "heat", "auto", "fan", "dry" }, c.AcModes);
        Assert.Equal("cold", (string)c.WifiAcCodes["mode_map"]!["cool"]!);
        Assert.False(c.HasAc);
    }

    [Fact]
    public void Merge_keeps_saved_and_fills_missing_nested()
    {
        Write("""{"ac":{"room_name":"Sala"}}""");
        var c = HmeConfig.Load(_dir);
        Assert.Equal("Sala", (string)c.Ac["room_name"]!);
        Assert.Equal(17, (int)c.Ac["temp_min"]!);
    }

    [Fact]
    public void Unknown_fields_survive_save()
    {
        Write("""{"futuro":{"x":1}}""");
        var c = HmeConfig.Load(_dir);
        c.Save();
        Assert.Contains("\"futuro\"", File.ReadAllText(File_));
    }

    [Fact]
    public void Has_ac_defaults_true_when_an_ac_is_configured()
    {
        Write("""{"ac":{"type":"ir","remote_id":"R1"}}""");
        Assert.True(HmeConfig.Load(_dir).HasAc);
    }

    [Fact]
    public void Has_ac_saved_value_wins()
    {
        Write("""{"has_ac":false,"ac":{"type":"ir","remote_id":"R1"}}""");
        Assert.False(HmeConfig.Load(_dir).HasAc);
    }

    [Fact]
    public void Secret_is_written_protected_and_reads_back()
    {
        var c = HmeConfig.Load(_dir);
        c.AccessSecret = "abc";
        c.Save();
        var disk = JsonNode.Parse(File.ReadAllText(File_))!;
        Assert.StartsWith("dpapi:", (string)disk["access_secret"]!);
        Assert.Equal("abc", HmeConfig.Load(_dir).AccessSecret);
    }

    [Fact]
    public void Legacy_b64_and_plain_are_read_and_rewritten()
    {
        Assert.Equal("abc", Secrets.Unprotect("b64:YWJj"));
        Assert.Equal("abc", Secrets.Unprotect("abc"));
        Write("""{"access_id":"K","access_secret":"b64:YWJj"}""");
        var c = HmeConfig.Load(_dir);
        Assert.Equal("abc", c.AccessSecret);
        Assert.StartsWith("dpapi:", (string)JsonNode.Parse(File.ReadAllText(File_))!["access_secret"]!);
    }

    [Fact]
    public void Positivo_password_is_rewritten_protected()
    {
        Write("""{"positivo":{"email":"a@b.c","password":"b64:cHc="}}""");
        HmeConfig.Load(_dir);
        Assert.StartsWith("dpapi:", (string)JsonNode.Parse(File.ReadAllText(File_))!["positivo"]!["password"]!);
    }

    [Fact]
    public void Unreadable_secret_sets_SecretError()
    {
        Write("""{"access_id":"K","access_secret":"dpapi:AAAA","configured":true}""");
        var c = HmeConfig.Load(_dir);
        Assert.Contains("Cole de novo nos ajustes", c.SecretError);
        Assert.Equal("", c.AccessSecret);
    }

    [Fact]
    public void Odd_types_fall_back_to_defaults()
    {
        Write("""{"ac":{"modes":[],"temp_min":"x"},"wifi_ac_codes":{"temp_scale":null},"port":"abc"}""");
        var c = HmeConfig.Load(_dir);
        Assert.Equal(new[] { "cool", "heat", "auto", "fan", "dry" }, c.AcModes);
        Assert.Equal(1.0, c.WifiTempScale);
        Assert.Equal(17, c.TempMin);
        Assert.Equal(8765, c.Port);
    }

    [Fact]
    public void Non_bool_flags_read_as_false()
    {
        Write("""{"configured":"sim","demo":1}""");
        var c = HmeConfig.Load(_dir);
        Assert.False(c.Configured);
        Assert.False(c.Demo);
    }

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        Write("{ nope");
        Assert.Equal("us", HmeConfig.Load(_dir).Region);
    }
}
