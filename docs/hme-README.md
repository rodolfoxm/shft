<div align="center">

# hme.

**Ar-condicionado, luzes e tomadas do Smart Life e da Positivo, direto do PC.**
Antes se chamava Controle Casa. Agora é o hme., com o visual do [shft.](https://github.com/rodolfoxm/shft).

</div>

## Baixar

- **Só o hme.:** baixe o `hme.exe` mais recente em [Releases](../../releases) e abra. Ele fica na bandeja, ao lado do relógio.
- **Com o shft.:** o hme. já vem no [shft.](https://github.com/rodolfoxm/shft/releases), como a casinha da barra de cima (Ajustes → Barra superior).

> O Windows pode mostrar "O Windows protegeu o computador", porque o programa não tem assinatura digital paga. Clique em **Mais informações → Executar assim mesmo**.

Quem já usava o Controle Casa não precisa reconfigurar: o hme. lê a mesma configuração. Feche o Controle Casa antigo antes de abrir o hme.

## O que ele faz

- **Ar-condicionado** Wi-Fi ou por controle universal IR do Smart Life: liga, temperatura, modo, ventilação e turbo, um comando por ajuste. Quem não tem ar desliga **Tenho ar-condicionado** nos Ajustes.
- **Luzes** (interruptores de várias teclas e lâmpadas com brilho, branco quente/frio e cor) e **tomadas**.
- **Cômodos**, com "Apagar" por cômodo e "Apagar tudo".
- **Positivo Casa Inteligente** (opcional), sem tirar os aparelhos do app da Positivo.
- **Economia da cota grátis da Tuya**: com o painel fechado, nenhuma consulta.
- **API local** para atalhos e Stream Deck em `127.0.0.1:8765`, igual à do Controle Casa.

## Primeira configuração

Os Ajustes do hme. mostram o passo a passo: um projeto grátis em [platform.tuya.com](https://platform.tuya.com), as chaves Access ID e Access Secret, e o app Smart Life vinculado pelo QR Code. Também dá para testar tudo com a demonstração, sem conta.

## Código

O código do hme. fica no repositório do shft., na pasta [`Hme/`](https://github.com/rodolfoxm/shft/tree/main/Hme). A versão em Python (Controle Casa 1.1.2) continua no histórico deste repositório.

## Licença

[PolyForm Noncommercial 1.0.0](LICENSE.md).
