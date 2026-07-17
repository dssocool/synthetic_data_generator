using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SyntheticDataGenerator.Models;
using SyntheticDataGenerator.Services;
using SyntheticDataGenerator.UI.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI.Controls;

public partial class SqlScopePicker : UserControl
{
    public static readonly DependencyProperty AllowColumnSelectionProperty =
        DependencyProperty.Register(
            nameof(AllowColumnSelection),
            typeof(bool),
            typeof(SqlScopePicker),
            new PropertyMetadata(false, OnAllowColumnSelectionChanged));

    private readonly SqlServerMetadataService _metadataService = new();
    private readonly HashSet<string> _selectedPatterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<SqlScopeTreeNode> _roots = [];
    private readonly Dictionary<string, TableInfo> _tableMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _customDependencies = [];
    private readonly List<ColumnValueListConfig> _customValueLists = [];

    private string _connectionString = string.Empty;
    private SqlScopeTreeNode? _focusedNode;
    private SqlScopeTreeNode? _selectedColumnNode;
    private bool _isUpdatingSelectAll;

    public SqlScopePicker()
    {
        InitializeComponent();
        ScopeTree.ItemsSource = _roots;
        Visibility = Visibility.Collapsed;
        UpdateScopeHeaderText();
    }

    public IReadOnlyCollection<string> SelectedPatterns => _selectedPatterns;
    public IReadOnlyList<string> CustomDependencies => _customDependencies;
    public IReadOnlyList<ColumnValueListConfig> CustomValueLists => _customValueLists;

    public bool AllowColumnSelection
    {
        get => (bool)GetValue(AllowColumnSelectionProperty);
        set => SetValue(AllowColumnSelectionProperty, value);
    }

