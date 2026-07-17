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

    private void OnRuleClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem)
            element = VisualTreeHelper.GetParent(element);

        if (element is not ListViewItem { Content: SavedRule rule })
            return;

        OpenRuleDialog(rule);
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
