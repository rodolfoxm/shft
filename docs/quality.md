# Qualidade

## Comandos

| Comando | Prova | Quando |
|---|---|---|
| `dotnet build` | compila com `Nullable` e analisadores .NET (`AnalysisLevel=latest`); avisos CA falham o build | toda mudança |
| `dotnet format whitespace --verify-no-changes` | formatação de espaço em branco | informativo; não é gate (veja abaixo) |
| `dotnet list package --vulnerable --include-transitive` | pacotes NuGet com aviso de segurança | ao mudar dependências |

Não há projeto de testes. A verificação de comportamento é manual, a partir dos critérios de [PRODUCT.md](../PRODUCT.md), testando um build de `bin/`. Isso exige encerrar a cópia em uso com `ClonaDock.exe --quit`: peça ao usuário antes e reabra `../ClonaDock-app/ClonaDock.exe` ao terminar; a cópia em uso nunca é sobrescrita.

## Regras e linha de base (adoção em 21/10/2026)

| Item | Medição | Decisão |
|---|---|---|
| Analisadores .NET, modo padrão (`latest`) | 0 achados | **gate:** `CodeAnalysisTreatWarningsAsErrors=true`; teste negativo com CA2200 confirmou que falha o build |
| Conjunto `latest-recommended` | ~170 avisos, quase todos de interop Win32 (CA1707 nomes com `_`, CA1401 P/Invoke visível) | não adotado: ruído para este tipo de código |
| `dotnet format whitespace` | 42 diferenças antigas (instruções em uma linha) | informativo; reformatar o projeto todo não traz benefício verificável |
| WFAC010 (DPI no `app.manifest`) | 1 aviso pré-existente | mantido; mudar o modo de DPI pode alterar a renderização da dock e exige teste visual |
| Tamanho (referência 350 linhas) | `DockController.cs` 801, `DockView.cs` 523, `SettingsWindow.cs` 487, `ProductivityWidgets.cs` 390, `Win32.cs` 354 | dívida registrada; refatorar só com testes ou verificação manual cobrindo o comportamento |

ESLint e o perfil de lint tipado do toolkit não se aplicam: não há JavaScript/TypeScript.

## Estratégia de testes

O risco está no estado global do Windows (barra de tarefas oculta, app bar, chave Run) e na recuperação após falha. Um projeto de testes (xUnit) para `Core/` (configurações, tema) seria o primeiro passo barato; para `Native/` a verificação continua manual. Criar o projeto de testes é uma decisão separada (pendência em `docs/setup.md`).

## Dependências (auditoria de 21/10/2026)

- `SQLitePCLRaw.lib.e_sqlite3` 2.1.6 (transitivo de `Microsoft.Data.Sqlite` 8.0.10): aviso **alto** GHSA-2m69-gcr7-jv3q (SQLite embutido vulnerável). Não há 2.1.x corrigida; a correção exige a linha 3.x (major). Exposição baixa: o app só lê uma cópia do banco de notificações do próprio Windows. Decisão pendente em `docs/setup.md`.

## Revisão depois de implementar

Escopo pela mudança real e pelo risco. Mudanças em `Native/` (P/Invoke, app bar, barra de tarefas) e na recuperação são sensíveis: use revisão independente quando disponível e teste manual de falha e reinício.

## Revisão de segurança

Prompt reutilizável em [prompts/security-review.md](prompts/security-review.md) (toolkit `790a895`), baseline ou diff, somente leitura, relatório privado fora do repositório, com rastreamento manual. Nenhuma revisão de baseline foi feita ainda.

<a id="harness"></a>

## Harness

**Modo:** adaptação nativa inspirada no Harness2 (prompt 15 do toolkit `790a895`); não executa o framework Harness2 e a CLI oficial não foi selecionada.

**Procedimento reutilizável** (após mudanças em `AGENTS.md`, skills, plugins ou delegação):

1. Casos de fumaça em cópias descartáveis de `templates/harness-evaluation/` do toolkit, fora do repositório; rode `scripts/validate-harness.mjs` na cópia intocada antes (esperado 5 aprovados, 2 reprovados).
2. Caso do projeto: um agente em contexto limpo descobre, só pelos arquivos do repositório, como testar um build local sem derrubar a cópia em uso e o que preservar ao mexer na barra de tarefas.
3. Registre cliente/modelo, entradas, comandos, arquivos alterados e aprovado/reprovado/não executado em `docs/setup.md`.
4. Só uma falha observada justifica uma comparação com uma mudança mínima e o mesmo avaliador.

Resultados da adoção: [setup.md](setup.md#avaliação-do-harness).

## Entrega

Checks são comandos explícitos. Sem hooks de git, bloqueio de push ou exigência de branch. Atualize este documento junto com o `.csproj` e regras que mudarem.
