namespace Tern.Remote.Authoring;

/// <summary>Reads just enough of a GraphQL document to tell whether sending it can write.</summary>
public static class GraphQlDocument
{
    /// <summary>
    /// True when the document defines a mutation. Comments, strings and anything inside a
    /// selection set or argument list are skipped, so a field or argument named <c>mutation</c>
    /// doesn't count. A top-level name spelled <c>mutation</c> counts even when it is only an
    /// operation's name, which errs on the side of needing <c>allowWrite</c>.
    /// </summary>
    public static bool HasMutation(string document)
    {
        var depth = 0;
        var i = 0;
        while (i < document.Length)
        {
            var c = document[i];
            if (c == '#')
            {
                while (i < document.Length && document[i] is not ('\n' or '\r'))
                {
                    i++;
                }
            }
            else if (c == '"')
            {
                i = SkipString(document, i);
            }
            else if (c is '{' or '(' or '[')
            {
                depth++;
                i++;
            }
            else if (c is '}' or ')' or ']')
            {
                depth--;
                i++;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < document.Length && (char.IsAsciiLetterOrDigit(document[i]) || document[i] == '_'))
                {
                    i++;
                }
                if (depth == 0 && document.AsSpan(start, i - start).SequenceEqual("mutation"))
                {
                    return true;
                }
            }
            else
            {
                i++;
            }
        }
        return false;
    }

    /// <summary>Returns the index just past the string that starts at <paramref name="start"/>.</summary>
    private static int SkipString(string document, int start)
    {
        if (document.AsSpan(start).StartsWith("\"\"\""))
        {
            var end = document.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
            while (end > 0 && document[end - 1] == '\\')
            {
                end = document.IndexOf("\"\"\"", end + 3, StringComparison.Ordinal);
            }
            return end < 0 ? document.Length : end + 3;
        }
        var i = start + 1;
        while (i < document.Length && document[i] is not ('"' or '\n' or '\r'))
        {
            i += document[i] == '\\' ? 2 : 1;
        }
        return i + 1;
    }
}
