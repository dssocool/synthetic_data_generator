using System.Windows;
using System.Windows.Input;
using SyntheticDataGenerator.UI.Models;
using SyntheticDataGenerator.UI.Services;

namespace SyntheticDataGenerator.UI;

public partial class ExecuteRuleDialog : Window
{
    private readonly SavedRule _rule;
    private readonly SyntheticDataExecutionService _executionService = new();
    private readonly RuleHistoryService _historyService = new();
    private CancellationTokenSource? _cancellationTokenSource;

    public ExecuteRuleDialog(SavedRule rule)
    {
        InitializeComponent();
        _rule = rule;

        RowsPerTableInput.Text = rule.RowsPerTable.ToString();
        SeedInput.Text = rule.Seed.ToString();
    }

    public bool ExecutionCompleted { get; private set; }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cancellationTokenSource is not null)
        {
            _cancellationTokenSource.Cancel();
            return;
        }

        DialogResult = false;
        Close();
    }

    private async void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseInputs(out var rowsPerTable, out var seed))
            return;

        SetBusy(true, "Starting execution...");

        _cancellationTokenSource = new CancellationTokenSource();
        var startedAt = DateTimeOffset.Now;
        var executionId = Guid.NewGuid().ToString("N");

        try
        {
            var progress = new Progress<SyntheticDataExecutionProgress>(UpdateProgress);
            var result = await _executionService.ExecuteAsync(
                _rule,
                rowsPerTable,
                seed,
                progress,
                _cancellationTokenSource.Token);

            var entry = new RuleExecutionEntry
            {
                Id = executionId,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.Now,
                RowsPerTable = rowsPerTable,
                Seed = seed,
                TotalRowsAffected = result.TotalRowsAffected,
                TableCount = result.TableCount,
                Success = result.Success,
                ErrorMessage = result.ErrorMessage,
                ExecutionMode = _rule.EnableDataOverwrite ? "update" : "insert",
                InsertedKeys = _rule.EnableDataOverwrite ? null : result.InsertedKeys
            };

            _historyService.RecordExecution(_rule.Id, entry);

            if (!result.Success)
            {
                MessageBox.Show(this,
                    result.ErrorMessage ?? "Execution failed.",
                    "Execution Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            ExecutionCompleted = true;
            MessageBox.Show(this,
                $"Inserted {result.TotalRowsAffected:N0} rows across {result.TableCount} table(s) in {result.Elapsed.TotalSeconds:F1}s.",
                "Execution Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Execution cancelled.";
            StatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            var entry = new RuleExecutionEntry
            {
                Id = executionId,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.Now,
                RowsPerTable = rowsPerTable,
                Seed = seed,
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionMode = _rule.EnableDataOverwrite ? "update" : "insert"
            };
            _historyService.RecordExecution(_rule.Id, entry);

            MessageBox.Show(this, ex.Message, "Execution Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            SetBusy(false, null);
        }
    }

    private void UpdateProgress(SyntheticDataExecutionProgress progress)
    {
        if (progress.TotalTables > 0 && progress.CompletedTables > 0)
        {
            var prefix = progress.TableSuccess == false ? "Failed" : "Completed";
            StatusText.Text = $"{prefix}: {progress.Message} ({progress.CompletedTables}/{progress.TotalTables})";
            if (progress.TableStatus is not null)
                StatusText.Text += $" — {progress.TableStatus}";
        }
        else
        {
            StatusText.Text = progress.Message;
        }

        StatusText.Visibility = Visibility.Visible;
    }

    private bool TryParseInputs(out int rowsPerTable, out int seed)
    {
        rowsPerTable = 0;
        seed = 0;

        if (!int.TryParse(RowsPerTableInput.Text.Trim(), out rowsPerTable) || rowsPerTable < 1)
        {
            MessageBox.Show(this, "Enter a valid rows per table value (1 or greater).",
                "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            RowsPerTableInput.Focus();
            return false;
        }

        if (!int.TryParse(SeedInput.Text.Trim(), out seed))
        {
            MessageBox.Show(this, "Enter a valid seed number.",
                "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            SeedInput.Focus();
            return false;
        }

        return true;
    }

    private void SetBusy(bool busy, string? status)
    {
        ExecuteButton.IsEnabled = !busy;
        RowsPerTableInput.IsEnabled = !busy;
        SeedInput.IsEnabled = !busy;
        CancelButton.Content = busy ? "Cancel Run" : "Cancel";
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;

        if (status is not null)
        {
            StatusText.Text = status;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
