using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClonaDock.Core;
using ClonaDock.Dock;
using ClonaDock.Ui;

namespace ClonaDock.Widgets;

public sealed class ClockWidget : WidgetBase
{
    TextBlock _time = null!, _sub = null!;

    static readonly (string Label, string Id)[] Zones =
    {
        ("São Paulo", "E. South America Standard Time"),
        ("Nova York", "Eastern Standard Time"),
        ("Los Angeles", "Pacific Standard Time"),
        ("Cidade do México", "Central Standard Time (Mexico)"),
        ("Londres", "GMT Standard Time"),
        ("Lisboa", "GMT Standard Time"),
        ("Paris", "Romance Standard Time"),
        ("Berlim", "W. Europe Standard Time"),
        ("Dubai", "Arabian Standard Time"),
        ("Índia", "India Standard Time"),
        ("Tóquio", "Tokyo Standard Time"),
        ("Sydney", "AUS Eastern Standard Time"),
    };

    public ClockWidget(DockItem item) : base(item) { }

    public override string Title => Item.Get("city") is string c ? $"Relógio · {c}" : "Relógio";

    bool Seconds => Item.Get("seconds") == "1";

    protected override void Build()
    {
        _time = Text(Seconds ? 31 : 40, FontWeights.SemiBold);
        _sub = Text(13, null, Muted);
        Body.Children.Add(Stack(_time, _sub));
    }

    protected override void Refresh()
    {
        var now = DateTime.Now;
        if (Item.Get("tz") is string id)
        {
            try { now = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch { }
        }
        _time.Text = now.ToString(Seconds ? "HH:mm:ss" : "HH:mm");
        _sub.Text = Item.Get("city") ?? now.ToString("ddd, d 'de' MMM", CultureInfo.CurrentCulture);
    }

    public override void OnClick(FrameworkElement anchor) => DockController.Open("ms-clock:");

    public override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(MenuItem("Mostrar segundos", () =>
        {
            Item.Set("seconds", Seconds ? null : "1");
            Save();
            ApplyTheme(Dark);
        }, Seconds));

        var zones = new MenuItem { Header = "Fuso horário" };
        zones.Items.Add(MenuItem("Hora deste PC", () => SetZone(null, null), Item.Get("tz") == null));
        foreach (var (label, id) in Zones)
            zones.Items.Add(MenuItem(label, () => SetZone(label, id), Item.Get("city") == label));
        menu.Items.Add(zones);
        menu.Items.Add(MenuItem("Abrir Relógio", () => DockController.Open("ms-clock:")));
    }

    void SetZone(string? label, string? id)
    {
        Item.Set("tz", id);
        Item.Set("city", label);
        Save();
        Refresh();
    }
}

public sealed class CalendarWidget : WidgetBase
{
    TextBlock _weekday = null!, _day = null!;

    public CalendarWidget(DockItem item) : base(item) { }

    public override string Title => DateTime.Now.ToString("dddd, d 'de' MMMM", CultureInfo.CurrentCulture);
    public override double WidthUnits => 1;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(20);

    protected override void Build()
    {
        _weekday = Text(15, FontWeights.SemiBold, new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
        _day = Text(52, FontWeights.Light);
        _day.Margin = new Thickness(0, -6, 0, 0);
        Body.Children.Add(Stack(_weekday, _day));
    }

    protected override void Refresh()
    {
        var now = DateTime.Now;
        _weekday.Text = now.ToString("ddd", CultureInfo.CurrentCulture).TrimEnd('.').ToUpperInvariant();
        _day.Text = now.Day.ToString();
    }

    public override void OnClick(FrameworkElement anchor) => new CalendarFlyout().ShowNear(anchor);

    public override void AddMenuItems(ContextMenu menu) =>
        menu.Items.Add(MenuItem("Abrir Calendário", () => DockController.Open("outlookcal:")));

    sealed class CalendarFlyout : FlyoutWindow
    {
        public CalendarFlyout()
        {
            var root = new StackPanel();
            root.Children.Add(Heading(DateTime.Now.ToString("dddd, d 'de' MMMM 'de' yyyy", CultureInfo.CurrentCulture)));
            root.Children.Add(new System.Windows.Controls.Calendar { SelectedDate = DateTime.Today, DisplayDate = DateTime.Today });
            var open = FlatButton(new TextBlock { Text = "Abrir Calendário  ›" }, () => { SafeClose(); DockController.Open("outlookcal:"); });
            open.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(open);
            Body = root;
        }
    }
}

public sealed class CountdownWidget : WidgetBase
{
    TextBlock _top = null!, _number = null!, _unit = null!;

