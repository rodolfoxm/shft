using Microsoft.Win32;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

public sealed class HmeStandaloneTests : IDisposable
{
    const string KeyPath = @"Software\shft-tests\Run";
    readonly RegistryKey _run = Registry.CurrentUser.CreateSubKey(KeyPath + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        var name = _run.Name[(Registry.CurrentUser.Name.Length + 1)..];
        _run.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(name, false);
    }

    [Fact]
    public void Migrates_controlecasa_exe_value()
    {
        _run.SetValue("ControleCasa", "\"C:\\x\\ControleCasa.exe\" --bandeja");
        Assert.True(HmeStartup.MigrateRunKey(_run, @"C:\apps\hme.exe"));
        Assert.Null(_run.GetValue("ControleCasa"));
        Assert.Equal("\"C:\\apps\\hme.exe\" --bandeja", _run.GetValue("hme"));
    }

    [Fact]
    public void Migrates_the_python_install_too()
    {
        _run.SetValue("ControleCasa", "\"C:\\Py\\pythonw.exe\" \"C:\\Users\\u\\Documents\\ControleCasa\\main.py\" --bandeja");
        Assert.True(HmeStartup.MigrateRunKey(_run, @"C:\apps\hme.exe"));
        Assert.Equal("\"C:\\apps\\hme.exe\" --bandeja", _run.GetValue("hme"));
    }

    [Fact]
    public void Leaves_other_values()
    {
        _run.SetValue("ControleCasa", "\"C:\\x\\outro.exe\"");
        _run.SetValue("Discord", "discord.exe");
        Assert.False(HmeStartup.MigrateRunKey(_run, @"C:\apps\hme.exe"));
        Assert.Equal("\"C:\\x\\outro.exe\"", _run.GetValue("ControleCasa"));
        Assert.Null(_run.GetValue("hme"));
    }

    [Fact]
    public void Startup_toggle_writes_and_removes_hme()
    {
        HmeStartup.Set(_run, true, @"C:\apps\hme.exe");
        Assert.True(HmeStartup.IsOn(_run));
        HmeStartup.Set(_run, false, @"C:\apps\hme.exe");
        Assert.False(HmeStartup.IsOn(_run));
    }

    [Fact]
    public void Engine_version_matches_the_standalone_project()
    {
        var csproj = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "standalone", "hme.csproj");
        var version = System.Xml.Linq.XDocument.Load(csproj).Descendants("Version").First().Value;
        Assert.Equal(version, HmeEngine.Version);
    }
}
