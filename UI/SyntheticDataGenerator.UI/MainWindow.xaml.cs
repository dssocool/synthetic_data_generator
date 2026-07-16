using System.Windows;

namespace SyntheticDataGenerator.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnCreateNewRuleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewRuleDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        // Rule creation flow will be wired up in a later step.
        _ = dialog.WizardState;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
