namespace FileOrganizer.Persistence;

/// <summary>A startup failure classified without exposing database paths or SQL.</summary>
public sealed class StoreInitializationException(string errorCode)
    : Exception("The application store could not be initialized.")
{
    public string ErrorCode { get; } = errorCode;
}
