namespace XmlVariableScanner.Core;

public readonly record struct TextSegment(int Start, int Length);

public sealed class XmlLineSegmenter
{
    private LexicalState _state = LexicalState.Text;

    public IReadOnlyList<TextSegment> GetSearchableSegments(string line, ReadingMode mode)
    {
        ArgumentNullException.ThrowIfNull(line);
        return mode == ReadingMode.FastText
            ? GetCommentFreeSegments(line)
            : GetXmlAwareSegments(line);
    }

    private IReadOnlyList<TextSegment> GetCommentFreeSegments(string line)
    {
        List<TextSegment> segments = [];
        int position = 0;

        while (position < line.Length)
        {
            if (_state == LexicalState.Comment)
            {
                int commentEnd = line.IndexOf("-->", position, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return segments;
                }

                position = commentEnd + 3;
                _state = LexicalState.Text;
                continue;
            }

            int commentStart = line.IndexOf("<!--", position, StringComparison.Ordinal);
            if (commentStart < 0)
            {
                AddSegment(segments, position, line.Length - position);
                break;
            }

            AddSegment(segments, position, commentStart - position);
            position = commentStart + 4;
            _state = LexicalState.Comment;
        }

        return segments;
    }

    private IReadOnlyList<TextSegment> GetXmlAwareSegments(string line)
    {
        List<TextSegment> segments = [];
        int position = 0;

        while (position < line.Length)
        {
            switch (_state)
            {
                case LexicalState.Text:
                {
                    int markupStart = line.IndexOf('<', position);
                    if (markupStart < 0)
                    {
                        AddSegment(segments, position, line.Length - position);
                        position = line.Length;
                        break;
                    }

                    AddSegment(segments, position, markupStart - position);
                    if (line.AsSpan(markupStart).StartsWith("<!--", StringComparison.Ordinal))
                    {
                        _state = LexicalState.Comment;
                        position = markupStart + 4;
                    }
                    else if (line.AsSpan(markupStart).StartsWith("<![CDATA[", StringComparison.Ordinal))
                    {
                        _state = LexicalState.CData;
                        position = markupStart + 9;
                    }
                    else if (line.AsSpan(markupStart).StartsWith("<?", StringComparison.Ordinal))
                    {
                        _state = LexicalState.ProcessingInstruction;
                        position = markupStart + 2;
                    }
                    else if (line.AsSpan(markupStart).StartsWith("<!", StringComparison.Ordinal))
                    {
                        _state = LexicalState.Declaration;
                        position = markupStart + 2;
                    }
                    else
                    {
                        _state = LexicalState.Tag;
                        position = markupStart + 1;
                    }

                    break;
                }

                case LexicalState.Tag:
                case LexicalState.Declaration:
                {
                    int special = FindFirst(line, position, '>', '\'', '"');
                    if (special < 0)
                    {
                        position = line.Length;
                        break;
                    }

                    char marker = line[special];
                    position = special + 1;
                    if (marker == '>')
                    {
                        _state = LexicalState.Text;
                    }
                    else
                    {
                        _state = marker == '"' ? LexicalState.DoubleQuotedAttribute : LexicalState.SingleQuotedAttribute;
                    }

                    break;
                }

                case LexicalState.DoubleQuotedAttribute:
                case LexicalState.SingleQuotedAttribute:
                {
                    char quote = _state == LexicalState.DoubleQuotedAttribute ? '"' : '\'';
                    int quoteEnd = line.IndexOf(quote, position);
                    if (quoteEnd < 0)
                    {
                        AddSegment(segments, position, line.Length - position);
                        position = line.Length;
                        break;
                    }

                    AddSegment(segments, position, quoteEnd - position);
                    position = quoteEnd + 1;
                    _state = LexicalState.Tag;
                    break;
                }

                case LexicalState.Comment:
                {
                    int commentEnd = line.IndexOf("-->", position, StringComparison.Ordinal);
                    if (commentEnd < 0)
                    {
                        position = line.Length;
                        break;
                    }

                    position = commentEnd + 3;
                    _state = LexicalState.Text;
                    break;
                }

                case LexicalState.CData:
                {
                    int cdataEnd = line.IndexOf("]]>", position, StringComparison.Ordinal);
                    if (cdataEnd < 0)
                    {
                        AddSegment(segments, position, line.Length - position);
                        position = line.Length;
                        break;
                    }

                    AddSegment(segments, position, cdataEnd - position);
                    position = cdataEnd + 3;
                    _state = LexicalState.Text;
                    break;
                }

                case LexicalState.ProcessingInstruction:
                {
                    int instructionEnd = line.IndexOf("?>", position, StringComparison.Ordinal);
                    if (instructionEnd < 0)
                    {
                        position = line.Length;
                        break;
                    }

                    position = instructionEnd + 2;
                    _state = LexicalState.Text;
                    break;
                }

                default:
                    throw new InvalidOperationException($"Неизвестное состояние XML: {_state}.");
            }
        }

        return segments;
    }

    private static int FindFirst(string value, int startIndex, params char[] characters)
    {
        for (int index = startIndex; index < value.Length; index++)
        {
            if (characters.Contains(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AddSegment(List<TextSegment> segments, int start, int length)
    {
        if (length > 0)
        {
            segments.Add(new TextSegment(start, length));
        }
    }

    private enum LexicalState
    {
        Text,
        Tag,
        DoubleQuotedAttribute,
        SingleQuotedAttribute,
        Comment,
        CData,
        ProcessingInstruction,
        Declaration
    }
}
