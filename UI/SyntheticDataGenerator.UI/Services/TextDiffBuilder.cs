using SyntheticDataGenerator.UI.Models;

namespace SyntheticDataGenerator.UI.Services;

public static class TextDiffBuilder
{
    public static IReadOnlyList<DiffTextSegment> BuildSingleSide(string text) =>
        [new DiffTextSegment { Text = text, IsDifferent = false }];

    public static (IReadOnlyList<DiffTextSegment> Historical, IReadOnlyList<DiffTextSegment> Current)
        BuildSideBySide(string historical, string current)
    {
        if (string.Equals(historical, current, StringComparison.Ordinal))
        {
            return (
                [new DiffTextSegment { Text = historical, IsDifferent = false }],
                [new DiffTextSegment { Text = current, IsDifferent = false }]);
        }

        var operations = BuildOperations(historical, current);
        return (
            BuildSegments(historical, operations, side: DiffSide.Historical),
            BuildSegments(current, operations, side: DiffSide.Current));
    }

    private enum DiffSide
    {
        Historical,
        Current
    }

    private enum DiffOperationKind
    {
        Equal,
        Delete,
        Insert
    }

    private readonly struct DiffOperation(DiffOperationKind kind, int sourceIndex, int targetIndex, char value)
    {
        public DiffOperationKind Kind { get; } = kind;
        public int SourceIndex { get; } = sourceIndex;
        public int TargetIndex { get; } = targetIndex;
        public char Value { get; } = value;
    }

    private static List<DiffOperation> BuildOperations(string left, string right)
    {
        var lcs = BuildLcsTable(left, right);
        var operations = new List<DiffOperation>();
        var leftIndex = left.Length;
        var rightIndex = right.Length;

        while (leftIndex > 0 || rightIndex > 0)
        {
            if (leftIndex > 0 && rightIndex > 0 && left[leftIndex - 1] == right[rightIndex - 1])
            {
                operations.Add(new DiffOperation(
                    DiffOperationKind.Equal,
                    leftIndex - 1,
                    rightIndex - 1,
                    left[leftIndex - 1]));
                leftIndex--;
                rightIndex--;
                continue;
            }

            if (rightIndex > 0 &&
                (leftIndex == 0 || lcs[leftIndex, rightIndex - 1] >= lcs[leftIndex - 1, rightIndex]))
            {
                operations.Add(new DiffOperation(
                    DiffOperationKind.Insert,
                    leftIndex,
                    rightIndex - 1,
                    right[rightIndex - 1]));
                rightIndex--;
                continue;
            }

            operations.Add(new DiffOperation(
                DiffOperationKind.Delete,
                leftIndex - 1,
                rightIndex,
                left[leftIndex - 1]));
            leftIndex--;
        }

        operations.Reverse();
        return operations;
    }

    private static int[,] BuildLcsTable(string left, string right)
    {
        var table = new int[left.Length + 1, right.Length + 1];

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                table[leftIndex, rightIndex] = left[leftIndex - 1] == right[rightIndex - 1]
                    ? table[leftIndex - 1, rightIndex - 1] + 1
                    : Math.Max(table[leftIndex - 1, rightIndex], table[leftIndex, rightIndex - 1]);
            }
        }

        return table;
    }

    private static List<DiffTextSegment> BuildSegments(
        string text,
        IReadOnlyList<DiffOperation> operations,
        DiffSide side)
    {
        var segments = new List<DiffTextSegment>();
        var builder = new System.Text.StringBuilder();
        var isDifferent = false;

        void Flush()
        {
            if (builder.Length == 0)
                return;

            segments.Add(new DiffTextSegment
            {
                Text = builder.ToString(),
                IsDifferent = isDifferent
            });
            builder.Clear();
        }

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case DiffOperationKind.Equal:
                    if (isDifferent)
                    {
                        Flush();
                        isDifferent = false;
                    }

                    builder.Append(operation.Value);
                    break;

                case DiffOperationKind.Delete when side == DiffSide.Historical:
                    if (!isDifferent)
                    {
                        Flush();
                        isDifferent = true;
                    }

                    builder.Append(operation.Value);
                    break;

                case DiffOperationKind.Insert when side == DiffSide.Current:
                    if (!isDifferent)
                    {
                        Flush();
                        isDifferent = true;
                    }

                    builder.Append(operation.Value);
                    break;
            }
        }

        Flush();

        if (segments.Count == 0)
        {
            segments.Add(new DiffTextSegment
            {
                Text = text,
                IsDifferent = false
            });
        }

        return segments;
    }
}
