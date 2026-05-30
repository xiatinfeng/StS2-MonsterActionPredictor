# MonsterActionPredictor

A Slay the Spire 2 MOD that predicts the next 2 moves of monsters,
displayed as translucent intent icons on their lower-right side.

Forked from [ByQsA/StS2-MonsterActionPredictor](https://github.com/aoyamaY/StS2-MonsterActionPredictor) (v1.0.1).

## Changes in this fork (v1.0.3)

- **SDK 4.6.2 rebuild** — original was built for Godot.NET.Sdk 4.5.1
- **JIT-safe architecture** — replaced Harmony `RollMove` patch with reflection-based
  polling to avoid `ICombatState` TypeLoadException on current STS2 builds
- **Reflection-based CombatState access** — all `Creature.CombatState` access uses
  reflection to avoid triggering JIT type-chain resolution issues
- **Debug logging off** by default (`EnableDebugLog = false`)

## Build

Open in Godot 4.6.2 Mono editor and press Ctrl+Shift+B, or:

```
dotnet build -c Debug
```

Output DLL and manifest are auto-copied to the game's Mods folder.

## License

MIT (same as original)
