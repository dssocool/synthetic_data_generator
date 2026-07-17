using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private void OnRuleRowClick(object sender, MouseButtonEventArgs e)
    {
        if (IsClickOnActionButton(e.OriginalSource as DependencyObject))
            return;

        var rule = GetClickedRule(e.OriginalSource as DependencyObject);
        if (rule is null)
            return;

        OpenRuleDetail(rule);
    }

    private void OpenRuleDetail(SavedRule rule)
    {
        var detail = new RuleDetailWindow(rule, _ruleStorage)
        {
            Owner = this
        };

        detail.ShowDialog();

        if (detail.RuleChanged)
            LoadRules();
    }

    private static bool IsClickOnActionButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static SavedRule? GetClickedRule(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListViewItem { Content: SavedRule rule })
                return rule;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
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
