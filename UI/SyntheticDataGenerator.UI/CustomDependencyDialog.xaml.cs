using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI;

public partial class CustomDependencyDialog : Window
{
    private readonly SqlServerMetadataService _metadataService = new();
    private readonly string _connectionString;
    private readonly string _sourceColumnRef;
    private readonly HashSet<string> _existingGroupColumns;
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _searchCts;

    public IReadOnlyList<string> SelectedColumnRefs { get; private set; } = [];

    public CustomDependencyDialog(
        string connectionString,
        string sourceColumnRef,
        IEnumerable<string> existingGroupColumns)
    {
        InitializeComponent();

        _connectionString = connectionString;
        _sourceColumnRef = sourceColumnRef;
        _existingGroupColumns = new HashSet<string>(existingGroupColumns, StringComparer.OrdinalIgnoreCase);

        SourceColumnText.Text = FormatColumnDisplay(sourceColumnRef);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RunSearchAsync();
        };

        StatusText.Text = "Type in the search box to find columns.";
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        var filter = SearchInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            ResultsList.ItemsSource = null;
            StatusText.Text = "Type in the search box to find columns.";
            return;
        }

        StatusText.Text = "Searching...";
        ResultsList.IsEnabled = false;

        try
        {
            var results = await _metadataService.SearchColumnsAsync(_connectionString, filter, ct: ct);
            if (ct.IsCancellationRequested)
                return;

            var items = results
                .Where(r => !string.Equals(r, _sourceColumnRef, StringComparison.OrdinalIgnoreCase))
                .Select(r => new ColumnSearchListItem(r))
                .ToList();

            ResultsList.ItemsSource = items;

            var preselected = items
                .Where(i => _existingGroupColumns.Contains(i.ColumnRef))
                .ToList();
            foreach (var item in preselected)
                ResultsList.SelectedItems.Add(item);

            StatusText.Text = items.Count == 0
                ? "No columns found. Try a different search."
                : $"{items.Count} column(s) found. Select one or more related columns.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
            ResultsList.ItemsSource = null;
        }
        finally
        {
            ResultsList.IsEnabled = true;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var selected = ResultsList.SelectedItems
            .OfType<ColumnSearchListItem>()
            .Select(i => i.ColumnRef)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one related column.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedColumnRefs = selected;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string FormatColumnDisplay(string columnRef)
    {
        var lastDot = columnRef.LastIndexOf('.');
        if (lastDot <= 0)
            return columnRef;

        var column = columnRef[(lastDot + 1)..];
        var table = columnRef[..lastDot];
        return $"{column} ({table})";
    }

    private sealed class ColumnSearchListItem(string refText)
    {
        public string ColumnRef { get; } = refText;
        public string DisplayText { get; } = FormatColumnDisplay(refText);
    }
}
