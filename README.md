# MongoDB Tiny Client

A small, friendly MongoDB GUI for Windows in the spirit of [DB Browser for SQLite](https://sqlitebrowser.org):
pick a collection on the left, browse and edit its documents in a grid, no shell needed.
Written in **.NET 8 / WPF** with the official MongoDB C# driver; dark theme, single portable exe.

---

## Features

| Area | What you can do |
| --- | --- |
| **Connection** | Any `mongodb://` or `mongodb+srv://` connection string; recent connections remembered (encrypted with Windows DPAPI for the current user) |
| **Sidebar** | Databases and collections with document counts; views marked as read-only. Right-click for create / rename / import / export / empty / drop |
| **Browse Data** | Grid with one column per field, paging, filter / sort / fields (projection) in MongoDB syntax, side panel with the full document as JSON |
| **Editing** | Double-click (or F2) a cell to change a single value, keeping its type; nested documents and arrays open the JSON editor. Add, duplicate and delete documents |
| **Indexes** | List, create (including unique, text, compound) and drop indexes |
| **Run Query** | Aggregation pipelines on the selected collection or database commands (`db.runCommand`), results as table or JSON, ready-made examples |
| **Info** | Collection / database statistics, or the definition of a view |
| **Import / Export** | Import JSON array, single document or JSON Lines; export JSON, JSON Lines or CSV (all documents matching the filter) |

### Handy details

* Filters accept the shell syntax: unquoted keys, `ObjectId("...")`, `ISODate("...")`, `/regex/i`.
* Clicking a column header sorts **on the server** (ascending → descending → off).
* Right-click a cell → **Filter: field = value** to drill down instantly.
* Dropping a database or a collection requires typing its name.

### Keyboard

| Key | Action |
| --- | --- |
| `Enter` (connection box) | connect |
| `Enter` (filter / sort / fields) | apply |
| `F5` | refresh / run the current tab |
| `Ctrl+Enter` (query editor) | run |
| `Ctrl+N` | new document |
| `Enter` / `Del` (grid) | edit / delete selected documents |
| `F2` (grid) | edit the current cell |
| `Ctrl+Left` / `Ctrl+Right` (grid) | previous / next page |
| `Ctrl+S` (document editor) | save |

---

## Build and run

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\MongoDBTinyClient.exe
```

Single portable exe, no .NET runtime required:

```powershell
.\publish.ps1   # -> publish\MongoDBTinyClient.exe
```

Settings and log live in `%LOCALAPPDATA%\MongoDBTinyClient` (`settings.json`, `mongodbtinyclient.log`).

## Project layout

| Folder | Content |
| --- | --- |
| `Core/` | `MongoSession` (driver wrapper), `BsonFormat` (BSON ↔ text, import/export), `DocumentRow` (grid rows), settings, log |
| `UI/` | `MainWindow`, `DocumentEditor`, `Dialog` (dark message/input boxes), `Theme.xaml` |
| `Interop/` | Dark title bar (DWM) |
