# Vidro de verdade — plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: superpowers:subagent-driven-development (recomendada) ou
> superpowers:executing-plans. Passos com caixas (`- [ ]`).

**Objetivo:** refração ao vivo (Liquid Glass de verdade) no Dock e nos painéis flutuantes do shft. e do hme., com a
lupa do Windows como fonte, visível em gravações, custo quase zero parado e zero em tela cheia.

**Arquitetura:** lupa (fundo sem as janelas do shft.) → janela da lupa sob o vidro (alfa 1) → captura dessa janela →
shader D3D11 → swap chain de composição numa janela de vidro sob a janela WPF dona. Uma thread para todas as lupas;
sinal de mudança (regiões alteradas do monitor) e eventos de janela acordam o vidro; política de ritmo adaptativa.

**Tecnologia:** .NET 8 WPF, Vortice (Direct3D11, DXGI, DirectComposition, D3DCompiler), Windows.Graphics.Capture,
Magnification API e SetWinEventHook por P/Invoke, xUnit.

**Desenho:** `docs/superpowers/specs/2026-11-22-real-glass-design.md`.
**Protótipo de referência (provado):** `%TEMP%\claude\...\scratchpad\glass-spike2` (`Glass.cs`, `MagHost.cs`,
`Program.cs`, `Glass.hlsl`); a tarefa 1 traz a bancada para o repositório antes de tudo.

## Restrições globais

- Nenhuma janela do shft. ganha `SetWindowDisplayAffinity`/`WDA_EXCLUDEFROMCAPTURE`.
- Vidro real atrás de `AppSettings.RealGlass` (padrão **false**); só onde o "Liquid Glass" da superfície está ligado
  (`LiquidGlass` no Dock, `PanelRim` nos painéis); Dock com `Glass == false` ("Fundo = nenhum") fica sem vidro.
- Ritmo: sinal e capturas com `MinUpdateInterval` 33 ms; lupa a 30 FPS por 400 ms após mudança relevante, senão a
  cada 2 s; quadro igual ao anterior (cópia 1/8) não é desenhado nem apresentado.
- Tela cheia no primeiro plano de um monitor: sessões, sinal e lupa daquele monitor fechados/parados, texturas e
  swap chains liberadas, vidro escondido, zero quadros. Superfície visível sobre tela cheia: só a cor do vidro.
- Uma única thread dona de todas as lupas (`MagInitialize` uma vez). Área lida pela lupa sempre dentro do monitor.
- Regiões alteradas totalmente dentro das nossas próprias janelas são ignoradas.
- Guarda: sem quadro por 2 s com a superfície ativa, perda do dispositivo ou falha da lupa → reserva (vidro atual),
  nova tentativa em 30 s, três falhas seguidas → vidro real desligado até reiniciar; nunca bloquear a thread da UI.
- Reserva sem aviso (só log): Windows sem `GraphicsCaptureSession.DirtyRegionMode` (< 10.0.26100) sinal só por
  eventos; Windows 10, sessão remota, sem D3D11 → vidro atual.
- TFM `net8.0-windows10.0.26100.0` (shft.csproj, standalone/hme.csproj, testes); `SupportedOSPlatformVersion` fica
  10.0.19041.0. Pacotes Vortice.* 3.8.3 (MIT).
- Crédito da ótica (Glasmio, Apache 2.0, que adapta AndroidLiquidGlass, Apache 2.0) em `THIRD-PARTY-NOTICES.md` e no
  topo do HLSL. Nenhum asset da Apple ou do Glasmio.
- Commits terminam com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; nada de push ou release sem pedido.

## Pontos de atenção na revisão

1. **Monitor com escala ≠ 100 %:** retângulos da lupa, do vidro e da captura sempre em pixels físicos; o vidro casa
   com o cartão. Teste em `GlassGeometryTests` (tarefa 3) com escala 1,5.
