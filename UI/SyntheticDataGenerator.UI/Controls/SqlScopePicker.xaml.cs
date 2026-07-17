using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI.Controls;

public partial class SqlScopePicker : UserControl
{
    private readonly SqlServerMetadataService _metadataService = new();
    private readonly HashSet<string> _selectedPatterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SqlScopeTreeNode> _roots = [];
    private readonly Dictionary<string, TableInfo> _tableMetadataCache = new(StringComparer.OrdinalIgnoreCase);

    private string _connectionString = string.Empty;
    private SqlScopeTreeNode? _focusedNode;
    private SqlScopeTreeNode? _selectedColumnNode;
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
        _selectedColumnNode = null;
        _tableMetadataCache.Clear();
        ClearColumnDetailPanel();

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
        var node = new SqlScopeTreeNode
        {
            DisplayName = displayName,
            IncludePattern = includePattern,
            Kind = kind,
            Parent = parent,
            IsSelected = kind == SqlScopeNodeKind.Column
                ? false
                : GetSelectionState(includePattern, CanExpand(kind))
        };

        if (CanExpand(kind))
            node.Children.Add(CreatePlaceholder(node));

        return node;
    }

    private static bool CanExpand(SqlScopeNodeKind kind) =>
        kind is SqlScopeNodeKind.Database or SqlScopeNodeKind.Schema or SqlScopeNodeKind.Table;

    private static SqlScopeTreeNode CreatePlaceholder(SqlScopeTreeNode parent) =>
        new()
        {
            DisplayName = string.Empty,
            IncludePattern = parent.IncludePattern,
            Kind = SqlScopeNodeKind.Table,
            Parent = parent,
            IsPlaceholder = true,
            IsVisible = false
        };

    private static void RemovePlaceholders(SqlScopeTreeNode node)
    {
        foreach (var placeholder in node.Children.Where(c => c.IsPlaceholder).ToList())
            node.Children.Remove(placeholder);
    }

    private async Task EnsureChildrenLoadedAsync(SqlScopeTreeNode node)
    {
        if (node.ChildrenLoaded || !node.CanExpand || node.IsLoadingChildren)
            return;

        node.IsLoadingChildren = true;
        try
        {
            RemovePlaceholders(node);

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

                case SqlScopeNodeKind.Table:
                    var schemaNode = node.Parent!;
                    var dbNode = schemaNode.Parent!;
                    var tableInfo = await _metadataService.GetTableInfoAsync(
                        _connectionString,
                        dbNode.DisplayName,
                        schemaNode.DisplayName,
                        node.DisplayName);

                    if (tableInfo is null)
                    {
                        if (node.Children.Count == 0)
                            node.Children.Add(CreatePlaceholder(node));
                        StatusText.Text = "Failed to load columns for this table.";
                        break;
                    }

                    _tableMetadataCache[tableInfo.FullName] = tableInfo;

                    foreach (var column in tableInfo.Columns)
                        node.Children.Add(CreateColumnNode(column, node, tableInfo));

                    if (tableInfo.Columns.Count == 0 && _selectedPatterns.Count == 0)
                        StatusText.Text = "No columns found.";
                    break;
            }

            node.ChildrenLoaded = true;
            ApplySearchFilter();
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            if (node.Children.Count == 0)
                node.Children.Add(CreatePlaceholder(node));

            StatusText.Text = $"Failed to load: {ex.Message}";
        }
        finally
        {
            node.IsLoadingChildren = false;
        }
    }

    private static SqlScopeTreeNode CreateColumnNode(
        ColumnInfo column,
        SqlScopeTreeNode tableNode,
        TableInfo tableInfo)
    {
        return new SqlScopeTreeNode
        {
            DisplayName = column.Name,
            IncludePattern = tableNode.IncludePattern,
            Kind = SqlScopeNodeKind.Column,
            Parent = tableNode,
            TableFullName = tableInfo.FullName,
            ColumnInfo = column,
            IsSelected = false
        };
    }

    private void ResetNavigationContext()
    {
        _focusedNode = null;
        UpdateSelectAllState();
    }

    private void UpdateNavigationContext(SqlScopeTreeNode node) =>
        _focusedNode = node;

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
        {
            if (node.Kind != SqlScopeNodeKind.Column)
                node.IsSelected = GetSelectionState(node.IncludePattern, node.CanExpand);
        }

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
        var visibleItems = GetVisibleNodes()
            .Where(n => n.Kind != SqlScopeNodeKind.Column)
            .ToList();

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
        ScopeTree.IsEnabled = !isBusy;
        if (message is not null)
            StatusText.Text = message;
    }

    private IEnumerable<SqlScopeTreeNode> GetVisibleNodes()
    {
        foreach (var node in EnumerateNodes(_roots))
        {
            if (node.IsVisible && !node.IsPlaceholder)
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
        if (e.OriginalSource is not TreeViewItem { DataContext: SqlScopeTreeNode node } || node.IsPlaceholder)
            return;

        await ExpandNodeAsync(node);
    }

    private async void OnItemLabelClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SqlScopeTreeNode node } || node.IsPlaceholder)
            return;

        e.Handled = true;

        if (node.Kind == SqlScopeNodeKind.Column)
        {
            SelectColumnNode(node);
            return;
        }

        if (!node.CanExpand)
            return;

        await ExpandNodeAsync(node);
    }

    private async Task ExpandNodeAsync(SqlScopeTreeNode node)
    {
        if (!node.IsExpanded)
            node.IsExpanded = true;

        UpdateNavigationContext(node);
        await EnsureChildrenLoadedAsync(node);
    }

    private void SelectColumnNode(SqlScopeTreeNode node)
    {
        if (node.Kind != SqlScopeNodeKind.Column || node.ColumnInfo is null)
            return;

        if (_selectedColumnNode is not null)
            _selectedColumnNode.IsColumnHighlighted = false;

        _selectedColumnNode = node;
        node.IsColumnHighlighted = true;
        node.IsTreeSelected = true;
        UpdateNavigationContext(node);
        UpdateColumnDetailPanel(node);
    }

    private void ClearColumnDetailPanel()
    {
        ColumnDetailPlaceholder.Visibility = Visibility.Visible;
        ColumnDetailPanel.Visibility = Visibility.Collapsed;
        FkDetailPanel.Visibility = Visibility.Collapsed;
        NoFkText.Visibility = Visibility.Collapsed;
        FkSelfRefText.Visibility = Visibility.Collapsed;
    }

    private void UpdateColumnDetailPanel(SqlScopeTreeNode node)
    {
        if (node.ColumnInfo is null || node.TableFullName is null)
        {
            ClearColumnDetailPanel();
            return;
        }

        ColumnDetailPlaceholder.Visibility = Visibility.Collapsed;
        ColumnDetailPanel.Visibility = Visibility.Visible;

        ColumnNameText.Text = $"{SqlTableName.ToBracketedPattern(node.TableFullName)}.{node.ColumnInfo.Name}";

        var nullability = node.ColumnInfo.IsNullable ? "NULL" : "NOT NULL";
        ColumnTypeText.Text = $"{node.ColumnInfo.SqlType} ({nullability})";

        if (!_tableMetadataCache.TryGetValue(node.TableFullName, out var tableInfo))
        {
            FkDetailPanel.Visibility = Visibility.Collapsed;
            NoFkText.Visibility = Visibility.Visible;
            return;
        }

        var compositeFk = tableInfo.GetGroupedForeignKeys()
            .FirstOrDefault(fk => fk.ColumnPairs.Any(pair =>
                pair.ParentColumn.Equals(node.ColumnInfo.Name, StringComparison.OrdinalIgnoreCase)));

        if (compositeFk is null)
        {
            FkDetailPanel.Visibility = Visibility.Collapsed;
            FkSelfRefText.Visibility = Visibility.Collapsed;
            NoFkText.Visibility = Visibility.Visible;
            return;
        }

        NoFkText.Visibility = Visibility.Collapsed;
        FkDetailPanel.Visibility = Visibility.Visible;
        FkNameText.Text = compositeFk.FkName;

        var referenceLines = new StringBuilder();
        foreach (var (parentColumn, referencedColumn) in compositeFk.ColumnPairs)
        {
            if (referenceLines.Length > 0)
                referenceLines.AppendLine();

            var refTable = compositeFk.FullReferencedTableName;
            referenceLines.Append(
                $"{parentColumn} → {SqlTableName.ToBracketedPattern(refTable)}.{referencedColumn}");
        }

        FkReferenceText.Text = referenceLines.ToString();
        FkSelfRefText.Visibility = compositeFk.IsSelfReferencing
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        ApplySearchFilter();

    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSelectAll)
            return;

        var selectAll = SelectAllCheckBox.IsChecked == true;
        foreach (var node in GetVisibleNodes().Where(n => n.Kind != SqlScopeNodeKind.Column))
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
        if (sender is not CheckBox checkBox
            || checkBox.DataContext is not SqlScopeTreeNode node
            || node.IsPlaceholder
            || node.Kind == SqlScopeNodeKind.Column)
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
    Table,
    Column
}

