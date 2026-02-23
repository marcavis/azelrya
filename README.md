# Azelrya

A lightweight fantasy map editor desktop app for painting land and water maps.

## Features

- Two-color terrain palette (land and water)
- Circular brush with adjustable size
- Undo/Redo with configurable history limit
- Import and export PNG maps
- View zoom presets (100%, 200%, 300%, 400%)
- Pixel-crisp nearest-neighbor rendering when zoomed

## Tech Stack

- C# (.NET 9)
- Avalonia UI
- ImageSharp

## Getting Started

### Prerequisites

- .NET SDK 9.0+

### Run

```bash
cd Azelrya
dotnet run
```

### Build

```bash
cd Azelrya
dotnet build
```

## Configuration

Runtime settings are loaded from `azelrya.config.json` in the app output directory.

Current setting:

- `historyLimit`: maximum undo/redo history depth (default: `50`)

## Project Structure

- `fantasymap.sln`
- `Azelrya/`

## Attribution

This project was developed with assistance from GitHub Copilot.

## License

No license has been specified yet.
