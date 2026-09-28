namespace SlideStudio.Models;

public sealed record SlideInfoDto(
    string FileName, string? Vendor, long Width, long Height, int LevelCount,
    IReadOnlyList<SlideLevelDto> Levels, IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<string> AssociatedImages);

