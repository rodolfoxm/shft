using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClonaDock.Core;
using ClonaDock.Dock;
using ClonaDock.Native;
using Forms = System.Windows.Forms;

namespace ClonaDock.Widgets;

public sealed class SystemActivityWidget : WidgetBase
{
    Ring _cpu = null!, _ram = null!;
    TextBlock _cpuText = null!, _ramText = null!;

    public SystemActivityWidget(DockItem item) : base(item) { }

    public override string Title => "Atividade do sistema";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(1.5);

    protected override void Build()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        (Ring ring, TextBlock value) Cell(int col, string name, Color color)
        {
            var ring = new Ring { Width = 60, Height = 60, Stroke = 6, Fill = new SolidColorBrush(color), TrackBrush = Track };
            var value = Text(14, FontWeights.SemiBold);
            var label = Text(10, null, Muted);
            label.Text = name;
            var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(ring);
            host.Children.Add(Stack(value, label));
            Grid.SetColumn(host, col);
            grid.Children.Add(host);
            return (ring, value);
        }
        (_cpu, _cpuText) = Cell(0, "CPU", Color.FromRgb(0x4C, 0xC2, 0xFF));
        (_ram, _ramText) = Cell(1, "Memória", Color.FromRgb(0xB4, 0x7B, 0xFF));
        Body.Children.Add(grid);
    }

    protected override void Refresh()
    {
        double cpu = SystemMeters.Cpu();
        var mem = SystemMeters.Memory();
        _cpu.Value = cpu;
        _cpuText.Text = $"{cpu * 100:0}%";
        _ram.Value = mem.Load;
        _ramText.Text = $"{mem.Load * 100:0}%";
        ToolTip = $"Memória: {SystemMeters.Bytes(mem.Used)} de {SystemMeters.Bytes(mem.Total)}";
    }

    public override void OnClick(FrameworkElement anchor) => DockController.Open("taskmgr.exe");

    public override void AddMenuItems(ContextMenu menu) =>
        menu.Items.Add(MenuItem("Abrir o Gerenciador de Tarefas", () => DockController.Open("taskmgr.exe")));
}

public sealed class NetworkWidget : WidgetBase
{
    Sparkline _chart = null!;
    TextBlock _down = null!, _up = null!;

    public NetworkWidget(DockItem item) : base(item) { }

    public override string Title => "Atividade de rede";

    protected override void Build()
    {
        var root = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        _down = Text(13, FontWeights.SemiBold, new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), HorizontalAlignment.Left);
        _up = Text(11, null, Muted, HorizontalAlignment.Right);
        var header = new Grid();
        header.Children.Add(_down);
        header.Children.Add(_up);
        _chart = new Sparkline
        {
            Line = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)),
            Area = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xC2, 0xFF)),
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(_chart, 1);
        root.Children.Add(header);
        root.Children.Add(_chart);
        Body.Children.Add(root);
    }

    protected override void Refresh()
    {
        if (!SystemMeters.IsOnline())
        {
            _down.Text = "Sem internet";
            _up.Text = "";
            _chart.Push(0);
            return;
        }
        var (down, up) = SystemMeters.Network();
        _down.Text = "↓ " + SystemMeters.Bytes(down, true);
        _up.Text = "↑ " + SystemMeters.Bytes(up, true);
        _chart.Push(down);
    }
}

public sealed class BatteryWidget : WidgetBase
{
    Ring _ring = null!;
    TextBlock _value = null!, _state = null!;

    public BatteryWidget(DockItem item) : base(item) { }

    public override string Title => "Bateria";
    public override double WidthUnits => 1;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(15);

    protected override void Build()
    {
        _ring = new Ring { Width = 84, Height = 84, Stroke = 7, TrackBrush = Track };
        _value = Text(20, FontWeights.SemiBold);
        _state = Text(10, null, Muted);
        Body.Children.Add(_ring);
        Body.Children.Add(Stack(_value, _state));
    }

    protected override void Refresh()
    {
        var p = Forms.SystemInformation.PowerStatus;
        if (p.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || p.BatteryLifePercent > 1)
        {
            _ring.Value = 1;
            _ring.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64));
            _value.Text = "⚡";
            _state.Text = "Na tomada";
            return;
        }
        double level = p.BatteryLifePercent;
        bool charging = p.PowerLineStatus == Forms.PowerLineStatus.Online;
        _ring.Value = level;
        _ring.Fill = new SolidColorBrush(level <= 0.2 && !charging ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x4C, 0xD9, 0x64));
        _value.Text = $"{level * 100:0}%";
        _state.Text = charging ? "Carregando" : "Na bateria";
    }

    public override void OnClick(FrameworkElement anchor) => DockController.Open("ms-settings:batterysaver");
}
