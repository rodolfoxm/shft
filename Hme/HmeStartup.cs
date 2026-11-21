using Microsoft.Win32;

namespace Shft.Hme;

/// <summary>hme.exe starting with Windows, through the "hme" value of the user's Run key.</summary>
public static class HmeStartup
{
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "hme";

    public static RegistryKey? OpenRun() => Registry.CurrentUser.OpenSubKey(RunPath, writable: true);

    static string Command(string exe) => $"\"{exe}\" --bandeja";

    /// <summary>Controle Casa's own start entry (its exe or its Python script) gives way to hme.exe.</summary>
    public static bool MigrateRunKey(RegistryKey run, string hmeExe)
    {
        if (run.GetValue("ControleCasa") is not string old || !old.Contains("ControleCasa", StringComparison.OrdinalIgnoreCase)) return false;
        run.SetValue(Name, Command(hmeExe));
        run.DeleteValue("ControleCasa", false);
        return true;
    }

    public static bool IsOn(RegistryKey run) => run.GetValue(Name) is string;

    public static void Set(RegistryKey run, bool on, string hmeExe)
    {
        if (on) run.SetValue(Name, Command(hmeExe));
        else run.DeleteValue(Name, false);
    }
}
