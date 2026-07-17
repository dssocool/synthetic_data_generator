using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SyntheticDataGenerator.UI.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI;

public partial class RuleModificationDiffDialog : Window
{
    private readonly SavedRule _currentRule;
    private readonly RuleModificationEntry _entry;
    private readonly RuleStorageService _ruleStorage;
    private readonly ObservableCollection<RuleFieldDiff> _diffs = [];

    public RuleModificationDiffDialog(
        SavedRule currentRule,
        RuleModificationEntry entry,
        RuleStorageService ruleStorage)
    {
        InitializeComponent();
        _currentRule = currentRule;
        _entry = entry;
        _ruleStorage = ruleStorage;

        DiffGrid.ItemsSource = _diffs;

        HeaderText.Text = $"Historical version from {_entry.ModifiedAt.LocalDateTime:g}";
        SummaryText.Text = _entry.Summary;
        HistoricalColumn.Header = $"Historical ({_entry.ModifiedAt.LocalDateTime:g})";
        Title = $"Compare Modification — {_entry.ModifiedAt.LocalDateTime:g}";

        if (_entry.Snapshot is null)
        {
            UnavailableText.Visibility = Visibility.Visible;
            DiffGrid.Visibility = Visibility.Collapsed;
            RevertButton.IsEnabled = false;
            return;
        }

        foreach (var diff in RuleDiffBuilder.Build(_entry.Snapshot, _currentRule))
            _diffs.Add(diff);

        RevertButton.IsEnabled = !RuleDiffBuilder.AreConfigurationsEqual(_entry.Snapshot, _currentRule);
    }

    public bool RuleReverted { get; private set; }

    public SavedRule? RevertedRule { get; private set; }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (_entry.Snapshot is null)
            return;

        var result = MessageBox.Show(
            this,
            $"Revert this rule to the version saved on {_entry.ModifiedAt.LocalDateTime:g}?\n\nCurrent settings will be replaced.",
            "Revert Rule",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            RevertedRule = _ruleStorage.RevertToSnapshot(_currentRule.Id, _entry.Snapshot);
            RuleReverted = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Revert Rule",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = RuleReverted;
        Close();
    }

    private void OnDiffGridLoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Height = double.NaN;
    }
}