    public CountdownWidget(DockItem item) : base(item) { }

    public override string Title => Item.Get("label") is string l ? $"Contagem regressiva · {l}" : "Contagem regressiva";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    DateTime? Target => DateTime.TryParse(Item.Get("target"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    protected override void Build()
    {
        _top = Text(13, null, Muted);
        _number = Text(40, FontWeights.SemiBold);
        _unit = Text(13, null, Muted);
        _number.Margin = new Thickness(0, -4, 0, -4);
        Body.Children.Add(Stack(_top, _number, _unit));
    }

    protected override void Refresh()
    {
        if (Target is not DateTime target)
        {
            _top.Text = "Contagem regressiva";
            _number.Text = "—";
            _unit.Text = "Clique para escolher";
            return;
        }
        var left = target - DateTime.Now;
        _top.Text = Item.Get("label") ?? target.ToString("d 'de' MMM", CultureInfo.CurrentCulture);
        if (left.TotalSeconds <= 0)
        {
            _number.Text = "0";
            _unit.Text = "Chegou o dia";
        }
        else if (left.TotalDays >= 1)
        {
            int days = (int)Math.Ceiling(left.TotalDays);
            _number.Text = days.ToString();
            _unit.Text = days == 1 ? "dia" : "dias";
        }
        else
        {
            _number.Text = $"{(int)left.TotalHours}h{left.Minutes:00}";
            _unit.Text = "faltam";
        }
    }

    public override void OnClick(FrameworkElement anchor) => Edit();

    public override void AddMenuItems(ContextMenu menu) => menu.Items.Add(MenuItem("Mudar data…", Edit));

    void Edit()
    {
        var current = Target?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? DateTime.Today.AddDays(30).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var text = InputDialog.Ask("Contagem regressiva", "Uma data e hora, como no exemplo (dd/mm/aaaa hh:mm).", current);
        if (text == null) return;
        if (!DateTime.TryParseExact(text.Trim(), new[] { "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "d/M/yyyy", "d/M/yyyy H:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            MessageBox.Show("Data não reconhecida. Use dd/mm/aaaa.", "ClonaDock");
            return;
        }
        var label = InputDialog.Ask("Contagem regressiva", "Contando até… (opcional)", Item.Get("label") ?? "");
        Item.Set("target", date.ToString("s", CultureInfo.InvariantCulture));
        Item.Set("label", string.IsNullOrWhiteSpace(label) ? null : label.Trim());
        Save();
        Refresh();
    }
}

public sealed class TimeProgressWidget : WidgetBase
{
    readonly Bar[] _bars = new Bar[3];
    readonly TextBlock[] _values = new TextBlock[3];

    public TimeProgressWidget(DockItem item) : base(item) { }

    public override string Title => "Progresso do tempo";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override void Build()
    {
        var grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        string[] names = { "Dia", "Semana", "Ano" };
        Color[] colors = { Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x7E, 0xE0, 0x81), Color.FromRgb(0xFF, 0xA9, 0x4D) };
        for (int i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var label = Text(12, null, Muted, HorizontalAlignment.Left);
            label.Text = names[i];
            label.VerticalAlignment = VerticalAlignment.Center;
            _bars[i] = new Bar { Height = 8, Fill = new SolidColorBrush(colors[i]), TrackBrush = Track, VerticalAlignment = VerticalAlignment.Center };
            _values[i] = Text(12, FontWeights.SemiBold, null, HorizontalAlignment.Right);
            _values[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, i); Grid.SetRow(_bars[i], i); Grid.SetRow(_values[i], i);
            Grid.SetColumn(_bars[i], 1); Grid.SetColumn(_values[i], 2);
            grid.Children.Add(label); grid.Children.Add(_bars[i]); grid.Children.Add(_values[i]);
        }
        Body.Children.Add(grid);
    }

    protected override void Refresh()
    {
        var now = DateTime.Now;
        double day = now.TimeOfDay.TotalHours / 24;
        int dow = ((int)now.DayOfWeek + 6) % 7; // Monday first
        double week = (dow + day) / 7;
        var yearStart = new DateTime(now.Year, 1, 1);
        double year = (now - yearStart).TotalDays / (DateTime.IsLeapYear(now.Year) ? 366 : 365);
        double[] v = { day, week, year };
        for (int i = 0; i < 3; i++)
        {
            _bars[i].Value = v[i];
            _values[i].Text = $"{v[i] * 100:0}%";
        }
    }
}
