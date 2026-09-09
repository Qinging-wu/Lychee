namespace Lychee.Core;

public class QueryFailedEventArgs : EventArgs
{
    public int FailureCount { get; }
    public string Message { get; }

    public QueryFailedEventArgs(int failureCount, string message)
    {
        FailureCount = failureCount;
        Message = message;
    }
}
