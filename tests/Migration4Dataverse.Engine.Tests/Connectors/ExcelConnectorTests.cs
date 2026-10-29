using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class ExcelConnectorTests
{
    [Fact]
    public async Task ReadsXlsxRowsAndSortsByKey()
    {
        using var files = new TemporaryDirectory();
        var path = files.GetPath("records.xlsx");
        CreateWorkbook(path, "Contacts", """
            <row r="1">
              <c r="A1" t="inlineStr"><is><t>id</t></is></c>
              <c r="B1" t="inlineStr"><is><t>name</t></is></c>
              <c r="C1" t="inlineStr"><is><t>amount</t></is></c>
            </row>
            <row r="2">
              <c r="A2"><v>2</v></c>
              <c r="B2" t="inlineStr"><is><t>Two</t></is></c>
              <c r="C2"><v>12.5</v></c>
            </row>
            <row r="3">
              <c r="A3"><v>1</v></c>
              <c r="B3" t="inlineStr"><is><t>One</t></is></c>
              <c r="C3"><v>7</v></c>
            </row>
            """);
        var level = new HierarchySourceLevel(
            "Contacts",
            "excel",
            path,
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0);
        await using var reader = new ExcelHierarchyRecordReader(
            level,
            new ExcelSourceOptions { Sheet = "Contacts" });

        var records = new List<JsonElement>();
        while (await reader.PeekAsync(CancellationToken.None) is JsonElement record)
        {
            records.Add(record.Clone());
            reader.Take();
        }

        Assert.Equal(2, records.Count);
        Assert.Equal(1, records[0].GetProperty("id").GetInt32());
        Assert.Equal("One", records[0].GetProperty("name").GetString());
        Assert.Equal(7, records[0].GetProperty("amount").GetDouble());
        Assert.Equal(2, records[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ReadsConfiguredColumnsAndTypesWithoutHeader()
    {
        using var files = new TemporaryDirectory();
        var path = files.GetPath("records.xlsx");
        CreateWorkbook(path, "Contacts", """
            <row r="1">
              <c r="A1"><v>1</v></c>
              <c r="B1" t="inlineStr"><is><t>One</t></is></c>
            </row>
            <row r="2">
              <c r="A2"><v>2</v></c>
              <c r="B2" t="inlineStr"><is><t>Two</t></is></c>
            </row>
            """);
        var level = new HierarchySourceLevel(
            "Contacts",
            "excel",
            path,
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0);
        await using var reader = new ExcelHierarchyRecordReader(
            level,
            new ExcelSourceOptions
            {
                HasHeaderRecord = false,
                Columns = ["id", "name"],
                ColumnTypes = new Dictionary<string, string> { ["id"] = "int" }
            });

        var first = await reader.PeekAsync(CancellationToken.None);

        Assert.True(first.HasValue);
        Assert.Equal(1, first.Value.GetProperty("id").GetInt32());
        Assert.Equal("One", first.Value.GetProperty("name").GetString());
    }

    internal static void CreateWorkbook(string path, string sheetName, string rows)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Add(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Add(archive, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Add(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        Add(archive, "xl/worksheets/sheet1.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>{rows}</sheetData>
            </worksheet>
            """);
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string GetPath(string name)
        {
            return Path.Combine(_path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
