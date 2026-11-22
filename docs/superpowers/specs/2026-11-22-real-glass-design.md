# Vidro de verdade (refração ao vivo) — desenho

Data: 2026-11-22 (revisado depois dos protótipos). Substitui o "sabor" Liquid Glass de `Ui/LiquidGlass.cs` nas
superfícies abaixo. Medições e imagens dos protótipos: `Documents/Clona Barra/vidro-prototipo/RELATORIO.md`.

## Intenção

O dono quer o Liquid Glass que **funciona de fato**: o vidro refrata, ao vivo, o que está atrás dele (outras janelas,
vídeo, papel de parede), como no iOS 26/27, e não uma imitação com brilho na borda. Referências: a API do
`expo-glass-effect` (estilos `regular`/`clear`, tinta, interativo) e a ótica do Glasmio (Apache 2.0).

Regras do dono:
- **Jogo ou app em tela cheia na frente: o vidro não pesa nada** — sem captura, sem quadros, sem animação.
- **Gravação e compartilhamento normais:** Dock, painéis **e o vidro** aparecem em Print Screen, Ferramenta de
  Captura, OBS, Discord e Teams. Nada do shft. pode ser escondido de capturas.
- Visual premium e desempenho com o mesmo peso; o mínimo de uso em segundo plano.

## Onde (sem exagero, como a Apple)

- **Dock** e **painéis flutuantes** (Central de Controle, notificações, calendário, Applications, painel do hme.).
- Não: barra de cima (transparente, como no macOS), listas, texto, conteúdo.
- Botões redondos dentro dos painéis seguem com a borda especular atual (vidro sobre vidro fica para depois).

## Objetivos

- Refração real na borda: dentro de uma faixa junto ao contorno, o fundo é deslocado ao longo da normal da forma
  arredondada com o perfil de vidro curvo `amount × (1 − √(u(2−u)))`, `u = clamp(−sd/height, 0, 1)` (ótica do
  Glasmio portada para HLSL, com crédito).
- Corpo: desfoque (`regular`) ou quase nítido (`clear`), saturação leve, tinta opcional, luz especular vinda do alto
  à esquerda, profundidade leve embaixo.
- Ao vivo: um vídeo atrás do Dock aparece refratado enquanto toca; uma janela arrastada por trás aparece no vidro.
- Mesmo vidro no shft. e no `hme.exe`.

## Fora do escopo (primeira versão)

- `GlassContainer` (vidros que se fundem), morph de forma, vidro dentro de vidro, dispersão cromática.
- Windows 10: lá fica o vidro atual (foto + borda).

## Anti-objetivos

- Qualquer custo do vidro com jogo/app em tela cheia na frente do monitor.
- Esconder qualquer janela do shft. de capturas (`WDA_EXCLUDEFROMCAPTURE` está proibido neste recurso).
- Borda amarela de captura, cursor dentro do vidro, vidro refratando a si mesmo, fantasma dos ícones.
- O vidro acordando a si mesmo (laço de redesenho).

## Abordagens consideradas

- **A1. Capturar o monitor e esconder só a camada de vidro (protótipo 1).** Funciona ao vivo, mas o vidro some
  das gravações (o Dock aparece sem ele) e apagar o Dock no shader deixa costuras. **Descartada** pela regra de gravação.
- **A2. Esconder Dock e painéis de toda captura.** Descartada pelo dono.
- **A3. Lupa do Windows (protótipo 2) — escolhida.** A API de lupa (`Magnification`) entrega uma imagem da tela que
  **deixa de fora só para ela** as janelas que pedirmos (o Dock, o vidro e a própria lupa). Ninguém mais é afetado:
  Dock, painéis e vidro aparecem em qualquer gravação. Sem recursão, sem fantasma, sem nada a apagar.

## Como fica

**Cadeia por superfície de vidro:**
lupa (fundo sem as nossas janelas) → janela da lupa logo abaixo do vidro, com alfa 1 (invisível na tela) →
Windows Graphics Capture **dessa janela** (sem borda, sem cursor) → textura Direct3D 11 → shader HLSL → swap chain
de composição numa janela de vidro própria (sem bitmap de redirecionamento), logo abaixo da janela WPF dona.
Nenhuma janela é excluída de captura.

