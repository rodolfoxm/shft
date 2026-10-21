using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClonaDock.Core;
using Windows.Media.Control;

namespace ClonaDock.Widgets;

/// <summary>
/// What is playing in any app that reports to Windows' media controls (Spotify, browsers, players).
/// Click toggles play/pause.
/// </summary>
public sealed class NowPlayingWidget : WidgetBase
{
    static GlobalSystemMediaTransportControlsSessionManager? _manager;

    Image _art = null!;
    TextBlock _title = null!, _artist = null!, _state = null!;
    string? _lastTrack;
    bool _busy;

    public NowPlayingWidget(DockItem item) : base(item) { }

    public override string Title => "Tocando agora";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(2);

    protected override void Build()
    {
        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        _art = new Image { Width = 72, Height = 72, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
        var artHost = new Border { CornerRadius = new CornerRadius(10), Width = 72, Height = 72, ClipToBounds = true, Background = Track, Child = _art };
        artHost.Clip = new RectangleGeometry(new Rect(0, 0, 72, 72), 10, 10);
        _title = Text(13, FontWeights.SemiBold, null, HorizontalAlignment.Left);
        _artist = Text(11, null, Muted, HorizontalAlignment.Left);
        _state = Text(10, null, Muted, HorizontalAlignment.Left);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_artist);
        text.Children.Add(_state);
        Grid.SetColumn(text, 1);
        grid.Children.Add(artHost);
        grid.Children.Add(text);
        Body.Children.Add(grid);
        _lastTrack = null;
    }

    static async Task<GlobalSystemMediaTransportControlsSession?> Session()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager.GetCurrentSession();
        }
        catch (Exception ex)
        {
            Log.Write("now playing: " + ex.Message);
            return null;
        }
    }

    protected override async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var session = await Session();
            if (session == null)
            {
                _title.Text = "Nada tocando";
                _artist.Text = "";
                _state.Text = "";
                _art.Source = null;
                _lastTrack = null;
                return;
            }
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            _title.Text = string.IsNullOrWhiteSpace(props.Title) ? "Sem título" : props.Title;
            _artist.Text = props.Artist ?? "";
            _state.Text = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "▶ Tocando" : "❚❚ Pausado";

            var track = props.Title + "|" + props.Artist;
            if (track != _lastTrack)
            {
                _lastTrack = track;
                _art.Source = props.Thumbnail == null ? null : await LoadThumbnail(props.Thumbnail);
            }
        }
        catch (Exception ex)
        {
            Log.Write("now playing: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    static async Task<ImageSource?> LoadThumbnail(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            var copy = new MemoryStream();
            await net.CopyToAsync(copy);
            copy.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = copy;
            bmp.DecodePixelWidth = 144;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public override async void OnClick(FrameworkElement anchor)
    {
        if (await Session() is { } s) await s.TryTogglePlayPauseAsync();
        Refresh();
    }

    public override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(MenuItem("Tocar / pausar", () => OnClick(this)));
        menu.Items.Add(MenuItem("Próxima", async () => { if (await Session() is { } s) await s.TrySkipNextAsync(); }));
        menu.Items.Add(MenuItem("Anterior", async () => { if (await Session() is { } s) await s.TrySkipPreviousAsync(); }));
    }
}
