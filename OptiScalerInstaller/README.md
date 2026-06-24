# Instalador de Mods — OptiScaler + Frame Generation

App com interface (.NET Framework / WinForms) que instala automaticamente, em jogos, o
**OptiScaler + dlssg-to-fsr3 + fakenvapi + OptiPatcher**, com upgrade de **DLSS 310.6** e
**FSR4** opcionais. Pensado para placas **RTX 20/30** (Frame Generation via FSR3).

## O que o app faz
- Detecta o motor do jogo: **Unreal Engine** (instala em `...\Binaries\Win64`) ou **flat/RE Engine** (instala na raiz).
- **Bloqueia** a instalação se detectar **anti-cheat** (EasyAntiCheat/BattlEye/Vanguard) — evita banimento.
- **Avisa** se o jogo não tem DLSS/FSR (onde o mod não funciona).
- Não sobrescreve DLLs nativos do jogo; faz **backup** do que troca.
- Campo de caminho com **colar** ou **Procurar**; menu do OptiScaler na tecla **=**.

## Arquivos
| Arquivo | Função |
|---|---|
| `OptiInstaller.cs` | Código do app |
| `MakeIcon.cs` | Gera o `skull.ico` |
| `skull.ico` | Ícone (caveira) |
| `Instalar_Mod.exe` | App compilado |
| `update.cfg` | Repositório do auto-update |
| `build.bat` | Recompila tudo |

## Compilar
Rode `build.bat` (usa o `csc.exe` do .NET Framework 4 que já vem no Windows). Não precisa instalar nada.

## Auto-update (GitHub Releases)
Ao abrir, o app lê `update.cfg` (`repo=usuario/repositorio`) e procura no GitHub a **release
mais nova que tenha o arquivo `Instalar_Mod.exe` anexado** (ignora releases de outros produtos
no mesmo repo). Se a tag for **maior** que a versão interna (`App.Version`), ele baixa e se
substitui sozinho, e reabre.

### Como lançar uma atualização
1. Aumente `App.Version` em `OptiInstaller.cs` (ex.: `2.1` -> `2.2`) e rode `build.bat`.
2. Crie uma **release** no GitHub com a tag igual (ex.: `v2.2`).
3. Anexe o novo `Instalar_Mod.exe` à release.

> ⚠️ A tag da release **deve bater** com `App.Version` do exe — senão entra em loop de update.

## Observações
- O `Instalar_Mod.exe` precisa rodar a partir da pasta que contém os **arquivos do mod**
  (`dxgi.dll`, `OptiScaler.ini`, `dlssg_to_fsr3...`, pasta `DLSS 310.6`, `FSR4_INT8...`, etc.).
  Esses binários de terceiros **não** ficam no git (são grandes e licenciados à parte).
- O exe **não é assinado**; o Windows Defender/SmartScreen pode exibir aviso de editor desconhecido.
