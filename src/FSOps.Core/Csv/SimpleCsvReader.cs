using System.Text;

namespace FSOps.Core.Csv;

/// <summary>
/// Small hand-rolled RFC4180-style CSV reader: quoted fields, commas and newlines inside
/// quotes, and doubled-quote escaping ("" -> "). Good enough for the OurAirports dataset
/// without pulling in a dependency for it.
/// </summary>
public static class SimpleCsvReader
{
    public static IEnumerable<string[]> Read(TextReader reader)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var sawAnyContentOnLine = false;
        int cur;

        while ((cur = reader.Read()) != -1)
        {
            var c = (char)cur;
            sawAnyContentOnLine = true;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    // swallow - the following \n (or end of stream) ends the record
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    yield return record.ToArray();
                    record.Clear();
                    sawAnyContentOnLine = false;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        // Final record if the file didn't end with a newline.
        if (sawAnyContentOnLine || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record.ToArray();
        }
    }

    /// <summary>Convenience for parsing a single line, mainly used in tests.</summary>
    public static string[] ReadLine(string line)
    {
        using var stringReader = new StringReader(line);
        return Read(stringReader).First();
    }
}