**Peças (pasta `Native/Glass/`):**
- `GlassDevice`: um dispositivo D3D11 para tudo; shader compilado uma vez; contexto usado sob uma trava.
- `MagnifierHost`: **uma única thread** dona de todas as lupas do processo (`MagInitialize` uma vez — com duas
  threads a exclusão falhou no segundo monitor e houve recursão). Cria, atualiza e destrói lupas; dorme num timer
  de alta resolução armado para a próxima superfície que precisa de imagem; sem superfície ativa, dorme até ser acordada.
- `GlassSurface`: janela de vidro + janela da lupa + sessão de captura da janela da lupa + forma (retângulo e raio).
  A área lida pela lupa fica dentro do monitor (passar da borda volta preto).
- `ChangeSignal` (um por monitor com vidro): captura do monitor usada **só como aviso** — modo de regiões alteradas,
  sem ler pixels. Regiões totalmente dentro das nossas próprias janelas são ignoradas (era o laço). Uma região que
  toca a área de um vidro acorda aquele vidro na hora.
- `WindowEvents`: ganchos de eventos do Windows (mover, mostrar, esconder, destruir, foco, minimizar), fora do nosso
  processo; uma janela que toca a área de um vidro o acorda na hora. Custo zero parado.
- `GlassShader.hlsl`: SDF do retângulo arredondado, refração, desfoque, saturação, tinta, especular, profundidade.

**Ritmo (o que os protótipos mediram como melhor):**
- Sinal de mudança e captura da janela da lupa limitados a 30 por segundo (`MinUpdateInterval`), em qualquer monitor
  (360 Hz incluído).
- A lupa é consultada a 30 FPS **só durante 400 ms depois de uma mudança relevante**; senão, uma checagem a cada 2 s.
- Antes de desenhar, uma cópia de 1/8 da imagem é comparada com a anterior; igual = não desenha nem apresenta.
- Resultado medido: parado ~1,1 % de um núcleo acima do controle; latência de acordar 38 ms mediana, 53 ms máximo
  (1 monitor), 70 ms máximo (2 monitores); movimento atrás do vidro +3 % CPU, +1 % GPU; memória +60 MB.

**Tela cheia e jogos:** com app em tela cheia no primeiro plano de um monitor (a regra que o shft. já usa,
`Monitors.FullScreenWindow`): a lupa daquele monitor para de ser consultada, as sessões de captura e o sinal daquele
monitor são fechados, texturas e swap chain liberadas e as janelas de vidro escondidas. Zero quadros. Ao sair, volta
sozinho. Superfície que continua visível sobre tela cheia (Dock com "esconder em tela cheia" desligado, painel aberto
por cima do jogo): vidro só com a cor (tinta + especular), sem lupa e sem captura.

**Painéis:** cada painel aberto ganha sua superfície ao abrir e a perde ao fechar (a lista de exclusão da lupa é
atualizada com as janelas abertas). Painel fechado = nada rodando para ele.

**Abrir e fechar painéis:** o vidro acompanha a animação do painel **quadro a quadro**. A escala, a origem e a
opacidade de `Motion.Open`/`Motion.Close` viram uma animação equivalente na DirectComposition (transformação e
opacidade do visual do vidro), iniciada no mesmo instante e com a mesma curva e duração, rodando no compositor;
o desenho do vidro durante a animação usa a forma final, escalada pela transformação. Antes de entrar no shft.,
o protótipo prova o sincronismo: abrir e fechar 20 vezes, capturando quadros, sem descompasso visível entre o
cartão e o vidro (borda do vidro dentro de 1 px da borda do cartão).

**Guarda:** um vigia por superfície. Se a captura parar de entregar quadros por mais de 2 s enquanto está ativa, se o
dispositivo D3D for perdido ou se uma chamada da lupa falhar, a superfície volta para o vidro atual (foto + borda),
registra no log e tenta de novo depois de 30 s; três falhas seguidas desligam o vidro real até reiniciar. Nada pode
travar a thread da interface (num protótipo antigo uma execução travou; não se repetiu, mas a guarda é obrigatória).

