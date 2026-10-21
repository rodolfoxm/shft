# Setup e evidências

## Fonte e contexto

- **Data:** 21/10/2026. **Toolkit:** [Vibe Coding Toolkit](https://github.com/soumatheusgomes/vibe-coding-toolkit), revisão única `790a895` (18/10/2026), clonada fora do projeto.
- **Cliente:** Claude Code no app desktop (Windows 11), modelo principal Claude Opus 5.5, autenticação preservada. Sem Codex.
- **Runtime:** .NET SDK 8.0.426, git 2.56.
- **Delegação:** sonnet redigiu `README.md`, `PRODUCT.md`, `docs/architecture.md`, `docs/design.md` e `docs/onboarding.md` (revisados pelo coordenador; uma afirmação errada sobre SQLite no prompt de segurança foi corrigida pelo coordenador); haiku nos casos de descoberta. Git e `.csproj` ficaram com o coordenador.

## Decisões e fontes

| Decisão | Motivo |
|---|---|
| Git local (`main`, sem remoto), commit inicial como backup; `bin/` e `obj/` ignorados | Projeto sem versionamento |
| Analisadores .NET explícitos (`EnableNETAnalyzers`, `AnalysisLevel=8.0`) com `CodeAnalysisTreatWarningsAsErrors` | Modo padrão sem nenhum achado na adoção → vira gate (regra do toolkit) |
| `latest-recommended` não adotado | ~170 avisos de estilo para interop Win32 (CA1707, CA1401) |
| `dotnet format` só informativo | 42 diferenças antigas; reformatar tudo sem benefício verificável |
| ESLint, lint tipado, skills React | Não se aplicam: sem JavaScript/TypeScript |
| Plugins no escopo do projeto; Context7 pelo plugin oficial | Sem mudança global, um único servidor |
| Sem correção do SQLitePCLRaw | Não há 2.1.x corrigida; a linha 3.x é major |

## Componentes

| Componente | Ação | Escopo | Estado e evidência |
|---|---|---|---|
| Superpowers, Ponytail, Caveman, Context7 | Instalar | Projeto (`.claude/settings.json`) | Configurados; **pendente** confiança e carga em sessão nova |
| RTK 1.0.1, uv 0.13.4, Graphify 0.9.93 | Reusar (instalados no usuário neste setup) | Usuário | Grafo: 886 nós, 2167 arestas (38 arquivos C#); `graphify query "What depends on SettingsStore?"` respondeu; hook do RTK pendente de reinício |
| `writing-guidelines`, `find-skills` | Instalar | Projeto `.claude/skills/` + `skills-lock.json` | Instaladas; **descobertas pelo Claude Code ao vivo** |
| Skills React/Vercel, React Native | Não se aplica | — | App .NET |
| Quality gates | Equivalente nativo | Projeto (`.csproj`) | Gate ativo; teste negativo com CA2200 falhou o build como esperado; sonda removida |
| Next.js runtime/bundle, PostgreSQL, gh-fix-ci, shadcn, Sentry | Não se aplica | — | Sem Next, PostgreSQL (SQLite só lê o banco de notificações do Windows), GitHub/Actions, shadcn ou Sentry |
| `differential-review` | Não instalar | — | Fallback: rastreamento manual do prompt de segurança |
| Navegador / DevTools | Não se aplica | — | UI WPF, sem web |
| Memória | Reusar | Cliente + repositório | Auto-memória do Claude Code + docs versionadas |
| Anthropic skills, plugins extras, aia-harness, hooks próprios | Não instalar / reusar | — | Sem necessidade |
| Harness2 (modo nativo) | Configurar | `docs/quality.md#harness` | Falha observada e corrigida (abaixo) |
| Revisão de segurança (prompt 13) | Copiar | `docs/prompts/security-review.md` | Pronto; não executada |

## Documentação criada

`README.md` (novo), `PRODUCT.md`, `AGENTS.md`, `CLAUDE.md`, `docs/architecture.md`, `docs/design.md`, `docs/onboarding.md`, `docs/quality.md`, `docs/setup.md`, `docs/prompts/security-review.md`. Não havia documentos anteriores.

## Checks antes e depois

| Item | Antes | Depois |
|---|---|---|
| `dotnet build` | OK (0 erros, 1 aviso WFAC010) | OK (0 erros, 1 aviso WFAC010), agora com gate de analisadores |
| Analisadores (modo padrão) | 0 achados | 0 achados |
| `dotnet list package --vulnerable` | — | 1 alto transitivo (SQLitePCLRaw.lib.e_sqlite3 2.1.6) |
| Reexecução | — | `skills add` repetido: arquivos e `skills-lock.json` idênticos |

Recursos temporários: a sonda `Core/_GateProbe.cs` foi criada e apagada no teste negativo; `git status` limpo depois.

## Memória e harness

<a id="avaliação-do-harness"></a>

### Avaliação do harness (21/10/2026, toolkit `790a895`)

Casos da fixture: aprovados para o cliente e harness compartilhados (detalhes em `../mojoTWEEKS/docs/setup.md`).

**Falha observada (linha de base, haiku em contexto limpo):** no caso de descoberta, o agente respondeu corretamente sobre build, barra de tarefas, testes e configurações, mas encontrou uma **contradição**: o `AGENTS.md` proibia encerrar a cópia em uso sem pedido e, ao mesmo tempo, mandava rodar `ClonaDock.exe --quit` (que a encerra) antes de testar. O mesmo valia para `docs/quality.md`. O agente concluiu, corretamente, que precisaria confirmar com o usuário.

**Candidato (uma mudança mínima):** regra única em `AGENTS.md`, `docs/quality.md` e `docs/onboarding.md`: testar o build local exige `--quit` na cópia em uso → pedir antes, reabrir `../ClonaDock-app/ClonaDock.exe` depois; nunca sobrescrever. Nenhuma regra foi enfraquecida.

**Comparação:** mesmo caso, mesmo modelo, contexto limpo. A pergunta ganhou um pedido explícito para apontar contradições (mais exigente que a linha de base). O agente respondeu ao item 2 com a regra única (pedir antes, `--quit`, reabrir a cópia em uso) e não encontrou mais a contradição. As duas observações que restaram não eram falhas: `setup.md` ainda não existia quando ele começou, e a suspeita de que `CodeAnalysisTreatWarningsAsErrors` cobriria todos os avisos é incorreta (o WFAC010 continua como aviso). **Candidato adotado.**

| Caso | Resultado |
|---|---|
| Descoberta, linha de base | **falhou** (contradição de instruções) |
| Descoberta, candidato | **aprovado** (contradição resolvida; candidato adotado) |
| Conflito de recursos, bloqueio parcial, pausa/retomada | **não executado** |

## Pendências e próxima entrega

| Pendência | Próxima ação |
|---|---|
| Carga dos plugins e do hook do RTK | Sessão nova no projeto, aceitar plugins; reiniciar o app para o hook |
| SQLitePCLRaw 2.1.6 (aviso alto) | Decisão: referenciar `SQLitePCLRaw.bundle_e_sqlite3` 3.x diretamente (major) e testar o `BadgeWatcher`, ou aceitar o risco (só lê um banco local do Windows) |
| Projeto de testes | Decisão: xUnit para `Core/` como primeiro passo |
| WFAC010 (DPI no manifesto) | Migrar para `ApplicationHighDpiMode` só com teste visual da dock |
| Arquivos acima de 350 linhas (`DockController.cs` 801…) | Refatorar só com testes ou verificação manual |
| Acessibilidade (dock só por mouse, Configurações não seguem o tema escuro) | Ver `docs/design.md` |
| Revisão de segurança de baseline | Rodar `docs/prompts/security-review.md` |

**Repetir este setup:** rode o prompt 12 do toolkit; ele reusa o estado e não duplica. Gatilhos: nova revisão do toolkit, mudança de .NET, criação do projeto de testes.


## Revisão do setup (21/10/2026)

Revisor independente, somente leitura (sonnet, contexto limpo), sobre o intervalo do commit inicial ao HEAD nos três repositórios: regressões nas mudanças de código, configurações (ESLint, `.claude/settings.json`, ignores) e exatidão dos comandos documentados. Rodou `npm run verify` (mojoTWEEKS), lint, build e o build do painel (AutoCutEdits) e `dotnet build --no-incremental` (ClonaDock). **Nenhum achado alto ou médio**; as mudanças de código preservam o comportamento. Achados baixos e destino:

| Achado | Destino |
|---|---|
| ClonaDock: `AnalysisLevel=latest` sem versão fixa quebraria o build num SDK novo | Corrigido: fixado em `8.0` |
| ClonaDock: WFAC010 não documentado | Já documentado em `docs/quality.md` (mantido) |
| AutoCutEdits: contagem "8 imports React" (eram 9) | Corrigido |
| AutoCutEdits: `verify` não construía o painel | Corrigido: `build-panel.cjs` entrou no `verify` (o smoke continua manual) |

Não coberto pela revisão: `npm test` e os smokes (rodados pelo coordenador), skills vendorizadas, números do `npm audit`, PRODUCT/architecture/design. Uma revisão não prova correção.
