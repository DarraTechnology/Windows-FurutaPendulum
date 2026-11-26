[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

Windows 向けの Furuta 回転倒立振子 EtherCAT 制御サンプルです。振子エンコーダを読み取り、EtherCAT 経由でアームのステッピング軸を駆動し、接続、有効化、中央復帰、エネルギー起振、平衡制御までを実行します。WinForms UI と CLI は同じ制御層と安全インターロックを共有します。

カウンタ型 IO スレーブと CiA 402 ステッピング軸を同一バスで協調させる事例であり、`Darra.EtherCAT.Master` の完全な使用例として利用できます。

## 動作環境

- Windows x64
- .NET Framework 4.7.2（`net472`）
- NuGet: `Darra.EtherCAT.Master` **2.13.16**

## 実行前提

- 公式インストーラで Darra EtherCAT Master をインストールし、Darra EtherCAT リアルタイムドライバを対象 NIC にバインドします。
- GUI から現場バスに対応する `config.deni` を書き出し、実行ファイルと同じディレクトリの `EtherCATRestore\` に配置します。
- 実機で動かす前に、ハード配線の非常停止、STO、機械リミット、現場の安全確保が有効であることを確認してください。ソフトウェアリミットだけに依存しないでください。
- ドライバへのアクセスには管理者権限が必要です。起動時の UAC 表示は正常です。

## ビルド

```powershell
dotnet build -c Debug -p:Platform=x64
```

出力: `bin\x64\Debug\net472\FurutaPendulum.exe`

引数なしで WinForms UI を起動し、`--cli` でコマンドラインモードになります。どちらも同じ制御層を使用します。

## 主な CLI コマンド

| コマンド | 用途 |
|---|---|
| `connect` | EtherCAT バスに接続し、現場設定を確認 |
| `enable` | 安全前提を満たした後にアームを有効化 |
| `recenter` | ストローク中央に戻して再センタリング |
| `swingup` | 自動起振し、条件を満たしたら平衡制御へ引き継ぎ |
| `balance --seconds 30` | 30 秒間の平衡を維持 |
| `stop` | 制御停止して有効化を解除 |

単発実行の例:

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

`--help` で全パラメータを確認できます。すべての運動コマンドは実機かつ有効な安全条件で使用してください。

## 検証状況

| 周期 | 状態 | 説明 |
|---|---|---|
| 1 ms | PASS | 30 秒平衡を完了、プロセス `exit=0`、`Fault` なし |
| 500 µs | PASS | 2 回の実行でいずれも 30 秒平衡、プロセス `exit=0`、`Fault` なし |

## 事例記事

- [500 µs 周期の Furuta 倒立振子](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/)（中国語）

## リポジトリ

- 本リポジトリ: https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master: https://github.com/DarraTechnology/Ethercat_Master
