using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI.Controls;

public partial class SqlScopePicker : UserControl
{
    private enum NavigationLevel
    {
        Database,
        Schema,
        Table
    }

    private readonly SqlServerMetadataService _metadataService = new();
    private readonly HashSet<string> _selectedPatterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SqlScopePickerItem> _items = [];

    private string _connectionString = string.Empty;
    private NavigationLevel _level = NavigationLevel.Database;
    private string? _currentDatabase;
    private string? _currentSchema;
    private bool _isUpdatingSelectAll;

    public SqlScopePicker()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        Visibility = Visibility.Collapsed;
    }

    public IReadOnlyCollection<string> SelectedPatterns => _selectedPatterns;

    public void SetSelectedPatterns(IEnumerable<string> patterns)
    {
        _selectedPatterns.Clear();
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
                _selectedPatterns.Add(NormalizePattern(pattern));
        }

        UpdateStatusText();
        RefreshItemSelectionState();
    }

    public async Task LoadDatabasesAsync(string connectionString)
    {
        _connectionString = connectionString.Trim();
        _level = NavigationLevel.Database;
        _currentDatabase = null;
        _currentSchema = null;

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        SetBusy(true, "Loading databases...");

        try
        {
            var databases = await _metadataService.GetDatabasesAsync(_connectionString);
            ShowDatabaseItems(databases);
            SetBusy(false);
            UpdateStatusText();
            if (databases.Count == 0)
                StatusText.Text = "No accessible databases found.";
        }
        catch (Exception ex)
        {
            _items.Clear();
            SetBusy(false);
            StatusText.Text = $"Failed to connect: {ex.Message}";
        }
    }

    private async Task LoadSchemasAsync(string database)
    {
        _level = NavigationLevel.Schema;
        _currentDatabase = database;
        _currentSchema = null;

        SetBusy(true, "Loading schemas...");
        try
        {
            var schemas = await _metadataService.GetSchemasAsync(_connectionString, database);
            ShowSchemaItems(database, schemas);
            SetBusy(false);
            UpdateStatusText();
            if (schemas.Count == 0)
                StatusText.Text = "No schemas found.";
        }
        catch (Exception ex)
        {
            _items.Clear();
            SetBusy(false);
            StatusText.Text = $"Failed to load schemas: {ex.Message}";
        }
    }

    private async Task LoadTablesAsync(string database, string schema)
    {
        _level = NavigationLevel.Table;
        _currentDatabase = database;
        _currentSchema = schema;

        SetBusy(true, "Loading tables...");
        try
        {
            var tables = await _metadataService.GetTablesAsync(_connectionString, database, schema);
            ShowTableItems(database, schema, tables);
            SetBusy(false);
            UpdateStatusText();
            if (tables.Count == 0)
                StatusText.Text = "No tables found.";
        }
        catch (Exception ex)
        {
            _items.Clear();
            SetBusy(false);
            StatusText.Text = $"Failed to load tables: {ex.Message}";
        }
    }

    private void ShowDatabaseItems(IReadOnlyList<string> databases)
    {
        _items.Clear();
        foreach (var database in databases)
        {
            _items.Add(new SqlScopePickerItem
            {
                DisplayName = database,
                IncludePattern = database,
                CanDrillDown = true,
                IsSelected = IsPatternSelected(database)
            });
        }

        UpdateNavigationUi();
        ApplySearchFilter();
    }

    private void ShowSchemaItems(string database, IReadOnlyList<string> schemas)
    {
        _items.Clear();
        foreach (var schema in schemas)
        {
            var pattern = $"{database}.{schema}";
            _items.Add(new SqlScopePickerItem
            {
                DisplayName = schema,
                IncludePattern = pattern,
                CanDrillDown = true,
                IsSelected = IsPatternSelected(pattern)
            });
        }

        UpdateNavigationUi();
        ApplySearchFilter();
    }

    private void ShowTableItems(string database, string schema, IReadOnlyList<string> tables)
    {
        _items.Clear();
        foreach (var table in tables)
        {
            var pattern = $"{database}.{schema}.{table}";
            _items.Add(new SqlScopePickerItem
            {
                DisplayName = table,
                IncludePattern = pattern,
                CanDrillDown = false,
                IsSelected = IsPatternSelected(pattern)
            });
        }

        UpdateNavigationUi();
        ApplySearchFilter();
    }

    private void UpdateNavigationUi()
    {
        BackButton.Visibility = _level == NavigationLevel.Database
            ? Visibility.Collapsed
            : Visibility.Visible;

        BreadcrumbText.Text = _level switch
        {
            NavigationLevel.Database => "All databases",
            NavigationLevel.Schema => _currentDatabase ?? string.Empty,
            NavigationLevel.Table => $"{_currentDatabase} > {_currentSchema}",
            _ => string.Empty
        };

        UpdateSelectAllState();
    }

    private void ApplySearchFilter()
    {
        var filter = SearchInput.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
        if (string.IsNullOrEmpty(filter))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = obj =>
                obj is SqlScopePickerItem item
                && item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        UpdateSelectAllState();
    }

    private void RefreshItemSelectionState()
    {
        foreach (var item in _items)
            item.IsSelected = IsPatternSelected(item.IncludePattern);

        UpdateSelectAllState();
        UpdateStatusText();
    }

    private static string NormalizePattern(string pattern) =>
        SqlTableName.NormalizeIdentifier(pattern);

    private bool IsPatternSelected(string includePattern)
    {
        var normalized = NormalizePattern(includePattern);
        return _selectedPatterns.Any(selected =>
            string.Equals(NormalizePattern(selected), normalized, StringComparison.OrdinalIgnoreCase)
            || SqlTableName.MatchesPattern(normalized, NormalizePattern(selected)));
    }

    private void AddPatternSelection(string includePattern) =>
        _selectedPatterns.Add(NormalizePattern(includePattern));

    private void RemovePatternSelection(string includePattern)
    {
        var normalized = NormalizePattern(includePattern);

        var coveringPattern = _selectedPatterns.FirstOrDefault(selected =>
            !string.Equals(NormalizePattern(selected), normalized, StringComparison.OrdinalIgnoreCase)
            && SqlTableName.MatchesPattern(normalized, NormalizePattern(selected)));

        if (coveringPattern is not null)
        {
            _selectedPatterns.Remove(coveringPattern);
            foreach (var sibling in _items)
            {
                if (!string.Equals(sibling.IncludePattern, includePattern, StringComparison.OrdinalIgnoreCase))
                    AddPatternSelection(sibling.IncludePattern);
            }

            return;
        }

        _selectedPatterns.RemoveWhere(selected =>
            string.Equals(NormalizePattern(selected), normalized, StringComparison.OrdinalIgnoreCase)
            || SqlTableName.MatchesPattern(NormalizePattern(selected), normalized));
    }

    private void UpdateSelectAllState()
    {
        var visibleItems = GetVisibleItems().ToList();
        _isUpdatingSelectAll = true;
        SelectAllCheckBox.IsChecked = visibleItems.Count > 0 && visibleItems.All(i => i.IsSelected);
        SelectAllCheckBox.IsEnabled = visibleItems.Count > 0;
        _isUpdatingSelectAll = false;
    }

    private void UpdateStatusText()
    {
        StatusText.Text = _selectedPatterns.Count == 0
            ? "Select one or more databases, schemas, or tables."
            : $"{_selectedPatterns.Count} selected: {string.Join(", ", _selectedPatterns.OrderBy(p => p).Select(SqlTableName.ToBracketedPattern))}";
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        SearchInput.IsEnabled = !isBusy;
        SelectAllCheckBox.IsEnabled = !isBusy;
        BackButton.IsEnabled = !isBusy;
        ItemsList.IsEnabled = !isBusy;
        if (message is not null)
            StatusText.Text = message;
    }

    private IEnumerable<SqlScopePickerItem> GetVisibleItems()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
        if (view.Filter is null)
            return _items;

        return _items.Where(i => view.Filter(i));
    }

    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        SearchInput.Text = string.Empty;

        switch (_level)
        {
            case NavigationLevel.Table:
                await LoadSchemasAsync(_currentDatabase!);
                break;
            case NavigationLevel.Schema:
                await LoadDatabasesAsync(_connectionString);
                break;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        ApplySearchFilter();

    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAll)
            return;

        var selectAll = SelectAllCheckBox.IsChecked == true;
        foreach (var item in GetVisibleItems())
        {
            if (selectAll)
                AddPatternSelection(item.IncludePattern);
            else
                RemovePatternSelection(item.IncludePattern);
        }

        RefreshItemSelectionState();
    }

    private void OnItemCheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not SqlScopePickerItem item)
            return;

        if (checkBox.IsChecked == true)
            AddPatternSelection(item.IncludePattern);
        else
            RemovePatternSelection(item.IncludePattern);

        RefreshItemSelectionState();
    }

    private async void OnItemNavigateClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SqlScopePickerItem item || !item.CanDrillDown)
            return;

        e.Handled = true;
        SearchInput.Text = string.Empty;

        switch (_level)
        {
            case NavigationLevel.Database:
                await LoadSchemasAsync(item.DisplayName);
                break;
            case NavigationLevel.Schema:
                await LoadTablesAsync(_currentDatabase!, item.DisplayName);
                break;
        }
    }
}

public sealed class SqlScopePickerItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string DisplayName { get; init; }
    public required string IncludePattern { get; init; }
    public required bool CanDrillDown { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
