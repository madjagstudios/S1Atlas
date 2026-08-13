using System.Text;

namespace S1Atlas.Extraction.Steam;

internal static class SteamAppManifestParser
{
    public static bool TryParse(
        string content,
        out SteamAppManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var tokenizer = new Tokenizer(content);
        var root = tokenizer.Next();
        if (root.Kind != TokenKind.QuotedString ||
            !string.Equals(root.Value, "AppState", StringComparison.OrdinalIgnoreCase) ||
            tokenizer.Next().Kind != TokenKind.OpenBrace)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = tokenizer.Next();
            if (key.Kind == TokenKind.CloseBrace)
            {
                break;
            }

            if (key.Kind != TokenKind.QuotedString)
            {
                return false;
            }

            var valueOrObject = tokenizer.Next();
            if (valueOrObject.Kind == TokenKind.QuotedString)
            {
                values[key.Value] = valueOrObject.Value;
                continue;
            }

            if (valueOrObject.Kind == TokenKind.OpenBrace)
            {
                if (!SkipObject(tokenizer))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        if (tokenizer.Next().Kind != TokenKind.End ||
            !TryGetRequired(values, "appid", out var appId) ||
            !TryGetRequired(values, "installdir", out var installDirectory) ||
            !TryGetRequired(values, "buildid", out var buildId))
        {
            return false;
        }

        manifest = new SteamAppManifest(appId, installDirectory, buildId);
        return true;
    }

    private static bool SkipObject(Tokenizer tokenizer)
    {
        var depth = 1;
        while (depth > 0)
        {
            var token = tokenizer.Next();
            switch (token.Kind)
            {
                case TokenKind.OpenBrace:
                    depth++;
                    break;
                case TokenKind.CloseBrace:
                    depth--;
                    break;
                case TokenKind.QuotedString:
                    break;
                case TokenKind.End:
                case TokenKind.Invalid:
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out var candidate) &&
            !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private enum TokenKind
    {
        Invalid,
        End,
        QuotedString,
        OpenBrace,
        CloseBrace
    }

    private readonly record struct Token(TokenKind Kind, string Value)
    {
        public static Token Invalid { get; } = new(TokenKind.Invalid, string.Empty);
        public static Token End { get; } = new(TokenKind.End, string.Empty);
        public static Token OpenBrace { get; } = new(TokenKind.OpenBrace, string.Empty);
        public static Token CloseBrace { get; } = new(TokenKind.CloseBrace, string.Empty);
    }

    private sealed class Tokenizer(string content)
    {
        private int _position;

        public Token Next()
        {
            SkipWhitespace();
            if (_position >= content.Length)
            {
                return Token.End;
            }

            return content[_position] switch
            {
                '"' => ReadQuotedString(),
                '{' => ReadSingleCharacter(Token.OpenBrace),
                '}' => ReadSingleCharacter(Token.CloseBrace),
                _ => ReadSingleCharacter(Token.Invalid)
            };
        }

        private Token ReadQuotedString()
        {
            _position++;
            var value = new StringBuilder();

            while (_position < content.Length)
            {
                var character = content[_position++];
                if (character == '"')
                {
                    return new Token(TokenKind.QuotedString, value.ToString());
                }

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (_position >= content.Length)
                {
                    return Token.Invalid;
                }

                var escaped = content[_position++];
                if (escaped is '\\' or '"')
                {
                    value.Append(escaped);
                }
                else
                {
                    value.Append('\\');
                    value.Append(escaped);
                }
            }

            return Token.Invalid;
        }

        private Token ReadSingleCharacter(Token token)
        {
            _position++;
            return token;
        }

        private void SkipWhitespace()
        {
            while (_position < content.Length &&
                   char.IsWhiteSpace(content[_position]))
            {
                _position++;
            }
        }
    }
}
