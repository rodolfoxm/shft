# Design e acessibilidade

## Direção

- Público: usuário único de Windows 10/11 que quer o visual de Dock do macOS ([PRODUCT.md](../PRODUCT.md)).
- Idioma: português do Brasil, texto fixo no código.
- Dispositivo: desktop, vários monitores, DPI por monitor. Não há layout para toque nem janelas redimensionáveis responsivas (só a janela de Configurações redimensiona, com mínimo 700x480).
- Tom: vidro translúcido, cantos arredondados, ícones grandes que crescem sob o ponteiro.

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

## O Dock

- **Barra**: preenchimento tingido (tema escuro `#1C1C20`, claro `#F4F4F6`, ou a cor escolhida) com borda de 1 px; opcionalmente borda Liquid Glass (brilho no topo e franja vermelho/ciano). Nunca totalmente transparente (alfa mínimo 1) para não perder cliques.
- **Vidro**: desfoque ("Leve") ou acrílico (padrão) sob a barra, em janela separada.
- **Tiles**: ícones de alta resolução; ampliação suave sob o ponteiro (1,0x desliga); indicador em bolinha para apps abertos (cor configurável); selo vermelho para notificações; rótulo com o nome ao passar o mouse; bounce ao abrir app.
- **Seções**: início (botão de apps, fixados, espaçadores, widgets), abertos (após separador) e fim (pastas, arquivos, Downloads, Lixeira, após separador).
- **Bordas**: embaixo, esquerda, direita, topo; ocultar automaticamente desliza a barra para fora.
- **Menus de contexto**: estilo próprio escuro/claro com sombra e cantos de 9 px.

## Painéis (flyouts)

`FlyoutWindow` é a base: janela sem borda, sempre no topo, cartão de raio 12 com sombra, abre ao lado do tile no lado oposto à borda da tela, fecha ao perder a ativação ou com Esc. Painéis existentes: lançador de apps (640x480, busca, Enter abre o primeiro resultado), pasta/Downloads (até 24 itens mais recentes, 420 px), calendário mensal, previsão de 6 dias, nota, lista de tarefas, escolha de cidade e `InputDialog`.

## Janela de Configurações

Janela de 820x640 (mínimo 700x480), navegação lateral e páginas em cartões com linhas "rótulo, ajuda, controle". Mudanças aplicam ao Dock imediatamente.

| Página | Conteúdo |
|---|---|
| Geral | Borda, monitor, modo da barra de tarefas, reservar espaço, ocultar automaticamente e atraso, esconder em tela cheia, iniciar com o Windows |
| Aparência | Claro/escuro/automático, desfoque (leve/acrílico), opacidade, Liquid Glass, arredondamento, distância da borda, cores do fundo e do indicador; tamanho dos ícones, ampliação, nomes, selos, animação de abertura |
| Itens do Dock | Botão de apps e seu ícone, apps abertos, Downloads, Lixeira, lista de itens (adicionar app/pasta, subir, descer, remover), apps que nunca aparecem |
| Widgets | Largura dos tiles, cidade do clima, Fahrenheit, widgets presentes e disponíveis |
| Sobre | Versão, descrição, abrir pasta de dados, encerrar |

## Widgets (13)

Relógio, Calendário, Clima, Nota adesiva, Lista de tarefas, Timer de foco (Pomodoro), Atividade do sistema (CPU/memória), Atividade de rede, Bateria, Beber água, Contagem regressiva, Progresso do tempo, Tocando agora. Atualização por `DispatcherTimer` próprio (padrão 1 s; Tocando agora 2 s). Largura de 1,2x a 3x o ícone (`TileWidth`). Todos seguem o tema claro/escuro.

## Estados

- Vazio: "Nada aqui ainda" (pasta vazia), "Nada chamado …" (busca), "Nenhum widget ainda".
- Erro: falha ao ler pasta mostra a mensagem no painel; falha ao abrir app ou fechar app de administrador mostra `MessageBox`; demais erros vão para o log.
- Carregamento: ícones do lançador carregam depois que a grade aparece. Estados de carregamento dos widgets (por exemplo, clima sem rede) não foram auditados.

## Acessibilidade (observações verificadas)

Estado atual, sem alegar conformidade:

- Nenhum uso de `AutomationProperties` no código; os tiles são elementos desenhados por `DockView`/`DockTile` e não há evidência de nomes acessíveis para leitores de tela.
- O Dock não recebe foco de teclado por desenho (`WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE` retorna "não ativar"); a interação com os tiles é por mouse. Não há atalho de teclado global nem navegação por setas no Dock.
- Teclado existente: Esc fecha painéis; Enter na busca do lançador; menus de contexto WPF navegáveis por setas (`DirectionalNavigation=Cycle`); a janela de Configurações usa controles WPF padrão (Tab, espaço).
- Dicas (tooltips) em itens do lançador, pastas e cores; rótulo flutuante no Dock.
- Movimento: ampliação, bounce e deslizar do auto-ocultar são sempre animados; o código não consulta a preferência de redução de movimento do Windows (nenhuma ocorrência de `SystemParameters`). O usuário pode zerar a ampliação (1,0x) e desligar a animação de abertura.
- Contraste: não medido. A janela de Configurações usa cores fixas claras (`#F3F3F6`, cartões brancos) e não acompanha o tema escuro; já menus e painéis acompanham.
- Alto contraste do Windows: não tratado explicitamente.

## Revisões

Nenhuma revisão formal de interface ou acessibilidade registrada no repositório.

## Manutenção

Mudanças visuais atualizam `Theme.cs`, `App.xaml` ou o componente correspondente e, se uma regra mudar, este documento.
