using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MongoDBTinyClient.Interop;

namespace MongoDBTinyClient.UI;

/// <summary>Dark-themed replacement for MessageBox: confirmations, messages and small input forms.</summary>
public partial class Dialog : Window
{
    private readonly List<TextBox> _fields = new();

    /// <summary>Validates the values; returns an error message, or null when they are fine.</summary>
    private Func<string[], string?>? _validate;

    private Dialog(Window? owner, string title, string message)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        Native.UseDarkTitleBar(this);

        OkButton.Click += (_, _) =>
        {
            var error = _validate?.Invoke(Values);
            if (error != null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        };
        Loaded += (_, _) =>
        {
            if (_fields.Count > 0) { _fields[0].Focus(); _fields[0].SelectAll(); }
            else OkButton.Focus();
        };
    }

    private string[] Values => _fields.Select(f => f.Text.Trim()).ToArray();

    private void AddField(string label, string value, string hint)
    {
        FieldsPanel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Style = (Style)FindResource("LabelText"),
            Margin = new Thickness(0, 12, 0, 6)
        });
        var box = new TextBox { Text = value, Tag = hint, Style = (Style)FindResource("ThemedTextBox") };
        box.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
        FieldsPanel.Children.Add(box);
        _fields.Add(box);
    }

    public static void Info(Window owner, string title, string message)
    {
        var dialog = new Dialog(owner, title, message);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    public static void Error(Window owner, string title, string message)
    {
        var dialog = new Dialog(owner, title, message);
        dialog.MessageText.Foreground = (System.Windows.Media.Brush)dialog.FindResource("DangerBrush");
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    public static bool Confirm(Window owner, string title, string message, string okText, bool danger = false)
    {
        var dialog = new Dialog(owner, title, message);
        dialog.OkButton.Content = okText;
        if (danger)
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("DangerButton");
            dialog.OkButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
        }
        return dialog.ShowDialog() == true;
    }

    /// <summary>Asks for one or more values. Fields are (label, initial value, hint).</summary>
    public static string[]? Prompt(Window owner, string title, string message, string okText,
        (string Label, string Value, string Hint)[] fields, Func<string[], string?>? validate = null)
    {
        var dialog = new Dialog(owner, title, message) { _validate = validate };
        dialog.OkButton.Content = okText;
        foreach (var f in fields) dialog.AddField(f.Label, f.Value, f.Hint);
        return dialog.ShowDialog() == true ? dialog.Values : null;
    }

    /// <summary>Destructive actions: the user must type the name to confirm.</summary>
    public static bool ConfirmByName(Window owner, string title, string message, string name, string okText)
    {
        var dialog = new Dialog(owner, title, message + $"\n\nType \"{name}\" to confirm.")
        {
            _validate = v => v[0] == name ? null : "The name does not match."
        };
        dialog.OkButton.Content = okText;
        dialog.OkButton.Style = (Style)dialog.FindResource("DangerButton");
        dialog.AddField("Name", string.Empty, name);
        return dialog.ShowDialog() == true;
    }
}
