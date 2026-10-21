# Código e arquitetura

## Stack real

| Item | Valor (do `ClonaDock.csproj`) |
|---|---|
| Runtime | .NET 8, `net8.0-windows10.0.19041.0`, `OutputType=WinExe` |
| UI | WPF (`UseWPF`) + Windows Forms (`UseWindowsForms`, só para `NotifyIcon` e `System.Drawing.Icon` da bandeja) |
| Pacotes NuGet | `Microsoft.Data.Sqlite` 8.0.10 (único; usado apenas em `BadgeWatcher`) |
| APIs do Windows | P/Invoke (`Native/Win32.cs` e afins), COM (`IShellItemImageFactory`, `ShellLink`), WinRT (`Windows.Media.Control`, no widget Tocando agora) |
| GC | Workstation, não concorrente, `ConserveMemory=5`, `TieredPGO` |
| Qualidade | Analisadores .NET com `AnalysisLevel=8.0` e `CodeAnalysisTreatWarningsAsErrors=true` |
| Testes | Nenhum projeto de teste existe |
| Manifesto | `app.manifest`: DPI `PerMonitorV2`, compatível com Windows 10/11 |

Persistência de configurações é **JSON**, não SQLite. SQLite só lê o banco de notificações do Windows.

## Mapa do código

```
Program.cs            ponto de entrada (STAThread); modos auxiliares sem WPF
App.xaml(.cs)         Application: estilos de menu, ciclo de vida, aplica tema e configurações de sistema
Core/                 AppSettings (modelo), SettingsStore (JSON), Theme (cores), Log
Dock/                 DockWindow, DockView, DockController, DockTile, GlassBar, BackdropWindow
Native/               Win32, AppBar, WindowTracker, TaskbarControl, IconLoader, BadgeWatcher,
                      Monitors, ShortcutResolver, SystemInfo, MemoryTrim
Ui/                   SettingsWindow, FlyoutWindow (base), LauncherWindow, StackFlyout, TrayIcon, InputDialog
Widgets/              WidgetBase (base), WidgetFactory, Time/Productivity/System/Weather/NowPlaying, Graphics
Assets/dock.ico       ícone do app e da bandeja (recurso WPF)
```

### Responsabilidades

| Componente | Responsabilidade |
|---|---|
| `Program` | Trata `--watchdog <pid>`, `--quit` e a checagem de instância única (mutex) antes de criar o WPF; só então instancia `App` |
| `App` | Cria `DockWindow`, `TrayIcon`, escuta eventos nomeados (`ClonaDock.ShowSettings`, `ClonaDock.Quit`) em threads de fundo, aplica barra de tarefas e Run key, trata exceções não tratadas (log) |
| `SettingsStore` | Carrega/salva `AppSettings` em `%APPDATA%\ClonaDock\settings.json`; evento estático `Changed` dispara reaplicação |
| `Theme` | Resolve cores a partir das configurações e do modo de cor do Windows (registro `Personalize`/`DWM`) |
| `DockWindow` | Janela transparente, sem foco (`WS_EX_NOACTIVATE`), posicionada na borda; registra a app bar; `DispatcherTimer` de 100 ms para auto-ocultar, tela cheia e `TaskbarControl.Enforce` |
| `BackdropWindow` | Janela separada atrás da barra que aplica o desfoque/acrílico do Windows (a janela do Dock é maior que a barra) |
| `DockView` | Painel que faz layout dos tiles, ampliação, rótulo, bounce, deslizar do auto-ocultar e arrastar/soltar; loop de quadros via `CompositionTarget.Rendering`, que para quando tudo assenta |
| `DockController` | Monta a lista de tiles (fixados, abertos, pasta/arquivo, Downloads, Lixeira), trata cliques, menus, drop, fixar/ocultar/renomear |
| `DockTile` | Um item visual (ícone ou widget), indicador de aberto, selo |
| `GlassBar` | Preenchimento e borda da barra, incluindo a borda "Liquid Glass" |
| `WindowTracker` | Lista de janelas de topo (como o Alt+Tab) via WinEvent hooks |
| `AppBar` | Reserva a borda da tela (`SHAppBarMessage`) e reage a tela cheia / reinício do Explorer |
| `TaskbarControl` | Oculta ou auto-oculta a barra de tarefas e a restaura, inclusive após falha |
| `BadgeWatcher` | Lê selos de notificação do `wpndatabase.db` |
| `IconLoader` | Ícones de alta resolução via `IShellItemImageFactory`, com cache |
| `ShortcutResolver` | Resolve `.lnk` para obter a chave de correspondência app fixado/aberto |
| `MemoryTrim` | `GC.Collect` compactante e `SetProcessWorkingSetSize` após startup e após fechar janelas |
| `FlyoutWindow` | Base de painéis que abrem junto a um tile e fecham ao perder o foco ou com Esc |
| `WidgetBase` | `Viewbox` com cartão de 100 unidades de altura, `DispatcherTimer` próprio e tema claro/escuro |

## Fluxo de inicialização

1. `Program.Main` (STA):
   - `--watchdog <pid>`: `TaskbarControl.RunWatchdog` espera o processo terminar e restaura a barra de tarefas se o marcador existir; retorna sem WPF.
   - `--quit`: sinaliza o evento `ClonaDock.Quit`; retorna.
   - Mutex `ClonaDock.SingleInstance`: se já existe, sinaliza `ClonaDock.ShowSettings`; retorna.
   - Caso contrário `new App()`, `InitializeComponent()`, `Run()` (`ShutdownMode=OnExplicitShutdown`).
