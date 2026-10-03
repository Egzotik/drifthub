using System.IO;
using System.Text;

namespace DriftHub.Services;

// Чтение текстовых файлов с автоопределением кодировки:
// BOM -> UTF-8, валидный UTF-8 -> UTF-8, иначе Windows-1251
// (русский Блокнот по умолчанию сохраняет в 1251 — без этого кракозябры).
public static class TextFiles
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Win1251;

    static TextFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Win1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;
    }

    public static string ReadAllText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Win1251.GetString(bytes);
        }
    }
}
