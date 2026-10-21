using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClonaDock.Core;
using ClonaDock.Ui;

namespace ClonaDock.Widgets;

public sealed record Place(string Name, string Region, double Latitude, double Longitude)
{
    public override string ToString() => string.IsNullOrEmpty(Region) ? Name : $"{Name}, {Region}";
}

public sealed record DayForecast(DateTime Date, double Max, double Min, int Code);

public sealed record Forecast(double Temperature, double FeelsLike, int Code, bool IsDay, IReadOnlyList<DayForecast> Days, DateTime Fetched);

/// <summary>Weather from Open-Meteo (free, no key). Only the chosen city's coordinates are sent.</summary>
public static class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static Forecast? _cached;
    static (double, double) _cachedFor;
    static Task<Forecast?>? _pending;

    static WeatherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ClonaDock/0.1");
    }

    public static async Task<IReadOnlyList<Place>> Search(string name)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=8&language=pt&format=json&name={Uri.EscapeDataString(name)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var list = new List<Place>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            string region = string.Join(", ", new[] { "admin1", "country" }
                .Select(k => r.TryGetProperty(k, out var v) ? v.GetString() : null)
                .Where(v => !string.IsNullOrEmpty(v)));
            list.Add(new Place(r.GetProperty("name").GetString() ?? "", region,
                r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return list;
    }

    public static Task<Forecast?> Get(double lat, double lon, bool force = false)
    {
        if (!force && _cached != null && _cachedFor == (lat, lon) && DateTime.Now - _cached.Fetched < TimeSpan.FromMinutes(20))
            return Task.FromResult<Forecast?>(_cached);
        if (_pending != null && !_pending.IsCompleted) return _pending;
        _pending = Fetch(lat, lon);
        return _pending;
    }

    static async Task<Forecast?> Fetch(double lat, double lon)
    {
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?" +
                      $"latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                      "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=6";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var days = new List<DayForecast>();
            var times = daily.GetProperty("time").EnumerateArray().ToList();
            var maxs = daily.GetProperty("temperature_2m_max").EnumerateArray().ToList();
            var mins = daily.GetProperty("temperature_2m_min").EnumerateArray().ToList();
            var codes = daily.GetProperty("weather_code").EnumerateArray().ToList();
            for (int i = 0; i < times.Count; i++)
                days.Add(new DayForecast(DateTime.Parse(times[i].GetString()!, inv), maxs[i].GetDouble(), mins[i].GetDouble(), codes[i].GetInt32()));

            _cached = new Forecast(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("apparent_temperature").GetDouble(),
                cur.GetProperty("weather_code").GetInt32(), cur.GetProperty("is_day").GetInt32() == 1, days, DateTime.Now);
            _cachedFor = (lat, lon);
            return _cached;
        }
        catch (Exception ex)
        {
            Log.Write("weather: " + ex.Message);
            return _cached;
        }
    }

    public static string Temp(double celsius)
    {
        bool f = SettingsStore.Current.Fahrenheit;
        return $"{(f ? celsius * 9 / 5 + 32 : celsius):0}Â°";
    }
}

public sealed class WeatherWidget : WidgetBase
{
    WeatherGlyph _glyph = null!;
    TextBlock _temp = null!, _city = null!, _range = null!;

    public WeatherWidget(DockItem item) : base(item) { }

    public override string Title => string.IsNullOrEmpty(SettingsStore.Current.WeatherCity) ? "Clima" : $"Clima Â· {SettingsStore.Current.WeatherCity}";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override Brush CardBackground(bool dark) =>
        new LinearGradientBrush(Color.FromRgb(0x3B, 0x7D, 0xD8), Color.FromRgb(0x23, 0x4E, 0x9A), 90);

