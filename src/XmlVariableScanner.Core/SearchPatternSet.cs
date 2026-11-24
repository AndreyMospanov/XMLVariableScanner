using System.Text;
using System.Text.RegularExpressions;

namespace XmlVariableScanner.Core;

public sealed class SearchPatternSet
{
    private const string TokenCharacter = @"[\p{L}\p{N}_%.\-]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly Regex _inclusion;
    private readonly IReadOnlyList<Regex> _exclusions;
    private readonly bool _usesNamedVariableGroup;

    public SearchPatternSet(
        string inclusionPattern,
        IEnumerable<string> exclusionPatterns,
        PatternMode mode,
        bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inclusionPattern);

        RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        _inclusion = mode == PatternMode.Simple
            ? CompileSimpleSearch(inclusionPattern.Trim(), options)
            : new Regex(inclusionPattern, options, RegexTimeout);
        _usesNamedVariableGroup = mode == PatternMode.RegularExpression
            && _inclusion.GetGroupNames().Contains("variable", StringComparer.Ordinal);

        _exclusions = exclusionPatterns
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => mode == PatternMode.Simple
                ? CompileSimpleExclusion(pattern.Trim(), options)
                : new Regex(pattern.Trim(), options, RegexTimeout))
            .ToArray();
    }

    public IEnumerable<string> FindMatches(string line, IReadOnlyList<TextSegment> segments)
    {
        foreach (TextSegment segment in segments)
        {
            int segmentEnd = segment.Start + segment.Length;
            Match match = _inclusion.Match(line, segment.Start);

            while (match.Success && match.Index < segmentEnd)
            {
                Group variableGroup = _usesNamedVariableGroup
                    ? match.Groups["variable"]
                    : match;

                if (match.Index + match.Length <= segmentEnd
                    && variableGroup.Success
                    && variableGroup.Length > 0
                    && !IsExcluded(variableGroup.Value))
                {
                    yield return variableGroup.Value;
                }

                int nextStart = match.Length == 0 ? match.Index + 1 : match.Index + match.Length;
                if (nextStart >= segmentEnd)
                {
                    break;
                }

                match = _inclusion.Match(line, nextStart);
            }
        }
    }

    private bool IsExcluded(string variable) => _exclusions.Any(regex => regex.IsMatch(variable));

    private static Regex CompileSimpleSearch(string pattern, RegexOptions options)
    {
        string body = ConvertGlob(pattern);
        return new Regex($"(?<!{TokenCharacter}){body}(?!{TokenCharacter})", options, RegexTimeout);
    }

    private static Regex CompileSimpleExclusion(string pattern, RegexOptions options)
    {
        string body = ConvertGlob(pattern);
        return new Regex($"\\A(?:{body})\\z", options, RegexTimeout);
    }

    private static string ConvertGlob(string pattern)
    {
        StringBuilder result = new(pattern.Length * 2);
        foreach (char character in pattern)
        {
            result.Append(character switch
            {
                '*' => TokenCharacter + "*",
                '?' => TokenCharacter,
                _ => Regex.Escape(character.ToString())
            });
        }

        return result.ToString();
    }
}
