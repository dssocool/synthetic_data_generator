using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI.Controls;

public partial class SqlScopePicker : UserControl
{
    private readonly SqlServerMetadataService _metadataService = new();
    private readonly HashSet<string> _selectedPatterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SqlScopeTreeNode> _roots = [];

    private string _connectionString = string.Empty;
    private SqlScopeTreeNode? _focusedNode;
    private bool _isUpdatingSelectAll;

    public SqlScopePicker()
    {
        InitializeComponent();
        ScopeTree.ItemsSource = _roots;
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
        _focusedNode = null;

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
            ShowDatabaseNodes(databases);
            SetBusy(false);
            UpdateStatusText();
            if (databases.Count == 0)
                StatusText.Text = "No accessible databases found.";
        }
        catch (Exception ex)
        {
            _roots.Clear();
            SetBusy(false);
            StatusText.Text = $"Failed to connect: {ex.Message}";
        }
    }

    private void ShowDatabaseNodes(IReadOnlyList<string> databases)
    {
        _roots.Clear();
        foreach (var database in databases)
        {
            _roots.Add(CreateNode(database, database, SqlScopeNodeKind.Database, parent: null));
        }

        ResetNavigationContext();
        ApplySearchFilter();
    }

    private SqlScopeTreeNode CreateNode(
        string displayName,
        string includePattern,
        SqlScopeNodeKind kind,
        SqlScopeTreeNode? parent)
    {
        return new SqlScopeTreeNode
        {
            DisplayName = displayName,
            IncludePattern = includePattern,
            Kind = kind,
            Parent = parent,
            IsSelected = GetSelectionState(includePattern, canExpand: kind != SqlScopeNodeKind.Table)
        };
    }

    private async Task EnsureChildrenLoadedAsync(SqlScopeTreeNode node)
    {
        if (node.ChildrenLoaded || !node.CanExpand || node.IsLoadingChildren)
            return;

        node.IsLoadingChildren = true;
        try
        {
            switch (node.Kind)
            {
                case SqlScopeNodeKind.Database:
                    var schemas = await _metadataService.GetSchemasAsync(_connectionString, node.DisplayName);
                    foreach (var schema in schemas)
                    {
                        var pattern = $"{node.DisplayName}.{schema}";
                        node.Children.Add(CreateNode(schema, pattern, SqlScopeNodeKind.Schema, node));
                    }

                    if (schemas.Count == 0 && _selectedPatterns.Count == 0)
                        StatusText.Text = "No schemas found.";
                    break;

                case SqlScopeNodeKind.Schema:
                    var database = node.Parent!.DisplayName;
                    var tables = await _metadataService.GetTablesAsync(_connectionString, database, node.DisplayName);
                    foreach (var table in tables)
                    {
                        var pattern = $"{database}.{node.DisplayName}.{table}";
                        node.Children.Add(CreateNode(table, pattern, SqlScopeNodeKind.Table, node));
                    }

                    if (tables.Count == 0 && _selectedPatterns.Count == 0)
                        StatusText.Text = "No tables found.";
                    break;
            }

            node.ChildrenLoaded = true;
            ApplySearchFilter();
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load: {ex.Message}";
        }
        finally
        {
            node.IsLoadingChildren = false;
        }
    }

    private void ResetNavigationContext()
    {
        _focusedNode = null;
        BreadcrumbText.Text = "All databases";
        BackButton.Visibility = Visibility.Collapsed;
        UpdateSelectAllState();
    }

    private void UpdateNavigationContext(SqlScopeTreeNode node)
    {
        _focusedNode = node;
        BreadcrumbText.Text = BuildNodePath(node);
        BackButton.Visibility = Visibility.Visible;
    }

    private static string BuildNodePath(SqlScopeTreeNode node)
    {
        var parts = new List<string>();
        var current = node;
        while (current is not null)
        {
            parts.Insert(0, current.DisplayName);
            current = current.Parent;
        }

        return string.Join(" > ", parts);
    }

    private void ApplySearchFilter()
    {
        var filter = SearchInput.Text.Trim();
        foreach (var root in _roots)
            ApplyFilterToNode(root, filter);

        UpdateSelectAllState();
    }

    private static bool ApplyFilterToNode(SqlScopeTreeNode node, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            node.IsVisible = true;
            foreach (var child in node.Children)
                ApplyFilterToNode(child, filter);
            return true;
        }

        var matchesSelf = node.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var childVisible = false;
        foreach (var child in node.Children)
            childVisible |= ApplyFilterToNode(child, filter);

        node.IsVisible = matchesSelf || childVisible;
        return node.IsVisible;
    }

    private void RefreshItemSelectionState()
    {
        foreach (var node in EnumerateNodes(_roots))
            node.IsSelected = GetSelectionState(node.IncludePattern, node.CanExpand);

        UpdateSelectAllState();
        UpdateStatusText();
    }

    private static string NormalizePattern(string pattern) =>
        SqlTableName.NormalizeIdentifier(pattern);

    private bool? GetSelectionState(string includePattern, bool canExpand)
    {
        var normalized = NormalizePattern(includePattern);

        var isFullySelected = _selectedPatterns.Any(selected =>
        {
            var selectedNormalized = NormalizePattern(selected);
            return string.Equals(selectedNormalized, normalized, StringComparison.OrdinalIgnoreCase)
                || SqlTableName.MatchesPattern(normalized, selectedNormalized);
        });

        if (isFullySelected)
            return true;

        if (!canExpand)
            return false;

        var hasPartialSelection = _selectedPatterns.Any(selected =>
        {
            var selectedNormalized = NormalizePattern(selected);
            return !string.Equals(selectedNormalized, normalized, StringComparison.OrdinalIgnoreCase)
                && SqlTableName.MatchesPattern(selectedNormalized, normalized);
        });

        return hasPartialSelection ? null : false;
    }

    private void AddPatternSelection(string includePattern) =>
        _selectedPatterns.Add(NormalizePattern(includePattern));

    private void RemovePatternSelection(string includePattern, SqlScopeTreeNode node)
    {
        var normalized = NormalizePattern(includePattern);

        var coveringPattern = _selectedPatterns.FirstOrDefault(selected =>
            !string.Equals(NormalizePattern(selected), normalized, StringComparison.OrdinalIgnoreCase)
            && SqlTableName.MatchesPattern(normalized, NormalizePattern(selected)));

        if (coveringPattern is not null)
        {
            _selectedPatterns.Remove(coveringPattern);
            foreach (var sibling in GetSiblings(node))
                AddPatternSelection(sibling.IncludePattern);

            return;
        }

        _selectedPatterns.RemoveWhere(selected =>
            string.Equals(NormalizePattern(selected), normalized, StringComparison.OrdinalIgnoreCase)
            || SqlTableName.MatchesPattern(NormalizePattern(selected), normalized));
    }

    private IEnumerable<SqlScopeTreeNode> GetSiblings(SqlScopeTreeNode node)
    {
        if (node.Parent is null)
            return _roots.Where(r => !ReferenceEquals(r, node));

        return node.Parent.Children.Where(c => !ReferenceEquals(c, node));
    }

    private void UpdateSelectAllState()
    {
        var visibleItems = GetVisibleNodes().ToList();
        _isUpdatingSelectAll = true;
        SelectAllCheckBox.IsChecked = visibleItems.Count switch
        {
            0 => false,
            _ when visibleItems.All(i => i.IsSelected == true) => true,
            _ when visibleItems.Any(i => i.IsSelected == true || i.IsSelected == null) => null,
            _ => false
        };
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
        ScopeTree.IsEnabled = !isBusy;
        if (message is not null)
            StatusText.Text = message;
    }

    private IEnumerable<SqlScopeTreeNode> GetVisibleNodes()
    {
        foreach (var node in EnumerateNodes(_roots))
        {
            if (node.IsVisible)
                yield return node;
        }
    }

    private static IEnumerable<SqlScopeTreeNode> EnumerateNodes(IEnumerable<SqlScopeTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
                yield return child;
        }
    }

    private async void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: SqlScopeTreeNode node })
            return;

        UpdateNavigationContext(node);
        await EnsureChildrenLoadedAsync(node);
    }

    private void OnTreeItemCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: SqlScopeTreeNode node })
            return;

        if (_focusedNode is null || !IsSameOrDescendant(_focusedNode, node))
            return;

        _focusedNode = node.Parent;
        if (_focusedNode is not null)
            UpdateNavigationContext(_focusedNode);
        else
            ResetNavigationContext();
    }

    private static bool IsSameOrDescendant(SqlScopeTreeNode node, SqlScopeTreeNode ancestor)
    {
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
            current = current.Parent;
        }

        return false;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_focusedNode is null)
        {
            ResetNavigationContext();
            return;
        }

        _focusedNode.IsExpanded = false;
        _focusedNode = _focusedNode.Parent;
        if (_focusedNode is not null)
            UpdateNavigationContext(_focusedNode);
        else
            ResetNavigationContext();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        ApplySearchFilter();

    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAll)
            return;

        var selectAll = SelectAllCheckBox.IsChecked == true;
        foreach (var node in GetVisibleNodes())
        {
            if (selectAll)
                AddPatternSelection(node.IncludePattern);
            else
                RemovePatternSelection(node.IncludePattern, node);
        }

        RefreshItemSelectionState();
    }

    private void OnItemCheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not SqlScopeTreeNode node)
            return;

        if (checkBox.IsChecked == true)
            AddPatternSelection(node.IncludePattern);
        else
            RemovePatternSelection(node.IncludePattern, node);

        RefreshItemSelectionState();
    }
}

public enum SqlScopeNodeKind
{
    Database,
    Schema,
    Table
}

public sealed class SqlScopeTreeNode : INotifyPropertyChanged
{
    private bool? _isSelected;
    private bool _isExpanded;
    private bool _isVisible = true;
    private bool _isLoadingChildren;

    public SqlScopeTreeNode? Parent { get; set; }
    public ObservableCollection<SqlScopeTreeNode> Children { get; } = [];

    public required string DisplayName { get; init; }
    public required string IncludePattern { get; init; }
    public required SqlScopeNodeKind Kind { get; init; }

    public bool CanExpand => Kind != SqlScopeNodeKind.Table;

    public bool ChildrenLoaded { get; set; }

    public bool IsLoadingChildren
    {
        get => _isLoadingChildren;
        set
        {
            if (_isLoadingChildren == value)
                return;

            _isLoadingChildren = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingChildren)));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;

            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public bool? IsSelected
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