2. **Monitor desconectado ou resolução mudando com vidro ativo:** a superfície daquele monitor é recriada ou vai à
   reserva, sem exceção na UI. Teste de política na tarefa 6 (`MonitorGone_goes_to_fallback`).
3. **Tela bloqueada / UAC (área de trabalho segura):** a captura para de entregar quadros; a guarda leva à reserva e
   volta depois, sem contar como falha definitiva. Teste na tarefa 6 (`Stall_while_locked_is_not_a_failure`).
4. **Painel aberto no monitor 2 com o Dock no monitor 1:** lupa e sinal do monitor certo; a lista de exclusão contém
   as janelas dos dois. Coberto pela bancada (tarefa 12, cenário "painel no monitor 2").
5. **Animação reduzida (`Motion.Reduced`):** o vidro do painel aparece com a opacidade, sem escala, igual ao cartão.
   Teste na tarefa 9 (`Reduced_motion_glass_follows_opacity_only`).

---

### Task 1: bancada no repositório e prova do sincronismo de painel

**Arquivos:** criar `tools/glass-bench/` (copiar o protótipo: `GlassBench.csproj`, `Native.cs`, `Scene.cs`,
`Glass.cs`, `MagHost.cs`, `Program.cs`, `Glass.hlsl`, `app.manifest`, `run.sh`); excluir `tools/**` do `shft.csproj`.

- [ ] Copiar o protótipo como está; `dotnet build tools/glass-bench -c Release` passa.
- [ ] Novo modo `panel` na bancada: um painel WPF 340×420 que abre e fecha com as mesmas animações de `Motion.Open`
      (`fromScale 0.95`, `fromY -8`, opacidade 130 ms, mola 300 ms) e `Motion.Close` (100 ms, escala 0,97), com o
      vidro sincronizado **por quadro**: em `CompositionTarget.Rendering`, ler escala, deslocamento e opacidade do
      elemento animado e aplicar à transformação e à opacidade do visual DirectComposition do vidro, `Commit()`.
- [ ] Medir: 20 aberturas e 20 fechamentos; a cada quadro, comparar o retângulo do cartão (WPF) com o do vidro
      (DComp) em pixels físicos. Esperado: diferença ≤ 1 px em todos os quadros; registrar no `live.log`.
- [ ] Se passar de 1 px: parar e relatar ao dono antes da tarefa 9.
- [ ] Commit `test(glass): bancada do vidro no repositorio e prova do sincronismo do painel`.

### Task 2: ajustes e projeto

**Arquivos:** `Core/AppSettings.cs`, `Ui/SettingsWindow.cs`, `shft.csproj`, `standalone/hme.csproj`,
`tests/shft.Tests/shft.Tests.csproj`, `THIRD-PARTY-NOTICES.md`; teste em `tests/shft.Tests/ShftTests.cs`.

**Produz:** `bool AppSettings.RealGlass` (padrão false); `GlassStyle AppSettings.GlassStyle` (padrão `Regular`,
enum já existe em `Ui/LiquidGlass.cs`).

- [ ] Testes `RealGlassSettingsTests`: `new AppSettings().RealGlass == false`; `GlassStyle == GlassStyle.Regular`;
      JSON antigo sem os campos carrega com esses padrões.
- [ ] Ver falhar; implementar; TFM e pacotes Vortice nos três projetos; crédito no `THIRD-PARTY-NOTICES.md`.
- [ ] Página Aparência: "Vidro real (experimental)" — "Refrata ao vivo o que está atrás do Dock e dos painéis.
      Pausa sozinho em jogos e tela cheia." e "Estilo do vidro" (Regular / Claro).
- [ ] Testes passam; commit `feat(glass): ajustes do vidro real (experimental)`.

### Task 3: ótica e geometria (puras)

**Arquivos:** criar `Native/Glass/GlassOptics.cs`; teste `tests/shft.Tests/GlassOpticsTests.cs`.

