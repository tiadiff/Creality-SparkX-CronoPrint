# Creality SparkX CronoPrint: Telemetry Console & Print History Suite

### High-Performance Desktop Telemetry, Range-Based G-Code Inspection & Real-Time Monitoring for Creality 3D Printers

**Creality SparkX CronoPrint** is a high-performance desktop application (.NET 8 WinForms + embedded Chromium WebView2) and telemetry analytics suite engineered specifically for the **Creality SparkX i7** and **K1 / K1 Max** CoreXY ecosystem running Moonraker and Klipper firmware.

<img width="822" height="629" alt="image" src="https://github.com/user-attachments/assets/72968ff5-c0ec-4edb-a905-e8d247fcce9e" />

The suite provides real-time print telemetry, integrated live chamber video monitoring, cost accounting, and ultra-fast G-code metadata extraction directly from remote printer storage without downloading multi-megabyte print files.

---

## Architecture & Range-Based Data Flow

Instead of saturating Wi-Fi networks by transferring full G-code files, CronoPrint leverages parallel HTTP byte-range requests throttled by semaphores:

```
                             CRONOPRINT ARCHITECTURE
 ┌──────────────────────┐         HTTP GET (List)          ┌───────────────────────────┐
 │ Moonraker History DB │ ───────────────────────────────> │ C# .NET 8 Telemetry Core  │
 └──────────────────────┘                                  │ (Parallel HTTP Range Sem) │
 ┌──────────────────────┐    HTTP Range [0..45KB] Thumb    │                           │
 │ G-Code Storage       │ ───────────────────────────────> │  - Base64 PNG Extraction  │
 │ (Remote Filesystem)  │ ──> HTTP Range [EOF-76KB..EOF] ─>│  - Slicer Config Parsing  │
 └──────────────────────┘                                  └─────────────┬─────────────┘
 ┌──────────────────────┐         WebRTC Stream                  Bidirectional Bridge
 │ Chamber Live Camera  │ ───────────────────────────────>         (PostMessage)
 └──────────────────────┘                                                │
                                                           ┌─────────────▼─────────────┐
                                                           │ Chromium WebView2 UI View │
                                                           │ (Dark High-Contrast Theme)│
                                                           └───────────────────────────┘
```

---

## Core Capabilities

### 1. Ultra-Fast Range-Based G-Code Metadata Extraction
* **Embedded Thumbnail Extraction:** Inspects the first 45 KB of G-code headers to extract high-resolution (up to 260x260) embedded base64 PNG thumbnails in milliseconds.
* **Slicer Profile Parsing:** Reads the final 76 KB of G-code footers to parse exact slicing parameters from Creality Print, OrcaSlicer, and PrusaSlicer:
  * Nozzle diameter and layer height (first layer and normal layers)
  * Infill density and infill geometry pattern
  * Wall loops / perimeter count and maximum Z-height
  * Ironing toggle and surface ironing type
  * Support enable flag and structure classification (Standard / Tree)
  * Extruder and heated bed operational temperatures

### 2. Multi-Filament & Multi-Color Accounting
* **Per-Spool Telemetry:** Parses multi-color arrays from modern slicer footers (Creality Print, OrcaSlicer) to track every filament used in a single print job:
  * Hexadecimal color swatch and display name (`filament_colour`, `default_filament_colour`)
  * Material classification (`filament_type`, e.g., PLA, PETG, ABS, TPU)
  * Filament spool preset identification (`filament_settings_id`)
  * Itemized usage per color in grams (`g`), percentage (`%`), and length (`m`)
  * Accurate financial cost attribution per color and cumulative print cost
  * Total toolhead filament swap count (`total filament change`)
* **Multi-Color Visual Breakdown:** Interactive multi-color segmented progress bars and individual filament cards in the Slide-Over Inspector drawer, list feed, and plate gallery.

### 3. Finished Model vs. Waste Feature Breakdown (Prime Tower, Support, Brim)
* **Extrusion Move Classifier:** Performs streaming relative extrusion analysis (`G1 ... E<val>`) along G-code feature tags:
  * **Finished Model:** Outer wall, Inner wall, Sparse infill, Solid infill, Top surface, Bridge infill
  * **Prime / Wipe Tower:** Toolhead purge and purge tower volume during color changes
  * **Supports:** Tree support, Standard support, and Support interface structures
  * **Brim / Skirt:** Bed adhesion structures
* **Zero-Overhead Fast Path:** Automatically detects single-structure jobs when supports, prime towers, and brims are disabled in slicer parameters (0 ms overhead).
* **Cost & Weight Accounting:** Accurately calculates how much weight and money went into the actual physical part versus auxiliary print waste.
* **Persistent Cache & On-Demand Engine:** Analyzed feature breakdowns are persisted to `feature_breakdown_cache.json` for zero-latency retrieval, with an interactive on-demand computation trigger in the desktop Inspector drawer.