public sealed class SqlScopeTreeNode : INotifyPropertyChanged
{
    private bool? _isSelected;
    private bool _isExpanded;
    private bool _isVisible = true;
    private bool _isLoadingChildren;
    private bool _isColumnHighlighted;
    private bool _isTreeSelected;

    public SqlScopeTreeNode? Parent { get; set; }
    public ObservableCollection<SqlScopeTreeNode> Children { get; } = [];

    public required string DisplayName { get; init; }
    public required string IncludePattern { get; init; }
    public required SqlScopeNodeKind Kind { get; init; }

    public string? TableFullName { get; init; }
    public ColumnInfo? ColumnInfo { get; init; }

    public bool CanExpand => Kind is SqlScopeNodeKind.Database or SqlScopeNodeKind.Schema or SqlScopeNodeKind.Table
        && !IsPlaceholder;

    public bool IsPlaceholder { get; init; }

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

    public bool IsColumnHighlighted
    {
        get => _isColumnHighlighted;
        set
        {
            if (_isColumnHighlighted == value)
                return;

            _isColumnHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsColumnHighlighted)));
        }
    }

    public bool IsTreeSelected
    {
        get => _isTreeSelected;
        set
        {
            if (_isTreeSelected == value)
                return;

            _isTreeSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTreeSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
