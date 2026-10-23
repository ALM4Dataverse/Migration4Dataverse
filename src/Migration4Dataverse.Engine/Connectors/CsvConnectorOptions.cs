using System.Globalization;
using System.Text;
using CsvHelper.Configuration;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class CsvConnectorOptions
{
    public string Delimiter { get; init; } = ",";

    public string Quote { get; init; } = "\"";

    public string Escape { get; init; } = "\"";

    public string Encoding { get; init; } = "utf-8";

    public bool DetectEncodingFromByteOrderMarks { get; init; } = true;

    public bool EmitByteOrderMark { get; init; }

    public bool HasHeaderRecord { get; init; } = true;

    public bool WriteHeaderRecord { get; init; } = true;

    public string[]? Headers { get; init; }

    public string[]? Columns { get; init; }

    public Dictionary<string, string> ColumnTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string[] NullValues { get; init; } = Array.Empty<string>();

    public string NullValue { get; init; } = string.Empty;

    public bool EmptyFieldAsNull { get; init; }

    public string Culture { get; init; } = "InvariantCulture";

    public string NewLine { get; init; } = "\n";

    public bool IgnoreBlankLines { get; init; } = true;

    public bool AllowComments { get; init; }

    public string Comment { get; init; } = "#";

    public bool DetectColumnCountChanges { get; init; } = true;

    public string TrimOptions { get; init; } = "None";

    public bool QuoteAllFields { get; init; }

    public Dictionary<string, string[]>? ColumnsByStep { get; init; }

    public Dictionary<string, string[]>? SourceColumnsByStep { get; init; }

    internal CsvConfiguration CreateConfiguration()
    {
        Validate();
        var configuration = new CsvConfiguration(ResolveCulture(Culture))
        {
            AllowComments = AllowComments,
            Comment = GetSingleCharacter(Comment, nameof(Comment)),
            Delimiter = Delimiter,
            DetectColumnCountChanges = DetectColumnCountChanges,
            Escape = GetSingleCharacter(Escape, nameof(Escape)),
            HasHeaderRecord = HasHeaderRecord,
            IgnoreBlankLines = IgnoreBlankLines,
            NewLine = NewLine,
            Quote = GetSingleCharacter(Quote, nameof(Quote)),
            TrimOptions = Enum.Parse<TrimOptions>(TrimOptions, ignoreCase: true)
        };

        if (QuoteAllFields)
        {
            configuration.ShouldQuote = _ => true;
        }

        return configuration;
    }

    internal Encoding CreateEncoding()
    {
        Validate();
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var normalizedName = Encoding.Trim();
        if (normalizedName.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            return new UTF8Encoding(EmitByteOrderMark, throwOnInvalidBytes: true);
        }
        if (normalizedName.Equals("utf-16", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.Equals("unicode", StringComparison.OrdinalIgnoreCase))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: EmitByteOrderMark, throwOnInvalidBytes: true);
        }
        if (normalizedName.Equals("utf-16be", StringComparison.OrdinalIgnoreCase))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: EmitByteOrderMark, throwOnInvalidBytes: true);
        }
        if (normalizedName.Equals("utf-32", StringComparison.OrdinalIgnoreCase))
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: EmitByteOrderMark, throwOnInvalidCharacters: true);
        }
        if (normalizedName.Equals("utf-32be", StringComparison.OrdinalIgnoreCase))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: EmitByteOrderMark, throwOnInvalidCharacters: true);
        }

        return System.Text.Encoding.GetEncoding(
            normalizedName,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    internal static CultureInfo ResolveCulture(string culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        return culture.Equals("InvariantCulture", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(culture);
    }

    internal static char GetSingleCharacter(string value, string propertyName)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 1)
        {
            throw new ArgumentException($"CSV option '{propertyName}' must contain exactly one character.");
        }

        return value[0];
    }

    internal void Validate(bool requireSourceHeaders = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Delimiter);
        ArgumentException.ThrowIfNullOrWhiteSpace(Encoding);
        if (NewLine is not ("\r" or "\n" or "\r\n"))
        {
            throw new ArgumentException("CSV option 'NewLine' must be LF, CR, or CRLF.", nameof(NewLine));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(TrimOptions);
        _ = GetSingleCharacter(Quote, nameof(Quote));
        _ = GetSingleCharacter(Escape, nameof(Escape));
        _ = GetSingleCharacter(Comment, nameof(Comment));
        _ = ResolveCulture(Culture);
        if (!Enum.TryParse<TrimOptions>(TrimOptions, ignoreCase: true, out _))
        {
            throw new ArgumentException($"CSV trim option '{TrimOptions}' is not supported.");
        }
        if (requireSourceHeaders && !HasHeaderRecord && (Headers is null || Headers.Length == 0))
        {
            throw new ArgumentException("CSV sources without a header row must declare Headers.");
        }
        if (NullValues is null || ColumnTypes is null)
        {
            throw new ArgumentException("CSV NullValues and ColumnTypes options cannot be null.");
        }
        if (Headers is not null)
        {
            ValidateColumns(Headers, nameof(Headers));
        }
        if (Columns is not null)
        {
            ValidateColumns(Columns, nameof(Columns));
        }
        if (ColumnsByStep is not null)
        {
            ValidateColumnsByStep(ColumnsByStep, "ColumnsByStep");
        }
        if (SourceColumnsByStep is not null)
        {
            ValidateColumnsByStep(SourceColumnsByStep, "SourceColumnsByStep");
        }
    }

    internal IReadOnlyList<string> GetSourceColumns(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (SourceColumnsByStep is null ||
            !SourceColumnsByStep.TryGetValue(stepId, out var columns) ||
            columns is null ||
            columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"CSV source options must declare SourceColumnsByStep.{stepId} when converting fixture data.");
        }

        return columns;
    }

    private static void ValidateColumnsByStep(
        IReadOnlyDictionary<string, string[]> columnsByStep,
        string propertyName)
    {
        foreach (var (stepId, columns) in columnsByStep)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
            if (columns is null || columns.Length == 0)
            {
                throw new ArgumentException($"CSV option '{propertyName}.{stepId}' must declare at least one column.");
            }
            ValidateColumns(columns, $"{propertyName}.{stepId}");
        }
    }

    private static void ValidateColumns(IReadOnlyList<string> columns, string propertyName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column);
            if (!names.Add(column))
            {
                throw new ArgumentException($"CSV option '{propertyName}' contains duplicate column '{column}'.");
            }
        }
    }
}
