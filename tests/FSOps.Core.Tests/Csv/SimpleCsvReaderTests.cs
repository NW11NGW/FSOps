using FSOps.Core.Csv;

namespace FSOps.Core.Tests.Csv;

public class SimpleCsvReaderTests
{
    [Fact]
    public void ReadLine_SplitsPlainCommaSeparatedFields()
    {
        var fields = SimpleCsvReader.ReadLine("6523,00A,heliport,Total RF Heliport");

        Assert.Equal(new[] { "6523", "00A", "heliport", "Total RF Heliport" }, fields);
    }

    [Fact]
    public void ReadLine_HandlesQuotedFieldWithEmbeddedComma()
    {
        var fields = SimpleCsvReader.ReadLine("1,\"Smith, John\",3");

        Assert.Equal(new[] { "1", "Smith, John", "3" }, fields);
    }

    [Fact]
    public void ReadLine_HandlesDoubledQuoteEscaping()
    {
        var fields = SimpleCsvReader.ReadLine("1,\"He said \"\"hi\"\"\",3");

        Assert.Equal(new[] { "1", "He said \"hi\"", "3" }, fields);
    }

    [Fact]
    public void ReadLine_PreservesEmptyFields()
    {
        var fields = SimpleCsvReader.ReadLine("1,,3,");

        Assert.Equal(new[] { "1", "", "3", "" }, fields);
    }

    [Fact]
    public void Read_HandlesMultipleRecordsAcrossLines()
    {
        var text = "a,b,c\n1,2,3\n4,5,6\n";
        using var reader = new StringReader(text);

        var records = SimpleCsvReader.Read(reader).ToList();

        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "a", "b", "c" }, records[0]);
        Assert.Equal(new[] { "1", "2", "3" }, records[1]);
        Assert.Equal(new[] { "4", "5", "6" }, records[2]);
    }

    [Fact]
    public void Read_HandlesQuotedFieldContainingNewline()
    {
        var text = "1,\"line one\nline two\",3\n";
        using var reader = new StringReader(text);

        var records = SimpleCsvReader.Read(reader).ToList();

        var record = Assert.Single(records);
        Assert.Equal("line one\nline two", record[1]);
    }

    [Fact]
    public void Read_HandlesLastRecordWithoutTrailingNewline()
    {
        var text = "a,b\n1,2";
        using var reader = new StringReader(text);

        var records = SimpleCsvReader.Read(reader).ToList();

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }

    [Fact]
    public void Read_HandlesCrLfLineEndings()
    {
        var text = "a,b\r\n1,2\r\n";
        using var reader = new StringReader(text);

        var records = SimpleCsvReader.Read(reader).ToList();

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "1", "2" }, records[1]);
    }
}
