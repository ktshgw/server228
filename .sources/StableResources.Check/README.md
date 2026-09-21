# Stable interface resource compatibility check

Build against the new module with `-p:EnhancedAuthReferencePath=/plugin/osu.Game.Rulesets.EnhancedAuth.dll`.
Run using the actual installed client DLLs:

```powershell
dotnet StableResources.Check.dll 'C:\Users\Kts\AppData\Local\osulazer\current' 'absolute\plugin.dll'
```

The check reads embedded resources and uses the installed framework's DummyRenderer,
TextureStore, LegacySkin loader and animation types. It never opens a profile, Realm,
live game data, network connection or client process. It verifies every embedded asset's
hash/size, priority over native default resources, user static/animated/transparent skin
overrides, hover consistency, animation framerate, one-provider frame selection, repeated
registration, renderer isolation and weak registry lifetimes.
