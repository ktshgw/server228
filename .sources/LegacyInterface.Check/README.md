# Legacy interface runtime and rendering checks

This project deliberately resolves game and framework assemblies from the **installed client**, not copied NuGet runtime dependencies. Compile to `bin/RuntimeCheck`; do not use an old `bin/Release/net8.0` output containing other game DLLs.

The game data, Realm database and preferences live in a fresh directory below the output directory's `profiles` folder (default `.test-tmp/legacy-v2/profiles`). The test uses `DummyAPIAccess` and never opens `D:\lazer` or contacts the live game server.

Build after producing the `SomsStableInterfaceRelease` merged module:

```powershell
& 'C:\Users\Kts\AppData\Local\Programs\DockerDesktop\resources\bin\docker.exe' run --rm -v 'E:\333\pythonEPTA\SERVAK\.sources:/work' -v pulse-dotnet-nuget:/root/.nuget -w /work mcr.microsoft.com/dotnet/sdk:8.0 dotnet build LegacyInterface.Check -c Release -o /work/LegacyInterface.Check/bin/RuntimeCheck --nologo
```

Run from the workspace root:

```powershell
dotnet .sources/LegacyInterface.Check/bin/RuntimeCheck/LegacyInterface.Check.dll 'C:\Users\Kts\AppData\Local\osulazer\current' 'E:\333\pythonEPTA\SERVAK\.sources\LazerAuthlibInjection\osu.Game.Rulesets.EnhancedAuth\bin\SomsStableInterfaceRelease\merged\osu.Game.Rulesets.EnhancedAuth.dll'
```

Add `--visual` to render in a real desktop window and save PNG screenshots from `GameHost.TakeScreenshotAsync()`. Add `--output=E:\path\to\directory` to select a different artifact and temporary profile directory. The graphical run switches between 1280×720 and 640×480 and exits on completion.

Add `--skin-directory=C:\path\to\skin` to use real skin files. This constructs a `LegacySkin` backed by that directory, including the standard `skin.ini` decoder, `@2x` handling and animation lookup. It reads the files without importing or changing the skin or the user's configuration. These runs also capture song selection and results at 1920×1080, plus selection-button hover artwork. The absent-assets test temporarily hides the provider; the subsequent narrow screenshots restore the real skin.

For a different compile-time module, pass `-p:EnhancedAuthReferencePath=/work/LazerAuthlibInjection/osu.Game.Rulesets.EnhancedAuth/bin/YourBuild/merged/osu.Game.Rulesets.EnhancedAuth.dll` to the Docker build command. The runtime module remains the second CLI argument, independently of this reference.

Real-skin example from the workspace root:

```powershell
dotnet .sources/LegacyInterface.Check/bin/RuntimeCheck/LegacyInterface.Check.dll 'C:\Users\Kts\AppData\Local\osulazer\current' 'E:\333\pythonEPTA\SERVAK\.sources\LazerAuthlibInjection\osu.Game.Rulesets.EnhancedAuth\bin\SomsStableSkinCompatibilityFinalRelease\merged\osu.Game.Rulesets.EnhancedAuth.dll' --visual '--skin-directory=C:\Users\Kts\AppData\Local\osu!\Skins\#Ktshgw_legacy' '--output=E:\333\pythonEPTA\SERVAK\.test-tmp\legacy-skin-compat\final-fumo'
```

Checks include complete component loading against the actual client version; native play, multiplayer and SOMSAI callbacks; unavailable Ranked; four rulesets on the results screen; pause actions; native mod state and changing free-mod permissions; room selection, native room filtering and suspension/resume; native keyboard-focus release and restoration; the classic search field forwarding and retaining a query; locked rulesets; missing/one-pixel skin assets; and 30 live preference/skin-change cycles followed by disposal with pending callbacks.

Additional skin compatibility checks cover an animated `ranking-panel` before its drawable loads, intentional transparent 1×1 assets, natural selection art size and bottom-left origin without expanding its click target, `-over` hover textures, the stable three-row osu! statistics order, `ScoreOverlap`, partial score fonts, and oversized decorative percent textures preserving readable accuracy digits.

Screenshots contain deterministic test map/room/score data. The native owner screens are lightweight fixtures; they do not run gameplay, matchmaking or multiplayer networking. The song-list fixture fills the alternate list directly, so its polling status may still read “Загрузка карт…”. Automated geometry checks complement manual inspection of the PNGs; they do not claim pixel-perfect equivalence to osu!stable.

Validated on 2026-09-08 against installed osu!lazer 2026.804.2 and plugin SHA-256 `A82E1BAA6377B7B80FC51D671187F65EDCF28F441149FDFA748D8B9E556DA044`: headless baseline, Fumo (`#Ktshgw_legacy`) visual and `Amedire2018` visual all exited 0. The final results and song-selection PNGs were also inspected manually. Outputs:

- `.test-tmp/legacy-skin-compat/final-baseline` — headless profile.
- `.test-tmp/legacy-skin-compat/final-fumo` — Fumo PNGs and isolated profile.
- `.test-tmp/legacy-skin-compat/final-amedire` — Amedire PNGs and isolated profile.

The earlier sibling `fumo` and `amedire` directories retain screenshots from before the last results fixes for comparison.
