# SimHub Reference
## Plugin Properties
- `DataCorePlugin.GameData.NewData.SpeedKmh`
- `DataCorePlugin.GameData.NewData.Gear`
- `DataCorePlugin.GameData.NewData.Rpms`
## iRacing Variables
- `PlayerCarMyIncidentCount`: Player incident count.
- `CarIdxLapDistPct`: Array of track percentages for 64 cars.
- `CarIdxTrackSurface`: Array of track surface states.
- `LongAccel`, `LatAccel`: Player G-forces.
## CSproj setup
- Target `net48`.
- `<PackageReference Include="Fleck" Version="1.2.0" />`
- `<PackageReference Include="IRSDKSharper" Version="1.1.4" />`
