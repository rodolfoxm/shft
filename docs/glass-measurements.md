# Vidro real — medições e limitações

Estado em 2026-11-22, branch `real-glass`. "Vidro real (experimental)" continua **desligado por padrão**.

## Medido (protótipo 2, lupa do Windows, ritmo adaptativo — mesma arquitetura integrada)

| Cenário | Resultado | Meta |
|---|---|---|
| Parado, acima do controle | ~1,1 % de um núcleo | ≤ 1,5 % |
| Latência de acordar (1 monitor) | mediana 38 ms, máx. 53 ms | ≤ 60 ms p90 |
| Latência de acordar (2 monitores) | máx. 70 ms | — |
| Movimento atrás do vidro | +3 % CPU, +1 % GPU | — |
| Animação longe do vidro | ~1,5 % de um núcleo | aceito |
| Memória | +60 MB | — |
| Tela cheia com foco | zero quadros, zero sessões | zero |
| Print Screen | Dock e vidro visíveis | visíveis |

## Verificado no código integrado (testes automáticos, 236 aprovados)

- `GlassSurfaceTests.Live_frame_has_no_owner_pixels_and_no_glass`: com um fundo de outro processo, a imagem da lupa
  não tem pixels da janela dona nem do próprio vidro por 6 quadros seguidos (o teste falha quando o vidro sai da
  lista de exclusão).
- `MagnifierHostTests`: duas lupas na mesma thread com lista de exclusão aceita.
- Dispositivo D3D11 e shader compilam na GPU desta máquina; checagem de quadro igual funciona.

## Achados da integração

- Pôr o vidro logo abaixo de uma dona "sempre no topo" não o torna "sempre no topo": o vidro entra primeiro na faixa da dona.
- A lupa ignora janelas que estão escondidas quando a lista de exclusão é definida: o vidro aparece antes da lupa e os
  painéis atualizam a lista depois de visíveis.
- A lupa não mostra as janelas WPF do próprio processo (inofensivo: todas são excluídas de qualquer jeito).

## Limitações conhecidas (para investigar depois)

- **Sincronia do vidro com a animação dos painéis:** 99,4–99,6 % dos quadros exatos (0 px). Em 40 ciclos, 3–4 quadros
  nas pontas do fade (opacidade 0,1–0,66) ficam 8–21 px fora — cada um dura 2,8 ms a 360 Hz. Aceito pelo dono por ora.
- Janelas de outros apps sobre o vidro aparecem refratadas na borda.
- Bateria completa no shft. integrado (parado, 2 monitores, tela cheia, painel abrindo, OBS) **ainda não rodada**:
  exige ligar o ajuste no shft. em uso. Rodar antes de tornar o vidro real o padrão.
