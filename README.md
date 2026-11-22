[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

Windows 上的 Furuta 旋转倒立摆 EtherCAT 控制示例。程序读取摆杆编码器，通过 EtherCAT 驱动摆臂步进轴，完成连接、使能、回中、能量起摆和平衡控制；WinForms 界面与 CLI 共用同一套控制层和安全联锁。

该案例演示计数类 IO 从站与 CiA 402 步进轴在同一总线上的协同，可作为 `Darra.EtherCAT.Master` 的完整使用参考。

## 环境与依赖

- Windows x64
- .NET Framework 4.7.2（`net472`）
- NuGet：`Darra.EtherCAT.Master` **2.13.23**

## 运行前提

- 通过官方安装器安装 Darra EtherCAT Master，并将 `DarraRT_Eth` 驱动绑定到对应网卡。
- 使用 GUI 导出与现场总线匹配的 `config.deni`，放到可执行文件目录下的 `EtherCATRestore\`。
- 真机运行前确认硬接线急停、STO、机械限位和现场清场有效，不要只依赖软件限位。
- 程序需要管理员权限访问驱动，启动时出现 UAC 提示属正常现象。

## 构建

```powershell
dotnet build -c Debug -p:Platform=x64
```

输出：`bin\x64\Debug\net472\FurutaPendulum.exe`。

不带参数启动 WinForms 界面；带 `--cli` 进入命令行模式，两种方式共用同一控制层。

## 主要 CLI 命令

| 命令 | 用途 |
|---|---|
| `connect` | 连接 EtherCAT 总线并检查现场配置 |
| `enable` | 满足安全前提后使能摆臂 |
| `recenter` | 回到行程中点并重新居中 |
| `swingup` | 自动起摆，满足条件后交接到平衡控制 |
| `balance --seconds 30` | 保持平衡 30 秒 |
| `stop` | 受控停止并退出使能 |

单次执行示例：

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

使用 `--help` 查看完整参数。所有运动命令都必须在真实硬件和有效安全条件下执行。

## 验证状态

| 周期 | 状态 | 说明 |
|---|---|---|
| 1 ms | PASS | 完成 30 秒平衡，进程 `exit=0`，全程无 `Fault` |
| 500 µs | PASS | 两次运行均完成 30 秒平衡，进程 `exit=0`，全程无 `Fault` |
| 250 µs | 已测，需重调 | 平衡阶段出现 `BalanceLost`，尚未达到 30 秒平衡，需继续整定参数 |

250 µs 不写成“已通过”。周期、PDO 布局和控制参数必须作为一组复核；修改周期后需重新导出 `config.deni`，并重新完成连接、使能、起摆、交棒和平衡验证。

## 案例文章

- [250 µs 周期下的 Furuta 倒立摆](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/)

## 仓库

- 本仓库：https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master：https://github.com/DarraTechnology/Ethercat_Master
