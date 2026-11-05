# Design e acessibilidade

## Direção

- Nome: shft., sempre em minúsculas e com o ponto.
- Público: usuário único de Windows que quer um desktop próprio, inspirado no macOS sem copiá-lo.
- Idioma: português do Brasil, texto fixo no código.
- Tom: minimalismo extremo, vidro com moderação, cantos generosos, movimento curto e preciso.

## Sistema visual existente

| Fonte da verdade | O que define |
|---|---|
| `Core/Theme.cs` | Cores da barra, borda, indicador, rótulo, menu e destaque; resolvidas a partir de `AppSettings` e do modo de cor do Windows |
| `App.xaml` | Pincéis `MenuBackBrush`, `MenuTextBrush`, `MenuSubtleBrush`, `MenuHoverBrush`, `MenuBorderBrush`, `MenuSeparatorBrush`, `AccentBrush` (substituídos em tempo de execução por `App.ApplyTheme`); fontes `UiFont` (Segoe UI Variable Text / Segoe UI) e `IconFont` (Segoe Fluent Icons / Segoe MDL2 Assets); estilo de `ContextMenu` e de itens |
| `Dock/GlassBar.cs` | Superfície da barra e borda (fina ou "Liquid Glass") |
| `Dock/BackdropWindow.cs` | Desfoque/acrílico atrás da barra |
| `Widgets/WidgetBase.cs` | Cartão do widget: 100 unidades de altura, raio 22, gradiente claro/escuro, fonte Segoe UI Variable Display |
| `Widgets/Graphics.cs` | Elementos gráficos próprios: `Ring`, `Bar`, `Sparkline`, `WeatherGlyph` |

Os valores de cor ficam nesses arquivos; este documento não os repete. Padrões de `AppSettings` relevantes: ícone 48 px, ampliação 1,7x, opacidade da barra 0,45, raio 18, folga da borda 6 px, vidro e acrílico ligados.

## Linguagem compartilhada

- **Cor:** tudo vem de `Theme`; `App.ApplyTheme` publica `TextBrush`, `TextSubtleBrush`, `TileBrush`, `TileHoverBrush`, `TileOnBrush`, `TileOnTextBrush`, `TileRimBrush` e `PanelTintBrush`.
- **Ícones:** Segoe Fluent Icons, em `Ui/GlassControls.cs` (`Glyph`).
- **Controles:** `CapsuleSlider`, `ControlTile` (desligado, ligado, passando o mouse, foco, indisponível, aguardando), `SwitchToggle`, em `Ui/GlassControls.cs`.
- **Movimento:** `Core/Motion.cs`. Abrir: opacidade em 130 ms e mola em escala e posição em 300 ms. Fechar: 100 ms. Pressionar: escala de 0,95. A preferência fica em Configurações > Animações; Automático segue "Efeitos de animação" do Windows, e Reduzido troca tudo por transições curtas.
- **Painéis:** cartão com raio de 18 a 26, foto desfocada do fundo com saturação 1,35, tinta do tema e borda Liquid Glass. Sem foto possível, cartão quase opaco.

## O Dock

- **Barra**: preenchimento tingido (tema escuro `#1C1C20`, claro `#F4F4F6`, ou a cor escolhida) com borda de 1 px; opcionalmente borda Liquid Glass (brilho no topo e franja vermelho/ciano). Nunca totalmente transparente (alfa mínimo 1) para não perder cliques.
- **Vidro**: desfoque ("Leve") ou acrílico (padrão) sob a barra, em janela separada.
- **Tiles**: ícones de alta resolução; ampliação suave sob o ponteiro (1,0x desliga); indicador em bolinha para apps abertos (cor configurável); selo vermelho para notificações; rótulo com o nome ao passar o mouse; bounce ao abrir app.
- **Seções**: início (botão de apps, fixados, espaçadores, widgets), abertos (após separador) e fim (pastas, arquivos, Downloads, Lixeira, após separador).
- **Bordas**: embaixo, esquerda, direita, topo; ocultar automaticamente desliza a barra para fora.
- **Menus de contexto**: estilo próprio escuro/claro com sombra e cantos de 9 px.

## Painéis (flyouts)

`FlyoutWindow` é a base: janela sem borda, sempre no topo, abre ao lado do elemento clicado, fecha ao perder a ativação ou com Esc. Painéis: Central de Controle, Applications (620 px, lista virtualizada), notificações, bandeja, calendário, pasta/Downloads (até 24 itens mais recentes, 420 px), calendário mensal, previsão de 6 dias, nota, lista de tarefas, escolha de cidade e `InputDialog`.

## Janela de Configurações

