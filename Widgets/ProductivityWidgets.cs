using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shft.Core;
using Shft.Ui;

namespace Shft.Widgets;

public sealed class NoteWidget : WidgetBase
{
    TextBlock _text = null!;

    public NoteWidget(DockItem item) : base(item) { }

    public override string Title => "Nota adesiva";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override Brush CardBackground(bool dark) =>
        new LinearGradientBrush(Color.FromRgb(0xFF, 0xEB, 0x8A), Color.FromRgb(0xFF, 0xD8, 0x4D), 90);

    protected override void Build()
    {
        _text = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x10)),
            Margin = new Thickness(12, 10, 12, 10),
            FontFamily = new FontFamily("Segoe Print, Segoe UI"),
        };
        Body.Children.Add(_text);
    }

    protected override void Refresh()
    {
        var text = Item.Get("text");
        _text.Text = string.IsNullOrWhiteSpace(text) ? "Clique para escrever uma nota" : text;
        _text.Opacity = string.IsNullOrWhiteSpace(text) ? 0.6 : 1;
    }

    public override void OnClick(FrameworkElement anchor)
    {
        var flyout = new NoteFlyout(Item.Get("text") ?? "");
        flyout.Closed += (_, _) =>
        {
            Item.Set("text", flyout.Value);
            Save();
            Refresh();
        };
        flyout.ShowNear(anchor);
    }

    public override void AddMenuItems(ContextMenu menu) =>
        menu.Items.Add(MenuItem("Editar nota", () => OnClick(this)));

    sealed class NoteFlyout : FlyoutWindow
    {
        readonly TextBox _box;
        public string Value => _box.Text;

        public NoteFlyout(string text)
        {
            Paper = Color.FromRgb(0xFF, 0xE6, 0x80);
            _box = new TextBox
            {
                Text = text,
                Width = 300,
                Height = 220,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x10)),
                FontFamily = new FontFamily("Segoe Print, Segoe UI"),
                FontSize = 14,
            };
            Body = _box;
            Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
        }
    }
}

public sealed class TodoWidget : WidgetBase
{
    public sealed class Todo
    {
        public string Text { get; set; } = "";
        public bool Done { get; set; }
    }

    TextBlock _count = null!, _first = null!, _caption = null!;

    public TodoWidget(DockItem item) : base(item) { }

    public override string Title => "Lista de tarefas";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    internal List<Todo> Load()
    {
        try { return JsonSerializer.Deserialize<List<Todo>>(Item.Get("todos") ?? "[]") ?? new(); }
        catch { return new(); }
    }

    internal void Store(List<Todo> todos)
    {
        Item.Set("todos", JsonSerializer.Serialize(todos));
        Save();
        Refresh();
    }

    protected override void Build()
    {
        _caption = Text(12, null, Muted, HorizontalAlignment.Left);
        _count = Text(34, FontWeights.SemiBold, null, HorizontalAlignment.Left);
        _first = Text(12, null, null, HorizontalAlignment.Left);
        _count.Margin = new Thickness(0, -4, 0, -2);
        var s = new StackPanel { Margin = new Thickness(14, 10, 12, 10), VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(_caption);
        s.Children.Add(_count);
        s.Children.Add(_first);
        Body.Children.Add(s);
    }

    protected override void Refresh()
    {
        var todos = Load();
        var open = todos.Where(t => !t.Done).ToList();
        _caption.Text = "Tarefas";
        _count.Text = open.Count.ToString();
        _first.Text = open.FirstOrDefault()?.Text ?? (todos.Count > 0 ? "Tudo feito" : "Clique para adicionar");
    }

    public override void OnClick(FrameworkElement anchor) => new TodoFlyout(this).ShowNear(anchor);

    public override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(MenuItem("Limpar concluídas", () => Store(Load().Where(t => !t.Done).ToList())));
    }

    sealed class TodoFlyout : FlyoutWindow
    {
        readonly TodoWidget _owner;
        readonly StackPanel _list = new();
        readonly TextBox _input;

