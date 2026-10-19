using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MongoDB.Bson;
using MongoDBTinyClient.Core;
using MongoDBTinyClient.Interop;

namespace MongoDBTinyClient.UI;

/// <summary>JSON editor for a single document. The save action runs while the window is open, so errors stay visible.</summary>
public partial class DocumentEditor : Window
{
    private readonly Func<BsonDocument, Task> _save;

    internal DocumentEditor(Window owner, string title, string header, BsonDocument document, Func<BsonDocument, Task> save)
    {
        _save = save;
        InitializeComponent();
        Owner = owner;
        Title = title;
        HeaderText.Text = header;
        Native.UseDarkTitleBar(this);

        Editor.Text = BsonFormat.ToEditableJson(document);

        FormatButton.Click += (_, _) => Format();
        SaveButton.Click += async (_, _) => await SaveAsync();
        Editor.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;

        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await SaveAsync();
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                Format();
            }
        };

        Loaded += (_, _) =>
        {
            Editor.Focus();
            // New documents start with an empty body: put the caret inside the braces
            int caret = Editor.Text.IndexOf('\n');
            Editor.CaretIndex = document.ElementCount == 0 && caret > 0 ? caret : 0;
        };
    }

    private BsonDocument? Parse()
    {
        try
        {
            return BsonFormat.ParseDocument(Editor.Text, "The text");
        }
        catch (FormatException ex)
        {
            ShowError(ex.Message);
            return null;
        }
    }

    private void Format()
    {
        var doc = Parse();
        if (doc != null) Editor.Text = BsonFormat.ToEditableJson(doc);
    }

    private async Task SaveAsync()
    {
        var doc = Parse();
        if (doc == null) return;

        SaveButton.IsEnabled = false;
        try
        {
            await _save(doc);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("Saving document", ex);
            ShowError(ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
