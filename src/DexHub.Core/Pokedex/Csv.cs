using System.Text;

namespace DexHub.Core.Pokedex;

/// <summary>Kleiner CSV-Leser (mit Anführungszeichen-Unterstützung), ohne externe Pakete.</summary>
public static class Csv
{
    public static List<Dictionary<string, string>> Parse(string text)
    {
        var rows = ParseRows(text);
        var result = new List<Dictionary<string, string>>();
        if (rows.Count == 0) return result;

        var header = rows[0];
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 1 && row[0].Length == 0) continue; // Leerzeile
            var dict = new Dictionary<string, string>(header.Count);
            for (int c = 0; c < header.Count; c++)
                dict[header[c]] = c < row.Count ? row[c] : "";
            result.Add(dict);
        }
        return result;
    }

    private static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = [];
            }
            else if (ch != '\r') field.Append(ch);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