    private static void OnAllowColumnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SqlScopePicker picker)
            return;

        picker.UpdateScopeHeaderText();
        if (e.NewValue is false)
            picker.StripColumnSelections();
    }

    public void SetSelectedPatterns(IEnumerable<string> patterns)
    {
        _selectedPatterns.Clear();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var parsed = IncludeScopePattern.Parse(pattern);
            if (string.IsNullOrWhiteSpace(parsed.TablePattern))
                continue;

            if (!AllowColumnSelection && parsed.HasColumnSelection)
            {
                _selectedPatterns.Add(parsed.TablePattern);
                continue;
            }

            _selectedPatterns.Add(parsed.ToIncludeLine());
        }

        UpdateStatusText();
        RefreshItemSelectionState();
    }

    public void SetColumnConfiguration(
        IEnumerable<string>? customDependencies,
        IEnumerable<ColumnValueListConfig>? customValueLists)
    {
        _customDependencies.Clear();
        if (customDependencies is not null)
        {
            foreach (var entry in customDependencies)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    _customDependencies.Add(entry);
            }
        }

        _customValueLists.Clear();
        if (customValueLists is not null)
        {
            foreach (var entry in customValueLists)
            {
                if (string.IsNullOrWhiteSpace(entry.Column))
                    continue;

                _customValueLists.Add(new ColumnValueListConfig
                {
                    Column = entry.Column,
                    File = entry.File,
                    Values = entry.Values?.ToList()
                });
            }
        }

        RefreshColumnConfigurationIndicators();
    }

    private void StripColumnSelections()
    {
        var tableOnlyPatterns = _selectedPatterns
            .Select(p => IncludeScopePattern.Parse(p))
            .Select(p => p.HasColumnSelection ? p.TablePattern : p.ToIncludeLine())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _selectedPatterns.Clear();
        foreach (var pattern in tableOnlyPatterns)
            _selectedPatterns.Add(pattern);

        RefreshItemSelectionState();
        UpdateStatusText();
    }

    private void UpdateScopeHeaderText()
    {
        ScopeHeaderText.Text = AllowColumnSelection
            ? "Include databases, schemas, tables, or columns"
            : "Include databases, schemas, or tables";
    }

    public async Task LoadDatabasesAsync(string connectionString)
    {
        _connectionString = connectionString.Trim();
        _focusedNode = null;
        _selectedColumnNode = null;
        _tableMetadataCache.Clear();
        UpdateColumnDetailsPanel(null);

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
            IsSelected = GetNodeSelectionState(kind, includePattern, parent, childrenLoaded: false)
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

                    TryNormalizeFullTableSelection(node);
                    RefreshColumnConfigurationIndicators();
                    break;
            }

            node.ChildrenLoaded = true;
            ApplySearchFilter();
            RefreshItemSelectionState();
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
            BaseColumnMetadataText = BuildColumnMetadataText(column, tableInfo),
            IsSelected = false
        };
    }

    private static string BuildColumnMetadataText(ColumnInfo column, TableInfo tableInfo)
    {
        var nullability = column.IsNullable ? "NULL" : "NOT NULL";
        var parts = new List<string> { $"{column.SqlType} ({nullability})" };

        if (column.IsPrimaryKey)
            parts.Add("PK");

        var foreignKey = tableInfo.GetGroupedForeignKeys()
            .FirstOrDefault(fk => fk.ColumnPairs.Any(pair =>
                pair.ParentColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase)));

        if (foreignKey is not null)
        {
            var references = foreignKey.ColumnPairs
                .Where(pair => pair.ParentColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase))
                .Select(pair =>
                    $"{SqlTableName.ToBracketedPattern(foreignKey.FullReferencedTableName)}.{pair.ReferencedColumn}");

            parts.Add($"FK → {string.Join(", ", references)}");
        }

        return string.Join(" · ", parts);
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
            node.IsSelected = node.Kind switch
            {
                SqlScopeNodeKind.Column => GetColumnSelectionState(node),
                SqlScopeNodeKind.Table => GetTableSelectionState(node),
                _ => GetSelectionState(node.IncludePattern, node.CanExpand)
            };
        }

        UpdateSelectAllState();
        UpdateStatusText();
    }

    private static string NormalizePattern(string pattern) =>
        IncludeScopePattern.Parse(pattern).TablePattern;

    private bool? GetNodeSelectionState(
        SqlScopeNodeKind kind,
        string includePattern,
        SqlScopeTreeNode? parent,
        bool childrenLoaded) =>
        kind switch
        {
            SqlScopeNodeKind.Column when parent is not null =>
                GetColumnSelectionState(includePattern, parent.DisplayName, childrenLoaded: false),
            SqlScopeNodeKind.Table => GetTableSelectionState(includePattern, childrenLoaded),
            _ => GetSelectionState(includePattern, canExpand: true)
        };

    private bool? GetColumnSelectionState(SqlScopeTreeNode columnNode) =>
        GetColumnSelectionState(columnNode.IncludePattern, columnNode.DisplayName, columnNode.Parent?.ChildrenLoaded == true);

    private bool? GetColumnSelectionState(string tablePattern, string columnName, bool childrenLoaded)
    {
        if (IsPatternFullySelected(tablePattern))
            return true;

        var columnSelection = GetColumnSelectionForTable(tablePattern);
        if (columnSelection is not null)
            return columnSelection.Contains(columnName);

        var ancestorState = GetSelectionState(tablePattern, canExpand: true);
        return ancestorState == true ? true : false;
    }

    private bool? GetTableSelectionState(SqlScopeTreeNode tableNode) =>
        GetTableSelectionState(tableNode.IncludePattern, tableNode.ChildrenLoaded, tableNode);

    private bool? GetTableSelectionState(string tablePattern, bool childrenLoaded, SqlScopeTreeNode? tableNode = null)
    {
        if (IsPatternFullySelected(tablePattern))
            return true;

        var columnSelection = GetColumnSelectionForTable(tablePattern);
        if (columnSelection is not null)
        {
            if (!childrenLoaded || tableNode is null)
                return null;

            var columnNodes = tableNode.Children
                .Where(c => c.Kind == SqlScopeNodeKind.Column && !c.IsPlaceholder)
                .ToList();

            if (columnNodes.Count == 0)
                return null;

            var selectedCount = columnNodes.Count(c => columnSelection.Contains(c.DisplayName));
            return selectedCount switch
            {
                0 => false,
                _ when selectedCount == columnNodes.Count => true,
                _ => null
            };
        }

        return GetSelectionState(tablePattern, canExpand: true);
    }

    private bool? GetSelectionState(string includePattern, bool canExpand)
    {
        var normalized = NormalizePattern(includePattern);

        if (IsPatternFullySelected(normalized))
            return true;

        if (!canExpand)
            return false;

        var hasPartialSelection = _selectedPatterns.Any(selected =>
        {
            var parsed = IncludeScopePattern.Parse(selected);
            var selectedNormalized = NormalizePattern(parsed.TablePattern);
            return !string.Equals(selectedNormalized, normalized, StringComparison.OrdinalIgnoreCase)
                && SqlTableName.MatchesPattern(selectedNormalized, normalized);
        });

        return hasPartialSelection ? null : false;
    }

    private bool IsPatternFullySelected(string includePattern)
    {
        var normalized = NormalizePattern(includePattern);

        return _selectedPatterns.Any(selected =>
        {
            var parsed = IncludeScopePattern.Parse(selected);
            if (parsed.HasColumnSelection)
                return false;

            var selectedNormalized = NormalizePattern(parsed.TablePattern);
            return string.Equals(selectedNormalized, normalized, StringComparison.OrdinalIgnoreCase)
                || SqlTableName.MatchesPattern(normalized, selectedNormalized);
        });
    }

    private HashSet<string>? GetColumnSelectionForTable(string tablePattern)
    {
        var normalized = NormalizePattern(tablePattern);

        foreach (var selected in _selectedPatterns)
        {
            var parsed = IncludeScopePattern.Parse(selected);
            if (!parsed.HasColumnSelection)
                continue;

            if (string.Equals(parsed.TablePattern, normalized, StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(parsed.Columns!, StringComparer.OrdinalIgnoreCase);
        }

        return null;
    }

    private void AddPatternSelection(string includePattern) =>
        _selectedPatterns.Add(NormalizePattern(includePattern));

    private void SetColumnSelectionForTable(string tablePattern, IEnumerable<string> columns)
    {
        RemoveTablePatterns(tablePattern);
        var columnList = columns
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (columnList.Count == 0)
            return;

        _selectedPatterns.Add(new IncludeScopePattern(NormalizePattern(tablePattern), columnList).ToIncludeLine());
    }

    private void RemoveTablePatterns(string tablePattern)
    {
        var normalized = NormalizePattern(tablePattern);
        _selectedPatterns.RemoveWhere(selected =>
            string.Equals(
                NormalizePattern(IncludeScopePattern.Parse(selected).TablePattern),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private void RemovePatternSelection(string includePattern, SqlScopeTreeNode node)
    {
        var normalized = NormalizePattern(includePattern);

        var coveringPattern = _selectedPatterns.FirstOrDefault(selected =>
        {
            var parsed = IncludeScopePattern.Parse(selected);
            if (parsed.HasColumnSelection)
                return false;

            var selectedNormalized = NormalizePattern(parsed.TablePattern);
            return !string.Equals(selectedNormalized, normalized, StringComparison.OrdinalIgnoreCase)
                && SqlTableName.MatchesPattern(normalized, selectedNormalized);
        });

        if (coveringPattern is not null)
        {
            _selectedPatterns.Remove(coveringPattern);
            foreach (var sibling in GetSiblings(node))
                AddPatternSelection(sibling.IncludePattern);

            return;
        }

        _selectedPatterns.RemoveWhere(selected =>
        {
            var parsed = IncludeScopePattern.Parse(selected);
            var selectedNormalized = NormalizePattern(parsed.TablePattern);
            return string.Equals(selectedNormalized, normalized, StringComparison.OrdinalIgnoreCase)
                || SqlTableName.MatchesPattern(selectedNormalized, normalized)
                || SqlTableName.MatchesPattern(normalized, selectedNormalized);
        });
    }

    private IEnumerable<SqlScopeTreeNode> GetSiblings(SqlScopeTreeNode node)
    {
        if (node.Parent is null)
            return _roots.Where(r => !ReferenceEquals(r, node));

        return node.Parent.Children.Where(c => !ReferenceEquals(c, node));
    }

    private void SelectColumn(SqlScopeTreeNode columnNode)
    {
        var tablePattern = columnNode.IncludePattern;
        var columnName = columnNode.DisplayName;

        if (IsPatternFullySelected(tablePattern))
            return;

        var existing = GetColumnSelectionForTable(tablePattern);
        if (existing is null)
        {
            SetColumnSelectionForTable(tablePattern, [columnName]);
            return;
        }

        existing.Add(columnName);
        SetColumnSelectionForTable(tablePattern, existing);
    }

    private void UnselectColumn(SqlScopeTreeNode columnNode)
    {
        var tablePattern = columnNode.IncludePattern;
        var columnName = columnNode.DisplayName;

        if (IsPatternFullySelected(tablePattern))
        {
            var tableNode = columnNode.Parent!;
            var remainingColumns = tableNode.Children
                .Where(c => c.Kind == SqlScopeNodeKind.Column
                    && !c.IsPlaceholder
                    && !string.Equals(c.DisplayName, columnName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.DisplayName)
                .ToList();

            RemoveTablePatterns(tablePattern);
            if (remainingColumns.Count > 0)
                SetColumnSelectionForTable(tablePattern, remainingColumns);

            return;
        }

        var existing = GetColumnSelectionForTable(tablePattern);
        if (existing is null)
            return;

        existing.Remove(columnName);
        if (existing.Count == 0)
            RemoveTablePatterns(tablePattern);
        else
            SetColumnSelectionForTable(tablePattern, existing);
    }

    private void TryNormalizeFullTableSelection(SqlScopeTreeNode tableNode)
    {
        var tablePattern = tableNode.IncludePattern;
        var columnSelection = GetColumnSelectionForTable(tablePattern);
        if (columnSelection is null)
            return;

        var columnNodes = tableNode.Children
            .Where(c => c.Kind == SqlScopeNodeKind.Column && !c.IsPlaceholder)
            .ToList();

        if (columnNodes.Count == 0)
            return;

        if (columnNodes.All(c => columnSelection.Contains(c.DisplayName)))
        {
            RemoveTablePatterns(tablePattern);
            AddPatternSelection(tablePattern);
        }
    }

    private IEnumerable<SqlScopeTreeNode> GetVisibleNodes()
    {
        foreach (var node in EnumerateNodes(_roots))
        {
            if (node.IsVisible && !node.IsPlaceholder)
                yield return node;
        }
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

    private static string FormatPatternForDisplay(string pattern)
    {
        var parsed = IncludeScopePattern.Parse(pattern);
        var bracketed = SqlTableName.ToBracketedPattern(parsed.TablePattern);
        return parsed.HasColumnSelection
            ? $"{bracketed}({string.Join(", ", parsed.Columns!)})"
            : bracketed;
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        SearchInput.IsEnabled = !isBusy;
        SelectAllCheckBox.IsEnabled = !isBusy;
        ScopeTree.IsEnabled = !isBusy;
        if (message is not null)
            StatusText.Text = message;
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

    private void OnScopeTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SqlScopeTreeNode { Kind: SqlScopeNodeKind.Column, IsPlaceholder: false } node)
            UpdateColumnDetailsPanel(node);
        else if (_selectedColumnNode is not null && !ReferenceEquals(e.NewValue, _selectedColumnNode))
            UpdateColumnDetailsPanel(null);
    }

    private void UpdateColumnDetailsPanel(SqlScopeTreeNode? node)
    {
        _selectedColumnNode = node?.Kind == SqlScopeNodeKind.Column ? node : null;

        if (_selectedColumnNode?.ColumnInfo is not { } column)
        {
            ColumnDetailsPanel.Visibility = Visibility.Collapsed;
            ColumnDetailsEmpty.Visibility = Visibility.Visible;
            return;
        }

        ColumnDetailsPanel.Visibility = Visibility.Visible;
        ColumnDetailsEmpty.Visibility = Visibility.Collapsed;

        var columnRef = GetColumnRef(_selectedColumnNode);
        var tableInfo = GetTableInfoForNode(_selectedColumnNode);

        DetailColumnNameText.Text = _selectedColumnNode.DisplayName;
        DetailTableNameText.Text = _selectedColumnNode.TableFullName ?? _selectedColumnNode.IncludePattern;
        DetailDataTypeText.Text = FormatColumnType(column);
        DetailNullableText.Text = column.IsNullable ? "Yes" : "No";
        DetailKeysText.Text = BuildKeysAndRelationshipsText(column, tableInfo);
        DetailCustomDepsText.Text = BuildCustomDependenciesText(columnRef);
        DetailValueListText.Text = BuildValueListText(columnRef);
    }

    private TableInfo? GetTableInfoForNode(SqlScopeTreeNode columnNode)
    {
        if (columnNode.TableFullName is not null
            && _tableMetadataCache.TryGetValue(columnNode.TableFullName, out var cached))
        {
            return cached;
        }

        return null;
    }

    private static string FormatColumnType(ColumnInfo column)
    {
        var formatted = SqlTypeInfo.FormatSqlColumnType(column);
        var extras = new List<string>();

        if (column.IsIdentity)
            extras.Add("IDENTITY");
        if (column.IsComputed)
            extras.Add("COMPUTED");
        if (column.IsRowVersion)
            extras.Add("ROWVERSION");
        if (column.IsUnique)
            extras.Add("UNIQUE");
        if (column.HasDefault)
            extras.Add($"DEFAULT {column.DefaultDefinition}");

        return extras.Count == 0 ? formatted : $"{formatted} ({string.Join(", ", extras)})";
    }

    private static string BuildKeysAndRelationshipsText(ColumnInfo column, TableInfo? tableInfo)
    {
        var lines = new List<string>();

        if (column.IsPrimaryKey)
            lines.Add("Primary key");

        if (tableInfo is not null)
        {
            foreach (var foreignKey in tableInfo.GetGroupedForeignKeys())
            {
                var relatedPairs = foreignKey.ColumnPairs
                    .Where(pair => pair.ParentColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (relatedPairs.Count == 0)
                    continue;

                var referencedTable = SqlTableName.ToBracketedPattern(foreignKey.FullReferencedTableName);
                foreach (var pair in relatedPairs)
                {
                    lines.Add($"Foreign key → {referencedTable}.{pair.ReferencedColumn}");
                }

                if (foreignKey.IsComposite)
                {
                    var compositeColumns = foreignKey.ColumnPairs
                        .Select(pair => pair.ParentColumn)
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    lines.Add($"Composite FK with: {string.Join(", ", compositeColumns)}");
                }
            }
        }

        return lines.Count == 0 ? "None" : string.Join(Environment.NewLine, lines);
    }

    private string BuildCustomDependenciesText(string columnRef)
    {
        var groupColumns = GetDependencyGroupColumns(columnRef).ToList();
        if (groupColumns.Count == 0)
            return "None";

        var related = groupColumns
            .Where(c => !string.Equals(c, columnRef, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (related.Count == 0)
            return "None";

        return string.Join(Environment.NewLine, related.Select(FormatColumnRefForDisplay));
    }

    private string BuildValueListText(string columnRef)
    {
        var valueList = _customValueLists.FirstOrDefault(v =>
            string.Equals(v.Column, columnRef, StringComparison.OrdinalIgnoreCase));

        if (valueList is null)
            return "None";

        if (valueList.HasFile)
            return $"File: {valueList.File}";

        if (valueList.HasInlineValues)
        {
            var preview = valueList.Values!
                .Take(5)
                .Select(v => $"'{v}'");

            var summary = string.Join(", ", preview);
            if (valueList.Values!.Count > 5)
                summary += $", … ({valueList.Values.Count} values)";

            return $"Inline values: {summary}";
        }

        return "None";
    }

    private static string FormatColumnRefForDisplay(string columnRef)
    {
        var lastDot = columnRef.LastIndexOf('.');
        if (lastDot < 0)
            return columnRef;

        var tablePattern = columnRef[..lastDot];
        var columnName = columnRef[(lastDot + 1)..];
        return $"{SqlTableName.ToBracketedPattern(tablePattern)}.{columnName}";
    }

    private void OnDetailCustomDepClick(object sender, RoutedEventArgs e)
    {
        if (_selectedColumnNode is null || string.IsNullOrWhiteSpace(_connectionString))
            return;

        ShowCustomDependencyDialog(_selectedColumnNode, GetColumnRef(_selectedColumnNode));
    }

    private void OnDetailValueListClick(object sender, RoutedEventArgs e)
    {
        if (_selectedColumnNode is null)
            return;

        ShowValueListDialog(GetColumnRef(_selectedColumnNode));
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
            node.IsTreeSelected = true;
            UpdateColumnDetailsPanel(node);
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
            || node.IsPlaceholder)
            return;

        if (node.Kind == SqlScopeNodeKind.Column)
        {
            if (!AllowColumnSelection)
                return;

            if (checkBox.IsChecked == true)
                SelectColumn(node);
            else
                UnselectColumn(node);

            if (node.Parent is not null)
                TryNormalizeFullTableSelection(node.Parent);
        }
        else
        {
            if (checkBox.IsChecked == true)
            {
                RemoveTablePatterns(node.IncludePattern);
                AddPatternSelection(node.IncludePattern);
            }
            else
            {
                RemovePatternSelection(node.IncludePattern, node);
            }
        }

        RefreshItemSelectionState();
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SqlScopeTreeNode node }
            || node.IsPlaceholder
            || node.Kind != SqlScopeNodeKind.Column)
            return;

        e.Handled = true;

        if (string.IsNullOrWhiteSpace(_connectionString))
            return;

        var columnRef = GetColumnRef(node);
        var menu = new ContextMenu();

        var header = new MenuItem
        {
            Header = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE8, 0xF4, 0xFC)),
                Padding = new Thickness(6, 4, 6, 4),
                Child = new TextBlock
                {
                    Text = node.DisplayName,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.DarkSlateGray
                }
            },
            IsHitTestVisible = false,
            Focusable = false
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        var dependencyItem = new MenuItem { Header = "Add custom dependency..." };
        dependencyItem.Click += (_, _) =>
            Dispatcher.BeginInvoke(() => ShowCustomDependencyDialog(node, columnRef));
        menu.Items.Add(dependencyItem);

        var valueListItem = new MenuItem { Header = "Set value list or file..." };
        valueListItem.Click += (_, _) =>
            Dispatcher.BeginInvoke(() => ShowValueListDialog(columnRef));
        menu.Items.Add(valueListItem);

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void ShowCustomDependencyDialog(SqlScopeTreeNode node, string columnRef)
    {
        var existingGroup = GetDependencyGroupColumns(columnRef);
        var dialog = new CustomDependencyDialog(_connectionString, columnRef, existingGroup)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
            return;

        UpdateCustomDependencyGroup(columnRef, dialog.SelectedColumnRefs);
    }

    private void ShowValueListDialog(string columnRef)
    {
        var existing = _customValueLists
            .FirstOrDefault(v => string.Equals(v.Column, columnRef, StringComparison.OrdinalIgnoreCase));

        var dialog = new ValueListDialog(columnRef, existing)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
            return;

        if (dialog.Result == ValueListDialog.ValueListDialogResult.Cleared)
            RemoveValueList(columnRef);
        else if (dialog.Result == ValueListDialog.ValueListDialogResult.Saved && dialog.ValueListConfig is not null)
            SetValueList(dialog.ValueListConfig);
    }

    private static string GetColumnRef(SqlScopeTreeNode node) =>
        $"{node.IncludePattern}.{node.DisplayName}";

    private IEnumerable<string> GetDependencyGroupColumns(string columnRef)
    {
        foreach (var group in _customDependencies)
        {
            var columns = ParseDependencyGroup(group);
            if (columns.Any(c => string.Equals(c, columnRef, StringComparison.OrdinalIgnoreCase)))
                return columns;
        }

        return [];
    }

    private static List<string> ParseDependencyGroup(string group) =>
        group.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void UpdateCustomDependencyGroup(string sourceColumnRef, IReadOnlyList<string> relatedColumnRefs)
    {
        var allColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceColumnRef };
        foreach (var column in relatedColumnRefs)
            allColumns.Add(column);

        _customDependencies.RemoveAll(group =>
            ParseDependencyGroup(group).Any(c =>
                string.Equals(c, sourceColumnRef, StringComparison.OrdinalIgnoreCase)));

        if (allColumns.Count >= 2)
        {
            var line = string.Join("|", allColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
            _customDependencies.Add(line);
        }

        RefreshColumnConfigurationIndicators();
        UpdateStatusText();
    }

    private void SetValueList(ColumnValueListConfig config)
    {
        _customValueLists.RemoveAll(v =>
            string.Equals(v.Column, config.Column, StringComparison.OrdinalIgnoreCase));
        _customValueLists.Add(new ColumnValueListConfig
        {
            Column = config.Column,
            File = config.File,
            Values = config.Values?.ToList()
        });

        RefreshColumnConfigurationIndicators();
        UpdateStatusText();
    }

    private void RemoveValueList(string columnRef)
    {
        _customValueLists.RemoveAll(v =>
            string.Equals(v.Column, columnRef, StringComparison.OrdinalIgnoreCase));
        RefreshColumnConfigurationIndicators();
        UpdateStatusText();
    }

    private void RefreshColumnConfigurationIndicators()
    {
        foreach (var node in EnumerateNodes(_roots).Where(n => n.Kind == SqlScopeNodeKind.Column))
        {
            var columnRef = GetColumnRef(node);
            var hints = new List<string>();

            if (_customDependencies.Any(g =>
                    ParseDependencyGroup(g).Any(c =>
                        string.Equals(c, columnRef, StringComparison.OrdinalIgnoreCase))))
            {
                hints.Add("custom dependency");
            }

            var valueList = _customValueLists.FirstOrDefault(v =>
                string.Equals(v.Column, columnRef, StringComparison.OrdinalIgnoreCase));
            if (valueList is not null)
            {
                hints.Add(valueList.HasFile ? "value file" : "value list");
            }

            node.ConfigurationHint = hints.Count == 0 ? string.Empty : string.Join(", ", hints);
        }

        if (_selectedColumnNode is not null)
            UpdateColumnDetailsPanel(_selectedColumnNode);
    }

    private void UpdateStatusText()
    {
        var parts = new List<string>();
        if (_selectedPatterns.Count == 0)
        {
            parts.Add("Select one or more databases, schemas, tables, or columns.");
        }
        else
        {
            parts.Add($"{_selectedPatterns.Count} selected: {string.Join(", ", _selectedPatterns.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(FormatPatternForDisplay))}");
        }

        if (_customDependencies.Count > 0)
            parts.Add($"{_customDependencies.Count} custom dependency group(s)");

        if (_customValueLists.Count > 0)
            parts.Add($"{_customValueLists.Count} value list(s)");

        StatusText.Text = string.Join(" · ", parts);
    }
}

public sealed class ColumnSelectionCheckboxVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return Visibility.Visible;

        if (values[0] is SqlScopeNodeKind kind && kind == SqlScopeNodeKind.Column)
        {
            var allowColumnSelection = values[1] is true;
            return allowColumnSelection ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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
    private bool _isTreeSelected;
    private string _configurationHint = string.Empty;

    public SqlScopeTreeNode? Parent { get; set; }
    public ObservableCollection<SqlScopeTreeNode> Children { get; } = [];

    public required string DisplayName { get; init; }
    public required string IncludePattern { get; init; }
    public required SqlScopeNodeKind Kind { get; init; }

    public string? TableFullName { get; init; }
    public ColumnInfo? ColumnInfo { get; init; }
    public string BaseColumnMetadataText { get; init; } = string.Empty;

    public string ConfigurationHint
    {
        get => _configurationHint;
        set
        {
            if (string.Equals(_configurationHint, value, StringComparison.Ordinal))
                return;

            _configurationHint = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConfigurationHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColumnMetadataText)));
        }
    }

    public string ColumnMetadataText => string.IsNullOrEmpty(ConfigurationHint)
        ? BaseColumnMetadataText
        : $"{BaseColumnMetadataText} · {ConfigurationHint}";

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
