namespace Migration4Dataverse.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int MigrationFailure = 1;
    public const int InvalidArgumentsOrJob = 2;
    public const int Cancelled = 3;
    public const int RuntimeStartupFailure = 4;
}
