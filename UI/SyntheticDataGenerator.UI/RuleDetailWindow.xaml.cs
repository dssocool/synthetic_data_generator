using System.Collections.ObjectModel;
using System.Windows;
using SyntheticDataGenerator.UI.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI;

public partial class RuleDetailWindow : Window
{
    private readonly RuleStorageService _ruleStorage;
    private readonly RuleHistoryService _historyService = new();
    private SavedRule _rule;

    private readonly ObservableCollection<RuleModificationEntry> _modifications = [];
    private readonly ObservableCollection<RuleExecutionEntry> _executions = [];

    public RuleDetailWindow(SavedRule rule, RuleStorageService ruleStorage)
    {
        InitializeComponent();
        _rule = rule;
        _ruleStorage = ruleStorage;

        ModificationsList.ItemsSource = _modifications;
        ExecutionsList.ItemsSource = _executions;

        LoadRuleDetails();
        LoadHistory();
    }

    public bool RuleChanged { get; private set; }

    private void LoadRuleDetails()
    {
        Title = _rule.Name;
        RuleNameText.Text = _rule.Name;
        RuleTypeText.Text = _rule.TypeDisplayName;
        RuleSummaryText.Text = BuildDetailSummary();
        RuleDatesText.Text =
            $"Created {_rule.CreatedAt.LocalDateTime:g}  ·  Last modified {_rule.ModifiedAt.LocalDateTime:g}";

        ExecuteButton.IsEnabled = _rule.RuleType == RuleType.GenerateSyntheticData;
    }

    private string BuildDetailSummary() =>
        _rule.RuleType switch
        {
            RuleType.GenerateSyntheticData =>
                $"Default rows per table: {_rule.RowsPerTable}  ·  Default seed: {_rule.Seed}  ·  Tables: {_rule.Summary}",
            RuleType.SimulatedSqlQuery =>
                $"Server: {_rule.SimulatedServerName}  ·  Query: {Truncate(_rule.SqlQuery, 120)}",
            _ => string.Empty
        };

    private void LoadHistory()
    {
        _modifications.Clear();
        foreach (var entry in _historyService.LoadModifications(_rule.Id))
            _modifications.Add(entry);

        _executions.Clear();
        foreach (var entry in _historyService.LoadExecutions(_rule.Id))
            _executions.Add(entry);
    }

    private void OnModificationDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModificationsList.SelectedItem is not RuleModificationEntry entry)
            return;

        var dialog = new RuleModificationDiffDialog(_rule, entry, _ruleStorage)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || !dialog.RuleReverted || dialog.RevertedRule is null)
            return;

        _rule = dialog.RevertedRule;
        RuleChanged = true;
        LoadRuleDetails();
        LoadHistory();
    }

    private void OnExecutionDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ExecutionsList.SelectedItem is not RuleExecutionEntry entry)
            return;

        if (entry.ExecutionMode?.Equals("update", StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (entry.InsertedKeys is not { Count: > 0 })
            return;

        var dialog = new ExecutionInsertedKeysDialog(entry)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ExecuteRuleDialog(_rule)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
            LoadHistory();
    }

    private void OnModifyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewRuleDialog(_rule)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _rule = _ruleStorage.Save(dialog.WizardState, dialog.WizardState.RuleId);
            RuleChanged = true;
            LoadRuleDetails();
            LoadHistory();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save Rule",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        var trimmed = value.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..(maxLength - 3)] + "...";
    }
}
