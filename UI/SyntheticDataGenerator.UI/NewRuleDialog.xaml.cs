using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI;

public partial class NewRuleDialog : Window
{
    private const int SelectTargetStepIndex = 0;
    private const int OptionsStepIndex = 1;
    private const int PreviewStepIndex = 2;

    private int _currentStep = SelectTargetStepIndex;
    private readonly SyntheticDataPreviewService _previewService = new();

    public NewRuleWizardState WizardState { get; } = new();

    public NewRuleDialog()
    {
        InitializeComponent();
        UpdateStep();
    }

    private void OnRuleTypeChanged(object sender, RoutedEventArgs e)
    {
        if (GenerateSyntheticDataOption.IsChecked == true)
            WizardState.RuleType = RuleType.GenerateSyntheticData;
        else if (SimulatedSqlQueryOption.IsChecked == true)
            WizardState.RuleType = RuleType.SimulatedSqlQuery;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep == SelectTargetStepIndex)
            return;

        _currentStep--;
        UpdateStep();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (!TryValidateCurrentStep())
            return;

        if (_currentStep == PreviewStepIndex)
        {
            DialogResult = true;
            Close();
            return;
        }

        if (_currentStep == OptionsStepIndex)
        {
            CaptureOptions();

            if (WizardState.RuleType == RuleType.GenerateSyntheticData)
            {
                if (!await TryGenerateSyntheticDataPreviewAsync())
                    return;
            }
        }