    protected override void Build()
    {
        var white = Brushes.White;
        var soft = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
        var grid = new Grid { Margin = new Thickness(10, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        _glyph = new WeatherGlyph { Width = 58, Height = 58, VerticalAlignment = VerticalAlignment.Center };
        _temp = Text(30, FontWeights.SemiBold, white, HorizontalAlignment.Left);
        _city = Text(11, null, soft, HorizontalAlignment.Left);
        _range = Text(11, null, soft, HorizontalAlignment.Left);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_city);
        right.Children.Add(_temp);
        right.Children.Add(_range);
        Grid.SetColumn(right, 1);
        grid.Children.Add(_glyph);
        grid.Children.Add(right);
        Body.Children.Add(grid);
    }

    protected override async void Refresh()
    {
        var s = SettingsStore.Current;
        if (string.IsNullOrEmpty(s.WeatherCity))
        {
            _city.Text = "Clima";
            _temp.Text = "â€”";
            _range.Text = "Escolha uma cidade";
            _glyph.Code = 2;
            return;
        }
        _city.Text = s.WeatherCity;
        var f = await WeatherService.Get(s.WeatherLatitude, s.WeatherLongitude);
        if (f == null)
        {
            _temp.Text = "â€”";
            _range.Text = "Sem conexÃ£o";
            return;
        }
        _glyph.Code = f.Code;
        _glyph.IsDay = f.IsDay;
        _temp.Text = WeatherService.Temp(f.Temperature);
        var today = f.Days.FirstOrDefault();
        _range.Text = today != null ? $"â†‘{WeatherService.Temp(today.Max)} â†“{WeatherService.Temp(today.Min)}" : WeatherGlyph.Describe(f.Code);
        ToolTip = $"{WeatherGlyph.Describe(f.Code)} Â· sensaÃ§Ã£o de {WeatherService.Temp(f.FeelsLike)}";
    }

    public override void OnClick(FrameworkElement anchor)
    {
        if (string.IsNullOrEmpty(SettingsStore.Current.WeatherCity)) PickCity();
        else new ForecastFlyout().ShowNear(anchor);
    }

    public override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(MenuItem("Trocar cidadeâ€¦", PickCity));
        menu.Items.Add(MenuItem("Fahrenheit (Â°F)", () =>
        {
            SettingsStore.Current.Fahrenheit = !SettingsStore.Current.Fahrenheit;
            SettingsStore.Save(notify: false);
            Refresh();
        }, SettingsStore.Current.Fahrenheit));
        menu.Items.Add(MenuItem("Atualizar agora", async () =>
        {
            var s = SettingsStore.Current;
            await WeatherService.Get(s.WeatherLatitude, s.WeatherLongitude, force: true);
            Refresh();
        }));
    }

    void PickCity()
    {
        var place = CityPicker.Ask();
        if (place == null) return;
        var s = SettingsStore.Current;
        s.WeatherCity = place.Name;
        s.WeatherLatitude = place.Latitude;
        s.WeatherLongitude = place.Longitude;
        SettingsStore.Save(notify: false);
        _ = WeatherService.Get(place.Latitude, place.Longitude, force: true).ContinueWith(_ => Dispatcher.BeginInvoke(Refresh));
    }

    sealed class ForecastFlyout : FlyoutWindow
    {
        public ForecastFlyout()
        {
            var s = SettingsStore.Current;
            var root = new StackPanel { Width = 300 };
            root.Children.Add(Heading(s.WeatherCity));
            var status = Subtle("Carregando a previsÃ£oâ€¦");
            root.Children.Add(status);
            var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            root.Children.Add(rows);
            Body = root;
            Loaded += async (_, _) =>
            {
                var f = await WeatherService.Get(s.WeatherLatitude, s.WeatherLongitude);
                if (f == null)
                {
                    status.Text = "Confira sua conexÃ£o e abra de novo.";
                    return;
                }
                status.Text = $"{WeatherGlyph.Describe(f.Code)} Â· {WeatherService.Temp(f.Temperature)} Â· sensaÃ§Ã£o de {WeatherService.Temp(f.FeelsLike)}";
                foreach (var d in f.Days)
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    var day = new TextBlock { Text = d.Date.Date == DateTime.Today ? "Hoje" : d.Date.ToString("ddd, d/M", CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center };
                    var glyph = new WeatherGlyph { Code = d.Code, Width = 28, Height = 28 };
                    var temps = new TextBlock { Text = $"{WeatherService.Temp(d.Max)}  /  {WeatherService.Temp(d.Min)}", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                    Grid.SetColumn(glyph, 1);
                    Grid.SetColumn(temps, 2);
                    row.Children.Add(day);
                    row.Children.Add(glyph);
                    row.Children.Add(temps);
                    rows.Children.Add(row);
                }
            };
        }
    }
}

/// <summary>Search Open-Meteo's places by name and pick one.</summary>
public static class CityPicker
{
    public static Place? Ask()
    {
        var window = new Window
        {
            Title = "Escolha uma cidade",
            Width = 420,
            Height = 380,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
        };
        var box = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        var search = new Button { Content = "Buscar", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var list = new ListBox { Margin = new Thickness(0, 10, 0, 10) };
        var status = new TextBlock { Foreground = Brushes.Gray };
        var ok = new Button { Content = "Usar esta cidade", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false };
        Place? result = null;

        async void Run()
        {
            var q = box.Text.Trim();
            if (q.Length < 2) return;
            status.Text = "Buscandoâ€¦";
            try
            {
                var places = await WeatherService.Search(q);
                list.ItemsSource = places;
                status.Text = places.Count == 0 ? "Nenhuma cidade com esse nome." : "";
            }
            catch (Exception ex)
            {
                status.Text = "Sem conexÃ£o: " + ex.Message;
            }
        }

        search.Click += (_, _) => Run();
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is Place p) { result = p; window.DialogResult = true; } };
        ok.Click += (_, _) => { result = list.SelectedItem as Place; window.DialogResult = true; };

        var top = new DockPanel();
        DockPanel.SetDock(search, System.Windows.Controls.Dock.Right);
        top.Children.Add(search);
        top.Children.Add(box);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(ok, System.Windows.Controls.Dock.Bottom);
        DockPanel.SetDock(status, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(ok);
        root.Children.Add(status);
        root.Children.Add(list);
        window.Content = root;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? result : null;
    }
}
