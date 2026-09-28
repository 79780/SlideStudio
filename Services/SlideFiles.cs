namespace SlideStudio.Services;

// Local blob emulator's storage boundary. The decoder never receives these paths.
public sealed class SlideFiles
{
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;
    public string Root { get; }
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".scn", ".svs", ".tif", ".tiff" };

    public SlideFiles(IConfiguration configuration, IWebHostEnvironment environment)
    {
        Root = Path.GetFullPath(configuration["SlidesPath"] ?? Path.Combine(environment.ContentRootPath, "..", "Lab Images"));
        Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Slides must be a normal directory, not a link.");
    }

    public string GetPath(string fileName, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.EndsWith('.') || fileName.EndsWith(' ') ||
            Path.GetFileName(fileName) != fileName)
            throw new SlideApiException(400, "Use a plain file name without directories or invalid characters.");
        var stem = fileName.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            throw new SlideApiException(400, "Reserved Windows file name.");
        if (!Extensions.Contains(Path.GetExtension(fileName)))
            throw new SlideApiException(415, "Allowed extensions: .scn, .svs, .tif, .tiff.");
        var path = Path.GetFullPath(Path.Combine(Root, fileName));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SlideApiException(400, "Invalid file name.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SlideApiException(400, "Linked slide files are not supported.");
        if (mustExist && !File.Exists(path))
            throw new SlideApiException(404, "Slide not found in the configured Lab Images folder.");
        return path;
    }
}


