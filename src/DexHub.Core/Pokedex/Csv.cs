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

    /// <summary>
    /// Liest eine (auch sehr große) CSV-Datei Zeile für Zeile, ohne alles in den Speicher zu laden.
    /// Spalten über ihren Namen: <c>row[file.Col("name")]</c>.
    /// </summary>
    public sealed class Reader
    {
        private readonly string _path;
        public string[] Header { get; }

        private Reader(string path, string[] header)
        {
            _path = path;
            Header = header;
        }

        public static Reader Open(string path)
        {
            using var reader = new StreamReader(path);
            return new Reader(path, ReadRow(reader) ?? []);
        }

        /// <summary>Index einer Spalte, -1 wenn es sie nicht gibt.</summary>
        public int Col(string name) => Array.IndexOf(Header, name);

        public IEnumerable<string[]> Rows()
        {
            using var reader = new StreamReader(_path);
            ReadRow(reader); // Kopfzeile überspringen
            while (ReadRow(reader) is { } row)
                if (!(row.Length == 1 && row[0].Length == 0)) yield return row;
        }

        private static string[]? ReadRow(StreamReader reader)
        {
            if (reader.Peek() < 0) return null;
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            while (true)
            {
                int next = reader.Read();
                if (next < 0) break;
                char ch = (char)next;
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                }
                else if (ch == '"') inQuotes = true;
                else if (ch == ',') { fields.Add(field.ToString()); field.Clear(); }
                else if (ch == '\n') break;
                else if (ch != '\r') field.Append(ch);
            }
            fields.Add(field.ToString());
            return fields.ToArray();
        }
    }
}