        public TodoFlyout(TodoWidget owner)
        {
            _owner = owner;
            var root = new StackPanel { Width = 300 };
            root.Children.Add(Heading("Lista de tarefas"));
            root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            _input = new TextBox { Margin = new Thickness(0, 8, 0, 6), Padding = new Thickness(8, 5, 8, 5) };
            _input.SetResourceReference(BackgroundProperty, "MenuHoverBrush");
            _input.SetResourceReference(ForegroundProperty, "MenuTextBrush");
            _input.SetResourceReference(BorderBrushProperty, "MenuBorderBrush");
            _input.SetResourceReference(TextBox.CaretBrushProperty, "MenuTextBrush");
            _input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(_input.Text)) return;
                var todos = _owner.Load();
                todos.Add(new Todo { Text = _input.Text.Trim() });
                _owner.Store(todos);
                _input.Clear();
                Fill();
            };
            root.Children.Add(_input);
            root.Children.Add(Subtle("Digite e tecle Enter para adicionar."));
            var clear = FlatButton(new TextBlock { Text = "Limpar concluídas" }, () =>
            {
                _owner.Store(_owner.Load().Where(t => !t.Done).ToList());
                Fill();
            });
            clear.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(clear);
            Body = root;
            Fill();
            Loaded += (_, _) => _input.Focus();
        }

        void Fill()
        {
            _list.Children.Clear();
            var todos = _owner.Load();
            if (todos.Count == 0) _list.Children.Add(Subtle("Nada a fazer. Digite abaixo para adicionar."));
            for (int i = 0; i < todos.Count; i++)
            {
                int index = i;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var check = new CheckBox { IsChecked = todos[i].Done, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                check.Click += (_, _) =>
                {
                    var t = _owner.Load();
                    if (index < t.Count) { t[index].Done = check.IsChecked == true; _owner.Store(t); Fill(); }
                };
                var remove = FlatButton(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 }, () =>
                {
                    var t = _owner.Load();
                    if (index < t.Count) { t.RemoveAt(index); _owner.Store(t); Fill(); }
                }, 4);
                DockPanel.SetDock(check, System.Windows.Controls.Dock.Left);
                DockPanel.SetDock(remove, System.Windows.Controls.Dock.Right);
                var text = new TextBlock { Text = todos[i].Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                if (todos[i].Done) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = 0.55; }
                row.Children.Add(check);
                row.Children.Add(remove);
                row.Children.Add(text);
                _list.Children.Add(row);
            }
        }
    }
}

public sealed class FocusTimerWidget : WidgetBase
{
    enum Phase { Focus, ShortBreak, LongBreak }

    Ring _ring = null!;
    TextBlock _time = null!, _phase = null!;
    Phase _current = Phase.Focus;
    bool _running;
    DateTime _endsAt;
    TimeSpan _remaining;
    int _sessions;

    public FocusTimerWidget(DockItem item) : base(item)
    {
        _remaining = Length(Phase.Focus);
    }

    public override string Title => "Timer de foco";
    public override double WidthUnits => 1;

    int FocusMinutes => int.TryParse(Item.Get("focus"), out var m) ? m : 25;

