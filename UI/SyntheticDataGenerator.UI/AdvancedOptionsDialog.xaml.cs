using System.Windows;

namespace SyntheticDataGenerator.UI;

public partial class AdvancedOptionsDialog : Window
{
    public int Seed { get; private set; }
    public bool EnableDataOverwrite { get; private set; }

    public AdvancedOptionsDialog(int seed, bool enableDataOverwrite)
    {
        InitializeComponent();
        SeedInput.Text = seed.ToString();
        EnableDataOverwriteCheckBox.IsChecked = enableDataOverwrite;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SeedInput.Text.Trim(), out var seed))
        {
            MessageBox.Show(this, "Enter a valid seed number.",
                "Advanced Options", MessageBoxButton.OK, MessageBoxImage.Information);
            SeedInput.Focus();
            return;
        }

        Seed = seed;
        EnableDataOverwrite = EnableDataOverwriteCheckBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
