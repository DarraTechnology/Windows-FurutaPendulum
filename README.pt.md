[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

Exemplo de controle EtherCAT de um pêndulo invertido rotativo de Furuta no Windows. O aplicativo lê o encoder do pêndulo, aciona o eixo de passo do braço via EtherCAT e executa conexão, habilitação, recentralização, subida por energia e controle de equilíbrio. A interface WinForms e a CLI compartilham a mesma camada de controle e os mesmos intertravamentos de segurança.

O exemplo demonstra a cooperação de escravos de IO de contagem e um eixo de passo CiA 402 no mesmo barramento, e serve como referência completa de uso do `Darra.EtherCAT.Master`.

## Ambiente e dependências

- Windows x64
- .NET Framework 4.7.2 (`net472`)
- NuGet: `Darra.EtherCAT.Master` **2.13.23**

## Antes de executar

- Instale o Darra EtherCAT Master com o instalador oficial e vincule o driver `DarraRT_Eth` à placa de rede correspondente.
- Exporte pela GUI um `config.deni` compatível com o barramento de campo e coloque-o em `EtherCATRestore\`, ao lado do executável.
- Antes de operar em hardware real, confirme a eficácia da parada de emergência cabeada, STO, limites mecânicos e liberação da área. Não dependa apenas dos limites de software.
- São necessários privilégios de administrador para acessar o driver; o aviso de UAC na inicialização é normal.

## Compilação

```powershell
dotnet build -c Debug -p:Platform=x64
```

Saída: `bin\x64\Debug\net472\FurutaPendulum.exe`

Sem argumentos, a interface WinForms é aberta; com `--cli`, entra no modo de linha de comando. Ambos usam a mesma camada de controle.

## Principais comandos CLI

| Comando | Finalidade |
|---|---|
| `connect` | Conectar ao barramento EtherCAT e verificar a configuração de campo |
| `enable` | Habilitar o braço após cumprir os pré-requisitos de segurança |
| `recenter` | Retornar ao meio do curso e recentralizar |
| `swingup` | Subida automática e transferência para o controle de equilíbrio quando as condições forem atendidas |
| `balance --seconds 30` | Manter o equilíbrio por 30 segundos |
| `stop` | Parada controlada e desabilitação |

Exemplo de execução única:

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

Use `--help` para ver todos os parâmetros. Todos os comandos de movimento devem ser usados em hardware real e com condições de segurança efetivas.

## Estado de verificação

| Ciclo | Estado | Observações |
|---|---|---|
| 1 ms | PASS | Equilíbrio de 30 s concluído, processo `exit=0`, sem `Fault` |
| 500 µs | PASS | Duas execuções, cada uma com 30 s de equilíbrio, processo `exit=0`, sem `Fault` |
| 250 µs | Não aprovado | `FollowingError` com decimation=2; `BalanceLost` em full-rate. Com o driver EZI e o envelope de controle atuais, 500 µs é a maior frequência estável |

250 µs não é aprovado e não deve ser declarado como aprovado. Com o driver EZI e o envelope de controle atuais, 500 µs é a maior frequência estável; a configuração decimation=2 produz `FollowingError` e a configuração full-rate produz `BalanceLost`. Ciclo, layout de PDO e parâmetros de controle devem ser revisados em conjunto. Após alterar o ciclo, exporte novamente o `config.deni` e repita a verificação de conexão, habilitação, subida, transferência e equilíbrio.

### Observador de perturbações

Não aprovado: em 0.065 ainda dispara `ThetaOverrun`. Fica desativado por padrão e não deve ser descrito como "rejeição de perturbações aprovada".

## Artigo do caso

- [Pêndulo de Furuta com ciclo de 250 µs](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/) (chinês)

## Repositório

- Este repositório: https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master: https://github.com/DarraTechnology/Ethercat_Master