**Reserva:** Windows 10, sessão remota, placa sem D3D11, lupa indisponível ou erro: vidro atual, só no log.

**Ajustes:** "Liquid Glass" do Dock e dos painéis ligam o vidro real; o fundo atual do Dock (desfoque/Acrylic) e a
foto dos painéis viram a reserva. Estilo Regular/Claro, um só para Dock e painéis. Nenhum ajuste de tela cheia.

**Dependências novas:** Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectComposition, Vortice.D3DCompiler (MIT).
Lupa e eventos por P/Invoke; captura via WinRT (já usado). TFM sobe para `net8.0-windows10.0.26100.0` (regiões
alteradas da captura); em Windows sem essa API, o sinal cai para "consultar a 30 FPS por 400 ms após evento de
janela e a cada 2 s" (sem vídeo ao vivo atrás do vidro).

**Crédito:** ótica do Glasmio (Apache 2.0, que adapta o AndroidLiquidGlass, também Apache 2.0), em
`THIRD-PARTY-NOTICES.md` e no topo do `GlassShader.hlsl`. Nenhum asset da Apple nem do Glasmio.

**Medições obrigatórias antes de ligar por padrão** (mesma bateria dos protótipos, controle antes de cada uma):
parado ≤ 1,5 % de um núcleo acima do controle; latência de acordar ≤ 60 ms p90 em 1 monitor; tela cheia = zero
quadros e zero sessões; Print Screen e OBS mostram Dock e vidro; nenhum laço (parado: ≤ 1 acordada por segundo).

## Decidido pelo dono

- Vidro de verdade (refração ao vivo), arquitetura da lupa.
- Nada de vidro rodando com jogo/app em tela cheia na frente.
- Dock, painéis e vidro visíveis em gravações e compartilhamentos.
- Sem exagero: Dock e painéis flutuantes.
- Otimização adaptativa: 30 FPS só quando algo mexe, quase zero parado, acordar rápido.
- O vidro dos painéis acompanha a animação de abrir e fechar quadro a quadro.

## Calls meus (o dono pode corrigir)

- Windows 10 com o vidro atual.
- Botões redondos com a borda especular; vidro sobre vidro depois.
- Limite de 30 FPS para o vidro mesmo em 360 Hz.
- Dispersão cromática desligada; `GlassContainer` fora da primeira versão.
- Superfície visível sobre tela cheia: só a cor do vidro, sem captura.
- `hme.exe` aberto junto com o shft.: cada um com suas lupas; cada lupa exclui as janelas do próprio processo, então
  pode refratar o painel do outro quando se sobrepõem (raro, aceito).
- Animação longe do vidro custa ~1,5 % de um núcleo (receber e ignorar avisos); aceito, porque tela cheia pausa tudo.

- **Liberação:** o vidro real entra atrás de um ajuste novo, "Vidro real (experimental)", **desligado por padrão**;
  os ajustes atuais continuam ligando o vidro atual. Eu rodo a bateria de medição no PC do dono; a bancada de testes
  dos protótipos vai para o repositório (`tools/glass-bench`). Só depois de passar o vidro real vira o padrão.
- **Ajustes combinados:** vidro real só onde o "Liquid Glass" daquela superfície está ligado (Dock e painéis
  continuam separados). Dock com "Fundo = nenhum" fica sem vidro nenhum. Desfoque/Acrylic do Dock e a foto dos painéis
  viram a reserva. O estilo Regular/Claro fica na página Aparência e vale para os dois. O controle "desfoque dos
  painéis" passa a mandar no desfoque do shader.
- **Janelas de outros apps sobre o vidro:** ficam refratadas na borda na primeira versão (limitação conhecida);
  excluí-las da lupa fica para depois.

## Fica para quem constrói

- Formato exato das classes e do ciclo de vida, respeitando as regras de tela cheia, gravação, laço e guarda.
- Valores finais de `height`, `amount`, desfoque e especular, calibrados pelo olho, sem exagero.
- Como o Dock informa forma e raio durante a magnificação.