        _currentStep++;
        UpdateStep();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async Task<bool> TryGenerateSyntheticDataPreviewAsync()
    {
        SetWizardBusy(true, "Generating preview rows...");

        try
        {
            var result = await _previewService.GeneratePreviewAsync(WizardState);
            if (!result.Success)
            {
                MessageBox.Show(this, result.ErrorMessage, "Create New Rule",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            WizardState.AppsettingsPath = result.AppsettingsPath;
            WizardState.PreviewTables = result.Tables;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Create New Rule",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetWizardBusy(false);
        }
    }

    private void SetWizardBusy(bool isBusy, string? message = null)
    {
        BackButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;
        NextButton.IsEnabled = !isBusy;

        if (isBusy)
        {
            BusyText.Text = message ?? string.Empty;
            BusyText.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;
        }
        else
        {
            BusyText.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
        }
    }

    private bool TryValidateCurrentStep()
    {
        switch (_currentStep)
        {
            case SelectTargetStepIndex:
                if (WizardState.RuleType == RuleType.None)
                {
                    MessageBox.Show(this, "Select a rule target to continue.", "Create New Rule",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                return true;

            case OptionsStepIndex:
                if (WizardState.RuleType == RuleType.GenerateSyntheticData)
                {
                    if (string.IsNullOrWhiteSpace(ConnectionStringInput.Text))
                    {
                        MessageBox.Show(this, "Enter a connection string to continue.", "Create New Rule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        ConnectionStringInput.Focus();
                        return false;
                    }

                    if (!int.TryParse(RowsPerTableInput.Text, out var rows) || rows <= 0)
                    {
                        MessageBox.Show(this, "Enter a positive number for rows per table.", "Create New Rule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        RowsPerTableInput.Focus();
                        return false;
                    }

                    if (!int.TryParse(SeedInput.Text, out _))
                    {
                        MessageBox.Show(this, "Enter a numeric seed to continue.", "Create New Rule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        SeedInput.Focus();
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(IncludeTablesInput.Text))
                    {
                        MessageBox.Show(this, "Add at least one table to Include to continue.", "Create New Rule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        IncludeTablesInput.Focus();
                        return false;
                    }
                }
                else if (WizardState.RuleType == RuleType.SimulatedSqlQuery)
                {
                    if (string.IsNullOrWhiteSpace(SimulatedServerNameInput.Text))
                    {
                        MessageBox.Show(this, "Enter a simulated server name to continue.", "Create New Rule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        SimulatedServerNameInput.Focus();
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(SqlQueryInput.Text))
                    {
                        MessageBox.Show(this, "Enter a SQL query to continue.", "Create New Rule",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        SqlQueryInput.Focus();
                        return false;
                    }
                }

                return true;

            default:
                return true;
        }
    }

    private void CaptureOptions()
    {
        if (WizardState.RuleType == RuleType.GenerateSyntheticData)
        {
            WizardState.ConnectionString = ConnectionStringInput.Text.Trim();
            WizardState.RowsPerTable = int.Parse(RowsPerTableInput.Text.Trim());
            WizardState.Seed = int.Parse(SeedInput.Text.Trim());
            WizardState.IncludeTables = IncludeTablesInput.Text.Trim();
            WizardState.PreviewTables = null;
            WizardState.AppsettingsPath = null;
        }
        else if (WizardState.RuleType == RuleType.SimulatedSqlQuery)
        {
            WizardState.SimulatedServerName = SimulatedServerNameInput.Text.Trim();
            WizardState.SqlQuery = SqlQueryInput.Text.Trim();
        }
    }

    private void UpdateStep()
    {
        SelectTargetStep.Visibility = _currentStep == SelectTargetStepIndex ? Visibility.Visible : Visibility.Collapsed;
        OptionsStep.Visibility = _currentStep == OptionsStepIndex ? Visibility.Visible : Visibility.Collapsed;
        PreviewStep.Visibility = _currentStep == PreviewStepIndex ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _currentStep > SelectTargetStepIndex;
        NextButton.Content = _currentStep == PreviewStepIndex ? "Finish" : "Next";

        StepTitleText.Text = _currentStep switch
        {
            SelectTargetStepIndex => "Step 1 of 3: Select Target",
            OptionsStepIndex => "Step 2 of 3: Options",
            _ => "Step 3 of 3: Preview"
        };

        if (_currentStep == OptionsStepIndex)
            UpdateOptionsPanel();

        if (_currentStep == PreviewStepIndex)
            UpdatePreviewPanel();
    }

    private void UpdateOptionsPanel()
    {
        var isGenerate = WizardState.RuleType == RuleType.GenerateSyntheticData;

        GenerateSyntheticDataOptions.Visibility = isGenerate ? Visibility.Visible : Visibility.Collapsed;
        SimulatedSqlQueryOptions.Visibility = isGenerate ? Visibility.Collapsed : Visibility.Visible;

        if (isGenerate)
        {
            ConnectionStringInput.Text = WizardState.ConnectionString;
            RowsPerTableInput.Text = WizardState.RowsPerTable.ToString();
            SeedInput.Text = WizardState.Seed.ToString();
            IncludeTablesInput.Text = WizardState.IncludeTables;
        }
        else
        {
            SimulatedServerNameInput.Text = WizardState.SimulatedServerName;
            SqlQueryInput.Text = WizardState.SqlQuery;
        }
    }

    private void UpdatePreviewPanel()
    {
        if (WizardState.RuleType == RuleType.GenerateSyntheticData)
        {
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewTabs.Visibility = Visibility.Visible;

            PreviewSummaryText.Text =
                $"appsettings.yaml created at:{Environment.NewLine}{WizardState.AppsettingsPath}{Environment.NewLine}{Environment.NewLine}" +
                $"Preview shows {SyntheticDataPreviewService.PreviewRowCount} generated rows per included table.";

            PreviewTabs.Items.Clear();
            foreach (var table in WizardState.PreviewTables ?? [])
            {
                var dataGrid = new DataGrid
                {
                    AutoGenerateColumns = true,
                    IsReadOnly = true,
                    CanUserAddRows = false,
                    CanUserDeleteRows = false,
                    ItemsSource = table.DataTable.DefaultView,
                    Margin = new Thickness(4)
                };

                PreviewTabs.Items.Add(new TabItem
                {
                    Header = table.TableName,
                    Content = dataGrid
                });
            }

            return;
        }

        PreviewTabs.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        PreviewSummaryText.Text = "Review your rule before finishing:";
        PreviewText.Text = BuildSimulatedSqlPreviewText();
    }

    private string BuildSimulatedSqlPreviewText()
    {
        return $"""
                Target: Make a query running on simulated SQL Server

                Simulated server: {WizardState.SimulatedServerName}

                SQL query:
                {WizardState.SqlQuery}
                """;
    }
}
