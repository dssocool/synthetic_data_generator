using System.Text;
using System.Windows;
using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI;

public partial class ExecutionInsertedKeysDialog : Window
{
    public ExecutionInsertedKeysDialog(RuleExecutionEntry entry)
    {
        InitializeComponent();

        HeaderText.Text = $"Execution {entry.StartedAtDisplay}";
        SummaryText.Text =
            $"{entry.ExecutionModeDisplay}  ·  {entry.InsertedKeysSummary}  ·  {entry.StatusDisplay}";

        KeysText.Text = BuildKeysText(entry.InsertedKeys);
    }

    private static string BuildKeysText(List<TableInsertedKeys>? insertedKeys)
    {
        if (insertedKeys is not { Count: > 0 })
            return "No inserted primary keys recorded for this execution.";

        var sb = new StringBuilder();
        foreach (var table in insertedKeys)
        {
            if (!table.HasPrimaryKey)
            {
                sb.AppendLine($"[{table.TableName}]");
                sb.AppendLine("  This table does not have a primary key.");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"[{table.TableName}] ({table.PrimaryKeys.Count} row{(table.PrimaryKeys.Count == 1 ? "" : "s")})");
            foreach (var pk in table.PrimaryKeys)
                sb.AppendLine($"  {pk}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
