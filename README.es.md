[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

Ejemplo de control EtherCAT de un péndulo invertido rotativo de Furuta en Windows. La aplicación lee el encoder del péndulo, acciona el eje paso a paso del brazo mediante EtherCAT y realiza conexión, habilitación, recentrado, elevación por energía y control de equilibrio. La interfaz WinForms y la CLI comparten la misma capa de control y los mismos enclavamientos de seguridad.

El ejemplo muestra la cooperación de esclavos de IO de contador y un eje paso a paso CiA 402 en el mismo bus, y sirve como referencia de uso completa de `Darra.EtherCAT.Master`.

## Entorno y dependencias

- Windows x64
- .NET Framework 4.7.2 (`net472`)
- NuGet: `Darra.EtherCAT.Master` **2.13.16**

## Antes de ejecutar

- Instale Darra EtherCAT Master con el instalador oficial y vincule el controlador de tiempo real de Darra EtherCAT a la tarjeta de red correspondiente.
- Exporte desde la GUI un `config.deni` compatible con el bus de campo y colóquelo en `EtherCATRestore\`, junto al ejecutable.
- Antes de operar con hardware real, confirme que la parada de emergencia cableada, el STO, los límites mecánicos y el despeje del área sean efectivos. No dependa solo de los límites de software.
- Se requieren privilegios de administrador para acceder al controlador; el aviso de UAC al iniciar es normal.

## Compilación

```powershell
dotnet build -c Debug -p:Platform=x64
```

Salida: `bin\x64\Debug\net472\FurutaPendulum.exe`

Sin argumentos se abre la interfaz WinForms; con `--cli` se entra en modo de línea de comandos. Ambos usan la misma capa de control.

## Comandos CLI principales

| Comando | Uso |
|---|---|
| `connect` | Conectar al bus EtherCAT y comprobar la configuración de campo |
| `enable` | Habilitar el brazo tras cumplir los requisitos de seguridad |
| `recenter` | Volver al punto medio del recorrido y recentrar |
| `swingup` | Elevación automática y transferencia al control de equilibrio al cumplir las condiciones |
| `balance --seconds 30` | Mantener el equilibrio durante 30 segundos |
| `stop` | Parada controlada y deshabilitación |

Ejemplo de ejecución única:

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

Use `--help` para ver todos los parámetros. Todos los comandos de movimiento deben usarse con hardware real y condiciones de seguridad efectivas.

## Estado de verificación

| Ciclo | Estado | Notas |
|---|---|---|
| 1 ms | PASS | Equilibrio de 30 s completado, proceso `exit=0`, sin `Fault` |
| 500 µs | PASS | Dos ejecuciones, cada una con 30 s de equilibrio, proceso `exit=0`, sin `Fault` |

## Artículo del caso

- [Péndulo de Furuta con ciclo de 500 µs](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/) (chino)

## Repositorio

- Este repositorio: https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master: https://github.com/DarraTechnology/Ethercat_Master
