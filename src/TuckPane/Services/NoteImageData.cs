namespace TuckPane.Services;

internal static class NoteImageData
{
    internal static byte[] Decode(string dataUrl)
    {
        if (dataUrl.Length > NoteStore.MaximumHtmlLength) throw new InvalidDataException("Image exceeds note limit.");
        int comma = dataUrl.IndexOf(',');
        string header = comma < 0 ? string.Empty : dataUrl[..comma].ToLowerInvariant();
        if (header is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/jpg;base64" or
            "data:image/gif;base64" or "data:image/webp;base64")) throw new InvalidDataException("Unsupported note image.");
        byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        if (bytes.Length == 0) throw new InvalidDataException("Empty note image.");
        return bytes;
    }
}
