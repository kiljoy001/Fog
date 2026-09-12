using System.Collections.ObjectModel;
using System.Text;
using LibTab;

namespace NinePSharp.Fog;

/// <summary>Closed, plain-cell LibTab schema for fog control records; never performs file IO.</summary>
public sealed class FogRecordSchema
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string name;
    private readonly string[] columns;
    private readonly string[] required;
    private readonly string[] keys;
    private readonly string header;

    public FogRecordSchema(string name, string[] columns, string[] required, string[] keys)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(keys);
        if (!ValidName(name) || columns.Length == 0 || keys.Length == 0 ||
            columns.Any(column => !ValidName(column)) || columns.Distinct(StringComparer.Ordinal).Count() != columns.Length ||
            required.Concat(keys).Any(column => !columns.Contains(column, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Invalid closed record schema.");
        }

        this.name = name;
        this.columns = (string[])columns.Clone();
        this.required = required.Concat(keys).Append(columns[0]).Distinct(StringComparer.Ordinal).ToArray();
        this.keys = (string[])keys.Clone();
        header = NewTable().Serialize();
    }

    public IReadOnlyList<IReadOnlyDictionary<string, string?>> Parse(ReadOnlySpan<byte> bytes, int maxBytes, int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        if (bytes.Length > maxBytes)
        {
            throw new FogException("limit");
        }

        try
        {
            string text = Utf8.GetString(bytes);
            Preflight(text, maxRows);
            TabTable table = TabTable.Parse("memory", text);
            if (table.Serialize() != text)
            {
                throw new FogException("invalid-request");
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<IReadOnlyDictionary<string, string?>>();
            foreach (TabRow row in table.Rows)
            {
                if (required.Any(column => row[column] is null) || row.Columns.Any(column => row[column] is null) ||
                    string.IsNullOrEmpty(row[columns[0]]))
                {
                    throw new FogException("invalid-request");
                }

                // Length-prefix each semantic component: neither separators nor escaping can alias a tuple.
                string identity = string.Concat(keys.Select(key => $"{row[key]!.Length}:{row[key]}"));
                if (!identities.Add(identity))
                {
                    throw new FogException("invalid-request");
                }

                rows.Add(new ReadOnlyDictionary<string, string?>(columns.ToDictionary(column => column, column => row[column], StringComparer.Ordinal)));
            }

            return rows.AsReadOnly();
        }
        catch (DecoderFallbackException)
        {
            throw new FogException("invalid-request");
        }
        catch (TabException)
        {
            throw new FogException("invalid-request");
        }
    }

    /// <summary>Writes with the real LibTab codec and applies the same closed-schema checks as reads.</summary>
    public byte[] Serialize(IEnumerable<IReadOnlyDictionary<string, string?>> rows, int maxBytes, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        TabTable table = NewTable();
        var output = new StringBuilder(header);
        long outputBytes = Utf8.GetByteCount(header);
        int count = 0;
        try
        {
            foreach (var row in rows)
            {
                if (++count > maxRows)
                {
                    throw new FogException("limit");
                }

                if (row.Keys.Any(key => !columns.Contains(key, StringComparer.Ordinal)) ||
                    !row.TryGetValue(columns[0], out string? head) || string.IsNullOrEmpty(head))
                {
                    throw new FogException("invalid-request");
                }

                // A separate table per row prevents upstream set deduplication before our validation.
                TabRow entry = table.AddRow(columns[0], head);
                foreach (string column in columns.Skip(1))
                {
                    if (row.TryGetValue(column, out string? value) && value is not null)
                    {
                        table.Set(entry, column, value);
                    }
                }

                string encoded = table.Serialize()[header.Length..];
                outputBytes += Utf8.GetByteCount(encoded);
                if (outputBytes > maxBytes)
                {
                    throw new FogException("limit");
                }

                output.Append(encoded);
                table.RemoveRow(entry);
            }

            byte[] result = Utf8.GetBytes(output.ToString());
            _ = Parse(result, maxBytes, maxRows);
            return result;
        }
        catch (TabException)
        {
            throw new FogException("invalid-request");
        }
        catch (ArgumentException)
        {
            throw new FogException("invalid-request");
        }
    }

    private TabTable NewTable() => TabTable.Create("memory", name, columns.Select(column => new TabColumn(column)));

    private void Preflight(string text, int maxRows)
    {
        if (!text.StartsWith(header, StringComparison.Ordinal) || !text.EndsWith("\n\n", StringComparison.Ordinal) ||
            text.Contains('\r') || text.Contains('\0'))
        {
            throw new FogException("invalid-request");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string body = text[header.Length..];
        if (body.Length == 0)
        {
            return;
        }

        if (body.Length < 2)
        {
            throw new FogException("invalid-request");
        }

        string[] blocks = body[..^2].Split("\n\n", StringSplitOptions.None);
        if (blocks.Length > maxRows)
        {
            throw new FogException("limit");
        }

        foreach (string block in blocks)
        {
            if (!seen.Add(block))
            {
                throw new FogException("invalid-request");
            }

            int previous = -1;
            foreach (string line in block.Split('\n'))
            {
                // LibTab's guard budgets a tab and LF, including on the row head.
                string cell = previous < 0 ? line : line.StartsWith('\t') ? line[1..] : string.Empty;
                int equals = cell.IndexOf('=');
                int index = equals < 0 ? -1 : Array.IndexOf(columns, cell[..equals]);
                if (index <= previous)
                {
                    throw new FogException("invalid-request");
                }

                if (Utf8.GetByteCount(cell) + 2 > TabTable.MaxCellLineBytes)
                {
                    throw new FogException("limit");
                }

                previous = index;
            }
        }
    }

    private static bool ValidName(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
