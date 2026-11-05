# shft.

## Problema e objetivo

O Dock (ClonaDock) e a barra superior (Seelen UI modificado com logo própria) rodavam como dois programas, e o Seelen sozinho ocupava cerca de 750 MB em 10 processos, por usar WebView2. O shft. reúne tudo num único app nativo, com identidade própria, minimalista e inspirada no macOS, sem perder os controles reais do Windows.

Sucesso medido: Dock e barra juntos com menos de 100 MB privados e menos de 0,5% de um núcleo em repouso (medido em 04/11/2026: 80 MB e 0,08%).

Distribuído pelo GitHub Releases, com instalador por usuário e pacote portátil, sob a licença PolyForm Noncommercial 1.0.0.

## Público e papéis

- Um único tipo de usuário: a pessoa logada no Windows. Não há contas, papéis nem autenticação.
- Idioma da interface: português do Brasil (textos fixos no código).

## Fluxos essenciais

| Fluxo | Ação | Resultado esperado |
|---|---|---|
| Abrir/ativar app fixado | Clicar no ícone | Se não há janela, inicia o app; se há uma, traz para frente (ou minimiza se já é a ativa); com várias, alterna entre elas |
| Nova janela | Clique do meio ou Shift+clique | Inicia o app mesmo já aberto |
| Ver apps abertos | Abrir qualquer app | Ícone aparece após um separador, com indicador de "aberto"; some ao fechar |
| Organizar | Arrastar para reordenar; arrastar para fora remove; soltar arquivo/pasta/atalho adiciona | Itens salvos em `settings.json` |
| Menu de contexto | Botão direito no ícone ou na barra | Opções do item (abrir, nova janela, manter no Dock, fechar janelas, renomear, remover) e da barra (adicionar app/pasta/espaçador/widget, posição, barra de tarefas, ocultar automático) |
| Widgets | Adicionar em Configurações > Widgets ou no menu da barra; clicar no widget | Tile vivo; alguns abrem painel (calendário, previsão, nota, tarefas) |
| Pasta/Downloads | Clicar | Painel com os 24 itens mais recentes |
| Lançador | Clicar no botão de apps | Grade pesquisável dos atalhos do menu Iniciar |
| Configurações | Ícone da bandeja, menu da barra, ou abrir o app de novo | Janela com Geral, Aparência, Itens do Dock, Widgets e Sobre; mudanças aplicam na hora |
| Barra superior | Clicar nos indicadores | Abre o painel correspondente; o mesmo botão fecha |
| Central de Controle | Ícone na barra, atalho ou `--toggle-controls` | Controles lidos do Windows e aplicados nele; indisponíveis aparecem esmaecidos |
| Applications | Botão no Dock, atalho (padrão Alt+Espaço) ou `--toggle-apps` | Busca com setas, Enter e Esc; favoritos e recentes |
| Limpar RAM | Clicar na memória da barra | O Windows pede elevação (UAC); ao fim, aviso com o total liberado |
| Primeira execução | Iniciar sem `settings.json` | Dock com Explorador, Edge, Chrome (quando instalados), Bloco de Notas, Gerenciador de Tarefas e widgets Relógio, Calendário e Atividade do sistema; Configurações abre sozinha |
| Encerrar | Bandeja ou menu da barra > Encerrar, ou `shft.exe --quit` | Barra de tarefas e espaço reservado restaurados |
| Iniciar com o Windows | Ligar em Geral | Valor `shft.` na chave Run do usuário |

## Fora de escopo (não implementado)

- Atualização automática dentro do app; assinatura de código.
- Internacionalização: só pt-BR.
- Qualquer coisa que dependa de código do PrimoDock original.

## Requisitos e restrições

- Windows 10 build 19041 ou superior (`net8.0-windows10.0.19041.0`), .NET 8, x64/AnyCPU conforme o SDK (não fixado no `.csproj`).
- Baixo consumo de memória: GC de estação de trabalho, sem GC concorrente, `System.GC.ConserveMemory=5` e compactação do heap após abrir/fechar janelas (`MemoryTrim`).
- DPI por monitor (`PerMonitorV2` no manifesto).
- Rede apenas no widget Clima (Open-Meteo, envia só nome pesquisado e coordenadas da cidade). Nenhuma outra chamada de rede encontrada.
- Selos de notificação leem o banco local do Windows (`wpndatabase.db`) em uma cópia temporária, somente linhas de selo.
- Efeitos no sistema: oculta/auto-oculta a barra de tarefas (Explorer), reserva área de trabalho (app bar), grava na chave Run.

## Limites conhecidos

- Bluetooth: liga, desliga e lista pareados; conectar um dispositivo específico abre as configurações do Windows (não há API pública para isso em dispositivos clássicos).
- Wi-Fi: rede com senha desconhecida abre o seletor do Windows.
- Não perturbe: abre a página do Windows; não há API pública.
- Luz noturna edita o estado interno do Windows; num formato desconhecido o botão fica indisponível.
- Vidro dos painéis: é uma foto desfocada tirada ao abrir; não acompanha o que muda atrás enquanto o painel está aberto.
- Menus do app ativo: só apps com barra de menu clássica; menus montados na hora de abrir podem vir vazios.
- Bandeja: apps que não reanunciam o ícone só aparecem na próxima atualização dele.

## Critérios de aceite

Observáveis (verificação manual, sem testes automatizados):

1. Com a barra de tarefas em "Deixar visível", o Dock aparece na borda escolhida, e janelas maximizadas param acima dele (com "Manter as janelas fora do Dock" ligado).
2. Abrir e fechar um app não fixado cria e remove o ícone na seção de abertos.
3. Mudar posição, tamanho, tema ou vidro em Configurações altera o Dock sem reiniciar.
4. Reordenar e remover itens persiste após reiniciar o app.
5. Ocultar a barra de tarefas e encerrar o shft. (inclusive matando o processo) devolve a barra de tarefas ao estado original.
6. Um app em tela cheia no mesmo monitor esconde o Dock (se a opção estiver ligada).
7. Abrir o executável uma segunda vez não cria outra barra; abre as configurações.

Técnicos: `dotnet build` conclui sem erros, com analisadores tratando avisos CA como erro (ver [docs/onboarding.md](docs/onboarding.md)).

## Premissas e pendências

- Não verificado em execução nesta documentação: os critérios acima foram derivados do código, não testados.
- Não há definição de próxima entrega no repositório.

## Manutenção

Atualize quando objetivo, escopo, restrições ou critérios mudarem. Detalhes técnicos: [docs/architecture.md](docs/architecture.md).