### 4. Live Chamber Camera & Fullscreen Modal
* Directly embeds the printer's WebRTC video stream (port 8000) alongside active print metrics.
* Single-click full-screen modal viewport with backdrop blur for detailed first-layer and print progress monitoring.

### 5. Comprehensive Print Telemetry Views
* **Horizontal Feed View:** Production-oriented list format displaying thumbnails, project filenames, slicer profiles, execution dates, duration, multi-filament swatches, feature breakdown badges, filament consumption, and calculated cost.
* **Plate Gallery View:** Responsive visual card grid focusing on 3D rendered thumbnails, multi-color badges, and rapid visual status assessment.
* **Data Table View:** High-density tabular layout with sorting, text search filtering, multi-color indicators, and nozzle/support segmentation.
* **Slide-Over Inspector Drawer:** Detailed side panel exposing deep slicer parameters, multi-filament itemized cards, model vs. waste breakdown bars, mechanical kinematics, temperatures, and one-click remote reprint dispatch via Moonraker.

### 6. Extended Italian Localization & Typographic Contrast
* Italian extended date format (e.g., `Lunedi 7 Gennaio 2026, 17:32`).
* Distinct, unambiguous badge labels for `Ugello` (Nozzle), `Layer` (Height), `Infill` (Density/Pattern), and `Supporti` (Supports).

### 7. Standalone HTML Snapshot Generator
* Includes a lightweight Python script (`generate_printer_report.py`) and Windows batch runner (`Genera_Report_Stampe.bat`) to generate offline, self-contained HTML reports (`printer_history.html`) with full multi-filament and breakdown visualization without compiling .NET binaries.

---

## Installation & Setup

### Prerequisites
* Windows 10 or Windows 11 (64-bit)
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or .NET 8.0 Desktop Runtime
* [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (pre-installed on Windows 11 and modern Windows 10)

### Quick Start (Desktop Console)
Launch the desktop console via the included batch script:
```powershell
.\Avvia_SparkX_Console.bat
```
*(This opens the command prompt and launches `SparkXPrintConsole.exe` directly, providing embedded real-time live telemetry, webcam streaming, and job history inside the desktop application window without launching any external browser).*

Alternatively, build and run from source in Release configuration:
```powershell
dotnet build SparkXPrintConsole -c Release
.\SparkXPrintConsole\bin\Release\net8.0-windows\SparkXPrintConsole.exe
```

### Standalone HTML Report
If you want to generate a static HTML report snapshot and view it in your default web browser:
```powershell
.\Genera_Report_Stampe.bat
```
or via Python CLI:
```powershell
python generate_printer_report.py --open
```
This queries Moonraker, extracts G-code metadata, produces `printer_history.html`, and opens it in your default web browser.

---

## Release Notes

### Version 1.2.0
- Added Multi-Filament and Multi-Color spool accounting: itemized usage per color in grams, percentage, and length, with individual filament cards and multi-color segmented progress bars.
- Added Finished Model vs. Waste Feature Breakdown: streaming relative extrusion analysis classifying Outer/Inner walls, Infill, Top surface, Prime/Wipe towers, Supports, and Brims.
- Added accurate cost and weight separation between actual physical parts and auxiliary print waste.
- Added persistent breakdown caching in `feature_breakdown_cache.json` with on-demand computation engine.
- Enhanced Slide-Over Inspector drawer, Horizontal Feed, and Plate Gallery with multi-color swatches and breakdown badges.
- Updated standalone Python Moonraker history extractor (`generate_printer_report.py`) and offline HTML snapshot (`printer_history.html`).

---

## File System Manifest

```
├── SparkXPrintConsole/             # C# .NET 8 WinForms + WebView2 desktop application
│   ├── Program.cs                  # High-DPI application entry point
│   ├── MainForm.cs                 # WinForms host window with DWM dark titlebar & WebView2 bridge
│   ├── PrinterService.cs           # Concurrent Moonraker HTTP range parser & live telemetry engine
│   ├── Models.cs                   # Strongly-typed print job & telemetry entities
│   ├── AppSettings.cs              # JSON configuration persistence
│   └── wwwroot/index.html          # High-contrast dark-mode dashboard UI (emerald accents)
├── Avvia_SparkX_Console.bat        # Launcher script: opens CMD prompt and starts SparkXPrintConsole.exe
├── Genera_Report_Stampe.bat        # Batch script: generates standalone printer_history.html & opens browser
├── generate_printer_report.py      # Standalone Python Moonraker history & range extractor
├── printer_history.html            # Standalone telemetry report snapshot
├── .gitignore                      # Git exclusion rules for .NET and temp files
├── LICENSE                         # MIT Open Source License
└── README.md                       # Complete technical specification
```

---

## License

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for complete details.

*Disclaimer: Creality, Creality Print, K1, K1 Max, and SparkX i7 are trademarks or registered trademarks of Shenzhen Creality 3D Technology Co., Ltd. Creality SparkX CronoPrint is an independent, non-affiliated open-source software project.*
