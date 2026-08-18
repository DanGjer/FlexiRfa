# FlexiRfa

FlexiRfa is a Revit extension for creating **rotatable families** — families that can be freely rotated in 3D via a nested "3D Orientation Family" — without having to model the geometry by hand in the Family Editor every time.

It ships with presets for common electrical fixtures (downlights, smoke detectors, electrical sockets, data outlets, rectangular light fixtures) plus a fully custom box/cylinder extrusion mode, and wires up the appropriate electrical connectors automatically.

## What it does

Copies one of the bundled rotatable family templates, sets the family category, generates the fixture geometry inside the nested "3D Orientation Family", creates the electrical connectors for the chosen preset, and loads the finished family into the active Revit document.

The tool is **create-only** by design. If a generated family isn't right, delete it from the model and generate a new one with adjusted settings.

## Requirements

- Autodesk Revit (built against **2026** by default — see `MainRevitVersion` in [FlexiRfa.csproj](FlexiRfa.csproj) if targeting a different version).
- .NET 8 SDK.
- The [flexRevit](https://www.flexrevit.com/) extension host, which loads and runs `IRevitExtension` add-ins like this one.

## Building and installing

```powershell
dotnet build FlexiRfa.csproj -c Debug
```

This produces the extension package under `bin/Debug/net8.0-windows/publish`. Point your flexRevit extension folder at that output (or use `dotnet publish`) so it appears in Revit's flexRevit panel.

## Using the tool

Run **FlexiRfa** from the flexRevit ribbon in Revit.

### Common fields

- **Family template** — the `.rft`/`.rfa` template to copy. Defaults to the shared network template.
- **New family name** — required. Generation fails if a family with this name already exists in the document, rather than silently overwriting it.
- **Family category** — the Revit category to assign, limited to the categories electrical engineers use (Electrical Equipment/Fixtures, Lighting Fixtures/Devices, Data, Communication, Fire Alarm, Security, Nurse Call and Telephone Devices).
- **Preset** — chooses the fixture shape and dimensions. Selecting **Custom** exposes the manual profile and connector controls.

### Presets

| Preset | Fields | Result | Connectors |
|---|---|---|---|
| **Custom** | Profile shape (Box/Cylinder), Width/Height or Diameter, Depth (all default to 200 mm) | A single extrusion. | Chosen via checkboxes |
| **Downlight** | Downlight diameter | Recessed cylindrical body with a ceiling trim ring. | 1 × Power |
| **Smoke Detector** | Smoke detector diameter | Surface-mounted sensor chamber with a ceiling plate 25 mm wider than the chamber. | 1 × Fire Alarm |
| **Electrical Socket (Single/Double/Quadruple)** | *(fixed dimensions)* | Wall plate with 1, 2 (stacked), or 4 (2×2 grid) recessed 40 mm outlets, each with pin holes. | 1 × Power |
| **Data Socket (Double RJ45)** | *(fixed dimensions)* | 85 × 85 mm wall plate with a stepped cover frame and two recessed, tapered RJ45 pockets. | 2 × Data |
| **Data Outlet (Single RJ45)** | *(fixed dimensions)* | As above with a single centred RJ45 pocket. | 1 × Data |
| **Light Fixture (Rectangular)** | Fixture length, Fixture width | Surface-mounted batten luminaire: shallow housing with an inset diffuser. | 1 × Power |

### Connectors

Electrical connectors are generated from scratch each time — the template does not need any pre-placed connectors.

For the **Custom** preset, tick the connectors you need. Each type is hosted on its own face of the extrusion so connectors never overlap and always land in a predictable spot:

| Connector | Face |
|---|---|
| Power | Left |
| Data | Right |
| Communication | Top |
| Fire alarm | Bottom |
| Security | Back |
| Controls | Front |

## Bundled templates

The repository includes ready-to-use rotatable family templates:

- [Roterbar Familie Template.rfa](Roterbar%20Familie%20Template.rfa) and versioned variants (`.0003`, `.0004`, `.0005`)
- [magiFamilyGeom Geometry.rfa](magiFamilyGeom%20Geometry.rfa)

Pick one of these as the **Family template**, or supply your own as long as it contains a nested family named **"3D Orientation Family"**.

## Troubleshooting

- **"A family named 'X' already exists in this document."** — pick a different name, or delete the existing family first.
- **"Could not find the nested '3D Orientation Family'..."** — the chosen template doesn't follow the expected structure; use one of the bundled templates.
- **"Could not find the extrusion geometry to host electrical connectors on."** — geometry generation produced no solid to attach connectors to; check the preset's dimension values.