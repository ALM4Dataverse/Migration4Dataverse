using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

public static class BinaryFileReferenceSupport
{
    public const string TransportMarker = "__migration4dataverse_sidecar_file_v1";

    public static string[] GetConfiguredBinaryColumns(string optionsJson, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        using var document = JsonDocument.Parse(optionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Options for file connector '{stepId}' must be a JSON object.");
        }

        var binaryColumns = document.RootElement
            .EnumerateObject()
            .FirstOrDefault(property => string.Equals(
                property.Name,
                "BinaryColumns",
                StringComparison.OrdinalIgnoreCase));
        if (binaryColumns.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }
        if (binaryColumns.Value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Options.BinaryColumns for file connector '{stepId}' must be an array of column names.");
        }

        var columns = new List<string>();
        var uniqueColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in binaryColumns.Value.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(column.GetString()) ||
                !uniqueColumns.Add(column.GetString()!))
            {
                throw new InvalidDataException(
                    $"Options.BinaryColumns for file connector '{stepId}' must contain unique, non-empty strings.");
            }
            columns.Add(column.GetString()!);
        }
        return columns.ToArray();
    }

    public static string GetDefaultRelativePath(
        long sequence,
        int recordIndex,
        string columnName,
        MigrationBinaryValue value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(recordIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(value);

        var fileName = GetSafeFileName(value);
        return $"{sequence:D20}-{recordIndex:D8}/{SanitizeSegment(columnName)}/{fileName}";
    }

    public static object CreateTransportReference(
        MigrationBinaryValue value,
        string runId,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var normalizedPath = NormalizeRelativePath(relativePath, nameof(relativePath));
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TransportMarker] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["RelativePath"] = $"{runHash}/{normalizedPath}",
                ["Content"] = value
            }
        };
    }

    public static MigrationBinaryValue NormalizeBinaryValue(object value, string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        var transport = MigrationBinaryValue.PrepareForTransport(value, stagingRoot);
        return MigrationBinaryValue.RestoreFromTransport(transport, stagingRoot) as MigrationBinaryValue
            ?? throw new InvalidDataException(
                $"Value of type '{value.GetType().FullName}' could not be converted to binary content.");
    }

    public static MigrationBinaryValue FromFileReference(
        string reference,
        string sourceFilePath,
        string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        var normalizedReference = NormalizeRelativePath(reference, nameof(reference));
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))
            ?? throw new InvalidOperationException(
                $"Source file '{sourceFilePath}' has no parent directory.");
        var resolvedPath = ResolveRelativePath(sourceDirectory, normalizedReference);
        return MigrationBinaryValue.FromFile(
            resolvedPath,
            stagingRoot,
            Path.GetFileName(resolvedPath));
    }

    public static MigrationBinaryValue FromResolverResult(
        object result,
        string sourceFilePath,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (result is MigrationBinaryValue binaryValue)
        {
            return binaryValue;
        }
        if (result is Stream stream)
        {
            using (stream)
            {
                return MigrationBinaryValue.FromStream(stream, stagingRoot);
            }
        }
        if (result is byte[] bytes)
        {
            return MigrationBinaryValue.FromBytes(bytes, stagingRoot);
        }

        var path = result switch
        {
            FileInfo file => file.FullName,
            string value => Path.IsPathRooted(value)
                ? value
                : Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))!,
                    value),
            _ => throw new ArgumentException(
                "A binary reference resolver must return a MigrationBinaryValue, stream, byte array, FileInfo, or path.",
                nameof(result))
        };
        return MigrationBinaryValue.FromFile(path, stagingRoot);
    }

    public static bool TryReadTransportReference(
        JsonElement value,
        out string relativePath,
        out JsonElement binaryValue)
    {
        relativePath = string.Empty;
        binaryValue = default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != 1 ||
            !string.Equals(properties[0].Name, TransportMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var descriptor = properties[0].Value;
        if (descriptor.ValueKind != JsonValueKind.Object ||
            !descriptor.TryGetProperty("RelativePath", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.String ||
            !descriptor.TryGetProperty("Content", out binaryValue))
        {
            throw new InvalidDataException("A staged binary sidecar reference is malformed.");
        }

        relativePath = NormalizeRelativePath(pathElement.GetString()!, "RelativePath");
        return true;
    }

    public static string ResolveSidecarPath(string sidecarRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarRoot);
        var normalizedPath = NormalizeRelativePath(relativePath, nameof(relativePath));
        return ResolveRelativePath(sidecarRoot, normalizedPath);
    }

    private static string GetSafeFileName(MigrationBinaryValue value)
    {
        var rawFileName = value.FileName?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(rawFileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            fileName = $"attachment{GetExtensionForContentType(value.ContentType ?? value.DetectMimeType())}";
        }
        else if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
        {
            fileName += GetExtensionForContentType(value.ContentType ?? value.DetectMimeType());
        }

        fileName = SanitizeSegment(fileName);
        if (fileName.Length <= 180)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var maxStemLength = Math.Max(1, 180 - extension.Length);
        return $"{stem[..Math.Min(stem.Length, maxStemLength)]}{extension}";
    }

    private static string GetExtensionForContentType(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "application/json" => ".json",
            "application/msword" => ".doc",
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "image/avif" => ".avif",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/svg+xml" => ".svg",
            "image/tiff" => ".tiff",
            "image/webp" => ".webp",
            "text/csv" => ".csv",
            "text/html" => ".html",
            "text/plain" => ".txt",
            "application/zip" => ".zip",
            _ => ".bin"
        };
    }

    private static string SanitizeSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars()
            .Concat("<>:\"/\\|?*".ToCharArray())
            .ToHashSet();
        var sanitized = new string(value.Select(character =>
                char.IsControl(character) || invalidCharacters.Contains(character)
                    ? '_'
                    : character)
            .ToArray())
            .Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "attachment";
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
        if (nameWithoutExtension.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExtension.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExtension.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExtension.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedDeviceName(nameWithoutExtension, "COM") ||
            IsNumberedDeviceName(nameWithoutExtension, "LPT"))
        {
            sanitized = $"_{sanitized}";
        }
        return sanitized;
    }

    private static bool IsNumberedDeviceName(string name, string prefix)
    {
        return name.Length == prefix.Length + 1 &&
               name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               name[^1] is >= '1' and <= '9';
    }

    private static string NormalizeRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            throw new ArgumentException("Binary attachment paths must be relative.", parameterName);
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Binary attachment paths must not contain empty, '.' or '..' path segments.",
                parameterName);
        }
        return string.Join('/', segments);
    }

    private static string ResolveRelativePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new ArgumentException("The binary attachment path escapes its configured root.", nameof(relativePath));
        }
        return fullPath;
    }
}
