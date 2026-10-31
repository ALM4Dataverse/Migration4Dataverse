using System.Collections;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class MigrationBinaryValueTests
{
    [Fact]
    public void Base64CanBeEncodedAndDecodedWithoutEmbeddingContentInTransportJson()
    {
        using var directory = new TemporaryDirectory();
        var content = Enumerable.Range(0, 256 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var base64 = Convert.ToBase64String(content);
        var binary = MigrationBinaryValue.FromBase64(
            base64,
            directory.Path,
            "report.bin",
            "application/octet-stream");

        Assert.Equal(content.LongLength, binary.Length);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(), binary.Sha256);
        Assert.Equal(base64, binary.ToBase64String());
        Assert.Equal(content, ReadAllBytes(binary.OpenRead()));

        var encoded = binary.EncodeBase64();
        Assert.Equal("base64", encoded.Representation);
        var decoded = encoded.DecodeBase64();
        Assert.Equal(content, ReadAllBytes(decoded.OpenRead()));

        var transport = MigrationBinaryValue.PrepareForTransport(
            new Dictionary<string, object?> { ["file"] = binary },
            directory.Path);
        var json = JsonSerializer.Serialize(transport);
        Assert.DoesNotContain(base64, json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var restored = Assert.IsAssignableFrom<IDictionary>(
            MigrationBinaryValue.RestoreFromTransport(
                document.RootElement.Clone(),
                directory.Path));
        var restoredValue = Assert.IsType<MigrationBinaryValue>(restored["file"]);
        Assert.Equal(binary.Sha256, restoredValue.Sha256);
    }

    [Fact]
    public void DetectsMimeTypeFromContentBeforeFileExtension()
    {
        using var directory = new TemporaryDirectory();
        var png = MigrationBinaryValue.FromBytes(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            directory.Path,
            "picture.txt");

        Assert.Equal("image/png", png.DetectMimeType());
    }

    [Fact]
    public void JsonSerializationUsesStagedStreamAndCanBeReadBack()
    {
        using var directory = new TemporaryDirectory();
        var value = new TestValue("record-1", [1, 2, 3]);
        var binary = MigrationBinaryValue.SerializeJson(value, directory.Path);

        var restored = Assert.IsType<TestValue>(binary.DeserializeJson(typeof(TestValue)));
        Assert.Equal(value.Name, restored.Name);
        Assert.Equal(value.Values, restored.Values);
        Assert.Equal("application/json", binary.ContentType);
    }

    [Fact]
    public void RejectsInvalidBase64AndReferencesOutsideTheStagingRoot()
    {
        using var directory = new TemporaryDirectory();

        Assert.Throws<FormatException>(() =>
            MigrationBinaryValue.FromBase64("not base64!", directory.Path));

        var externalPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        File.WriteAllText(externalPath, "outside", Encoding.UTF8);
        try
        {
            var descriptor = new Dictionary<string, object?>
            {
                ["__migration4dataverse_binary_v1"] = new Dictionary<string, object?>
                {
                    ["Version"] = 1,
                    ["Path"] = Path.GetRelativePath(directory.Path, externalPath),
                    ["Representation"] = "binary",
                    ["Length"] = 7,
                    ["Sha256"] = new string('0', 64)
                }
            };
            Assert.Throws<InvalidDataException>(() =>
                MigrationBinaryValue.RestoreFromTransport(descriptor, directory.Path));
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    private sealed record TestValue(string Name, int[] Values);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"migration4dataverse-binary-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
