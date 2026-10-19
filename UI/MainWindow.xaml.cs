using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDBTinyClient.Core;
using MongoDBTinyClient.Interop;

namespace MongoDBTinyClient.UI;

public partial class MainWindow : Window
{
    private enum NodeKind { Database, Collection, Placeholder }

    private sealed record NodeInfo(NodeKind Kind, string Database, string? Collection = null, bool IsView = false);

    private const int MaxQueryResults = 1000;
    private const int ImportBatchSize = 1000;

    private const string DefaultPipeline = "[\n  { $match: {} },\n  { $limit: 20 }\n]";
    private const string DefaultCommand = "{ dbStats: 1 }";

    private static readonly string[] SystemDatabases = { "admin", "config", "local" };

    private readonly AppSettings _settings;
    private MongoSession? _session;

    // Current selection
    private string? _db;
    private string? _coll;
    private bool _isView;

    // Browse tab state
    private int _page;
    private long _total;
    private List<string> _columns = new();
    private List<DocumentRow> _rows = new();
    private bool _cellEditing;
    private DocumentRow? _contextRow;
    private string? _contextField;

    // Query tab state
    private List<BsonDocument> _queryResults = new();
    private string _pipelineText = DefaultPipeline;
    private string _commandText = DefaultCommand;
    private bool _commandModeForced; // switched to command mode only because no collection was selected

    private int _busyCount;
    private bool _infoDirty = true;

    public MainWindow()
    {
        InitializeComponent();
        Native.UseDarkTitleBar(this);

        _settings = SettingsStore.Load();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        ConnectionBox.Text = _settings.Connections.FirstOrDefault() ?? AppSettings.DefaultConnection;
        PageSizeBox.Text = _settings.PageSize.ToString(CultureInfo.InvariantCulture);
        QueryEditor.Text = _pipelineText;

        WireEvents();
        UpdateConnectionUi();
        UpdateSelectionUi();

        Loaded += (_, _) =>
        {
            ConnectionBox.Focus();
            ConnectionBox.CaretIndex = ConnectionBox.Text.Length;
        };
        Closing += (_, _) => SaveWindowSettings();
        Closed += (_, _) => _session?.Dispose();
    }

    // ============================================================ wiring

