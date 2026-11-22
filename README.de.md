[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

EtherCAT-Regelungsbeispiel für ein rotierendes Furuta-Pendel unter Windows. Die Anwendung liest den Pendelgeber, treibt die Schrittmotor-Achse des Arms über EtherCAT an und führt Verbinden, Freigabe, Rückzentrierung, energiebasiertes Aufschwingen und Balanceregelung aus. WinForms-Oberfläche und CLI nutzen dieselbe Regelungsschicht und dieselben Sicherheitsverriegelungen.

Das Beispiel zeigt das Zusammenspiel von Zähler-IO-Slaves und einer CiA-402-Schrittmotorachse am selben Bus und dient als vollständige Anwendungsreferenz für `Darra.EtherCAT.Master`.

## Voraussetzungen

- Windows x64
- .NET Framework 4.7.2 (`net472`)
- NuGet: `Darra.EtherCAT.Master` **2.13.23**

## Vor dem Start

- Darra EtherCAT Master mit dem offiziellen Installer installieren und den Treiber `DarraRT_Eth` an die vorgesehene NIC binden.
- Eine zum Feldbus passende `config.deni` in der GUI exportieren und im Verzeichnis `EtherCATRestore\` neben der ausführbaren Datei ablegen.
- Vor dem Betrieb an realer Hardware sicherstellen, dass fest verdrahteter Not-Halt, STO, mechanische Endschalter und die Absicherung des Arbeitsbereichs wirksam sind. Sich nicht allein auf Softwaregrenzen verlassen.
- Für den Treiberzugriff sind Administratorrechte erforderlich; eine UAC-Abfrage beim Start ist normal.

## Build

```powershell
dotnet build -c Debug -p:Platform=x64
```

Ausgabe: `bin\x64\Debug\net472\FurutaPendulum.exe`

Ohne Argumente startet die WinForms-Oberfläche; mit `--cli` startet der Kommandozeilenmodus. Beide nutzen dieselbe Regelungsschicht.

## Wichtige CLI-Befehle

| Befehl | Zweck |
|---|---|
| `connect` | EtherCAT-Bus verbinden und Feldkonfiguration prüfen |
| `enable` | Arm nach Erfüllung der Sicherheitsvoraussetzungen freigeben |
| `recenter` | In die Hubmitte zurückkehren und neu zentrieren |
| `swingup` | Automatisch aufschwingen und bei Erfüllung an die Balanceregelung übergeben |
| `balance --seconds 30` | 30 Sekunden balancieren |
| `stop` | Geregelten Stopp ausführen und Freigabe aufheben |

Beispiel für einen Einzellauf:

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

Mit `--help` werden alle Parameter angezeigt. Alle Bewegungsbefehle dürfen nur an realer Hardware unter wirksamen Sicherheitsbedingungen verwendet werden.

## Verifikationsstatus

| Zyklus | Status | Hinweis |
|---|---|---|
| 1 ms | PASS | 30 s Balance abgeschlossen, Prozess `exit=0`, kein `Fault` |
| 500 µs | PASS | Zwei Läufe mit jeweils 30 s Balance, Prozess `exit=0`, kein `Fault` |
| 250 µs | Nicht bestanden | `FollowingError` bei decimation=2; `BalanceLost` bei full-rate. Mit dem aktuellen EZI-Treiber und Regelungs-Envelope ist 500 µs die höchste stabile Frequenz |

250 µs ist nicht bestanden und darf nicht als bestanden bezeichnet werden. Mit dem aktuellen EZI-Treiber und Regelungs-Envelope ist 500 µs die höchste stabile Frequenz; die decimation=2-Konfiguration erzeugt `FollowingError`, die full-rate-Konfiguration `BalanceLost`. Zykluszeit, PDO-Layout und Regelparameter müssen als Einheit geprüft werden. Nach einer Änderung der Zykluszeit `config.deni` neu exportieren und Verbinden, Freigabe, Aufschwingen, Übergabe und Balance erneut verifizieren.

### Störbeobachter

Nicht bestanden: Bei 0.065 wird weiterhin `ThetaOverrun` ausgelöst. Standardmäßig deaktiviert; nicht als „Störunterdrückung bestanden“ bezeichnen.

## Fallstudie

- [Furuta-Pendel bei 250 µs Zykluszeit](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/) (Chinesisch)

## Repository

- Dieses Repository: https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master: https://github.com/DarraTechnology/Ethercat_Master
