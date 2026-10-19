# MongoDB Tiny Client — Lightweight MongoDB GUI & Client for Windows

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0_WPF-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-0078D6.svg)](https://microsoft.com/windows)
[![Portable](https://img.shields.io/badge/Portable-Single_EXE-success.svg)](publish.ps1)

A fast, lightweight, and portable **MongoDB client and GUI for Windows** in the spirit of [DB Browser for SQLite](https://sqlitebrowser.org). 

Browse collections, edit documents directly in a spreadsheet-style grid or JSON editor, manage indexes, and execute aggregation queries without launching bloated tools or entering shell commands. Built with **.NET 8 / WPF** and the official MongoDB C# driver.

---

## ⚡ Why MongoDB Tiny Client? (Alternative to MongoDB Compass)

Most modern MongoDB desktop clients (like MongoDB Compass) are built on Electron, requiring hundreds of megabytes of RAM and noticeable startup times. **MongoDB Tiny Client** is designed for developers and sysadmins who want:

* **Instant startup & tiny footprint**: Native WPF application, starts in milliseconds with minimal memory usage.
* **100% Portable**: Single standalone executable — no installer, no admin rights, and no pre-installed .NET runtime required.
* **Familiar grid view**: Browse collections like a database table. Double-click to edit values in-place while preserving BSON types.
* **Safe connection storage**: Recent connections are stored locally and encrypted using Windows DPAPI.

---

## ✨ Features

| Area | Features & Capabilities |
| --- | --- |
| **Connection Manager** | Connect to any `mongodb://` or `mongodb+srv://` (Atlas) URI. Recent connections remembered securely (DPAPI encrypted). |
| **Sidebar & Navigation** | Hierarchical database and collection browser with real-time document counts. Read-only view indicator. Context menu to create, rename, empty, drop, import, or export collections. |
| **Data Browser** | Tabular grid with dynamic columns per field, pagination, and side-by-side full JSON document viewer. |
| **Query & Filter** | Server-side filter, sort, and projection (field selection) accepting standard MongoDB shell syntax (`ObjectId("...")`, `ISODate("...")`, `/regex/i`, unquoted keys). |
| **Document Editing** | Inline cell editing (`F2`), full BSON/JSON editor with syntax validation (`Ctrl+S`), document duplication, insertion, and deletion. |
| **Index Management** | Inspect existing indexes (keys, type, unique constraints), create new compound/text/unique indexes, and drop indexes. |
| **Aggregation & Commands** | Dedicated query runner for aggregation pipelines and database commands (`db.runCommand`), with tabular and JSON output modes and built-in templates. |
| **Import & Export** | Import JSON arrays, single documents, or JSON Lines (NDJSON). Export filtered results directly to JSON, JSON Lines, or CSV. |
| **Statistics** | Detailed database and collection metrics (storage size, document count, index sizes). |

### Handy details

* Clicking a column header sorts **on the server** (ascending → descending → off).
* Right-click any cell → **Filter: field = value** to drill down immediately.
* Destructive actions (dropping databases or collections) require typing the target name to prevent accidents.

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| `Enter` (connection bar) | Connect to server |
| `Enter` (filter / sort / fields) | Apply filter or projection |
| `F5` | Refresh data / rerun query |
| `Ctrl+Enter` (query editor) | Run aggregation pipeline / command |
| `Ctrl+N` | Insert new document |
| `F2` (data grid) | Edit selected cell in-place |
| `Enter` (data grid) | Open full document in JSON editor |
| `Delete` (data grid) | Delete selected document(s) |
| `Ctrl+Left` / `Ctrl+Right` | Navigate previous / next page |
| `Ctrl+S` (document editor) | Save document changes |

---

## 🚀 Download & Quick Start

### Run Portable Executable
Download the latest `MongoDBTinyClient.exe` from the [Releases](https://github.com/finalbyte1/MongoDBTinyClient/releases) tab and run it directly. No installation required.

### Build from Source

**Requirements:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows).

```powershell
# Clone the repository
git clone https://github.com/finalbyte1/MongoDBTinyClient.git
cd MongoDBTinyClient

# Build and run in debug/release
dotnet build -c Release
.\bin\Release\net8.0-windows\MongoDBTinyClient.exe
```

### Produce a Self-Contained Single EXE

Run the included build script to produce a single self-contained executable with no external runtime dependencies:

```powershell
.\publish.ps1   # Outputs to .\publish\MongoDBTinyClient.exe
```

Configuration and logs are stored under `%LOCALAPPDATA%\MongoDBTinyClient\`.

---

## 📁 Architecture & Layout

* **`Core/`**: `MongoSession` (MongoDB driver wrapper), `BsonFormat` (BSON parser & JSON/CSV import/export), `DocumentRow` (dynamic grid model), settings & logging.
* **`UI/`**: `MainWindow` (grid, tabs, sidebar), `DocumentEditor` (JSON editor modal), `Dialog` (custom dark message boxes), `Theme.xaml` (modern dark UI theme).
* **`Interop/`**: Windows Desktop Window Manager (DWM) interop for native dark title bar styling.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE) — see the LICENSE file for details.
