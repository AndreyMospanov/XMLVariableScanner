using System.Text;
using System.Text.RegularExpressions;

namespace XmlVariableScanner.Core;

internal static partial class XmlTextReaderFactory
{
    private const int BufferSize = 64 * 1024;

    static XmlTextReaderFactory()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static StreamReader Open(string path)
    {
        FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);

        try
        {
            Encoding encoding = DetectEncoding(stream);
            stream.Position = 0;
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static Encoding DetectEncoding(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[512];
        int bytesRead = stream.Read(prefix);
        ReadOnlySpan<byte> data = prefix[..bytesRead];

        if (data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        }

        if (data.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
        }

        if (data.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
        }

        if (data.StartsWith(new byte[] { 0xFF, 0xFE }) || LooksLikeUtf16LittleEndian(data))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: data.StartsWith(new byte[] { 0xFF, 0xFE }), throwOnInvalidBytes: true);
        }

        if (data.StartsWith(new byte[] { 0xFE, 0xFF }) || LooksLikeUtf16BigEndian(data))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: data.StartsWith(new byte[] { 0xFE, 0xFF }), throwOnInvalidBytes: true);
        }

        string declaration = Encoding.ASCII.GetString(data);
        Match match = EncodingDeclarationRegex().Match(declaration);
        if (match.Success)
        {
            try
            {
                return Encoding.GetEncoding(
                    match.Groups["encoding"].Value,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                // Неизвестная кодировка будет обработана как UTF-8 и попадёт в журнал при ошибке декодирования.
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    private static bool LooksLikeUtf16LittleEndian(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'<' && data[1] == 0 && data[2] == (byte)'?' && data[3] == 0;

    private static bool LooksLikeUtf16BigEndian(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == (byte)'<' && data[2] == 0 && data[3] == (byte)'?';

    [GeneratedRegex("encoding\\s*=\\s*['\"](?<encoding>[^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodingDeclarationRegex();
}
