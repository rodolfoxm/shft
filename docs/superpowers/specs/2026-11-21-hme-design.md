# hme. (ex-Controle Casa) dentro do shft. — desenho

Data: 2026-11-21. Referência do comportamento: `rodolfoxm/controle-casa` v1.1.2 (Python: `core.py`, `positivo.py`,
`protecao.py`, `main.py`, `index.html`).

## Intenção

O Controle Casa controla ar-condicionado, luzes e tomadas do Smart Life (Tuya) e da Positivo pelo PC. Ele passa a se
chamar **hme.** e é reescrito em C# dentro do repositório do shft., por dois motivos do dono:

1. **Unificar:** no shft., o hme. é um ícone de casa na barra de cima que abre um painel no visual da Central de
   Controle (brilho, modo escuro, som). Ícone de casa mantido; a interface segue o shft.
2. **Distribuir:** quem quiser só o hme. baixa um programa separado. Todo update do hme. sai nos dois: junto do shft.
   e como download próprio. Mais tarde tudo vai para o site.

O dono preferiu reescrever (em vez de manter o Python como motor) para ter um programa só, mais leve e mais fácil de
distribuir, aproveitando o que o Python já resolveu.

## Objetivos

- Paridade com o Controle Casa 1.1.2 (lista abaixo), no visual do shft.
- **Novo:** ajuste "Tenho ar-condicionado". Desligado, some o cartão do ar, o atalho do ar no menu da bandeja e a
  bolinha de "ar ligado". Padrão: ligado se já houver um ar configurado, desligado se não.
- Um só código de motor e painel, compilado em dois programas: o shft. e o `hme.exe` sozinho.
- A configuração atual continua valendo: quem usa o Controle Casa não refaz nada.

## Fora do escopo

- Controle local pela rede (LAN). O Python só usa a nuvem; o hme. também.
- Os temas próprios do Controle Casa (fundo preto, vidro acrílico, `ui.tint`): o hme. segue o tema do shft.
- O site. Só se prepara a distribuição para ele.

## O que não pode acontecer (anti-objetivos)

- Voltar a gastar a cota grátis da Tuya (cerca de 26.000 chamadas por mês). As regras de economia da 1.1.2 são
  requisito, com teste.
- Gravar segredo legível no disco.
- shft. e `hme.exe` consultando a nuvem ao mesmo tempo no mesmo PC (dobraria o consumo e os comandos).
- Deixar o shft. mais pesado com o hme. desligado ou fechado: sem conta configurada, nenhuma thread, timer ou chamada
  de rede.

## Abordagens consideradas

- **A. O shft. só desenha; o Python continua sendo o motor**, aberto escondido e usado pelo servidor local
  (`127.0.0.1:8765`) que já existe. Pouco código novo, mas 23 MB de Python a mais no instalador, um segundo processo na
  memória e duas linguagens para sempre.
- **B. Reescrever o hme. em C# no repositório do shft. (escolhida pelo dono).** Um runtime só, mais leve, e o hme.
  sozinho ganha o visual do shft. Custa uma reescrita de cerca de 2.000 linhas; o risco de regressão se controla usando
  o Python como especificação e portando cada regra com teste.

## Paridade (o que o Python faz e o hme. tem de fazer)

**Nuvem Tuya (OpenAPI, com as chaves de cada usuário, coladas no assistente; nada embutido no programa):**
- Assinatura HMAC-SHA256 da OpenAPI e token de acesso (o Python usa `tinytuya.Cloud`).
- Regiões: `us`, `us-e`, `eu`, `eu-w`, `in`, `sg`, `cn`; teste de conexão com detecção automática da região.
- Aparelhos: `/v1.0/iot-01/associated-users/devices`; status: `/v1.0/iot-03/devices/{id}/status`.
- Comandos com três caminhos, na ordem que o Python tenta e lembra: `iot-03/.../commands`, `shadow/properties/issue`,
  `v1.0/devices/.../commands`.
- Cômodos: `/v1.0/homes/{hid}/rooms` e os aparelhos de cada um, lidos uma vez por dia.

**Ar-condicionado:**
- Ar Wi-Fi: códigos configuráveis (`wifi_ac_codes`: power, temp, escala, modo e ventilação com mapas, turbo).
- Ar por controle IR: hub (`/v2.0/infrareds/...`), remoto, status, envio por cena, padrão ou chave, com o método que
  funcionou lembrado (`_remember_method`).