    private void WireEvents()
    {
        ConnectButton.Click += async (_, _) => await ConnectOrDisconnectAsync();
        ConnectionBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await ConnectAsync(); }
        };
        ConnectionBox.TextChanged += (_, _) => UpdateConnectionUi();
        RecentButton.Click += (_, _) => ShowRecentMenu();

        NewDatabaseButton.Click += async (_, _) => await CreateDatabaseAsync();
        RefreshTreeButton.Click += async (_, _) => await LoadTreeAsync();
        DbTree.SelectedItemChanged += async (_, _) => await OnTreeSelectionChangedAsync();

        Tabs.SelectionChanged += async (s, e) =>
        {
            if (e.OriginalSource != Tabs) return; // bubbled from inner grids
            if (Tabs.SelectedItem == InfoTab && _infoDirty) await LoadInfoAsync();
        };

        // Browse
        FindButton.Click += async (_, _) => await ApplyFilterAsync();
        ClearFilterButton.Click += async (_, _) =>
        {
            FilterBox.Text = SortBox.Text = ProjectionBox.Text = string.Empty;
            await ApplyFilterAsync();
        };
        foreach (var box in new[] { FilterBox, SortBox, ProjectionBox, PageSizeBox })
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; await ApplyFilterAsync(); }
            };

        FirstPageButton.Click += async (_, _) => await GoToPageAsync(0);
        PrevPageButton.Click += async (_, _) => await GoToPageAsync(_page - 1);
        NextPageButton.Click += async (_, _) => await GoToPageAsync(_page + 1);
        LastPageButton.Click += async (_, _) => await GoToPageAsync(LastPage);

        AddDocButton.Click += (_, _) => AddDocument();
        EditDocButton.Click += (_, _) => EditSelectedDocument();
        CloneDocButton.Click += (_, _) => DuplicateSelectedDocument();
        DeleteDocButton.Click += async (_, _) => await DeleteSelectedDocumentsAsync();
        ImportButton.Click += async (_, _) => await ImportAsync();
        ExportButton.Click += (_, _) => ShowExportMenu();
        CopyDocButton.Click += (_, _) => CopySelectedAsJson();
        PreviewEditButton.Click += (_, _) => EditSelectedDocument();

        DocsGrid.SelectionChanged += (_, _) => UpdatePreview();
        DocsGrid.Sorting += async (_, e) => { e.Handled = true; await SortByColumnAsync(e.Column); };
        DocsGrid.BeginningEdit += OnBeginningEdit;
        DocsGrid.CellEditEnding += OnCellEditEnding;
        DocsGrid.PreviewKeyDown += async (_, e) => await OnDocsGridKeyDownAsync(e);
        DocsGrid.PreviewMouseRightButtonDown += (_, e) => RememberContextCell(e);
        DocsGrid.ContextMenuOpening += (_, e) => { if (!BuildDocsContextMenu()) e.Handled = true; };
        DocsGrid.ContextMenu = new ContextMenu();
        DocsGrid.MouseDoubleClick += (_, e) =>
        {
            // Double-click on the row number column opens the whole document
            if (FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject)?.Column?.DisplayIndex == 0)
                EditSelectedDocument();
        };

        // Indexes
        CreateIndexButton.Click += async (_, _) => await CreateIndexAsync();
        IndexKeysBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await CreateIndexAsync(); }
        };
        DropIndexButton.Click += async (_, _) => await DropIndexAsync();
        RefreshIndexesButton.Click += async (_, _) => await LoadIndexesAsync();

        // Query
        AggregateMode.Checked += (_, _) => SwitchQueryMode(aggregate: true);
        CommandMode.Checked += (_, _) => SwitchQueryMode(aggregate: false);
        RunQueryButton.Click += async (_, _) => await RunQueryAsync();
        ExamplesButton.Click += (_, _) => ShowExamplesMenu();
        ResultTableView.Checked += (_, _) => UpdateResultView();
        ResultJsonView.Checked += (_, _) => UpdateResultView();
        CopyResultsButton.Click += (_, _) =>
        {
            if (_queryResults.Count > 0) CopyToClipboard(ResultsJson.Text, "Results copied as JSON.");
        };
        ExportResultsButton.Click += async (_, _) => await ExportDocumentsAsync(_queryResults, "results");
        QueryEditor.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await RunQueryAsync();
            }
        };

        PreviewKeyDown += async (_, e) => await OnWindowKeyDownAsync(e);
    }

    private async Task OnWindowKeyDownAsync(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (_session == null) await ConnectAsync();
            else if (Tabs.SelectedItem == QueryTab) await RunQueryAsync();
            else if (Tabs.SelectedItem == IndexesTab) await LoadIndexesAsync();
            else if (Tabs.SelectedItem == InfoTab) await LoadInfoAsync();
            else await ApplyFilterAsync();
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control && Tabs.SelectedItem == BrowseTab)
        {
            e.Handled = true;
            AddDocument();
        }
    }

    // ============================================================ connection

    private async Task ConnectOrDisconnectAsync()
    {
        if (_session != null && ConnectionBox.Text.Trim() == _session.ConnectionString) Disconnect();
        else await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var connectionString = ConnectionBox.Text.Trim();
        if (connectionString.Length == 0) connectionString = ConnectionBox.Text = AppSettings.DefaultConnection;
        if (_session != null && connectionString == _session.ConnectionString) return;

        Disconnect();
        ConnectButton.IsEnabled = false;
        await RunAsync("Connecting…", async () =>
        {
            var session = await MongoSession.ConnectAsync(connectionString);
            _session = session;
            _settings.RememberConnection(connectionString);
            SettingsStore.Save(_settings);

            await LoadTreeAsync();
            SetStatus($"Connected to {session.DisplayName}");

            try
            {
                var info = await session.ServerStatusAsync();
                ServerText.Text = $"MongoDB {info.GetValue("version", "?")}  ·  {session.DisplayName}";
            }
            catch (MongoException)
            {
                ServerText.Text = session.DisplayName;
            }
        });
        ConnectButton.IsEnabled = true;
        UpdateConnectionUi();
        UpdateSelectionUi();
    }

    private void Disconnect()
    {
        if (_session == null) return;
        _session.Dispose();
        _session = null;
        _db = _coll = null;
        DbTree.Items.Clear();
        ServerText.Text = string.Empty;
        SetStatus("Disconnected");
        UpdateConnectionUi();
        UpdateSelectionUi();
    }

    private void UpdateConnectionUi()
    {
        bool connected = _session != null;
        bool sameText = connected && ConnectionBox.Text.Trim() == _session!.ConnectionString;
        ConnectButton.Content = sameText ? "Disconnect" : "Connect";
        ConnectButton.Style = (Style)FindResource(sameText ? "FlatButton" : "AccentButton");
        ConnectionDot.Fill = (Brush)FindResource(connected ? "AccentBrush" : "TrackBrush");
        NewDatabaseButton.IsEnabled = RefreshTreeButton.IsEnabled = connected;
        TreeHint.Visibility = connected && DbTree.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!connected) TreeHint.Text = "Not connected.";
    }

    private void ShowRecentMenu()
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var connections = _settings.Connections;
        if (connections.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No recent connections", IsEnabled = false });

        foreach (var cs in connections)
        {
            var item = new MenuItem { Header = BsonFormat.MaskConnectionString(cs), Icon = Glyph("") };
            item.Click += async (_, _) =>
            {
                ConnectionBox.Text = cs;
                await ConnectAsync();
            };
            menu.Items.Add(item);
        }

        if (connections.Count > 0)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear recent connections", Icon = Glyph("") };
            clear.Click += (_, _) =>
            {
                _settings.RecentConnections.Clear();
                SettingsStore.Save(_settings);
            };
            menu.Items.Add(clear);
        }
        menu.IsOpen = true;
    }

    // ============================================================ tree

    private async Task LoadTreeAsync()
    {
        if (_session == null) return;
        var session = _session;

        // Keep expanded databases and the selection across refreshes
        var expanded = DbTree.Items.OfType<TreeViewItem>()
            .Where(i => i.IsExpanded).Select(i => ((NodeInfo)i.Tag).Database).ToHashSet();
        var (selDb, selColl) = (_db, _coll);

        await RunAsync("Loading databases…", async () =>
        {
            var names = await session.ListDatabasesAsync();
            DbTree.Items.Clear();
            var ordered = names.Where(n => !SystemDatabases.Contains(n))
                .Concat(names.Where(n => SystemDatabases.Contains(n)));
            foreach (var name in ordered)
                DbTree.Items.Add(CreateDatabaseNode(name));

            TreeHint.Text = names.Count == 0 ? "No databases. Use + to create one." : string.Empty;
            UpdateConnectionUi();

            // Expand what was open before, or the only / default user database
            var userDbs = names.Where(n => !SystemDatabases.Contains(n)).ToList();
            if (expanded.Count == 0)
            {
                if (session.DefaultDatabase != null) expanded.Add(session.DefaultDatabase);
                else if (userDbs.Count == 1) expanded.Add(userDbs[0]);
            }

            foreach (var item in DbTree.Items.OfType<TreeViewItem>())
            {
                var db = ((NodeInfo)item.Tag).Database;
                if (!expanded.Contains(db)) continue;
                await LoadCollectionsAsync(item);
                item.IsExpanded = true;
                if (db == selDb) Reselect(item, selColl);
            }
            SetStatus($"{names.Count} database(s)");
        });
    }

    private static void Reselect(TreeViewItem dbItem, string? collection)
    {
        if (collection == null) { dbItem.IsSelected = true; return; }
        var match = dbItem.Items.OfType<TreeViewItem>()
            .FirstOrDefault(i => ((NodeInfo)i.Tag).Collection == collection);
        if (match != null) match.IsSelected = true;
        else dbItem.IsSelected = true;
    }

    private TreeViewItem CreateDatabaseNode(string name)
    {
        bool system = SystemDatabases.Contains(name);
        var item = new TreeViewItem
        {
            Header = NodeHeader("", name, null, system),
            Tag = new NodeInfo(NodeKind.Database, name)
        };
        AutomationProperties.SetName(item, name);
        item.Items.Add(new TreeViewItem { Header = "Loading…", Tag = new NodeInfo(NodeKind.Placeholder, name) });
        item.Expanded += async (_, e) =>
        {
            if (e.OriginalSource == item && HasPlaceholder(item)) await LoadCollectionsAsync(item);
        };

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Create collection…", "", async () => await CreateCollectionAsync(name)));
        menu.Items.Add(MenuEntry("Refresh", "", async () => await LoadCollectionsAsync(item)));
        menu.Items.Add(MenuEntry("Run command…", "", () =>
        {
            item.IsSelected = true;
            CommandMode.IsChecked = true;
            Tabs.SelectedItem = QueryTab;
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Drop database…", "", async () => await DropDatabaseAsync(name)));
        item.ContextMenu = menu;
        return item;
    }

    private static bool HasPlaceholder(TreeViewItem item) =>
        item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: NodeInfo { Kind: NodeKind.Placeholder } };

    private async Task LoadCollectionsAsync(TreeViewItem dbItem)
    {
        if (_session == null) return;
        var session = _session;
        var db = ((NodeInfo)dbItem.Tag).Database;

        await RunAsync($"Loading collections of {db}…", async () =>
        {
            var collections = await session.ListCollectionsAsync(db);
            var selected = _db == db ? _coll : null;
            dbItem.Items.Clear();
            var counts = new List<(TreeViewItem Item, string Name)>();
            foreach (var c in collections)
            {
                var node = CreateCollectionNode(db, c);
                dbItem.Items.Add(node);
                if (!c.IsView) counts.Add((node, c.Name));
                if (c.Name == selected) node.IsSelected = true;
            }
            if (collections.Count == 0)
                dbItem.Items.Add(new TreeViewItem
                {
                    Header = new TextBlock { Text = "(no collections)", Style = (Style)FindResource("CaptionText") },
                    Tag = new NodeInfo(NodeKind.Placeholder, db),
                    Focusable = false
                });
            SetStatus($"{db}: {collections.Count} collection(s)");

            // Document counts arrive later, without blocking the UI
            _ = FillCountsAsync(session, db, counts);
        });
    }

    private async Task FillCountsAsync(MongoSession session, string db, List<(TreeViewItem Item, string Name)> nodes)
    {
        foreach (var chunk in nodes.Chunk(8))
        {
            var tasks = chunk.Select(async n =>
            {
                try { return (n.Item, n.Name, Count: await session.EstimatedCountAsync(db, n.Name)); }
                catch (MongoException) { return (n.Item, n.Name, Count: -1L); }
            }).ToList();
            foreach (var (item, name, count) in await Task.WhenAll(tasks))
                if (count >= 0) item.Header = NodeHeader("", name, count.ToString("N0", CultureInfo.InvariantCulture), false);
        }
    }

    private TreeViewItem CreateCollectionNode(string db, CollectionInfo c)
    {
        var item = new TreeViewItem
        {
            Header = NodeHeader(c.IsView ? "" : "", c.Name, c.IsView ? "view" : null, false),
            Tag = new NodeInfo(NodeKind.Collection, db, c.Name, c.IsView),
            ToolTip = c.IsView ? "Read-only view" : null
        };
        AutomationProperties.SetName(item, c.Name);

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Open", "", () => { item.IsSelected = true; Tabs.SelectedItem = BrowseTab; }));
        if (!c.IsView)
        {
            menu.Items.Add(MenuEntry("Indexes", "", () => { item.IsSelected = true; Tabs.SelectedItem = IndexesTab; }));
            menu.Items.Add(MenuEntry("Import documents…", "", async () => { item.IsSelected = true; await ImportAsync(); }));
        }
        menu.Items.Add(MenuEntry("Export documents…", "", () => { item.IsSelected = true; ShowExportMenu(); }));
        menu.Items.Add(MenuEntry("Copy name", "", () => CopyToClipboard(c.Name, $"Copied \"{c.Name}\".")));
        menu.Items.Add(new Separator());
        if (!c.IsView)
        {
            menu.Items.Add(MenuEntry("Rename…", "", async () => await RenameCollectionAsync(db, c.Name)));
            menu.Items.Add(MenuEntry("Delete all documents…", "", async () => await EmptyCollectionAsync(db, c.Name)));
        }
        menu.Items.Add(MenuEntry(c.IsView ? "Drop view…" : "Drop collection…", "",
            async () => await DropCollectionAsync(db, c.Name)));
        item.ContextMenu = menu;
        return item;
    }

    private FrameworkElement NodeHeader(string glyph, string name, string? badge, bool subtle)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            Style = (Style)FindResource("IconText"),
            Foreground = (Brush)FindResource(subtle ? "SubtleTextBrush" : "AccentBrush"),
            Margin = new Thickness(0, 1, 7, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = (Brush)FindResource(subtle ? "SubtleTextBrush" : "TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (badge != null)
            panel.Children.Add(new TextBlock
            {
                Text = badge,
                Style = (Style)FindResource("CaptionText"),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        return panel;
    }

    private async Task OnTreeSelectionChangedAsync()
    {
        if (DbTree.SelectedItem is not TreeViewItem { Tag: NodeInfo node } item || node.Kind == NodeKind.Placeholder)
            return;

        if (node.Kind == NodeKind.Database)
        {
            if (_db == node.Database && _coll == null) return;
            _db = node.Database;
            _coll = null;
            _isView = false;
            if (!item.IsExpanded) item.IsExpanded = true;
        }
        else
        {
            if (_db == node.Database && _coll == node.Collection) return;
            _db = node.Database;
            _coll = node.Collection;
            _isView = node.IsView;
            FilterBox.Text = SortBox.Text = ProjectionBox.Text = string.Empty;
            _page = 0;
        }

        _infoDirty = true;
        UpdateSelectionUi();

        if (_coll != null)
        {
            await LoadDocumentsAsync();
            await LoadIndexesAsync();
        }
        if (Tabs.SelectedItem == InfoTab) await LoadInfoAsync();
    }

    private void UpdateSelectionUi()
    {
        bool connected = _session != null;
        bool hasDb = connected && _db != null;
        bool hasColl = hasDb && _coll != null;

        WelcomePanel.Visibility = hasDb ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility = hasDb ? Visibility.Visible : Visibility.Collapsed;
        if (connected && !hasDb)
        {
            WelcomeTitle.Text = "Pick a collection";
            WelcomeText.Text = "Choose a database and a collection on the left to browse its documents. " +
                               "Right-click an item for more actions (create, rename, import, drop…).";
            WelcomeExamples.Visibility = Visibility.Collapsed;
        }
        else if (!connected)
        {
            WelcomeTitle.Text = "Connect to a MongoDB server";
            WelcomeText.Text = "Type a connection string at the top and press Enter. Examples:";
            WelcomeExamples.Visibility = Visibility.Visible;
        }

        BrowseTab.IsEnabled = IndexesTab.IsEnabled = hasColl;
        if (hasDb && !hasColl && (Tabs.SelectedItem == BrowseTab || Tabs.SelectedItem == IndexesTab))
            Tabs.SelectedItem = InfoTab;
        else if (hasColl && Tabs.SelectedItem == InfoTab && !_infoDirty)
            Tabs.SelectedItem = BrowseTab;

        string target = hasColl ? $"{_db}.{_coll}" : _db ?? string.Empty;
        CollectionTitle.Text = target + (_isView ? "   (read-only view)" : string.Empty);
        IndexesTitle.Text = target;

        bool editable = hasColl && !_isView;
        AddDocButton.IsEnabled = DeleteDocButton.IsEnabled = CloneDocButton.IsEnabled = ImportButton.IsEnabled = editable;
        EditDocButton.IsEnabled = PreviewEditButton.IsEnabled = editable;
        CreateIndexButton.IsEnabled = DropIndexButton.IsEnabled = editable;
        AggregateMode.IsEnabled = hasColl;
        if (!hasColl && AggregateMode.IsChecked == true)
        {
            CommandMode.IsChecked = true;
            _commandModeForced = true;
        }
        else if (hasColl && _commandModeForced)
        {
            AggregateMode.IsChecked = true;
        }
        UpdateQueryTarget();

        if (!hasColl)
        {
            DocsGrid.ItemsSource = null;
            IndexesGrid.ItemsSource = null;
            PreviewBox.Text = string.Empty;
        }
    }

    // ============================================================ database / collection actions

    private async Task CreateDatabaseAsync()
    {
        if (_session == null) return;
        var values = Dialog.Prompt(this, "Create database",
            "MongoDB creates a database together with its first collection.", "Create",
            new[] { ("Database name", string.Empty, "e.g. shop"), ("First collection", string.Empty, "e.g. customers") },
            v => v[0].Length == 0 || v[1].Length == 0 ? "Both names are required." : null);
        if (values == null) return;

        await RunAsync("Creating database…", async () =>
        {
            await _session.CreateCollectionAsync(values[0], values[1]);
            (_db, _coll) = (values[0], values[1]);
            await LoadTreeAsync();
            SelectNode(values[0], values[1]);
            SetStatus($"Created {values[0]}.{values[1]}");
        }, showDialog: true);
    }

    private async Task CreateCollectionAsync(string db)
    {
        if (_session == null) return;
        var values = Dialog.Prompt(this, "Create collection", $"New collection in \"{db}\".", "Create",
            new[] { ("Collection name", string.Empty, "e.g. orders") },
            v => v[0].Length == 0 ? "The name is required." : null);
        if (values == null) return;

        await RunAsync("Creating collection…", async () =>
        {
            await _session.CreateCollectionAsync(db, values[0]);
            var dbItem = FindDatabaseNode(db);
            if (dbItem != null)
            {
                await LoadCollectionsAsync(dbItem);
                dbItem.IsExpanded = true;
            }
            SelectNode(db, values[0]);
            SetStatus($"Created {db}.{values[0]}");
        }, showDialog: true);
    }

    private async Task RenameCollectionAsync(string db, string name)
    {
        if (_session == null) return;
        var values = Dialog.Prompt(this, "Rename collection", $"Rename \"{db}.{name}\".", "Rename",
            new[] { ("New name", name, name) },
            v => v[0].Length == 0 ? "The name is required." : v[0] == name ? "Type a different name." : null);
        if (values == null) return;

        await RunAsync("Renaming collection…", async () =>
        {
            await _session.RenameCollectionAsync(db, name, values[0]);
            if (_db == db && _coll == name) _coll = values[0];
            var dbItem = FindDatabaseNode(db);
            if (dbItem != null) await LoadCollectionsAsync(dbItem);
            SetStatus($"Renamed to {db}.{values[0]}");
        }, showDialog: true);
    }

    private async Task EmptyCollectionAsync(string db, string name)
    {
        if (_session == null) return;
        if (!Dialog.ConfirmByName(this, "Delete all documents",
                $"Every document in \"{db}.{name}\" will be deleted. Indexes are kept.\nThis cannot be undone.",
                name, "Delete all"))
            return;

        await RunAsync("Deleting documents…", async () =>
        {
            long deleted = await _session.DeleteManyAsync(db, name, new BsonDocument());
            if (_db == db && _coll == name) await LoadDocumentsAsync();
            await RefreshCollectionCountAsync(db);
            SetStatus($"Deleted {deleted:N0} document(s) from {db}.{name}");
        }, showDialog: true);
    }

    private async Task DropCollectionAsync(string db, string name)
    {
        if (_session == null) return;
        if (!Dialog.ConfirmByName(this, "Drop collection",
                $"\"{db}.{name}\" and all its documents and indexes will be permanently deleted.",
                name, "Drop"))
            return;

        await RunAsync("Dropping collection…", async () =>
        {
            await _session.DropCollectionAsync(db, name);
            if (_db == db && _coll == name) { _coll = null; UpdateSelectionUi(); }
            var dbItem = FindDatabaseNode(db);
            if (dbItem != null) await LoadCollectionsAsync(dbItem);
            SetStatus($"Dropped {db}.{name}");
        }, showDialog: true);
    }

    private async Task DropDatabaseAsync(string db)
    {
        if (_session == null) return;
        if (!Dialog.ConfirmByName(this, "Drop database",
                $"The database \"{db}\" with all its collections will be permanently deleted.", db, "Drop"))
            return;

        await RunAsync("Dropping database…", async () =>
        {
            await _session.DropDatabaseAsync(db);
            if (_db == db) { _db = _coll = null; UpdateSelectionUi(); }
            await LoadTreeAsync();
            SetStatus($"Dropped database {db}");
        }, showDialog: true);
    }

    private async Task RefreshCollectionCountAsync(string db)
    {
        var dbItem = FindDatabaseNode(db);
        if (_session == null || dbItem == null) return;
        var nodes = dbItem.Items.OfType<TreeViewItem>()
            .Where(i => i.Tag is NodeInfo { Kind: NodeKind.Collection, IsView: false })
            .Select(i => (i, ((NodeInfo)i.Tag).Collection!)).ToList();
        await FillCountsAsync(_session, db, nodes);
    }

    private TreeViewItem? FindDatabaseNode(string db) =>
        DbTree.Items.OfType<TreeViewItem>().FirstOrDefault(i => ((NodeInfo)i.Tag).Database == db);

    private void SelectNode(string db, string collection)
    {
        var dbItem = FindDatabaseNode(db);
        if (dbItem == null) return;
        dbItem.IsExpanded = true;
        var item = dbItem.Items.OfType<TreeViewItem>().FirstOrDefault(i => ((NodeInfo)i.Tag).Collection == collection);
        if (item != null)
        {
            // Force a reload even if this collection was already the current one
            if (_db == db && _coll == collection) _coll = null;
            item.IsSelected = true;
        }
    }

    // ============================================================ browse: loading

    private int PageSize
    {
        get
        {
            if (int.TryParse(PageSizeBox.Text.Trim(), out int size) && size is >= 1 and <= 5000)
            {
                _settings.PageSize = size;
                return size;
            }
            PageSizeBox.Text = _settings.PageSize.ToString(CultureInfo.InvariantCulture);
            return _settings.PageSize;
        }
    }

    private int LastPage => _total <= 0 ? 0 : (int)((_total - 1) / PageSize);

    private async Task ApplyFilterAsync()
    {
        _page = 0;
        await LoadDocumentsAsync();
    }

    private async Task GoToPageAsync(int page)
    {
        if (page < 0) page = 0;
        if (_total >= 0 && page > LastPage) page = LastPage;
        if (page == _page) return;
        _page = page;
        await LoadDocumentsAsync();
    }

    private (BsonDocument Filter, BsonDocument Sort, BsonDocument Projection)? ReadQuery()
    {
        try
        {
            return (BsonFormat.ParseDocument(FilterBox.Text, "The filter"),
                BsonFormat.ParseDocument(SortBox.Text, "The sort"),
                BsonFormat.ParseDocument(ProjectionBox.Text, "The fields projection"));
        }
        catch (FormatException ex)
        {
            SetStatus(ex.Message, error: true);
            return null;
        }
    }

    private async Task LoadDocumentsAsync(int reselectIndex = -1)
    {
        if (_session == null || _db == null || _coll == null) return;
        if (_cellEditing) DocsGrid.CancelEdit();
        var query = ReadQuery();
        if (query == null) return;
        var (filter, sort, projection) = query.Value;
        var (session, db, coll) = (_session, _db, _coll);
        int pageSize = PageSize;

        await RunAsync($"Loading {db}.{coll}…", async () =>
        {
            var watch = Stopwatch.StartNew();
            var result = await session.FindAsync(db, coll, filter, sort, projection, _page * pageSize, pageSize);
            if (db != _db || coll != _coll) return; // the user moved on meanwhile

            _total = result.Total;
            if (result.Documents.Count == 0 && _page > 0 && _total > 0)
            {
                _page = LastPage; // e.g. after deleting the last rows of the last page
                await LoadDocumentsAsync();
                return;
            }

            ShowDocuments(result.Documents, sort);
            if (reselectIndex >= 0 && _rows.Count > 0)
            {
                DocsGrid.SelectedIndex = Math.Min(reselectIndex, _rows.Count - 1);
                DocsGrid.ScrollIntoView(DocsGrid.SelectedItem);
            }
            UpdatePaging(result.Documents.Count, pageSize);
            SetStatus($"{result.Documents.Count:N0} document(s) loaded in {watch.ElapsedMilliseconds:N0} ms" +
                      (filter.ElementCount > 0 ? "  ·  filter applied" : string.Empty));
        });
    }

    private void ShowDocuments(List<BsonDocument> docs, BsonDocument sort)
    {
        var columns = DocumentRow.CollectColumns(docs);
        int first = _page * PageSize + 1;
        _rows = docs.Select((d, i) => new DocumentRow(d, columns, first + i)).ToList();

        if (!columns.SequenceEqual(_columns) || DocsGrid.Columns.Count == 0)
            BuildColumns(DocsGrid, columns, editable: !_isView);
        _columns = columns;

        DocsGrid.ItemsSource = _rows;

        // Show the server-side sort on the column headers (after ItemsSource, which resets it)
        var sortField = sort.ElementCount > 0 ? sort.GetElement(0) : (BsonElement?)null;
        foreach (var column in DocsGrid.Columns)
            column.SortDirection = sortField is { } s && column.SortMemberPath == s.Name
                ? (s.Value.IsNumeric && s.Value.ToDouble() < 0
                    ? System.ComponentModel.ListSortDirection.Descending
                    : System.ComponentModel.ListSortDirection.Ascending)
                : null;

        DocsEmpty.Text = FilterBox.Text.Trim().Length > 0 ? "No documents match the filter." : "This collection is empty.";
        DocsEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void BuildColumns(DataGrid grid, IReadOnlyList<string> columns, bool editable)
    {
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new Binding(nameof(DocumentRow.Number)) { StringFormat = "N0" },
            IsReadOnly = true,
            CanUserSort = false,
            ElementStyle = (Style)FindResource("GridCellNumber"),
            MinWidth = 40
        });
        for (int i = 0; i < columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                SortMemberPath = columns[i],
                Binding = new Binding($"Cells[{i}]") { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay },
                IsReadOnly = !editable || columns[i] == "_id",
                ElementStyle = (Style)FindResource("GridCellText"),
                EditingElementStyle = (Style)FindResource("GridCellEditor"),
                MaxWidth = 420,
                MinWidth = 50
            });
        }
    }

    private void UpdatePaging(int shown, int pageSize)
    {
        long from = shown == 0 ? 0 : (long)_page * pageSize + 1;
        long to = (long)_page * pageSize + shown;
        string total = _total < 0 ? "?" : _total.ToString("N0", CultureInfo.InvariantCulture);
        PageText.Text = shown == 0 ? $"0 of {total}" : $"{from:N0} – {to:N0} of {total}";

        bool hasNext = _total < 0 ? shown == pageSize : to < _total;
        FirstPageButton.IsEnabled = PrevPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = hasNext;
        LastPageButton.IsEnabled = hasNext && _total >= 0;
    }

    private void UpdatePreview()
    {
        var row = DocsGrid.SelectedItem as DocumentRow;
        PreviewBox.Text = row == null
            ? (DocsGrid.SelectedItems.Count > 1 ? $"{DocsGrid.SelectedItems.Count} documents selected" : string.Empty)
            : BsonFormat.ToEditableJson(row.Document);
        bool one = row != null && !_isView;
        EditDocButton.IsEnabled = PreviewEditButton.IsEnabled = CloneDocButton.IsEnabled = one;
        DeleteDocButton.IsEnabled = !_isView && DocsGrid.SelectedItems.Count > 0;
        CopyDocButton.IsEnabled = row != null;
    }

    private async Task SortByColumnAsync(DataGridColumn column)
    {
        var field = column.SortMemberPath;
        if (string.IsNullOrEmpty(field)) return;
        // First click ascending, second descending, third removes the sort
        SortBox.Text = column.SortDirection switch
        {
            null => $"{{ {QuoteField(field)}: 1 }}",
            System.ComponentModel.ListSortDirection.Ascending => $"{{ {QuoteField(field)}: -1 }}",
            _ => string.Empty
        };
        await ApplyFilterAsync();
    }

    private static string QuoteField(string field) =>
        Regex.IsMatch(field, @"^[A-Za-z_$][A-Za-z0-9_$]*$") ? field : new BsonString(field).ToJson();

    // ============================================================ browse: editing

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not DocumentRow row || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;
        var field = e.Column.SortMemberPath;
        var value = row.Document.TryGetValue(field, out var v) ? v : null;
        if (!BsonFormat.IsInlineEditable(value))
        {
            // Nested documents and arrays are edited in the JSON editor
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => EditDocument(row));
            return;
        }
        _cellEditing = true;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        _cellEditing = false;
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not DocumentRow row) return;
        if (e.EditingElement is not TextBox box || _session == null || _db == null || _coll == null) return;

        var field = e.Column.SortMemberPath;
        int column = _columns.IndexOf(field);
        var original = row.Document.TryGetValue(field, out var v) ? v : null;
        var oldText = BsonFormat.ToCellText(original);
        if (box.Text == oldText) return;
        if (original == null && box.Text.Trim().Length == 0) return; // nothing typed in a missing field

        BsonValue newValue;
        try
        {
            newValue = BsonFormat.ParseCell(box.Text, original);
        }
        catch (FormatException ex)
        {
            e.Cancel = true;
            SetStatus(ex.Message + "  (Esc to cancel)", error: true);
            return;
        }

        if (row.Id is not { } id)
        {
            e.Cancel = true;
            SetStatus("This document has no _id and cannot be updated.", error: true);
            return;
        }

        var (session, db, coll) = (_session, _db, _coll);
        _ = RunAsync("Saving…", async () =>
        {
            try
            {
                await session.SetFieldAsync(db, coll, id, field, newValue);
                row.SetField(field, newValue, column);
                if (DocsGrid.SelectedItem == row) UpdatePreview();
                SetStatus($"Updated {field} = {BsonFormat.ToCellText(newValue)}");
            }
            catch
            {
                // Put the old value back in the grid, then report
                if (column >= 0) row.Cells[column] = oldText;
                throw;
            }
        }, showDialog: true);
    }

    private async Task OnDocsGridKeyDownAsync(KeyEventArgs e)
    {
        if (_cellEditing) return;
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await DeleteSelectedDocumentsAsync();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            EditSelectedDocument();
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (PrevPageButton.IsEnabled) await GoToPageAsync(_page - 1);
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (NextPageButton.IsEnabled) await GoToPageAsync(_page + 1);
        }
    }

    private void EditSelectedDocument()
    {
        if (DocsGrid.SelectedItem is DocumentRow row) EditDocument(row);
    }

    private void EditDocument(DocumentRow row)
    {
        if (_session == null || _db == null || _coll == null) return;
        if (_isView) { SetStatus("Views are read-only.", error: true); return; }
        if (row.Id is not { } id) { SetStatus("This document has no _id and cannot be updated.", error: true); return; }

        var (session, db, coll) = (_session, _db, _coll);
        int index = DocsGrid.SelectedIndex;
        var editor = new DocumentEditor(this, "Edit document",
            $"{db}.{coll}   ·   _id: {BsonFormat.ToCellText(id)}", row.Document, async doc =>
            {
                if (!doc.Contains("_id")) doc.InsertAt(0, new BsonElement("_id", id));
                else if (doc["_id"] != id)
                    throw new InvalidOperationException(
                        "The _id of a document cannot be changed. Use Duplicate to create a copy with a new _id, then delete the original.");
                await session.ReplaceAsync(db, coll, id, doc);
            });
        if (editor.ShowDialog() == true)
        {
            SetStatus("Document saved");
            _ = LoadDocumentsAsync(index);
        }
    }

    private void AddDocument()
    {
        if (_session == null || _db == null || _coll == null || _isView) return;
        OpenInsertEditor(TemplateFromColumns(), "New document");
    }

    private void DuplicateSelectedDocument()
    {
        if (DocsGrid.SelectedItem is not DocumentRow row) return;
        var copy = row.Document.DeepClone().AsBsonDocument;
        copy.Remove("_id");
        OpenInsertEditor(copy, "Duplicate document");
    }

    private void OpenInsertEditor(BsonDocument template, string title)
    {
        if (_session == null || _db == null || _coll == null || _isView) return;
        var (session, db, coll) = (_session, _db, _coll);
        var editor = new DocumentEditor(this, title,
            $"Insert into {db}.{coll}. Leave out _id to have one generated automatically.", template,
            doc => session.InsertAsync(db, coll, doc));
        if (editor.ShowDialog() == true)
        {
            SetStatus("Document inserted");
            _ = LoadDocumentsAsync();
            _ = RefreshCollectionCountAsync(db);
        }
    }

    /// <summary>An empty document with the fields seen on this page, so the user only fills in values.</summary>
    private BsonDocument TemplateFromColumns()
    {
        var template = new BsonDocument();
        foreach (var field in _columns.Where(c => c != "_id"))
        {
            var sample = _rows.Select(r => r.Document.GetValue(field, BsonNull.Value))
                .FirstOrDefault(v => !v.IsBsonNull) ?? BsonNull.Value;
            template[field] = sample.BsonType switch
            {
                BsonType.String => string.Empty,
                BsonType.Int32 => 0,
                BsonType.Int64 => 0L,
                BsonType.Double => 0.0,
                BsonType.Decimal128 => new BsonDecimal128(0),
                BsonType.Boolean => false,
                BsonType.DateTime => new BsonDateTime(DateTime.UtcNow),
                BsonType.Array => new BsonArray(),
                BsonType.Document => new BsonDocument(),
                _ => BsonNull.Value
            };
        }
        return template;
    }

    private async Task DeleteSelectedDocumentsAsync()
    {
        if (_session == null || _db == null || _coll == null || _isView) return;
        var rows = DocsGrid.SelectedItems.OfType<DocumentRow>().ToList();
        if (rows.Count == 0) return;
        var ids = rows.Select(r => r.Id).OfType<BsonValue>().ToList();
        if (ids.Count < rows.Count)
        {
            SetStatus("Some selected documents have no _id and cannot be deleted.", error: true);
            return;
        }

        string what = rows.Count == 1 ? $"the document with _id {BsonFormat.ToCellText(ids[0])}" : $"{rows.Count} documents";
        if (!Dialog.Confirm(this, "Delete documents", $"Delete {what} from {_db}.{_coll}?\nThis cannot be undone.",
                "Delete", danger: true))
            return;

        var (session, db, coll) = (_session, _db, _coll);
        int index = DocsGrid.SelectedIndex;
        await RunAsync("Deleting…", async () =>
        {
            long deleted = await session.DeleteByIdsAsync(db, coll, ids);
            await LoadDocumentsAsync(index);
            _ = RefreshCollectionCountAsync(db);
            SetStatus($"Deleted {deleted:N0} document(s)");
        }, showDialog: true);
    }

    // ============================================================ browse: context menu, copy

    private void RememberContextCell(MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        _contextRow = cell?.DataContext as DocumentRow;
        _contextField = cell?.Column?.SortMemberPath;
        if (_contextRow != null && !DocsGrid.SelectedItems.Contains(_contextRow))
            DocsGrid.SelectedItem = _contextRow;
    }

    private bool BuildDocsContextMenu()
    {
        var menu = DocsGrid.ContextMenu!;
        menu.Items.Clear();
        if (_contextRow == null) return false;
        var row = _contextRow;

        if (!string.IsNullOrEmpty(_contextField) && row.Document.TryGetValue(_contextField, out var value))
        {
            var field = _contextField;
            string literal = BsonFormat.ToCompactJson(value);
            string shortLiteral = literal.Length > 40 ? literal[..40] + "…" : literal;
            menu.Items.Add(MenuEntry($"Filter: {field} = {shortLiteral}", "", async () =>
            {
                FilterBox.Text = $"{{ {QuoteField(field)}: {literal} }}";
                await ApplyFilterAsync();
            }));
            menu.Items.Add(MenuEntry("Copy value", "", () =>
                CopyToClipboard(value.IsString ? value.AsString : BsonFormat.ToEditableJson(value), "Value copied.")));
            menu.Items.Add(new Separator());
        }

        if (!_isView)
        {
            menu.Items.Add(MenuEntry("Edit document…", "", () => EditDocument(row), "Enter"));
            menu.Items.Add(MenuEntry("Duplicate document…", "", DuplicateSelectedDocument));
        }
        menu.Items.Add(MenuEntry("Copy as JSON", "", CopySelectedAsJson));
        if (!_isView)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(DocsGrid.SelectedItems.Count > 1 ? $"Delete {DocsGrid.SelectedItems.Count} documents…" : "Delete document…",
                "", async () => await DeleteSelectedDocumentsAsync(), "Del"));
        }
        return true;
    }

    private void CopySelectedAsJson()
    {
        var docs = DocsGrid.SelectedItems.OfType<DocumentRow>().Select(r => r.Document).ToList();
        if (docs.Count == 0) return;
        var text = docs.Count == 1 ? BsonFormat.ToEditableJson(docs[0]) : BsonFormat.ToEditableJson(new BsonArray(docs));
        CopyToClipboard(text, docs.Count == 1 ? "Document copied as JSON." : $"{docs.Count} documents copied as JSON.");
    }

    private void CopyToClipboard(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(message);
        }
        catch (Exception ex)
        {
            SetStatus("Clipboard busy: " + ex.Message, error: true);
        }
    }

    // ============================================================ import / export

    private async Task ImportAsync()
    {
        if (_session == null || _db == null || _coll == null || _isView) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Import into {_db}.{_coll}",
            Filter = "JSON files (*.json;*.jsonl;*.ndjson)|*.json;*.jsonl;*.ndjson|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        List<BsonDocument> docs;
        try
        {
            docs = BsonFormat.ParseImportFile(await File.ReadAllTextAsync(dialog.FileName));
        }
        catch (Exception ex)
        {
            Dialog.Error(this, "Import", $"The file could not be read as JSON.\n\n{ex.Message}");
            return;
        }
        if (docs.Count == 0) { Dialog.Info(this, "Import", "The file contains no documents."); return; }
        if (!Dialog.Confirm(this, "Import", $"Insert {docs.Count:N0} document(s) from \"{Path.GetFileName(dialog.FileName)}\" into {_db}.{_coll}?", "Import"))
            return;

        var (session, db, coll) = (_session, _db, _coll);
        await RunAsync("Importing…", async () =>
        {
            long inserted = 0;
            int failed = 0;
            string? firstError = null;
            foreach (var batch in docs.Chunk(ImportBatchSize))
            {
                var r = await session.InsertManyAsync(db, coll, batch);
                inserted += r.Inserted;
                failed += r.Failed;
                firstError ??= r.FirstError;
                SetStatus($"Importing… {inserted + failed:N0} / {docs.Count:N0}");
            }
            await LoadDocumentsAsync();
            _ = RefreshCollectionCountAsync(db);
            SetStatus($"Imported {inserted:N0} document(s)" + (failed > 0 ? $", {failed:N0} failed" : string.Empty), error: failed > 0);
            if (failed > 0)
                Dialog.Error(this, "Import", $"{inserted:N0} document(s) inserted, {failed:N0} failed.\n\nFirst error: {firstError}");
        }, showDialog: true);
    }

    private void ShowExportMenu()
    {
        if (_session == null || _db == null || _coll == null) return;
        var menu = new ContextMenu { PlacementTarget = ExportButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string scope = FilterBox.Text.Trim().Length > 0 ? "matching documents" : "all documents";
        menu.Items.Add(MenuEntry($"JSON array, {scope}…", "", async () => await ExportCollectionAsync(ExportFormat.Json)));
        menu.Items.Add(MenuEntry($"JSON Lines, {scope}…", "", async () => await ExportCollectionAsync(ExportFormat.JsonLines)));
        menu.Items.Add(MenuEntry($"CSV, {scope}…", "", async () => await ExportCollectionAsync(ExportFormat.Csv)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Copy selected documents as JSON", "", CopySelectedAsJson));
        menu.IsOpen = true;
    }

    private enum ExportFormat { Json, JsonLines, Csv }

    private async Task ExportCollectionAsync(ExportFormat format)
    {
        if (_session == null || _db == null || _coll == null) return;
        var query = ReadQuery();
        if (query == null) return;
        var (session, db, coll) = (_session, _db, _coll);

        var path = AskSavePath(coll, format);
        if (path == null) return;

        await RunAsync("Exporting…", async () =>
        {
            var docs = await session.FindAllAsync(db, coll, query.Value.Filter, query.Value.Sort, query.Value.Projection);
            await WriteExportAsync(path, docs, format);
            SetStatus($"Exported {docs.Count:N0} document(s) to {path}");
        }, showDialog: true);
    }

    private async Task ExportDocumentsAsync(List<BsonDocument> docs, string baseName)
    {
        if (docs.Count == 0) { SetStatus("Nothing to export: run a query first.", error: true); return; }
        var path = AskSavePath(baseName, ExportFormat.Json);
        if (path == null) return;
        var format = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Csv
            : path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? ExportFormat.JsonLines
            : ExportFormat.Json;
        await RunAsync("Exporting…", async () =>
        {
            await WriteExportAsync(path, docs, format);
            SetStatus($"Exported {docs.Count:N0} document(s) to {path}");
        }, showDialog: true);
    }

    private string? AskSavePath(string baseName, ExportFormat format)
    {
        var dialog = new SaveFileDialog
        {
            FileName = baseName,
            Filter = "JSON array (*.json)|*.json|JSON Lines (*.jsonl)|*.jsonl|CSV (*.csv)|*.csv",
            FilterIndex = (int)format + 1,
            AddExtension = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static async Task WriteExportAsync(string path, List<BsonDocument> docs, ExportFormat format)
    {
        await Task.Run(() =>
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            switch (format)
            {
                case ExportFormat.Csv:
                    BsonFormat.WriteCsv(writer, DocumentRow.CollectColumns(docs), docs);
                    break;
                case ExportFormat.JsonLines:
                    BsonFormat.WriteJsonLines(writer, docs);
                    break;
                default:
                    BsonFormat.WriteJsonArray(writer, docs);
                    break;
            }
        });
    }

    // ============================================================ indexes

    private async Task LoadIndexesAsync()
    {
        if (_session == null || _db == null || _coll == null) return;
        if (_isView) { IndexesGrid.ItemsSource = null; return; }
        var (session, db, coll) = (_session, _db, _coll);
        await RunAsync("Loading indexes…", async () =>
        {
            var indexes = await session.ListIndexesAsync(db, coll);
            if (db == _db && coll == _coll) IndexesGrid.ItemsSource = indexes;
        }, quiet: true);
    }

    private async Task CreateIndexAsync()
    {
        if (_session == null || _db == null || _coll == null || _isView) return;
        BsonDocument keys;
        try
        {
            keys = BsonFormat.ParseDocument(IndexKeysBox.Text, "The index keys");
        }
        catch (FormatException ex)
        {
            SetStatus(ex.Message, error: true);
            return;
        }
        if (keys.ElementCount == 0) { SetStatus("Type the index keys, e.g. { email: 1 }", error: true); IndexKeysBox.Focus(); return; }

        var (session, db, coll) = (_session, _db, _coll);
        string? name = IndexNameBox.Text.Trim();
        bool unique = IndexUniqueCheck.IsChecked == true;
        await RunAsync("Creating index…", async () =>
        {
            var created = await session.CreateIndexAsync(db, coll, keys, name, unique);
            IndexKeysBox.Text = IndexNameBox.Text = string.Empty;
            IndexUniqueCheck.IsChecked = false;
            await LoadIndexesAsync();
            SetStatus($"Index {created} created");
        }, showDialog: true);
    }

    private async Task DropIndexAsync()
    {
        if (_session == null || _db == null || _coll == null || _isView) return;
        if (IndexesGrid.SelectedItem is not IndexInfo index) { SetStatus("Select an index first.", error: true); return; }
        if (index.IsIdIndex) { SetStatus("The _id index cannot be dropped.", error: true); return; }
        if (!Dialog.Confirm(this, "Drop index", $"Drop the index \"{index.Name}\" {index.Keys}?", "Drop", danger: true)) return;

        var (session, db, coll) = (_session, _db, _coll);
        await RunAsync("Dropping index…", async () =>
        {
            await session.DropIndexAsync(db, coll, index.Name);
            await LoadIndexesAsync();
            SetStatus($"Index {index.Name} dropped");
        }, showDialog: true);
    }

    // ============================================================ query

    private bool IsAggregateMode => AggregateMode.IsChecked == true;

    private void SwitchQueryMode(bool aggregate)
    {
        if (QueryEditor == null) return; // during InitializeComponent
        _commandModeForced = false;
        // Each mode keeps its own text
        if (aggregate) { _commandText = QueryEditor.Text; QueryEditor.Text = _pipelineText; }
        else { _pipelineText = QueryEditor.Text; QueryEditor.Text = _commandText; }
        UpdateQueryTarget();
    }

    private void UpdateQueryTarget()
    {
        if (QueryTarget == null) return;
        QueryTarget.Text = IsAggregateMode
            ? (_coll != null ? $"pipeline on {_db}.{_coll}" : "select a collection")
            : $"command on database {_db ?? "admin"}";
    }

    private async Task RunQueryAsync()
    {
        if (_session == null) return;
        Tabs.SelectedItem = QueryTab;
        var session = _session;
        var text = QueryEditor.Text;

        // A pipeline typed in command mode: run it as a pipeline
        if (!IsAggregateMode && text.TrimStart().StartsWith('[') && _coll != null)
        {
            _pipelineText = text;
            AggregateMode.IsChecked = true;
            _commandText = DefaultCommand; // the switch saved the pipeline as command text
        }

        if (IsAggregateMode)
        {
            if (_db == null || _coll == null) { SetStatus("Select a collection to run a pipeline on.", error: true); return; }
            List<BsonDocument> pipeline;
            try { pipeline = BsonFormat.ParsePipeline(text); }
            catch (FormatException ex) { SetStatus(ex.Message, error: true); return; }

            var (db, coll) = (_db, _coll);
            await RunAsync("Running pipeline…", async () =>
            {
                var watch = Stopwatch.StartNew();
                var results = await session.AggregateAsync(db, coll, pipeline, MaxQueryResults);
                ShowQueryResults(results, null);
                ResultTableView.IsChecked = true;
                string capped = results.Count >= MaxQueryResults ? $" (first {MaxQueryResults:N0})" : string.Empty;
                QueryInfo.Text = $"{results.Count:N0} document(s){capped}  ·  {watch.ElapsedMilliseconds:N0} ms";
                SetStatus("Pipeline executed");
            });
        }
        else
        {
            BsonDocument command;
            try { command = BsonFormat.ParseDocument(text, "The command"); }
            catch (FormatException ex) { SetStatus(ex.Message, error: true); return; }
            if (command.ElementCount == 0) { SetStatus("Type a command, e.g. { dbStats: 1 }", error: true); return; }

            var db = _db ?? "admin";
            await RunAsync("Running command…", async () =>
            {
                var watch = Stopwatch.StartNew();
                var result = await session.RunCommandAsync(db, command);
                // Commands such as find, aggregate or listCollections return their documents in a cursor
                var batch = result.TryGetValue("cursor", out var cursor) && cursor.IsBsonDocument &&
                            cursor.AsBsonDocument.TryGetValue("firstBatch", out var first) && first.IsBsonArray
                    ? first.AsBsonArray.OfType<BsonDocument>().ToList()
                    : null;
                ShowQueryResults(batch ?? new List<BsonDocument> { result }, batch == null ? result : null);
                QueryInfo.Text = (batch != null ? $"{batch.Count:N0} document(s)  ·  " : string.Empty) +
                                 $"{watch.ElapsedMilliseconds:N0} ms";
                if (batch == null) ResultJsonView.IsChecked = true;
                SetStatus($"Command {command.GetElement(0).Name} executed on {db}");

                // Commands may have created or dropped collections
                if (Regex.IsMatch(command.GetElement(0).Name, "^(create|drop|renameCollection|insert|delete)", RegexOptions.IgnoreCase))
                {
                    var dbItem = FindDatabaseNode(db);
                    if (dbItem != null) await LoadCollectionsAsync(dbItem);
                }
            });
        }
    }

    private void ShowQueryResults(List<BsonDocument> docs, BsonDocument? single)
    {
        _queryResults = docs;
        var columns = DocumentRow.CollectColumns(docs);
        BuildColumns(ResultsGrid, columns, editable: false);
        ResultsGrid.ItemsSource = docs.Select((d, i) => new DocumentRow(d, columns, i + 1)).ToList();
        ResultsJson.Text = single != null
            ? BsonFormat.ToEditableJson(single)
            : BsonFormat.ToEditableJson(new BsonArray(docs));
        UpdateResultView();
    }

    private void UpdateResultView()
    {
        if (ResultsGrid == null) return;
        bool table = ResultTableView.IsChecked == true;
        ResultsGrid.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        ResultsJson.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowExamplesMenu()
    {
        var menu = new ContextMenu { PlacementTarget = ExamplesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var aggregate = new (string Title, string Text)[]
        {
            ("Filter, sort and limit", "[\n  { $match: { status: \"active\" } },\n  { $sort: { createdAt: -1 } },\n  { $limit: 50 }\n]"),
            ("Count documents per value", "[\n  { $group: { _id: \"$status\", count: { $sum: 1 } } },\n  { $sort: { count: -1 } }\n]"),
            ("Sum and average", "[\n  { $group: {\n      _id: \"$customerId\",\n      total: { $sum: \"$amount\" },\n      average: { $avg: \"$amount\" }\n  } },\n  { $sort: { total: -1 } }\n]"),
            ("Join another collection ($lookup)", "[\n  { $lookup: {\n      from: \"customers\",\n      localField: \"customerId\",\n      foreignField: \"_id\",\n      as: \"customer\"\n  } },\n  { $unwind: \"$customer\" },\n  { $limit: 20 }\n]"),
            ("Expand an array ($unwind)", "[\n  { $unwind: \"$items\" },\n  { $group: { _id: \"$items.sku\", quantity: { $sum: \"$items.qty\" } } }\n]"),
            ("Field types overview", "[\n  { $project: { fields: { $objectToArray: \"$$ROOT\" } } },\n  { $unwind: \"$fields\" },\n  { $group: { _id: { field: \"$fields.k\", type: { $type: \"$fields.v\" } }, count: { $sum: 1 } } },\n  { $sort: { \"_id.field\": 1 } }\n]"),
        };
        var commands = new (string Title, string Text)[]
        {
            ("Database statistics", "{ dbStats: 1, scale: 1024 }"),
            ("Collection statistics", "{ collStats: \"" + (_coll ?? "collection") + "\", scale: 1024 }"),
            ("Find with a filter", "{\n  find: \"" + (_coll ?? "collection") + "\",\n  filter: { },\n  limit: 20\n}"),
            ("Count with a filter", "{ count: \"" + (_coll ?? "collection") + "\", query: { } }"),
            ("Distinct values of a field", "{ distinct: \"" + (_coll ?? "collection") + "\", key: \"status\" }"),
            ("List collections", "{ listCollections: 1, nameOnly: true }"),
            ("Server build info", "{ buildInfo: 1 }"),
            ("Current operations (admin)", "{ currentOp: 1, active: true }"),
        };

        foreach (var (title, text) in aggregate)
        {
            var item = MenuEntry(title, "", () => { AggregateMode.IsChecked = true; QueryEditor.Text = text; QueryEditor.Focus(); });
            item.IsEnabled = _coll != null;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var (title, text) in commands)
            menu.Items.Add(MenuEntry(title, "", () => { CommandMode.IsChecked = true; QueryEditor.Text = text; QueryEditor.Focus(); }));
        menu.IsOpen = true;
    }

    // ============================================================ info

    private async Task LoadInfoAsync()
    {
        if (_session == null || _db == null) { InfoBox.Text = string.Empty; return; }
        _infoDirty = false;
        var (session, db, coll, isView) = (_session, _db, _coll, _isView);
        await RunAsync("Loading statistics…", async () =>
        {
            var sb = new StringBuilder();
            BsonDocument stats;
            if (coll != null && isView)
            {
                // Views have no statistics: show their definition instead
                var list = await session.RunCommandAsync(db, new BsonDocument
                {
                    { "listCollections", 1 },
                    { "filter", new BsonDocument("name", coll) }
                });
                var info = list["cursor"]["firstBatch"].AsBsonArray.FirstOrDefault()?.AsBsonDocument ?? new BsonDocument();
                var options = info.GetValue("options", new BsonDocument()).AsBsonDocument;
                sb.AppendLine($"View            {db}.{coll}   (read-only)");
                sb.AppendLine($"Source          {db}.{options.GetValue("viewOn", "?")}");
                sb.AppendLine();
                sb.AppendLine("Pipeline:");
                sb.Append(BsonFormat.ToEditableJson(options.GetValue("pipeline", new BsonArray())));
                if (db == _db && coll == _coll) InfoBox.Text = sb.ToString();
                SetStatus("View definition loaded");
                return;
            }
            if (coll != null)
            {
                stats = await session.RunCommandAsync(db, new BsonDocument("collStats", coll));
                sb.AppendLine($"Collection      {db}.{coll}");
                sb.AppendLine($"Documents       {Num(stats, "count")}");
                sb.AppendLine($"Data size       {Bytes(stats, "size")}   (average document {Bytes(stats, "avgObjSize")})");
                sb.AppendLine($"Storage size    {Bytes(stats, "storageSize")}");
                sb.AppendLine($"Indexes         {Num(stats, "nindexes")}   ({Bytes(stats, "totalIndexSize")})");
                if (stats.GetValue("capped", false).ToBoolean()) sb.AppendLine("Capped          yes");
            }
            else
            {
                stats = await session.DatabaseStatsAsync(db);
                sb.AppendLine($"Database        {db}");
                sb.AppendLine($"Collections     {Num(stats, "collections")}   (views: {Num(stats, "views")})");
                sb.AppendLine($"Documents       {Num(stats, "objects")}");
                sb.AppendLine($"Data size       {Bytes(stats, "dataSize")}");
                sb.AppendLine($"Storage size    {Bytes(stats, "storageSize")}");
                sb.AppendLine($"Indexes         {Num(stats, "indexes")}   ({Bytes(stats, "indexSize")})");
            }
            sb.AppendLine();
            sb.AppendLine("Raw statistics:");
            stats.Remove("indexDetails");
            stats.Remove("wiredTiger"); // very long and rarely useful
            sb.Append(BsonFormat.ToEditableJson(stats));
            if (db == _db && coll == _coll) InfoBox.Text = sb.ToString();
            SetStatus("Statistics loaded");
        });

        static string Num(BsonDocument d, string key) =>
            d.TryGetValue(key, out var v) && v.IsNumeric ? v.ToInt64().ToString("N0", CultureInfo.InvariantCulture) : "-";

        static string Bytes(BsonDocument d, string key) =>
            d.TryGetValue(key, out var v) && v.IsNumeric ? BsonFormat.FormatBytes(v.ToDouble()) : "-";
    }

    // ============================================================ helpers

    /// <summary>Runs an operation showing a busy indicator; errors go to the status bar (and optionally a dialog).</summary>
    private async Task<bool> RunAsync(string busyText, Func<Task> action, bool showDialog = false, bool quiet = false)
    {
        BeginBusy(quiet ? null : busyText);
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(busyText, ex);
            var message = FriendlyError(ex);
            SetStatus(message, error: true);
            if (showDialog) Dialog.Error(this, "Error", message);
            else if (_session == null) Dialog.Error(this, "Connection failed", message);
            return false;
        }
        finally
        {
            EndBusy();
        }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        TimeoutException => "The server did not answer in time. Check host name, port, firewall/VPN and that MongoDB is running.",
        MongoAuthenticationException => "Authentication failed: check user name, password and authSource (often ?authSource=admin).",
        MongoConfigurationException => "Invalid connection string: " + ex.Message,
        MongoCommandException cmd => cmd.ErrorMessage ?? cmd.Message,
        MongoWriteException write => write.WriteError?.Message ?? write.Message,
        _ => ex.Message
    };

    private void BeginBusy(string? text)
    {
        if (text != null) SetStatus(text);
        if (_busyCount++ > 0) return;
        Cursor = Cursors.AppStarting;
        BusyIcon.Visibility = Visibility.Visible;
        BusySpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void EndBusy()
    {
        if (--_busyCount > 0) return;
        _busyCount = 0;
        Cursor = null;
        BusyIcon.Visibility = Visibility.Collapsed;
        BusySpin.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text.Replace("\r", " ").Replace("\n", " ");
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "SubtleTextBrush");
        StatusText.ToolTip = text.Length > 80 ? text : null;
    }

    private MenuItem MenuEntry(string header, string glyph, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Icon = Glyph(glyph), InputGestureText = gesture ?? string.Empty };
        item.Click += (_, _) => action();
        return item;
    }

    private TextBlock Glyph(string glyph) => new() { Text = glyph, Style = (Style)FindResource("IconText") };

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private void SaveWindowSettings()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _ = PageSize; // validates and stores the page size
        SettingsStore.Save(_settings);
    }
}
