using System.Windows;
using Microsoft.Win32;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI;

public partial class ValueListDialog : Window
{
    public enum ValueListDialogResult
    {
        Cancelled,
        Cleared,
        Saved
    }

    public ValueListDialogResult Result { get; private set; } = ValueListDialogResult.Cancelled;
    public ColumnValueListConfig? ValueListConfig { get; private set; }

    public ValueListDialog(string columnRef, ColumnValueListConfig? existing = null)
    {
        InitializeComponent();
        ColumnNameText.Text = FormatColumnDisplay(columnRef);
        _columnRef = columnRef;

        if (existing is not null)
        {
            if (existing.HasFile)
            {
                ValueFileOption.IsChecked = true;
                FilePathInput.Text = existing.File ?? string.Empty;
            }
            else if (existing.HasInlineValues)
            {
                InlineValuesOption.IsChecked = true;
                InlineValuesInput.Text = string.Join(Environment.NewLine, existing.Values!);
            }
        }

        UpdateValueSourceVisibility();
    }

    private readonly string _columnRef;

    private static string FormatColumnDisplay(string columnRef)
    {
        var lastDot = columnRef.LastIndexOf('.');
        if (lastDot <= 0)
            return columnRef;

        var column = columnRef[(lastDot + 1)..];
        var table = columnRef[..lastDot];
        return $"{column} ({table})";
    }

    private void OnValueSourceChanged(object sender, RoutedEventArgs e) =>
        UpdateValueSourceVisibility();

    private void UpdateValueSourceVisibility()
    {
        var useInline = InlineValuesOption.IsChecked == true;
        InlineValuesInput.Visibility = useInline ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = useInline ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select value file",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            FilePathInput.Text = dialog.FileName;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Result = ValueListDialogResult.Cleared;
        ValueListConfig = null;
        DialogResult = true;
        Close();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (InlineValuesOption.IsChecked == true)
        {
            var values = InlineValuesInput.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (values.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one value, one per line.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                InlineValuesInput.Focus();
                return;
            }

            ValueListConfig = new ColumnValueListConfig
            {
                Column = _columnRef,
                Values = values
            };
        }
        else
        {
            var filePath = FilePathInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show(this, "Select a value file.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ValueListConfig = new ColumnValueListConfig
            {
                Column = _columnRef,
                File = filePath
            };
        }

        Result = ValueListDialogResult.Saved;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = ValueListDialogResult.Cancelled;
        DialogResult = false;
        Close();
    }
}
