// The three download formats. These ran only through the browser suite before the exporter moved out
// of App.razor.cs, so the edge cases below — quoting, escaping, and the names and characters a server
// response can carry that XML cannot — were never covered directly.
public class ResultExporterTests
{
    [Test]
    public async Task CsvWritesAHeaderAndTheRowsInOrder()
    {
        var csv = ResultExporter.Csv(
            ["Name", "Status"],
            [["Aaron", "FullTime"], ["Carol", "Contractor"]]);

        await Assert.That(csv.ReplaceLineEndings("\n")).IsEqualTo(
                """
                Name,Status
                Aaron,FullTime
                Carol,Contractor

                """);
    }

    // RFC 4180: only a field carrying a comma, a quote, or a newline is quoted.
    [Test]
    [Arguments("plain", "plain")]
    [Arguments("has,comma", "\"has,comma\"")]
    [Arguments("has\"quote", "\"has\"\"quote\"")]
    [Arguments("", "")]
    public async Task CsvQuotesOnlyWhatRfc4180Requires(string value, string expected)
    {
        var csv = ResultExporter.Csv(["Column"], [[value]]);

        await Assert.That(csv.ReplaceLineEndings("\n").Split('\n')[1]).IsEqualTo(expected);
    }

    // A field carrying a newline is quoted and spans two lines of the output, so it is asserted
    // against the whole body rather than against one split line.
    [Test]
    [Arguments("has\nnewline")]
    [Arguments("has\rreturn")]
    public async Task CsvQuotesAFieldCarryingANewline(string value)
    {
        var csv = ResultExporter.Csv(["Column"], [[value]]);

        await Assert.That(csv).StartsWith($"Column{Environment.NewLine}\"{value}\"");
    }

    // A cell a spreadsheet would read as a formula is prefixed with an apostrophe, which it takes as
    // "text follows" and does not display. The rows are database content, so a value beginning with
    // '=' is not a curiosity: it is whatever an end user typed into a form. A number keeps its sign —
    // it is a value, and no formula is a number.
    [Test]
    [Arguments("=1+1", "'=1+1")]
    [Arguments("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [Arguments("+cmd", "'+cmd")]
    [Arguments("-cmd", "'-cmd")]
    [Arguments("@SUM(A1)", "'@SUM(A1)")]
    [Arguments("\tx", "'\tx")]
    [Arguments("-5", "-5")]
    [Arguments("-1.5e3", "-1.5e3")]
    [Arguments("+7", "+7")]
    [Arguments("a=b", "a=b")]
    public async Task CsvNeutralisesAFieldASpreadsheetWouldExecute(string value, string expected)
    {
        var csv = ResultExporter.Csv(["Column"], [[value]]);

        await Assert.That(csv.ReplaceLineEndings("\n").Split('\n')[1]).IsEqualTo(expected);
    }

    // A leading carriage return is both a formula trigger and a character that forces quoting.
    [Test]
    public async Task CsvNeutralisesAndQuotesALeadingReturn()
    {
        var csv = ResultExporter.Csv(["Column"], [["\rx"]]);

        await Assert.That(csv).StartsWith($"Column{Environment.NewLine}\"'\rx\"");
    }

    [Test]
    public async Task XmlNestsAProjectedNavigation()
    {
        var xml = ResultExporter.Xml(Rows("""[{"name":"Aaron","department":{"name":"Ops"}}]"""));

        await Assert.That(xml.ReplaceLineEndings("\n")).IsEqualTo(
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
                """);
    }

    [Test]
    public async Task XmlWritesACollectionAsItemElements()
    {
        var xml = ResultExporter.Xml(Rows("""[{"tags":["a","b"]}]"""));

        await Assert.That(xml).Contains("<item>a</item>");
        await Assert.That(xml).Contains("<item>b</item>");
    }

    // An absent value stays an empty element rather than being dropped, so every row keeps the same
    // shape.
    [Test]
    public async Task XmlKeepsANullAsAnEmptyElement()
    {
        var xml = ResultExporter.Xml(Rows("""[{"manager":null}]"""));

        await Assert.That(xml).Contains("<manager />");
    }

    [Test]
    public async Task XmlEscapesTextContent()
    {
        var xml = ResultExporter.Xml(Rows("""[{"name":"a & b < c > d"}]"""));

        await Assert.That(xml).Contains("<name>a &amp; b &lt; c &gt; d</name>");
    }

    // XML 1.0 has no spelling at all for most control characters, so a value carrying one must not be
    // able to produce a document no parser will open.
    [Test]
    public async Task XmlDropsControlCharactersItCannotSpell()
    {
        // Built rather than written: a literal control character cannot appear inside a JSON
        // string, so the serializer is what puts it there in the escaped form a server would.
        var value = "a" + (char) 0 + "b" + (char) 7 + "c";
        var xml = ResultExporter.Xml(Rows(JsonSerializer.Serialize(new[] { new { name = value } })));

        await Assert.That(xml).Contains("<name>abc</name>");
    }

    [Test]
    public async Task XmlKeepsTheWhitespaceItCanSpell()
    {
        var xml = ResultExporter.Xml(Rows("""[{"name":"a\tb"}]"""));

        await Assert.That(xml).Contains("<name>a\tb</name>");
    }

    // Member names are the caller's own C# identifiers, but the rows are the server's response.
    [Test]
    public async Task XmlSanitizesAMemberNameThatIsNotAnXmlName()
    {
        var xml = ResultExporter.Xml(Rows("""[{"1st name":"Aaron"}]"""));

        await Assert.That(xml).Contains("<_st_name>Aaron</_st_name>");
    }

    [Test]
    public async Task XmlSanitizesAnEmptyMemberName()
    {
        var xml = ResultExporter.Xml(Rows("""[{"":"Aaron"}]"""));

        await Assert.That(xml).Contains("<_>Aaron</_>");
    }

    [Test]
    public async Task JsonWritesTheRowsAsTheServerSentThem()
    {
        var json = ResultExporter.Json(Rows("""[{"name":"Aaron"}]"""));

        await Assert.That(json.ReplaceLineEndings("\n")).IsEqualTo(
                """
                [
                  {
                    "name": "Aaron"
                  }
                ]
                """);
    }

    static IReadOnlyList<JsonElement> Rows(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
}
