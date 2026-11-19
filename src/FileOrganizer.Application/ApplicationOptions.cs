namespace FileOrganizer.Application;

public sealed record ApplicationOptions
{
    public long LogFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int RetainedLogFiles { get; init; } = 5;

    public void Validate()
    {
        if (LogFileSizeBytes < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(LogFileSizeBytes));
        }

        if (RetainedLogFiles is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(RetainedLogFiles));
        }
    }
}
