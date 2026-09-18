# Acoustic Kitty

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![AI-DECLARATION: assist](https://img.shields.io/badge/䷼%20AI--DECLARATION-assist-fef9c3?labelColor=fef9c3)](https://ai-declaration.md)

A [Dalamud](https://github.com/goatcorp/Dalamud) counterintelligence plugin for
Final Fantasy XIV that cross-references nearby in-game characters against their
[Lodestone](https://na.finalfantasyxiv.com/lodestone/) profile, and builds an
audit queue against **EchoVault**'s registration API to see if they're already
registered as verified sightings contributors (stalkers).

> Acoustic Kitty was a 1960s secret Central Intelligence Agency (CIA) project
> to use cats to spy on the Kremlin and Soviet embassies. The idea was to fit
> cats with microphones and an antenna to transmit the data. It was considered
> a failure and declared to be a total loss.
— [Acoustic Kitty (Wikipedia)](https://en.wikipedia.org/wiki/Acoustic_Kitty)

## Install

```
https://repo.echo-unvaulted.net
```

See instructions at https://github.com/sunnie-sunday/DalamudPlugins

## Network requirements

By default the plugin won't directly connect to Echo's API and will suggest
setting up a proxy. You may prefer to connect directly when using a VPN.
Otherwise you'll need a SOCKS5 proxy provider.

## Data policy

This plugin only communicates with The Lodestone and Echo's registration
endpoints. It doesn't by itself send any data to *Sunnie Sunday* or the
[Echo-Unvaulted](https://echo-unvaulted.net) project. The entire cache database
that **Acoustic Kitty** builds is in Dalamud's local folder and isn't synced
with anyone.

It doesn't use `Account ID` in any way, that's a strict policy for all my tools.

Comparing the data used by **Echo**'s plugin and the **EchoVault** website:

| FFXIV / Dalamud                   | Echo's plugin | Acoustic Kitty | Description                        |
| ---                               | ---           | ---            | ---                                |
| **Identity**                      |               |                |                                    |
| `ICharacter.Name`                 | ✅            | ✅             | The character's name.              |
| `Character.ContentId`             | ⚠️            | ⚠️             | The character's permanent ID.      |
| `Character.AccountId`             | ☢️            | —              | The account's ID stalking alts.    |
| **World & Location**              |               |                |                                    |
| `IPlayerCharacter.HomeWorld`      | ✅            | ✅             | The character's home world.        |
| `IPlayerCharacter.CurrentWorld`   | ✅            | ✅             | The world the character is seen.   |
| `IClientState.TerritoryType`      | ✅            | ✅             | The zone the character is seen.    |
| `IGameObject.Position`            | ✅            | —              | The character's coordinates.       |
| **Class/Job & Level**             |               |                |                                    |
| `ICharacter.ClassJob`             | ✅            | ✅             | The character's current class/job. |
| `ICharacter.Level`                | ✅            | ✅             | The character's current level.     |
| **Appearance**                    |               |                |                                    |
| `ICharacter.CustomizeData.Sex`    | 👀            | 👀             | The character's gender (♂/♀).      |
| `ICharacter.Customize`            | 👀            | —              | The character's appearance.        |
| `DrawData.EquipmentModelIds`      | 👀            | —              | Each gear slots appearance.        |
| `ICharacter.CurrentMount`         | ✅            | —              | The mount the character is riding. |
| **Social**                        |               |                |                                    |
| `Character.CharacterData.TitleId` | ✅            | ✅             | The character's used title.        |
| `ICharacter.CompanyTag`           | ✅            | ✅             | The character's FC tag.            |
| `Character.Battalion`             | ✅            | —              | The character's Grand Company.     |
| `ICharacter.OnlineStatus`         | ⚠️            | —              | The character's status icon.       |

| The Lodestone               | EchoVault's website | Acoustic Kitty |
| ---                         | ---                 | ---            |
| **Identity**                |                     |                |
| Lodestone ID                | ⚠️                  | ⚠️             |
| Name                        | ✅                  | ✅             |
| Title                       | ✅                  | ✅             |
| Home World                  | ✅                  | ✅             |
| **Appearance & Backstory**  |                     |                |
| Avatar                      | 👀                  | 👀             |
| Portrait                    | 👀                  | —              |
| Race                        | 👀                  | —              |
| Clan/Tribe                  | 👀                  | —              |
| Gender (♂/♀)                | 👀                  | 👀             |
| Nameday                     | ✅                  | —              |
| Guardian                    | ✅                  | —              |
| City-state                  | ✅                  | —              |
| Equipped gear               | 👀                  | —              |
| **Company & Social**        |                     |                |
| Grand Company               | ✅                  | —              |
| Grand Company rank          | ✅                  | —              |
| Free Company name           | ✅                  | ✅             |
| Free Company ID             | ✅                  | ✅             |
| **Class/Job**               |                     |                |
| Active class/job            | ✅                  | ✅             |
| Current level               | ✅                  | ✅             |
| All class/job levels        | ✅                  | —              |
| **Collections & Activity**  |                     |                |
| Minions                     | ✅                  | —              |
| Mounts                      | ✅                  | —              |
| Achievements                | ✅                  | —              |
