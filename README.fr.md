[中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [Português](README.pt.md) | [Русский](README.ru.md)

# Windows FurutaPendulum

Exemple de commande EtherCAT d'un pendule inversé rotatif de Furuta sous Windows. L'application lit l'encodeur du pendule, entraîne l'axe pas à pas du bras via EtherCAT et réalise la connexion, l'activation, le recentrage, la montée en énergie et la régulation d'équilibre. L'interface WinForms et la CLI partagent la même couche de commande et les mêmes verrouillages de sécurité.

L'exemple illustre la coexistence d'esclaves d'E/S de comptage et d'un axe pas à pas CiA 402 sur le même bus, et sert de référence d'utilisation complète pour `Darra.EtherCAT.Master`.

## Environnement et dépendances

- Windows x64
- .NET Framework 4.7.2 (`net472`)
- NuGet : `Darra.EtherCAT.Master` **2.13.23**

## Avant l'exécution

- Installez Darra EtherCAT Master avec l'installateur officiel et liez le pilote `DarraRT_Eth` à la carte réseau concernée.
- Exportez depuis l'interface un `config.deni` correspondant au bus de terrain et placez-le dans `EtherCATRestore\`, à côté de l'exécutable.
- Avant toute utilisation sur matériel réel, vérifiez l'efficacité de l'arrêt d'urgence câblé, du STO, des limites mécaniques et du dégagement de la zone. Ne vous fiez pas uniquement aux limites logicielles.
- Des privilèges administrateur sont nécessaires pour accéder au pilote ; l'invite UAC au démarrage est normale.

## Compilation

```powershell
dotnet build -c Debug -p:Platform=x64
```

Sortie : `bin\x64\Debug\net472\FurutaPendulum.exe`

Sans argument, l'interface WinForms démarre ; avec `--cli`, le mode ligne de commande est utilisé. Les deux partagent la même couche de commande.

## Principales commandes CLI

| Commande | Rôle |
|---|---|
| `connect` | Se connecter au bus EtherCAT et vérifier la configuration terrain |
| `enable` | Activer le bras après validation des prérequis de sécurité |
| `recenter` | Revenir au milieu de course et recentrer |
| `swingup` | Montée automatique puis passage à la régulation d'équilibre lorsque les conditions sont remplies |
| `balance --seconds 30` | Maintenir l'équilibre pendant 30 secondes |
| `stop` | Arrêt contrôlé et désactivation |

Exemple d'exécution unique :

```powershell
.\bin\x64\Debug\net472\FurutaPendulum.exe --cli "connect; enable; recenter; swingup; balance --seconds 30; stop" --yes
```

Utilisez `--help` pour la liste complète des paramètres. Toutes les commandes de mouvement doivent être utilisées sur du matériel réel avec des conditions de sécurité effectives.

## État de vérification

| Cycle | État | Remarques |
|---|---|---|
| 1 ms | PASS | Équilibre de 30 s terminé, processus `exit=0`, aucun `Fault` |
| 500 µs | PASS | Deux exécutions, chacune avec 30 s d'équilibre, processus `exit=0`, aucun `Fault` |
| 250 µs | Testé, réglage à reprendre | `BalanceLost` pendant l'équilibre ; 30 s non atteintes ; réglage des paramètres en cours |

250 µs n'est pas déclaré comme validé. La période, la disposition des PDO et les paramètres de commande doivent être revus ensemble. Après un changement de période, réexportez `config.deni` et refaites la vérification de connexion, activation, montée, passage et équilibre.

## Article de cas

- [Pendule de Furuta à un cycle de 250 µs](https://ethercat.darra.xyz/doc/examples/furuta-pendulum-250us/) (chinois)

## Dépôt

- Ce dépôt : https://github.com/DarraTechnology/Windows-FurutaPendulum
- Darra EtherCAT Master : https://github.com/DarraTechnology/Ethercat_Master
