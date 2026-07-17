using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SyntheticDataGenerator.UI.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI;

public partial class MainWindow : Window
{
    private readonly RuleStorageService _ruleStorage = new();
    private readonly ObservableCollection<SavedRule> _rules = [];

    public MainWindow()
    {
        InitializeComponent();
        RulesList.ItemsSource = _rules;
        LoadRules();
    }

    private void LoadRules()
    {
        _rules.Clear();
        foreach (var rule in _ruleStorage.LoadAll())
            _rules.Add(rule);
    }

    private void OnCreateNewRuleClick(object sender, RoutedEventArgs e)
    {
        OpenRuleDialog();
    }

    private void OnRunRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedRule rule })
            return;

        if (!rule.CanExecute)
            return;

        var dialog = new ExecuteRuleDialog(rule)
        {
            Owner = this
        };

        dialog.ShowDialog();
    }

    private void OnModifyRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedRule rule })
            return;

        OpenRuleDialog(rule);
    }

    private void OnDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedRule rule })
            return;

        var result = MessageBox.Show(this,
            $"Delete rule \"{rule.Name}\"? This cannot be undone.",
            "Delete Rule",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _ruleStorage.Delete(rule.Id);
            LoadRules();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete Rule",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenRuleDialog(SavedRule? existingRule = null)
    {
        var dialog = new NewRuleDialog(existingRule)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _ruleStorage.Save(dialog.WizardState, dialog.WizardState.RuleId);
            LoadRules();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save Rule",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
