namespace Lychee.Core;

public class QueryRecoveredEventArgs : EventArgs
{
    public string Ip { get; }
    public string Display { get; }

    public QueryRecoveredEventArgs(string ip, string display)
    {
        Ip = ip;
        Display = display;
    }
}
