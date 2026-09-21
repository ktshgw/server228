# Native legacy screen integration

Runs the real installed `OsuGame` and its screen stack with the SOMS! module. Unlike the isolated drawable fixture, the owners are fully loaded native screens.

The test generates a short silent `.osz`, imports it through `BeatmapManager`, opens the legacy Play/Solo menu, checks imported browser cards, selects Autoplay in the real mod overlay, starts gameplay, reaches real results, retries through the legacy result button, and restores the native results view.

Every run creates a new profile below the supplied output directory. `DummyAPIAccess` replaces login/API access; no user database, account, or installed client files are modified. The first-run setup wizard is disabled only in that generated profile. Cosmetic assets may still produce requests to the deliberately invalid test hostname.

Build with the SDK container from the workspace root:

```powershell
docker run --rm -v 'E:\333\pythonEPTA\SERVAK\.sources:/work' -v pulse-dotnet-nuget:/root/.nuget -w /work mcr.microsoft.com/dotnet/sdk:8.0 dotnet build LegacyScreens.Integration -c Release -o /work/LegacyScreens.Integration/bin/RuntimeCheck --nologo
```

Run against installed assemblies, keeping the package's build-time assemblies out of the test output:

```powershell
dotnet .sources/LegacyScreens.Integration/bin/RuntimeCheck/LegacyScreens.Integration.dll 'C:\Users\Kts\AppData\Local\osulazer\current' 'E:\333\pythonEPTA\SERVAK\.sources\LazerAuthlibInjection\osu.Game.Rulesets.EnhancedAuth\bin\SomsStableInterfaceFinalRelease\merged\osu.Game.Rulesets.EnhancedAuth.dll' 'E:\333\pythonEPTA\SERVAK\.test-tmp\legacy-integration'
```

Exit code zero and the final `PASS MainMenu -> SoloSongSelect ...` line are required. A 120-second wall-clock timeout prevents an indefinite hang. This is a headless functional check; `LegacyInterface.Check --visual` separately validates rendered layout.