- Um ajuste vira um único comando, para o ar não apitar várias vezes.
- Modos `cool, heat, auto, fan, dry` (nessa ordem, a dos índices do IR); ventilação `auto, low, mid, high`; faixa
  `temp_min`/`temp_max`; sensor de temperatura do cômodo opcional (`room_sensor_id`).

**Luzes e tomadas:**
- Interruptores de várias teclas (`switch_1..n`), com nome por tecla (`channel_names`).
- Lâmpadas: liga/desliga, brilho, branco quente/frio e cor, com as escalas de `SCALES` (v1 e v2).
- Tomadas: liga/desliga.
- Nomes próprios (`names`) e aparelhos escondidos (`hide`).

**Cômodos:** agrupamento, filtro, "Apagar" por cômodo e "Apagar tudo"; casamento de nomes de cômodo sem acento
(`match_room`), ordem dos cômodos (`room_order`, lista `rooms`).

**Positivo Casa Inteligente (opcional):**
- Entra como o app do celular (e-mail e senha) na API móvel da Tuya (`a1.tuyaus.com/api.json`), com a assinatura,
  `CLIENT_ID`, `APP_KEY` e `deviceId` fixo por instalação de `positivo.py`.
- Sessão guardada protegida; até 3 tentativas quando a Positivo demora; 1 minuto de espera depois de falha.
- Ids prefixados `pos:`; cores no formato bruto (`colour_from_raw`/`colour_to_raw`).
- Não oficial: se parar, o resto do hme. continua.

**Economia de cota (1.1.2):**
- Painel escondido: nenhuma chamada.
- Painel aberto: no máximo uma consulta por minuto, ou uma a cada 10 minutos sem uso.
- Depois de um comando, o estado é atualizado localmente, sem reler a nuvem.
- Contador de chamadas do mês, mostrado no diagnóstico.
- Erro de cota esgotada (28841004): o hme. para de tentar e mostra uma mensagem clara.

**Lista guardada:** a última lista boa (só nome, tipo e estado; sem chaves, IP ou localização) aparece quando a Tuya
bloqueia ou a Positivo não responde.

**Configuração e segredos:**
- Mesmo arquivo: `%APPDATA%\ControleCasa\config.json`, com a mesma estrutura e a mesma mescla com os padrões.
- O Access Secret e a senha e sessão da Positivo ficam em `dpapi:<base64>` (DPAPI do usuário). Os formatos legados
  `b64:` e texto puro são lidos e regravados protegidos. Se o DPAPI falhar, nada é gravado.
- Gravação atômica (arquivo `.tmp` e depois troca).

**Assistente de primeira configuração:** os passos da Tuya do README, teste da conexão e descoberta automática dos
aparelhos e do ar; o modo demonstração funciona sem conta.

**Ajustes:** chaves e região, Positivo, cômodos, nomes, aparelhos escondidos, o ar (tipo, faixa e modos), o novo
"Tenho ar-condicionado" e o diagnóstico (uso da cota e teste do ar).

**Bandeja (hme. sozinho):** casinha; clique abre o painel; o botão direito mostra o menu "ligar/desligar o ar" (só com
ar) e "apagar as luzes"; bolinha azul com o ar ligado.

## Como fica

**Código.** No repositório do shft., uma pasta `Hme/` com:
- o motor, sem WPF: configuração, nuvem Tuya, ar, aparelhos, cômodos, Positivo, cota, lista guardada e demonstração;
- o painel e as janelas do hme., feitos com os componentes do shft. (`FlyoutWindow`, `GlassControls`, `GlyphView`,
  temas).

Um segundo projeto, `hme/hme.csproj`, gera o `hme.exe` com esse código e os arquivos de interface do shft. de que ele
precisa, mais um ícone de bandeja.

**No shft.:**
- Um item "hme." na barra de cima com o ícone de casa. O clique abre o painel do hme. no lugar da Central de Controle.
  O item é arrastável como os outros.
- Um ajuste na página Barra superior liga e desliga o item. Desligado, o motor não carrega.
- O painel segue o tema do shft.

**Os dois no mesmo PC:** um mutex com nome do motor do hme. (por usuário). Quem chega depois não consulta a nuvem. Se
o shft. está com o hme. ligado, o `hme.exe` avisa e fecha. Se o `hme.exe` já está aberto, o shft. deixa a casinha
abrir o `hme.exe`, sem motor próprio.

