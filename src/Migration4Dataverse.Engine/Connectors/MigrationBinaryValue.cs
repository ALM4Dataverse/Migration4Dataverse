using System.Collections;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class MigrationBinaryValue
{
    private const string TransportMarker = "__migration4dataverse_binary_v1";
    private readonly string _artifactRoot;
    private readonly string _artifactPath;

    private MigrationBinaryValue(
        string artifactRoot,
        string artifactPath,
        string? fileName,
        string? contentType,
        string representation,
        long? expectedLength = null,
        string? expectedSha256 = null)
    {
        _artifactRoot = Path.GetFullPath(artifactRoot);
        _artifactPath = ValidateArtifactPath(_artifactRoot, artifactPath);
        FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType;
        Representation = representation;
        Length = new FileInfo(_artifactPath).Length;
        if (expectedLength.HasValue && expectedLength.Value != Length)
        {
            throw new InvalidDataException("A binary artifact's length does not match its stored reference.");
        }
        Sha256 = expectedSha256 ?? ComputeSha256(_artifactPath);
    }

    public string? FileName { get; }

    public string? ContentType { get; }

    public string Representation { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public string StagingRoot => _artifactRoot;

    public static string GetRunStagingRoot(string? migrationDatabasePath, string? runId)
    {
        var runDirectory = string.IsNullOrWhiteSpace(runId)
            ? "adhoc"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(migrationDatabasePath))
        {
            var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(migrationDatabasePath));
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
            {
                return Path.Combine(databaseDirectory, ".migration-binary-staging", runDirectory);
            }
        }
        return Path.Combine(Path.GetTempPath(), "migration4dataverse-binary", runDirectory);
    }

    public static void DeleteRunStagingRoot(string? migrationDatabasePath, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var stagingRoot = GetRunStagingRoot(migrationDatabasePath, runId);
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }

    public static MigrationBinaryValue FromFile(
        string filePath,
        string stagingRoot,
        string? fileName = null,
        string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        var sourcePath = Path.GetFullPath(filePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The binary source file does not exist.", sourcePath);
        }

        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        return FromStream(
            source,
            stagingRoot,
            fileName ?? Path.GetFileName(sourcePath),
            contentType);
    }

    public static MigrationBinaryValue FromStagedFile(
        string filePath,
        string stagingRoot,
        string? fileName = null,
        string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        return new MigrationBinaryValue(
            stagingRoot,
            filePath,
            fileName ?? Path.GetFileName(filePath),
            contentType,
            "binary");
    }

    public static MigrationBinaryValue FromBytes(
        byte[] content,
        string stagingRoot,
        string? fileName = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var source = new MemoryStream(content, writable: false);
        return FromStream(source, stagingRoot, fileName, contentType);
    }

    public static MigrationBinaryValue FromStream(
        Stream content,
        string stagingRoot,
        string? fileName = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!content.CanRead)
        {
            throw new ArgumentException("The binary content stream must be readable.", nameof(content));
        }

        using var writer = CreateWriter(stagingRoot, fileName, contentType);
        content.CopyTo(writer.Stream);
        return writer.Complete();
    }

    public static MigrationBinaryValue FromBase64(
        string content,
        string stagingRoot,
        string? fileName = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        using var writer = CreateWriter(stagingRoot, fileName, contentType);
        DecodeBase64(content, writer.Stream);
        return writer.Complete();
    }

    public static MigrationBinaryValue SerializeJson(
        object? value,
        string stagingRoot,
        JsonSerializerOptions? options = null,
        string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        using var writer = CreateWriter(
            stagingRoot,
            fileName ?? "value.json",
            "application/json");
        JsonSerializer.Serialize(writer.Stream, value, options);
        return writer.Complete();
    }

    public object? DeserializeJson(Type resultType, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resultType);
        using var source = OpenRead();
        return JsonSerializer.Deserialize(source, resultType, options);
    }

    public Stream OpenRead()
    {
        return new FileStream(
            _artifactPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public Stream OpenBase64Read()
    {
        if (Representation == "base64")
        {
            return OpenRead();
        }
        return new CryptoStream(
            OpenRead(),
            new ToBase64Transform(),
            CryptoStreamMode.Read);
    }

    public MigrationBinaryValue EncodeBase64()
    {
        using var writer = CreateWriter(
            _artifactRoot,
            FileName is null ? null : $"{FileName}.base64",
            ContentType);
        using (var encoder = new CryptoStream(
                   writer.Stream,
                   new ToBase64Transform(),
                   CryptoStreamMode.Write,
                   leaveOpen: true))
        using (var source = OpenRead())
        {
            source.CopyTo(encoder);
            encoder.FlushFinalBlock();
        }

        return writer.Complete("base64");
    }

    public MigrationBinaryValue DecodeBase64()
    {
        if (Representation != "base64")
        {
            throw new InvalidOperationException("Only a Base64-encoded binary value can be decoded.");
        }
        using var writer = CreateWriter(
            _artifactRoot,
            FileName is null ? null : Path.GetFileNameWithoutExtension(FileName),
            ContentType);
        using var source = OpenRead();
        using var decoder = new CryptoStream(
            source,
            new FromBase64Transform(FromBase64TransformMode.IgnoreWhiteSpaces),
            CryptoStreamMode.Read,
            leaveOpen: true);
        decoder.CopyTo(writer.Stream);
        return writer.Complete();
    }

    public string ToBase64String()
    {
        if (Representation == "base64")
        {
            using var base64Reader = OpenTextReader(Encoding.ASCII);
            return base64Reader.ReadToEnd();
        }
        using var encoded = OpenBase64Read();
        using var reader = new StreamReader(encoded, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    public StreamReader OpenTextReader(Encoding? encoding = null)
    {
        return new StreamReader(OpenRead(), encoding ?? new UTF8Encoding(false, true));
    }

    public string DetectMimeType()
    {
        Span<byte> header = stackalloc byte[16];
        using (var stream = OpenRead())
        {
            var read = 0;
            while (read < header.Length)
            {
                var count = stream.Read(header[read..]);
                if (count == 0)
                {
                    break;
                }
                read += count;
            }
            var detected = DetectMimeType(header[..read]);
            if (detected is not null)
            {
                return detected;
            }
        }

        return Path.GetExtension(FileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".csv" => "text/csv",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".gif" => "image/gif",
            ".html" or ".htm" => "text/html",
            ".jpeg" or ".jpg" => "image/jpeg",
            ".json" => "application/json",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".rtf" => "application/rtf",
            ".svg" => "image/svg+xml",
            ".tif" or ".tiff" => "image/tiff",
            ".txt" => "text/plain",
            ".webp" => "image/webp",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    public MigrationBinaryValueWriter CreateWriter(
        string? fileName = null,
        string? contentType = null)
    {
        return CreateWriter(_artifactRoot, fileName ?? FileName, contentType ?? ContentType);
    }

    public static MigrationBinaryValueWriter CreateWriter(
        string stagingRoot,
        string? fileName = null,
        string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        var artifactPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.bin");
        var temporaryPath = $"{artifactPath}.tmp";
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new MigrationBinaryValueWriter(
            Path.GetFullPath(stagingRoot),
            artifactPath,
            temporaryPath,
            fileName,
            contentType,
            stream);
    }

    public static object? PrepareForTransport(object? value, string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        return ConvertForTransport(value, stagingRoot, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    public static bool ContainsBinaryValues(object? value)
    {
        return FindBinaryValue(
            value,
            new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    public static object? RestoreFromTransport(object? value, string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        return ConvertFromTransport(value, Path.GetFullPath(stagingRoot));
    }

    internal static MigrationBinaryValue FromTransport(
        string artifactRoot,
        string relativePath,
        string? fileName,
        string? contentType,
        string representation,
        long? expectedLength = null,
        string? expectedSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullRoot = Path.GetFullPath(artifactRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        return new MigrationBinaryValue(
            fullRoot,
            fullPath,
            fileName,
            contentType,
            representation,
            expectedLength,
            expectedSha256);
    }

    private static object? ConvertForTransport(
        object? value,
        string stagingRoot,
        HashSet<object> visited)
    {
        if (value is null or string || value.GetType().IsPrimitive || value is decimal or Guid or DateTime or DateTimeOffset)
        {
            return value;
        }
        if (value is MigrationBinaryValue binaryValue)
        {
            return binaryValue.ToTransportValue(stagingRoot);
        }
        if (value is byte[] bytes)
        {
            return FromBytes(bytes, stagingRoot).ToTransportValue(stagingRoot);
        }
        if (value is Stream stream)
        {
            return FromStream(stream, stagingRoot).ToTransportValue(stagingRoot);
        }

        if (value is PSObject wrapper && wrapper.BaseObject is not PSCustomObject)
        {
            return ConvertForTransport(wrapper.BaseObject, stagingRoot, visited);
        }
        var instance = value;
        if (instance is IDictionary dictionary)
        {
            if (!visited.Add(instance))
            {
                throw new InvalidDataException("A binary-bearing record contains a reference cycle.");
            }
            try
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                    {
                        throw new InvalidDataException("Record dictionaries containing binary values must use string keys.");
                    }
                    result.Add(key, ConvertForTransport(entry.Value, stagingRoot, visited));
                }
                return result;
            }
            finally
            {
                visited.Remove(instance);
            }
        }
        if (value is PSObject record)
        {
            if (!visited.Add(record))
            {
                throw new InvalidDataException("A binary-bearing record contains a reference cycle.");
            }
            try
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in record.Properties)
                {
                    if (property.IsGettable && property.MemberType != PSMemberTypes.Method)
                    {
                        result[property.Name] = ConvertForTransport(property.Value, stagingRoot, visited);
                    }
                }
                return result;
            }
            finally
            {
                visited.Remove(record);
            }
        }
        if (value is IEnumerable values)
        {
            if (!visited.Add(value))
            {
                throw new InvalidDataException("A binary-bearing record contains a reference cycle.");
            }
            try
            {
                return values.Cast<object?>()
                    .Select(item => ConvertForTransport(item, stagingRoot, visited))
                    .ToArray();
            }
            finally
            {
                visited.Remove(value);
            }
        }
        return value;
    }

    private static bool FindBinaryValue(object? value, HashSet<object> visited)
    {
        if (value is null or string)
        {
            return false;
        }
        if (value is MigrationBinaryValue or byte[] or Stream)
        {
            return true;
        }
        if (value.GetType().IsPrimitive || value is decimal or Guid or DateTime or DateTimeOffset)
        {
            return false;
        }
        if (value is PSObject wrapper && wrapper.BaseObject is not PSCustomObject)
        {
            return FindBinaryValue(wrapper.BaseObject, visited);
        }

        var instance = value;
        if (!visited.Add(instance))
        {
            return false;
        }
        try
        {
            if (instance is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (FindBinaryValue(entry.Value, visited))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (value is PSObject record)
            {
                foreach (var property in record.Properties)
                {
                    if (property.IsGettable &&
                        property.MemberType != PSMemberTypes.Method &&
                        FindBinaryValue(property.Value, visited))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (value is IEnumerable values)
            {
                foreach (var item in values)
                {
                    if (FindBinaryValue(item, visited))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            visited.Remove(instance);
        }
    }

    private static object? ConvertFromTransport(object? value, string stagingRoot)
    {
        if (value is JsonElement jsonElement)
        {
            value = JsonElementToObject(jsonElement);
        }
        if (value is IDictionary dictionary)
        {
            if (TryReadTransportValue(dictionary, stagingRoot, out var restored))
            {
                return restored;
            }
            var result = new Hashtable(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key)
                {
                    result[key] = ConvertFromTransport(entry.Value, stagingRoot);
                }
            }
            return result;
        }
        if (value is IEnumerable values and not string and not byte[])
        {
            return values.Cast<object?>()
                .Select(item => ConvertFromTransport(item, stagingRoot))
                .ToArray();
        }
        return value;
    }

    private static bool TryReadTransportValue(
        IDictionary dictionary,
        string stagingRoot,
        out MigrationBinaryValue value)
    {
        value = null!;
        if (dictionary.Count != 1 ||
            !dictionary.Contains(TransportMarker) ||
            dictionary[TransportMarker] is not IDictionary descriptor ||
            !TryGetLong(descriptor, "Version", out var version) ||
            version != 1 ||
            descriptor["Path"] is not string path ||
            descriptor["Representation"] is not string representation ||
            !TryGetLong(descriptor, "Length", out var length) ||
            descriptor["Sha256"] is not string sha256 ||
            sha256.Length != 64)
        {
            return false;
        }

        var fileName = descriptor["FileName"] as string;
        var contentType = descriptor["ContentType"] as string;
        value = FromTransport(
            stagingRoot,
            path,
            fileName,
            contentType,
            representation,
            length,
            sha256);
        return true;
    }

    private static bool TryGetLong(IDictionary values, string key, out long result)
    {
        try
        {
            result = Convert.ToInt64(values[key], System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private object ToTransportValue(string stagingRoot)
    {
        var root = Path.GetFullPath(stagingRoot);
        var relativePath = Path.GetRelativePath(root, _artifactPath);
        _ = ValidateArtifactPath(root, _artifactPath);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TransportMarker] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Version"] = 1,
                ["Path"] = relativePath,
                ["FileName"] = FileName,
                ["ContentType"] = ContentType,
                ["Representation"] = Representation,
                ["Length"] = Length,
                ["Sha256"] = Sha256
            }
        };
    }

    private static object? JsonElementToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(property => property.Name, property => JsonElementToObject(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToObject).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException($"Unsupported JSON value kind '{value.ValueKind}'.")
        };
    }

    private static string ValidateArtifactPath(string stagingRoot, string artifactPath)
    {
        var fullPath = Path.GetFullPath(artifactPath);
        var relativePath = Path.GetRelativePath(stagingRoot, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A binary artifact reference points outside its staging directory.");
        }
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("A referenced binary artifact is missing.", fullPath);
        }
        return fullPath;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DecodeBase64(string base64, Stream destination)
    {
        Span<char> quartet = stackalloc char[4];
        Span<byte> decoded = stackalloc byte[3];
        var count = 0;
        var finished = false;
        foreach (var character in base64)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }
            if (finished)
            {
                throw new FormatException("The Base64 value contains data after padding.");
            }

            quartet[count++] = character;
            if (count != quartet.Length)
            {
                continue;
            }

            if (!Convert.TryFromBase64Chars(quartet, decoded, out var bytesWritten))
            {
                throw new FormatException("The value is not valid Base64.");
            }
            destination.Write(decoded[..bytesWritten]);
            finished = quartet[2] == '=' || quartet[3] == '=';
            count = 0;
        }
        if (count != 0)
        {
            throw new FormatException("The Base64 value has an incomplete final block.");
        }
    }

    private static string? DetectMimeType(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }
        if (header.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }
        if (header.StartsWith("BM"u8))
        {
            return "image/bmp";
        }
        if (header.Length >= 12 &&
            header[..4].SequenceEqual("RIFF"u8) &&
            header[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        if (header.Length >= 8 &&
            (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8)))
        {
            return "image/tiff";
        }
        if (header.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) ||
            header.StartsWith(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) ||
            header.StartsWith(new byte[] { 0x50, 0x4B, 0x07, 0x08 }))
        {
            return "application/zip";
        }
        if (header.StartsWith(new byte[] { 0x1F, 0x8B }))
        {
            return "application/gzip";
        }
        if (header.StartsWith("ID3"u8))
        {
            return "audio/mpeg";
        }
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
        {
            return "video/mp4";
        }
        return null;
    }
}

public sealed class MigrationBinaryValueWriter : IDisposable
{
    private readonly string _artifactRoot;
    private readonly string _artifactPath;
    private readonly string _temporaryPath;
    private readonly string? _fileName;
    private readonly string? _contentType;
    private FileStream? _stream;
    private bool _completed;

    internal MigrationBinaryValueWriter(
        string artifactRoot,
        string artifactPath,
        string temporaryPath,
        string? fileName,
        string? contentType,
        FileStream stream)
    {
        _artifactRoot = artifactRoot;
        _artifactPath = artifactPath;
        _temporaryPath = temporaryPath;
        _fileName = fileName;
        _contentType = contentType;
        _stream = stream;
    }

    public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(MigrationBinaryValueWriter));

    public MigrationBinaryValue Complete(string representation = "binary")
    {
        if (_completed)
        {
            throw new InvalidOperationException("The binary artifact writer has already been completed.");
        }
        var stream = _stream ?? throw new ObjectDisposedException(nameof(MigrationBinaryValueWriter));
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        _stream = null;
        File.Move(_temporaryPath, _artifactPath);
        _completed = true;
        return MigrationBinaryValue.FromTransport(
            _artifactRoot,
            Path.GetRelativePath(_artifactRoot, _artifactPath),
            _fileName,
            _contentType,
            representation);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (!_completed && File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }
    }
}
