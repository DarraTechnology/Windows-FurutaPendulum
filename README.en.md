[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

A Furuta rotary inverted pendulum EtherCAT control example for Windows. The application reads the pendulum encoder, drives the arm stepper axis over EtherCAT, and performs connect, enable, recenter, energy-based swing-up and balance control. The WinForms UI and CLI share the same control layer and safety interlocks.

The example shows counter-type IO slaves and a CiA 402 stepper axis working on the same bus, and serves as a complete usage reference for `Darra.EtherCAT.Master`.

## Requirements

- Windows x64
- .NET Framework 4.7.2 (`net472`)
- NuGet: `Darra.EtherCAT.Master` **2.13.23**

## Before running

- Install Darra EtherCAT Master with the official installer and bind the `DarraRT_Eth` driver to the target NIC.
- Export a `config.deni` matching the fieldbus from the GUI and place it under `EtherCATRestore\` next to the executable.
- Before running on real hardware, confirm that the hard-wired emergency stop, STO, mechanical limits and site clearance are effective. Do not rely on software limits alone.
- Administrator privileges are required to access the driver; a UAC prompt at startup is expected.

## Build

```powershell
dotnet build -c Debug -p:Platform=x64
```

Output: `bin\x64\Debug\net472\FurutaPendulum.exe`.

Run without arguments to open the WinForms UI, or with `--cli` for command-line mode. Both use the same control layer.

## Main CLI commands

| Command | Purpose |
|---|---|
| `connect` | Connect to the EtherCAT bus and check the field configuration |
| `enable` | Enable the arm after the safety prerequisites are met |
| `recenter` | Return to the travel midpoint and recenter |
| `swingup` | Run automatic swing-up and hand over to balance control when ready |
| `balance --seconds 30` | Hold balance for 30 seconds |
| `stop` | Perform a controlled stop and disable the arm |

Single-run example:

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

Use `--help` for the full parameter list. All motion commands must be used on real hardware under effective safety conditions.

## Verification status

| Cycle | Status | Notes |
|---|---|---|
| 1 ms | PASS | 30 s balance completed, process `exit=0`, no `Fault` |
| 500 µs | PASS | Two runs, each with 30 s balance, process `exit=0`, no `Fault` |
| 250 µs | Not passed | `FollowingError` with decimation=2; `BalanceLost` at full rate. With the current EZI driver and control envelope, 500 µs is the highest stable frequency |

250 µs is not passed and must not be described as passed. With the current EZI driver and control envelope, 500 µs is the highest stable frequency; the decimation=2 configuration produces `FollowingError`, and the full-rate configuration produces `BalanceLost`. Cycle time, PDO layout and control parameters must be reviewed as one set. After changing the cycle time, re-export `config.deni` and repeat connect, enable, swing-up, handover and balance verification.

### Disturbance observer

Not passed: at 0.065 it still triggers `ThetaOverrun`. It is disabled by default and must not be described as "disturbance rejection passed".

## Case article

- [Furuta inverted pendulum at a 250 µs cycle](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/) (Chinese)

## Repository

- This repository: https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master: https://github.com/DarraTechnology/Ethercat_Master