**Produz:**
- `static double GlassOptics.Displacement(double signedDistance, double height, double amount)` =
  `amount * (1 - sqrt(u*(2-u)))`, `u = clamp(-sd/height, 0, 1)`.
- `static double GlassOptics.RoundedSdf(Point p, Size size, double radius, out Vector normal)`.
- `static Int32Rect GlassGeometry.SourceRect(Int32Rect glassPx, int margin, Int32Rect monitorPx)` (glass + margem,
  deslocada para caber no monitor, nunca cortada).
- `static Int32Rect GlassGeometry.ToPhysical(Rect dip, double scale)`.

- [ ] Testes:
  - `Displacement(0, 18, 14) == 14`; `Displacement(-18, 18, 14) == 0`; `Displacement(-9, 18, 14)` ≈ `14*(1-√0.75)`.
  - `RoundedSdf` no centro < 0, na borda ≈ 0, fora > 0; normal no meio da borda de cima = (0,−1); no canto, unitária.
  - `SourceRect` de um vidro colado no fundo de 1920×1080 com margem 48: base em 1080, altura preservada.
  - `GlassGeometryTests.Scale_150`: `ToPhysical(new Rect(10, 20, 100, 50), 1.5) == (15, 30, 150, 75)` (ponto 1).
- [ ] Ver falhar; implementar; passar; commit `feat(glass): otica e geometria do vidro`.

### Task 4: dispositivo e shader

**Arquivos:** criar `Native/Glass/GlassShader.hlsl` (recurso embutido), `Native/Glass/GlassDevice.cs`.

**Consome:** tarefa 3 (mesma fórmula no HLSL). **Produz:**
- `sealed class GlassDevice : IDisposable` com `static GlassDevice? TryCreate(out string? why)`, `ID3D11Device Device`,
  `object Gate`, `IDirect3DDevice WinRt`, `void Draw(GlassDraw d)`.
- `record struct GlassDraw(ID3D11RenderTargetView Target, ID3D11ShaderResourceView Source, int Width, int Height,
  float Radius, float OffsetX, float OffsetY, int SourceWidth, int SourceHeight, GlassStyle Style, bool Dark,
  float Blur, Color? Tint)`.
- Parâmetros iniciais: `height 18`, `amount 14`; `Regular`: desfoque = `PanelBlur` (padrão 10) + 4; `Clear`: 2.
- `bool GlassDevice.SameAsLast(ID3D11ShaderResourceView src, int w, int h, ref byte[]? previous)` (cópia 1/8).

- [ ] HLSL do protótipo sem o modo "máscara" (a lupa não vê o Dock); cabeçalho de crédito.
- [ ] Teste `GlassDeviceTests.Creates_or_explains` (pula se não houver GPU): `TryCreate` devolve dispositivo ou
      motivo, nunca lança.
- [ ] Commit `feat(glass): dispositivo Direct3D e shader do vidro`.

### Task 5: política de ritmo e de mudança (pura)

**Arquivos:** criar `Native/Glass/GlassPacing.cs`; teste `tests/shft.Tests/GlassPacingTests.cs`.

**Produz:**
- `sealed class GlassPacing(Func<long> now, long ticksPerSecond)` com `void Changed()`, `long NextRefresh(long lastRefresh)`,
  `bool Active`. Constantes: ativo 400 ms, ocioso 2 s, 30 FPS.
- `static bool GlassPacing.Relevant(Int32Rect dirty, Int32Rect area, IReadOnlyList<Int32Rect> ours)`: falso se a
  região está toda dentro de alguma das nossas; verdadeiro se toca a área.

- [ ] Testes: depois de `Changed()`, `NextRefresh(t) == t + 1/30 s` por 400 ms e `t + 2 s` depois;
      `Relevant` ignora região dentro do host da lupa, aceita região maior que cruza a área, ignora região longe.
