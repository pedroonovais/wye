<img src="" alt="wye" height="48">

Cliente git de desktop em **.NET 10 + Avalonia**, inspirado no GitHub Desktop.
Roda em Linux, Windows e macOS.

A análise do GitHub Desktop está em [docs/EVIDENCIAS.md](docs/EVIDENCIAS.md), com trechos do código
original e o número de cada linha citada. O wye usa os mesmos comandos git que o GitHub Desktop e muda
o resto: app nativo em vez de Electron, git do sistema, atualização automática do status e nenhuma telemetria.

## O que já funciona (fases 1–3)

- Adicionar uma pasta que já é repositório ou criar um repositório novo (`git init`).
- Ver as mudanças de cada arquivo com status e diff colorido.
- **Stage por arquivo e por linha.** Clique nas linhas `+`/`-` do diff e use "Preparar linhas selecionadas"
  ou "Tirar linhas do stage". Também funciona com arquivo novo.
- Commit com resumo e descrição (`git commit -F - --cleanup=strip`).
- Histórico com os arquivos e o diff de cada commit.
- Branches: trocar (inclusive para uma branch remota), criar e ver ahead/behind.
- Fetch, pull e push. Branch sem upstream é publicada com `--set-upstream`.
- Atualização automática quando arquivos mudam (`FileSystemWatcher`) e fetch em segundo plano a cada ~1 h.

**Próxima fase (4):** login no GitHub por Device Flow, helper de credenciais próprio e lista de repositórios do
GitHub para clonar. Enquanto isso, push e pull usam o helper de credenciais que já estiver configurado no seu
git (ex.: `gh auth setup-git`, Git Credential Manager, chave SSH no `ssh-agent`).

## Requisitos

- .NET SDK 10
- git 2.30 ou mais novo no `PATH`. Para usar outro executável, defina `WYE_GIT=/caminho/do/git`.

## Executar e testar

```bash
dotnet run --project src/Wye.App
dotnet test                                   # núcleo, git real e interface headless
WYE_SCREENSHOTS=/tmp/shots dotnet test tests/Wye.App.Tests   # salva capturas PNG da UI
```

Os dados ficam em `~/.config/wye/wye.db` (Linux), `%APPDATA%\wye` (Windows) ou
`~/Library/Application Support/wye` (macOS). É SQLite comum.

## Estrutura

| Projeto | Papel | Equivalente no GitHub Desktop |
|---|---|---|
| `src/Wye.Core` | Modelos e parsers puros: status porcelain v2, log com `%x00`, diff unificado, `PatchBuilder` (stage parcial) | `lib/status-parser.ts`, `lib/diff-parser.ts`, `lib/patch-formatter.ts` |
| `src/Wye.Git` | `GitRunner` (processo git), `GitLocator`, `GitRepository` (operações) | `lib/git/core.ts` + `lib/git/*.ts` |
| `src/Wye.Data` | Repositórios e preferências em SQLite | `lib/databases/*` (IndexedDB/Dexie) |
| `src/Wye.App` | UI Avalonia + MVVM, watcher e fetch em segundo plano | `ui/*`, `lib/stores/*` |
| `tests/*` | xUnit: parsers, integração com git real (repo temporário + remoto bare local) e UI headless | — |

## Publicar

```bash
dotnet publish src/Wye.App -c Release -r linux-x64  --self-contained
dotnet publish src/Wye.App -c Release -r win-x64    --self-contained
dotnet publish src/Wye.App -c Release -r osx-arm64  --self-contained
```

No macOS, para distribuir sem aviso do Gatekeeper, o `.app` precisa ser assinado e notarizado com uma conta
Apple Developer.