2. `App.OnStartup`: threads ouvintes dos dois eventos; handlers de exceção (log, `TaskbarControl.Restore()` em erro fatal e no fim da sessão); `TaskbarControl.RecoverFromCrash()`; `SettingsStore.Load()`.
3. `new DockWindow()`, `Show()`, `Controller.Start()` (inicia `WindowTracker`, `ApplySettings`).
4. `ApplySystemSettings()`: modo da barra de tarefas (e watchdog se passou de "manter" para ocultar) e Run key.
5. `TrayIcon`, assinatura em `SettingsStore.Changed`, `MemoryTrim.Soon(…, 5)`; na primeira execução abre as Configurações.
6. Saída: `OnExit` descarta o ícone, fecha o Dock, `TaskbarControl.Restore()`, solta o mutex.

## Threading e modelo de UI

- Todo o WPF roda na thread de UI única (STA). Timers são `DispatcherTimer`, não threads.
- Threads de fundo: os dois ouvintes de eventos nomeados (voltam à UI via `Dispatcher.BeginInvoke`) e `Task.Run(Read)` do `BadgeWatcher` (cópia e leitura do banco).
- Chamadas assíncronas ao Open-Meteo e ao gerenciador de mídia (WinRT) nos widgets. Detalhes de sincronização além disso não foram auditados.
- Mudança de configuração: UI altera `SettingsStore.Current` e chama `Save()`, que grava e dispara `Changed`; `App` reaplica no `DockController` e nas configurações de sistema. A janela de Configurações usa debounce de 220 ms para sliders.

## Persistência

- `AppSettings` (`Core/AppSettings.cs`) serializado com `System.Text.Json`, enums como texto, indentado.
- Escrita em `settings.json.tmp` seguida de `File.Move(overwrite)`.
- Arquivo ilegível: registra no log, copia para `settings.json.broken` e cria padrões.
- Itens do Dock (`DockItem`) guardam estado de widgets em `Data` (dicionário string/string): texto de notas, data de contagem etc.
- Sem migrações de esquema nem campo de versão no JSON (não encontrado). Campos novos usam o valor padrão da classe.

## Como o Dock acompanha janelas e a barra de tarefas

- **Janelas**: `WindowTracker` registra WinEvent hooks (foreground, minimizar, destruir/ocultar, mudança de nome, cloak). Eventos disparam um debounce de 120 ms; há também um refresh de segurança a cada 3 s. `EnumWindows` filtra o que o Alt+Tab mostraria; resolve o exe do processo, lida com Store apps (`ApplicationFrameHost`, AppUserModelID) e com processos "helper" (Electron/CEF/Steam), atribuindo-os ao processo pai.
- **Casamento**: `DockController` casa janelas com tiles fixados pela chave em minúsculas do caminho (resolvido de `.lnk`) ou do `shell:AppsFolder\<AUMID>`.
- **Barra de tarefas**: `TaskbarControl` guarda o estado original (`ABM_GETSTATE`), grava `taskbar-restore.json`, liga auto-ocultar e, no modo "Ocultar por completo", esconde `Shell_TrayWnd`/`Shell_SecondaryTrayWnd`. Reaplica o ocultamento por hook de `EVENT_OBJECT_SHOW`, por `Enforce()` a cada ~1 s e respeita a tecla Windows e o Iniciar. A restauração vem de `OnExit`, `SessionEnding`, exceção fatal, `RecoverFromCrash` no próximo start e do processo watchdog.
- **Tela cheia**: callback da app bar (`ABN_FULLSCREENAPP`) e checagem a cada 500 ms do primeiro plano ocupar todo o monitor.

## Fronteiras de interop nativa

Concentradas em `Native/`: `Win32.cs` (declarações), `AppBar`, `WindowTracker`, `TaskbarControl`, `IconLoader`, `ShortcutResolver`, `Monitors`, `SystemInfo` (GetSystemTimes, memória, rede, registro Run, pastas conhecidas). Exceções fora de `Native/`: `DockWindow` e `BackdropWindow` usam `Win32` e `HwndSource` diretamente; `DockController` declara um `DllImport` próprio; `NowPlayingWidget` usa WinRT.

## Dívida de manutenção (acima da referência de 350 linhas)

| Arquivo | Linhas | Observação |
|---|---|---|
| `Dock/DockController.cs` | 801 | Mistura montagem de tiles, cliques, menus, diálogos e lançamento de apps; dividir por responsabilidade |
| `Dock/DockView.cs` | 523 | Layout, animação e arrastar no mesmo tipo; extrair o arrastar |
| `Ui/SettingsWindow.cs` | 487 | Cinco páginas em um só arquivo; uma classe por página |
| `Widgets/ProductivityWidgets.cs` | 390 | Quatro widgets e dois painéis em um arquivo; um arquivo por widget |
| `Native/Win32.cs` | 354 | Declarações P/Invoke; apenas acima da referência |

Outros grandes, ainda abaixo: `DockWindow.cs` 339, `WeatherWidget.cs` 298, `WindowTracker.cs` 284.

## Riscos e observações verificadas

- Modifica estado global do sistema (barra de tarefas, área de trabalho). O mecanismo de recuperação existe, mas depende do marcador em `%APPDATA%\ClonaDock`.
- Strings de interface estão fixas em pt-BR dentro do código.
- Sem automação de testes: regressões nas interações com o Windows só são vistas em execução manual.

## Manutenção

Atualize este mapa quando diretórios, dependências ou fronteiras mudarem. Registre apenas comportamento verificado.