**O Controle Casa em Python:** o `hme.exe` o substitui. O hme. usa o mesmo arquivo de configuração. O `ControleCasa.exe`
tem de estar fechado: um aviso pede para fechá-lo se estiver rodando, e o hme. não mexe nele. O Python não grava
nenhum campo que o hme. desconheça; o hme. preserva no arquivo os campos que não usa.

**Versões e release:**
- O hme. tem número de versão próprio no `hme/hme.csproj`, começando em 2.0.0 (o Python parou na 1.1.2).
- O workflow do shft. compila e testa os dois.
- Na tag do shft., a release do shft. sai como hoje, com o hme. embutido. Se a versão do hme. ainda não tiver release
  em `rodolfoxm/hme`, o mesmo workflow cria a release `vX.Y.Z` lá, com o `hme.exe` e o SHA256. Isso usa um token com
  permissão de escrita só nesse repositório (segredo `HME_RELEASE_TOKEN`), que o dono cria.
- O repositório `rodolfoxm/controle-casa` é renomeado para `rodolfoxm/hme`; o GitHub redireciona os links antigos.
  O código Python fica no histórico desse repositório. O README passa a apontar para o download e para o código no
  shft.

## Decidido pelo dono

- Reescrita em C# (abordagem B).
- Nome **hme.**; ícone de casa mantido; interface no visual do shft.
- O download do hme. sozinho fica no repositório `controle-casa`, renomeado para `hme`.
- Opção de esconder o ar-condicionado.

## Calls meus (o dono pode corrigir)

- O mesmo `config.json` em `%APPDATA%\ControleCasa`: ninguém reconfigura nada.
- Versão própria do hme., começando na 2.0.0.
- Os temas antigos do Controle Casa saem.
- O mutex do motor impede consulta dobrada.
- O padrão do "Tenho ar-condicionado" depende de já haver ar configurado.
- O `hme.exe` é portátil e self-contained, num arquivo só.
- A API local continua, com a rota nova `/api/show`.
- O motor fica com quem pegar o mutex primeiro; o shft. assume quando o `hme.exe` fecha.
- O `hme.exe` assume a entrada de inicialização do Controle Casa.

**API local para atalhos.** O README do Controle Casa documenta a API em `127.0.0.1:8765` para atalhos e Stream Deck:
`/api/state`, `/api/ac`, `/api/device/ID`, `/api/lights-off` e `/api/quit`, com o cabeçalho `X-Controle-Casa`. Ela
continua, com as mesmas rotas, a mesma porta (`port` no config) e as mesmas regras de host e cabeçalho, só no
processo que tem o motor e só enquanto ele roda. Além disso, uma rota `/api/show` abre o painel, para o shft. mostrar
um `hme.exe` que já está aberto.

**Quem tem o motor.** O motor é de quem pegar primeiro o mutex, um por usuário.
- O `hme.exe` aberto à mão ou com o Windows, com o motor já no shft.: avisa uma vez que o hme. já está na barra do
  shft. e fecha.
- O shft. com o motor no `hme.exe`: a casinha chama `/api/show` do `hme.exe`. Se o `hme.exe` fechar, o shft. tenta
  pegar o mutex no próximo clique e passa a ser o motor.
- Os arquivos de estado (`config.json`, contador de chamadas, lista guardada) são os mesmos em
  `%APPDATA%\ControleCasa`. Como só quem tem o mutex grava, o contador da cota não se divide.

**Migração do Controle Casa.** O `hme.exe` é portátil (um exe só, como o `ControleCasa.exe`). Na primeira execução,
se o valor `ControleCasa` em `HKCU\...\Run` apontar para o `ControleCasa.exe`, ele dá lugar a um valor `hme` que
aponta para o `hme.exe` com `--bandeja`. O shft. não mexe nesse valor. A opção "Iniciar com o Windows" dos Ajustes do
hme. sozinho cuida do valor `hme`.

## Fica para quem constrói

- Como compartilhar o código entre os dois projetos (arquivos ligados ou biblioteca), desde que o shft. continue um exe
  só.
- O desenho exato do painel, dentro do estilo da Central de Controle.
- A ordem de portar os módulos e a forma dos testes. As funções puras do Python (escalas, cores, cômodos,
  `parse_device`, assinatura) ganham testes com os mesmos valores.
- DPAPI por P/Invoke ou pelo pacote da Microsoft.