- [ ] Ver falhar; implementar; commit `feat(glass): ritmo adaptativo e filtro de mudancas`.

### Task 6: guarda (pura)

**Arquivos:** criar `Native/Glass/GlassWatchdog.cs`; teste `tests/shft.Tests/GlassWatchdogTests.cs`.

**Produz:** `sealed class GlassWatchdog(Func<long> now, long tps)` com `void Frame()`, `void Failed(string why)`,
`void Locked(bool locked)`, `GlassHealth State` (`Live`, `Fallback`, `Off`), `bool ShouldRetry`.

- [ ] Testes:
  - `Stall_2s_goes_to_fallback`; `Retry_after_30s`; `Three_failures_turn_it_off`.
  - `Stall_while_locked_is_not_a_failure` (ponto 3); `MonitorGone_goes_to_fallback` (ponto 2: `Failed("monitor")`).
- [ ] Ver falhar; implementar; commit `feat(glass): guarda do vidro real`.

### Task 7: lupa, sinal e eventos de janela

**Arquivos:** criar `Native/Glass/MagnifierHost.cs`, `Native/Glass/ChangeSignal.cs`, `Native/Glass/WindowEvents.cs`.

**Consome:** tarefa 5. **Produz:**
- `static class MagnifierHost`: `void Invoke(Action)`, `void Add(IGlassSource)`, `void Remove(IGlassSource)`,
  `void Wake()`, `void Shutdown()`; thread única STA; timer de alta resolução para a próxima superfície devida.
- `interface IGlassSource { Int32Rect ScreenArea { get; } bool Paused { get; } long NextRefresh { get; } void Refresh(); void Poke(); }`.
- `sealed class ChangeSignal : IDisposable` (por monitor): `ChangeSignal(IntPtr monitor, Func<IReadOnlyList<Int32Rect>> ours, Action<Int32Rect> onRelevant)`;
  `DirtyRegionMode.ReportOnly`, cursor e borda desligados, 33 ms.
- `static class WindowEvents`: `void Start(Action<Int32Rect?> onWindowChange)`, `void Stop()`; eventos 0x8001–0x8003,
  0x800B (só `OBJID_WINDOW`), 0x0003, 0x0016–0x0017, `WINEVENT_SKIPOWNPROCESS`.
- [ ] Teste de integração `MagnifierHostTests.Filter_list_works_for_two_controls` (pula sem área de trabalho
      interativa): duas lupas na mesma thread, `MagSetWindowFilterList` verdadeiro nas duas.
- [ ] Commit `feat(glass): lupa numa thread so, sinal de mudanca e eventos de janela`.

### Task 8: superfície de vidro

**Arquivos:** criar `Native/Glass/GlassSurface.cs`; teste `tests/shft.Tests/GlassSurfaceTests.cs`.

**Consome:** tarefas 3–7. **Produz:** `sealed class GlassSurface : IGlassSource, IDisposable` com
`static GlassSurface? TryCreate(IntPtr ownerHwnd, GlassDevice dev, out string? why)`, `void SetShape(Int32Rect glassPx, double radiusPx)`,
`void Show()`, `void Hide()`, `void Pause()`, `void Resume()`, `IDCompositionVisual Visual`, `event Action<GlassHealth>? HealthChanged`,
`static Func<IReadOnlyList<IntPtr>> OurWindows` (lista de exclusão: Dock, barras, painéis abertos, vidros, hosts).

- [ ] Janela de vidro (`WS_EX_NOREDIRECTIONBITMAP|TOOLWINDOW|NOACTIVATE|TRANSPARENT|LAYERED`) logo abaixo da dona;
      host da lupa (alfa 1) logo abaixo do vidro; captura da janela do host; render com `SameAsLast`.
- [ ] Teste `Shape_keeps_source_inside_monitor` (usa `GlassGeometry`, sem janelas).
- [ ] Teste de integração `Live_frame_has_no_owner_pixels` (pula sem desktop): dona com um quadrado vermelho sobre
      fundo azul; a primeira imagem da lupa não tem pixel vermelho.