Tem identidade própria e superfícies sólidas, sem vidro. Os tokens ficam em `Ui/SettingsTheme.xaml`, mesclado só nessa janela: barra, Dock e painéis seguem o tema compartilhado. Ela é sempre escura; "Claro ou escuro" vale para os outros componentes.

| Token | Cor |
|---|---|
| Fundo principal | `#0F1114` |
| Sidebar | `#171A1F` |
| Superfícies | `#23272E` |
| Superfície elevada | `#323943` |
| Divisórias | `#343941` |
| Texto principal | `#E6E7EA` |
| Texto secundário | `#9CA3AF` |
| Destaque | `#8EA4FF` |

Os componentes ficam em `Ui/SettingsKit.cs`: página, seção, cartão, linha, botão, switch, slider, lista, campo e captura de atalho. Uma linha indisponível diz o motivo no lugar da descrição, e um atalho ocupado mostra o erro na própria linha. Espaçamento em múltiplos de 4 px; janela de 960x680, mínimo 760x480.

| Página | Conteúdo |
|---|---|
| Aparência | Tema, transparência e desfoque dos painéis, borda Liquid Glass |
| Barra superior | Mostrar, monitores, reservar espaço, altura, modo do fundo, opacidade, contraste adaptativo, logo, nome, itens, relógio |
| Dock | Borda, monitor, distância, desfoque, opacidade, cantos, cores, ícones, ampliação |
| Itens do Dock | Botão de Applications, apps abertos, Downloads, Lixeira, lista de itens, apps ocultos |
| Central de Controle | Atalho, ícones fixados da bandeja |
| Applications | Atalho, favoritos, recentes |
| Animações | Modo de movimento, velocidade |
| Comportamento | Reservar espaço, ocultar o Dock, atraso, tela cheia |
| Inicialização e sistema | Iniciar com o Windows, barra de tarefas |
| Widgets | Largura, clima, widgets |
| Sobre | Versão, créditos, pasta de dados, encerrar |

## Marca

Símbolo de duas barras diagonais paralelas, com pontas arredondadas. `scripts/make-branding.ps1` gera tudo em `branding/`, o `Assets/shft.ico` e as imagens do instalador a partir de uma única geometria. O nome é sempre `shft.`, em minúsculas e com o ponto.

## Widgets (13)

Relógio, Calendário, Clima, Nota adesiva, Lista de tarefas, Timer de foco (Pomodoro), Atividade do sistema (CPU/memória), Atividade de rede, Bateria, Beber água, Contagem regressiva, Progresso do tempo, Tocando agora. Atualização por `DispatcherTimer` próprio (padrão 1 s; Tocando agora 2 s). Largura de 1,2x a 3x o ícone (`TileWidth`). Todos seguem o tema claro/escuro.

## Estados

- Vazio: "Nada aqui ainda" (pasta vazia), "Nada chamado …" (busca), "Nenhum widget ainda".
- Erro: falha ao ler pasta mostra a mensagem no painel; falha ao abrir app ou fechar app de administrador mostra `MessageBox`; demais erros vão para o log.
- Carregamento: ícones do lançador carregam depois que a grade aparece. Estados de carregamento dos widgets (por exemplo, clima sem rede) não foram auditados.

## Acessibilidade (observações verificadas)

Estado atual, sem alegar conformidade:

- A barra, os painéis e as Configurações têm nomes acessíveis (`AutomationProperties`), e foram operados por UI Automation nos testes. Os tiles do Dock continuam desenhados por `DockView`/`DockTile`, sem nomes.
- O Dock não recebe foco de teclado por desenho (`WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE` retorna "não ativar"); a interação com os tiles é por mouse. Não há atalho de teclado global nem navegação por setas no Dock.
- Teclado existente: atalhos globais configuráveis; Esc fecha painéis; setas, Enter, Page Up/Down em Applications; sliders aceitam setas e roda do mouse; menus de contexto WPF navegáveis por setas (`DirectionalNavigation=Cycle`); a janela de Configurações usa controles WPF padrão (Tab, espaço).
- Dicas (tooltips) em itens do lançador, pastas e cores; rótulo flutuante no Dock.
- Movimento: barra e painéis seguem `Core/Motion.cs`; o pulo do Dock também. A ampliação e o deslizar do auto-ocultar do Dock seguem os próprios ajustes.
- Contraste: não medido. Menus, painéis e Configurações acompanham o tema.
- Alto contraste do Windows: não tratado explicitamente.

## Revisões

Nenhuma revisão formal de interface ou acessibilidade registrada no repositório.

## Manutenção

Mudanças visuais atualizam `Theme.cs`, `App.xaml` ou o componente correspondente e, se uma regra mudar, este documento.
