[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

Windows용 Furuta 회전 도립진자 EtherCAT 제어 예제입니다. 진자 엔코더를 읽고 EtherCAT을 통해 암 스테핑 축을 구동하며, 연결, 활성화, 중앙 복귀, 에너지 기반 기상, 균형 제어를 수행합니다. WinForms UI와 CLI는 동일한 제어 계층과 안전 인터록을 공유합니다.

카운터형 IO 슬레이브와 CiA 402 스테핑 축이 같은 버스에서 동작하는 사례를 보여 주며, `Darra.EtherCAT.Master`의 완전한 사용 예제로 활용할 수 있습니다.

## 환경 및 종속성

- Windows x64
- .NET Framework 4.7.2 (`net472`)
- NuGet: `Darra.EtherCAT.Master` **2.13.23**

## 실행 전제

- 공식 설치 프로그램으로 Darra EtherCAT Master를 설치하고 `DarraRT_Eth` 드라이버를 대상 NIC에 바인딩합니다.
- GUI에서 현장 버스에 맞는 `config.deni`를 내보내 실행 파일과 같은 디렉터리의 `EtherCATRestore\`에 둡니다.
- 실제 하드웨어에서 실행하기 전에 하드와이어 비상 정지, STO, 기계 리밋, 현장 정리가 유효한지 확인하십시오. 소프트웨어 리밋에만 의존하지 마십시오.
- 드라이버 접근에 관리자 권한이 필요하며, 시작 시 UAC 창이 표시되는 것은 정상입니다.

## 빌드

```powershell
dotnet build -c Debug -p:Platform=x64
```

출력: `bin\x64\Debug\net472\FurutaPendulum.exe`

인수 없이 실행하면 WinForms UI가 열리고, `--cli`를 사용하면 명령줄 모드로 진입합니다. 두 방식 모두 같은 제어 계층을 사용합니다.

## 주요 CLI 명령

| 명령 | 용도 |
|---|---|
| `connect` | EtherCAT 버스에 연결하고 현장 구성을 확인 |
| `enable` | 안전 전제를 충족한 후 암 활성화 |
| `recenter` | 행정 중앙으로 복귀하고 재센터링 |
| `swingup` | 자동 기상 후 조건이 충족되면 균형 제어로 인계 |
| `balance --seconds 30` | 30초 동안 균형 유지 |
| `stop` | 제어 정지 후 활성화 해제 |

단일 실행 예:

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

`--help`로 전체 파라미터를 확인할 수 있습니다. 모든 운동 명령은 실제 하드웨어와 유효한 안전 조건에서만 사용하십시오.

## 검증 상태

| 주기 | 상태 | 설명 |
|---|---|---|
| 1 ms | PASS | 30초 균형 완료, 프로세스 `exit=0`, `Fault` 없음 |
| 500 µs | PASS | 2회 실행 모두 30초 균형, 프로세스 `exit=0`, `Fault` 없음 |
| 250 µs | 시험 완료, 재조정 필요 | 균형 단계에서 `BalanceLost` 발생, 30초 균형 미달, 파라미터 조정 계속 필요 |

250 µs는 "통과"로 표기하지 않습니다. 주기, PDO 레이아웃, 제어 파라미터는 한 세트로 검토해야 합니다. 주기를 변경한 후에는 `config.deni`를 다시 내보내고 연결, 활성화, 기상, 인계, 균형 검증을 다시 수행하십시오.

## 사례 글

- [250 µs 주기의 Furuta 도립진자](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/) (중국어)

## 저장소

- 이 저장소: https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master: https://github.com/DarraTechnology/Ethercat_Master