    TimeSpan Length(Phase p) => p switch
    {
        Phase.Focus => TimeSpan.FromMinutes(FocusMinutes),
        Phase.ShortBreak => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15),
    };

    static string PhaseName(Phase p) => p switch { Phase.Focus => "Foco", Phase.ShortBreak => "Pausa curta", _ => "Pausa longa" };

    protected override void Build()
    {
        var color = Color.FromRgb(0xFF, 0x5F, 0x57);
        _ring = new Ring { Width = 84, Height = 84, Stroke = 7, Fill = new SolidColorBrush(color), TrackBrush = Track };
        _time = Text(19, FontWeights.SemiBold);
        _phase = Text(10, null, Muted);
        var center = Stack(_time, _phase);
        Body.Children.Add(_ring);
        Body.Children.Add(center);
    }

    protected override void Refresh()
    {
        if (_running)
        {
            _remaining = _endsAt - DateTime.Now;
            if (_remaining <= TimeSpan.Zero) Finish();
        }
        var total = Length(_current);
        _ring.Value = 1 - _remaining.TotalSeconds / Math.Max(1, total.TotalSeconds);
        _ring.Fill = new SolidColorBrush(_current == Phase.Focus ? Color.FromRgb(0xFF, 0x5F, 0x57) : Color.FromRgb(0x4C, 0xD9, 0x64));
        _time.Text = $"{(int)_remaining.TotalMinutes:00}:{_remaining.Seconds:00}";
        _phase.Text = _running ? PhaseName(_current) : $"{PhaseName(_current)} · pausado";
    }

    void Finish()
    {
        _running = false;
        SystemSounds.Asterisk.Play();
        if (_current == Phase.Focus)
        {
            _sessions++;
            _current = _sessions % 4 == 0 ? Phase.LongBreak : Phase.ShortBreak;
        }
        else _current = Phase.Focus;
        _remaining = Length(_current);
    }

    public override void OnClick(FrameworkElement anchor)
    {
        if (_running)
        {
            _remaining = _endsAt - DateTime.Now;
            _running = false;
        }
        else
        {
            _endsAt = DateTime.Now + _remaining;
            _running = true;
        }
        Refresh();
    }

    public override void AddMenuItems(ContextMenu menu)
    {
        var lengths = new MenuItem { Header = "Duração do foco" };
        foreach (var m in new[] { 15, 25, 30, 45, 50, 60 })
        {
            int minutes = m;
            lengths.Items.Add(MenuItem($"{minutes} min", () =>
            {
                Item.Set("focus", minutes.ToString());
                Save();
                if (_current == Phase.Focus) { _running = false; _remaining = Length(Phase.Focus); }
                Refresh();
            }, FocusMinutes == minutes));
        }
        menu.Items.Add(lengths);
        menu.Items.Add(MenuItem("Pular para a próxima fase", () => { Finish(); Refresh(); }));
        menu.Items.Add(MenuItem("Recomeçar", () =>
        {
            _running = false;
            _current = Phase.Focus;
            _sessions = 0;
            _remaining = Length(Phase.Focus);
            Refresh();
        }));
    }
}

public sealed class WaterWidget : WidgetBase
{
    Ring _ring = null!;
    TextBlock _count = null!, _caption = null!;

    public WaterWidget(DockItem item) : base(item) { }

    public override string Title => "Beber água";
    public override double WidthUnits => 1;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    int Goal => int.TryParse(Item.Get("goal"), out var g) && g > 0 ? g : 8;

    int Count
    {
        get
        {
            if (Item.Get("date") != DateTime.Today.ToString("yyyy-MM-dd")) return 0;
            return int.TryParse(Item.Get("count"), out var c) ? c : 0;
        }
        set
        {
            Item.Set("date", DateTime.Today.ToString("yyyy-MM-dd"));
            Item.Set("count", Math.Max(0, value).ToString());
            Save();
            Refresh();
        }
    }

    protected override void Build()
    {
        _ring = new Ring { Width = 84, Height = 84, Stroke = 7, Fill = new SolidColorBrush(Color.FromRgb(0x3E, 0xA8, 0xFF)), TrackBrush = Track };
        _count = Text(24, FontWeights.SemiBold);
        _caption = Text(10, null, Muted);
        Body.Children.Add(_ring);
        Body.Children.Add(Stack(_count, _caption));
    }

    protected override void Refresh()
    {
        int c = Count, g = Goal;
        _ring.Value = (double)c / g;
        _count.Text = c.ToString();
        _caption.Text = c >= g ? "Meta batida" : $"de {g} copos";
    }

    public override void OnClick(FrameworkElement anchor) => Count = Count + 1;

    public override void AddMenuItems(ContextMenu menu)
    {
        menu.Items.Add(MenuItem("Desfazer um copo", () => Count = Count - 1));
        menu.Items.Add(MenuItem("Zerar o dia", () => Count = 0));
        var goal = new MenuItem { Header = "Meta do dia" };
        foreach (var n in new[] { 4, 6, 8, 10, 12 })
        {
            int value = n;
            goal.Items.Add(MenuItem($"{value} copos", () => { Item.Set("goal", value.ToString()); Save(); Refresh(); }, Goal == value));
        }
        menu.Items.Add(goal);
    }
}
