# CyberEngine Unity Exporter (UPM Local Package)

## Install from local path

In `Packages/manifest.json` of a Unity project:

```json
{
  "dependencies": {
    "com.yann.cyberengine.exporter": "file:../../UnityCyberEngineExporterPackage"
  }
}
```

Note: this relative path is resolved from `<UnityProject>/Packages/manifest.json`.

## Usage

- Add `SceneExporter` component on any GameObject.
- Use Inspector button `Export`.
- Or menu:
  - `Tools/CyberEngine Exporter/Export Using Selected SceneExporter`
  - `Tools/CyberEngine Exporter/Export Active Scene (Default Options)`
  - `Tools/CyberEngine Exporter/Export Project (Build Settings)`
  - `Tools/CyberEngine Exporter/Export Project + C++ Project`

## Notes

- Ignores Unity baking artifacts (`Lightmap-*`, `ReflectionProbe-*`, `LightingData.asset`, etc.).
- Does not export `.asset` or `.terrainlayer` files.
- Terrain weightmap is generated as RGBA from splat layers and reused in generated C++.
- Exports `component_audit.json` and `component_audit.md` with unsupported built-in component priorities.
- Generates `project/` with a CMake runner and `project/external/CyberEngine` symlink when enabled.
