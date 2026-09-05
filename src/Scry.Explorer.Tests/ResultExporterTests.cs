// The three download formats. These ran only through the browser suite before the exporter moved out
// of App.razor.cs, so the edge cases below — quoting, escaping, and the names and characters a server
// response can carry that XML cannot — were never covered directly.
[TestFixture]
public class ResultExporterTests
{
    [Test]
    public void CsvWritesAHeaderAndTheRowsInOrder()
    {
        var csv = ResultExporter.Csv(
            ["Name", "Status"],
            [["Aaron", "FullTime"], ["Carol", "Contractor"]]);

        Assert.That(
            csv.ReplaceLineEndings("\n"),
            Is.EqualTo(
                """
                Name,Status
                Aaron,FullTime
                Carol,Contractor

                """));
    }

    // RFC 4180: only a field carrying a comma, a quote, or a newline is quoted.
    [TestCase("plain", "plain")]
    [TestCase("has,comma", "\"has,comma\"")]
    [TestCase("has\"quote", "\"has\"\"quote\"")]
    [TestCase("", "")]
    public void CsvQuotesOnlyWhatRfc4180Requires(string value, string expected)
    {
        var csv = ResultExporter.Csv(["Column"], [[value]]);

        Assert.That(csv.ReplaceLineEndings("\n").Split('\n')[1], Is.EqualTo(expected));
    }

    // A field carrying a newline is quoted and spans two lines of the output, so it is asserted
    // against the whole body rather than against one split line.
    [TestCase("has\nnewline")]
    [TestCase("has\rreturn")]
    public void CsvQuotesAFieldCarryingANewline(string value)
    {
        var csv = ResultExporter.Csv(["Column"], [[value]]);

        Assert.That(csv, Does.StartWith($"Column{Environment.NewLine}\"{value}\""));
    }

    // A cell a spreadsheet would read as a formula is prefixed with an apostrophe, which it takes as
    // "text follows" and does not display. The rows are database content, so a value beginning with
    // '=' is not a curiosity: it is whatever an end user typed into a form. A number keeps its sign —
    // it is a value, and no formula is a number.
    [TestCase("=1+1", "'=1+1")]
    [TestCase("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [TestCase("+cmd", "'+cmd")]
    [TestCase("-cmd", "'-cmd")]
    [TestCase("@SUM(A1)", "'@SUM(A1)")]
    [TestCase("\tx", "'\tx")]
    [TestCase("-5", "-5")]
    [TestCase("-1.5e3", "-1.5e3")]
    [TestCase("+7", "+7")]
    [TestCase("a=b", "a=b")]
    public void CsvNeutralisesAFieldASpreadsheetWouldExecute(string value, string expected)
    {
        var csv = ResultExporter.Csv(["Column"], [[value]]);

        Assert.That(csv.ReplaceLineEndings("\n").Split('\n')[1], Is.EqualTo(expected));
    }

    // A leading carriage return is both a formula trigger and a character that forces quoting.
    [Test]
    public void CsvNeutralisesAndQuotesALeadingReturn()
    {
        var csv = ResultExporter.Csv(["Column"], [["\rx"]]);

        Assert.That(csv, Does.StartWith($"Column{Environment.NewLine}\"'\rx\""));
    }

    [Test]
    public void XmlNestsAProjectedNavigation()
    {
        var xml = ResultExporter.Xml(Rows("""[{"name":"Aaron","department":{"name":"Ops"}}]"""));

        Assert.That(
            xml.ReplaceLineEndings("\n"),
            Is.EqualTo(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <results>
                  <row>
                    <name>Aaron</name>
                    <department>
                      <name>Ops</name>
                    </department>
                  </row>
                </results>
                """));
    }

    [Test]
    public void XmlWritesACollectionAsItemElements()
    {
        var xml = ResultExporter.Xml(Rows("""[{"tags":["a","b"]}]"""));

        Assert.That(xml, Does.Contain("<item>a</item>"));
        Assert.That(xml, Does.Contain("<item>b</item>"));
    }

    // An absent value stays an empty element rather than being dropped, so every row keeps the same
    // shape.
    [Test]
    public void XmlKeepsANullAsAnEmptyElement()
    {
        var xml = ResultExporter.Xml(Rows("""[{"manager":null}]"""));

        Assert.That(xml, Does.Contain("<manager />"));
    }

    [Test]
    public void XmlEscapesTextContent()
    {
        var xml = ResultExporter.Xml(Rows("""[{"name":"a & b < c > d"}]"""));

        Assert.That(xml, Does.Contain("<name>a &amp; b &lt; c &gt; d</name>"));
    }

    // XML 1.0 has no spelling at all for most control characters, so a value carrying one must not be
    // able to produce a document no parser will open.
    [Test]
    public void XmlDropsControlCharactersItCannotSpell()
    {
        // Built rather than written: a literal control character cannot appear inside a JSON
        // string, so the serializer is what puts it there in the escaped form a server would.
        var value = "a" + (char) 0 + "b" + (char) 7 + "c";
        var xml = ResultExporter.Xml(Rows(JsonSerializer.Serialize(new[] { new { name = value } })));

        Assert.That(xml, Does.Contain("<name>abc</name>"));
    }

    [Test]
    public void XmlKeepsTheWhitespaceItCanSpell()
    {
        var xml = ResultExporter.Xml(Rows("""[{"name":"a\tb"}]"""));

        Assert.That(xml, Does.Contain("<name>a\tb</name>"));
    }

    // Member names are the caller's own C# identifiers, but the rows are the server's response.
    [Test]
    public void XmlSanitizesAMemberNameThatIsNotAnXmlName()
    {
        var xml = ResultExporter.Xml(Rows("""[{"1st name":"Aaron"}]"""));

        Assert.That(xml, Does.Contain("<_st_name>Aaron</_st_name>"));
    }

    [Test]
    public void XmlSanitizesAnEmptyMemberName()
    {
        var xml = ResultExporter.Xml(Rows("""[{"":"Aaron"}]"""));

        Assert.That(xml, Does.Contain("<_>Aaron</_>"));
    }

    [Test]
    public void JsonWritesTheRowsAsTheServerSentThem()
    {
        var json = ResultExporter.Json(Rows("""[{"name":"Aaron"}]"""));

        Assert.That(
            json.ReplaceLineEndings("\n"),
            Is.EqualTo(
                """
                [
                  {
                    "name": "Aaron"
                  }
                ]
                """));
    }

    static IReadOnlyList<JsonElement> Rows(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
}
