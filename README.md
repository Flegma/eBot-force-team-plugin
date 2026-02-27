# eBot Force Team Plugin

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for CS2 that allows [eBot](https://github.com/Flegma/eBot-CSGO) to force players to their correct teams via RCON.

## How it works

The plugin registers a server command that can be called via RCON:

```
css_ebot_force_team <steamid64> <ct|t>
```

When eBot detects a player has joined the wrong team (based on team rosters), it sends this command to move them to the correct side.

## Installation

1. Download `EbotForceTeam.zip` from the [latest release](https://github.com/Flegma/eBot-force-team-plugin/releases/latest)
2. Extract it into `game/csgo/addons/counterstrikesharp/plugins/`
3. Restart the CS2 server

The resulting folder structure should be:

```
game/csgo/addons/counterstrikesharp/plugins/
└── EbotForceTeam/
    └── EbotForceTeam.dll
```

## Requirements

- CS2 dedicated server
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed on the server

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd EbotForceTeam
dotnet build -c Release
```

The built DLL will be at `EbotForceTeam/bin/Release/net8.0/EbotForceTeam.dll`.
