namespace Migration4Dataverse.Engine.Connectors;

internal static class ReadOnlySqlValidator
{
    public static void ValidateSelect(string sql, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var firstToken = SkipTrivia(sql, 0);
        var startsWithSelect = sql.AsSpan(firstToken).StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
        var startsWithWith = sql.AsSpan(firstToken).StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
        var tokenLength = startsWithSelect ? "SELECT".Length : startsWithWith ? "WITH".Length : 0;
        if (tokenLength == 0 ||
            (sql.Length > firstToken + tokenLength &&
             !char.IsWhiteSpace(sql[firstToken + tokenLength]) &&
             (startsWithWith || sql[firstToken + tokenLength] is not ('(' or '/' or '-'))))
        {
            throw new ArgumentException(
                $"{description} must be a read-only SELECT or WITH query.",
                nameof(sql));
        }

        var quote = '\0';
        var bracketedIdentifier = false;
        for (var index = firstToken; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (quote != '\0')
            {
                if (current == quote && next == quote && quote is '\'' or '"' or '`')
                {
                    index++;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (bracketedIdentifier)
            {
                if (current == ']' && next == ']')
                {
                    index++;
                }
                else if (current == ']')
                {
                    bracketedIdentifier = false;
                }
                continue;
            }
            if (current == '-' && next == '-')
            {
                var lineEnd = sql.IndexOfAny(['\r', '\n'], index + 2);
                index = lineEnd < 0 ? sql.Length : lineEnd - 1;
                continue;
            }
            if (current == '/' && next == '*')
            {
                var commentEnd = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    throw new ArgumentException($"{description} contains an unterminated SQL comment.", nameof(sql));
                }
                index = commentEnd + 1;
                continue;
            }
            if (current == '\'')
            {
                quote = '\'';
            }
            else if (current is '"' or '`')
            {
                quote = current;
            }
            else if (current == '[')
            {
                bracketedIdentifier = true;
            }
            else if (current == ';')
            {
                if (SkipTrivia(sql, index + 1) != sql.Length)
                {
                    throw new ArgumentException($"{description} must contain a single SQL statement.", nameof(sql));
                }
                return;
            }
        }

        if (quote != '\0' || bracketedIdentifier)
        {
            throw new ArgumentException($"{description} contains an unterminated SQL quoted value.", nameof(sql));
        }
    }

    private static int SkipTrivia(string sql, int position)
    {
        while (position < sql.Length)
        {
            if (char.IsWhiteSpace(sql[position]))
            {
                position++;
                continue;
            }
            if (position + 1 < sql.Length && sql[position] == '-' && sql[position + 1] == '-')
            {
                var lineEnd = sql.IndexOfAny(['\r', '\n'], position + 2);
                position = lineEnd < 0 ? sql.Length : lineEnd + 1;
                continue;
            }
            if (position + 1 < sql.Length && sql[position] == '/' && sql[position + 1] == '*')
            {
                var commentEnd = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    throw new ArgumentException("SQL contains an unterminated comment.", nameof(sql));
                }
                position = commentEnd + 2;
                continue;
            }
            break;
        }
        return position;
    }
}