- [ ] Commit `feat(glass): superficie de vidro com lupa, captura e render`.

### Task 9: painéis

**Arquivos:** modificar `Ui/FlyoutWindow.cs`; teste em `tests/shft.Tests/ShftTests.cs`.

**Consome:** tarefa 8. **Produz:** `static GlassDevice? GlassRuntime.Device` (criado sob demanda, `null` se reserva).

- [ ] Em `Dress()`: com `RealGlass && PanelRim && !HighContrast` e dispositivo disponível, criar a `GlassSurface` do
      painel (forma = `Frame` menos a margem da sombra, raio = `Radius`) em vez da foto; senão, caminho atual.
- [ ] Sincronismo por quadro como provado na tarefa 1: durante `Motion.Open/Close`, em `CompositionTarget.Rendering`,
      copiar escala, deslocamento (origem = `_origin`) e opacidade de `Frame` para `Visual`; parar quando as animações
      terminam; `Close` remove a superfície depois do fade.
- [ ] Teste `Reduced_motion_glass_follows_opacity_only` (ponto 5): com `Motion.Reduced`, a função que calcula a
      transformação do vidro a partir de `Frame` devolve escala 1 e só opacidade.
- [ ] Bancada: cenário "painel no monitor 2" (ponto 4) sem fantasma nem recursão.
- [ ] Commit `feat(glass): vidro real nos paineis, acompanhando a animacao`.

### Task 10: Dock

**Arquivos:** modificar `Dock/DockWindow.cs` (`SyncBackdrop`), `Dock/DockView.cs` (`GlassBar` sem corpo quando o vidro real está ativo).

- [ ] Em `SyncBackdrop`: com `RealGlass && LiquidGlass && Glass` e dispositivo disponível, usar uma `GlassSurface`
      no lugar do `BackdropWindow` (mesmo retângulo físico, recortado ao monitor; raio da barra); a magnificação só
      muda a forma (`SetShape`), sem recriar.
- [ ] `_fullScreen` verdadeiro → `Pause()`; falso → `Resume()`. Auto-ocultar escondido → `Hide()`.
- [ ] Commit `feat(glass): vidro real no Dock`.

### Task 11: tela cheia, monitores e hme.exe

**Arquivos:** `Bar/MenuBars.cs`, `App.xaml.cs` (WM_DISPLAYCHANGE), `Hme/HmeApp.cs`.

- [ ] Por monitor: com app em tela cheia no primeiro plano (`Monitors.FullScreenWindow`), pausar todas as superfícies
      daquele monitor e parar seu `ChangeSignal`; superfície ainda visível vira só cor.
- [ ] `WM_DISPLAYCHANGE`: recriar superfícies e sinais (ponto 2).
- [ ] `hme.exe`: `GlassRuntime` disponível; o painel do hme. usa o mesmo caminho da tarefa 9.
- [ ] Commit `feat(glass): pausa em tela cheia, troca de monitores e hme.exe`.

### Task 12: medição e liberação

**Arquivos:** `tools/glass-bench/` (cenários do shft. real), `docs/glass-measurements.md`.

- [ ] Bateria no PC do dono, controle antes de cada configuração: parado, animação longe, movimento atrás,
      2 monitores, tela cheia (com foco), painel abrindo, Print Screen.
- [ ] Metas do desenho: parado ≤ 1,5 % acima do controle; acordar ≤ 60 ms p90 (1 monitor); tela cheia = zero
      quadros e zero sessões; Print Screen mostra Dock e vidro; parado ≤ 1 acordada por segundo.
- [ ] Números em `docs/glass-measurements.md`. Se tudo passar, perguntar ao dono antes de virar `RealGlass` padrão.
- [ ] Commit `docs(glass): medicoes do vidro real`.
