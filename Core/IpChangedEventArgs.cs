namespace Lychee.Core;

public class IpChangedEventArgs : EventArgs
{
    public string OldIp { get; }
    public string NewIp { get; }

    public IpChangedEventArgs(string oldIp, string newIp)
    {
        OldIp = oldIp;
        NewIp = newIp;
    }
}
