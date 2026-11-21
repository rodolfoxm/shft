using System.Net;
using Shft.Core;

namespace Shft.Hme;

/// <summary>
/// The one hme. engine of this user: whoever holds the mutex reads the cloud, writes the state files and answers on
/// the local port. shft. and hme.exe both go through here, so they never poll the cloud twice.
/// </summary>
public sealed class EngineHost : IDisposable
{
    public const string MutexName = @"Local\hme.engine";

    readonly Mutex _mutex;
    readonly bool _startApi;
    HttpListener? _api;

    EngineHost(Mutex mutex, HmeConfig cfg, Action show, Action quit, bool startApi)
    {
        (_mutex, Config, Show, Quit, _startApi) = (mutex, cfg, show, quit, startApi);
        Usage = new Usage(cfg.Folder);
    }

    public HmeConfig Config { get; private set; }
    public Usage Usage { get; }
    public IHome? Home { get; private set; }
    public string? BootError { get; private set; }
    public Action Show { get; }
    public Action Quit { get; }
    public event Action? Changed;

    /// <summary>Null when another process already owns the engine.</summary>
    public static EngineHost? TryAcquire(HmeConfig cfg, Action show, Action quit, string mutexName = MutexName, bool startApi = true)
    {
        var mutex = new Mutex(false, mutexName);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // the last owner crashed; the engine is free
        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        return new EngineHost(mutex, cfg, show, quit, startApi);
    }

    public async Task Start()
    {
        (Home, BootError) = await HmeEngineFactory.Create(Config, Usage);
        if (_startApi && _api == null) _api = LocalApi.Start(Config.Port, this);
        Changed?.Invoke();
    }

    /// <summary>After the settings were saved: a new home from the new config.</summary>
    public async Task Reload()
    {
        Config = HmeConfig.Load(Config.Folder);
        (Home, BootError) = await HmeEngineFactory.Create(Config, Usage);
        Changed?.Invoke();
    }

    /// <summary>Hands a home the settings window already built (demo, or a fresh connection) without connecting twice.</summary>
    public void Use(IHome? home, string? error = null)
    {
        (Home, BootError) = (home, error);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        try { _api?.Close(); }
        catch (ObjectDisposedException) { }
        Usage.Flush();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException ex) { Log.Write("hme: engine mutex released from another thread: " + ex.Message); }
        _mutex.Dispose();
    }
}
