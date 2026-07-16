using System.Windows;
using System.Windows.Controls;

namespace SyntheticDataGenerator.UI;

public partial class NewRuleDialog : Window
{
    private const int SelectTargetStepIndex = 0;
    private const int OptionsStepIndex = 1;
    private const int PreviewStepIndex = 2;

    private int _currentStep = SelectTargetStepIndex;

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

    private void OnNextClick(object sender, RoutedEventArgs e)
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
            CaptureOptions();

        _currentStep++;
        UpdateStep();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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
            WizardState.IncludeTables = IncludeTablesInput.Text.Trim();
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
            PreviewText.Text = BuildPreviewText();
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
            IncludeTablesInput.Text = WizardState.IncludeTables;
        }
        else
        {
            SimulatedServerNameInput.Text = WizardState.SimulatedServerName;
            SqlQueryInput.Text = WizardState.SqlQuery;
        }
    }

    private string BuildPreviewText()
    {
        if (WizardState.RuleType == RuleType.GenerateSyntheticData)
        {
            var includeTables = string.IsNullOrWhiteSpace(WizardState.IncludeTables)
                ? "(all tables)"
                : WizardState.IncludeTables;

            return $"""
                    Target: Generate synthetic data into SQL Server

                    Connection string:
                    {WizardState.ConnectionString}

                    Rows per table: {WizardState.RowsPerTable}

                    Include tables:
                    {includeTables}
                    """;
        }

        return $"""
                Target: Make a query running on simulated SQL Server

                Simulated server: {WizardState.SimulatedServerName}

                SQL query:
                {WizardState.SqlQuery}
                """;
    }
}
