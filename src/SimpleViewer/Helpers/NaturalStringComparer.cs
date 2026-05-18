using System.Globalization;
using System.Text;

namespace SimpleViewer.Helpers;

/// <summary>
/// Natural-order string comparer aligned with Python <c>natsort</c> behavior for file names.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var leftParts = Tokenize(Path.GetFileName(x) ?? x);
        var rightParts = Tokenize(Path.GetFileName(y) ?? y);
        var count = Math.Min(leftParts.Count, rightParts.Count);

        for (var i = 0; i < count; i++)
        {
            var left = leftParts[i];
            var right = rightParts[i];
            var leftIsNumber = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumber = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            int result;
            if (leftIsNumber && rightIsNumber)
            {
                result = leftNumber.CompareTo(rightNumber);
            }
            else
            {
                result = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return leftParts.Count.CompareTo(rightParts.Count);
    }

    private static List<string> Tokenize(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                if (current.Length > 0 && !char.IsDigit(current[0]))
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                current.Append(ch);
            }
            else
            {
                if (current.Length > 0 && char.IsDigit(current[0]))
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
