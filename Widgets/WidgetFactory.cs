using Shft.Core;

namespace Shft.Widgets;

public static class WidgetFactory
{
    public static WidgetBase Create(DockItem item) => item.Widget switch
    {
        WidgetKind.Clock => new ClockWidget(item),
        WidgetKind.Calendar => new CalendarWidget(item),
        WidgetKind.Weather => new WeatherWidget(item),
        WidgetKind.Note => new NoteWidget(item),
        WidgetKind.Todo => new TodoWidget(item),
        WidgetKind.FocusTimer => new FocusTimerWidget(item),
        WidgetKind.SystemActivity => new SystemActivityWidget(item),
        WidgetKind.Network => new NetworkWidget(item),
        WidgetKind.Battery => new BatteryWidget(item),
        WidgetKind.Water => new WaterWidget(item),
        WidgetKind.Countdown => new CountdownWidget(item),
        WidgetKind.TimeProgress => new TimeProgressWidget(item),
        WidgetKind.NowPlaying => new NowPlayingWidget(item),
        _ => new ClockWidget(item),
    };

    public static string Name(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => "Relógio",
        WidgetKind.Calendar => "Calendário",
        WidgetKind.Weather => "Clima",
        WidgetKind.Note => "Nota adesiva",
        WidgetKind.Todo => "Lista de tarefas",
        WidgetKind.FocusTimer => "Timer de foco",
        WidgetKind.SystemActivity => "Atividade do sistema",
        WidgetKind.Network => "Atividade de rede",
        WidgetKind.Battery => "Bateria",
        WidgetKind.Water => "Beber água",
        WidgetKind.Countdown => "Contagem regressiva",
        WidgetKind.TimeProgress => "Progresso do tempo",
        WidgetKind.NowPlaying => "Tocando agora",
        _ => kind.ToString(),
    };
}
