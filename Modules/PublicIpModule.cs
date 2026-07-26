using System.Net.Http;
using System.Text.Json;
using Lychee.Core;

namespace Lychee.Modules;

public sealed class PublicIpModule : InfoModuleBase
{
    public override string Id => "public-ip";
    public override string DisplayName => "Public IP / Location";
    public override string Icon => "\uE774";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.CacheControl =
            new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
        client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        return client;
    }

    private static string WithCacheBuster(string url)
    {
        var sep = url.Contains('?') ? "&" : "?";
        return $"{url}{sep}_={DateTimeOffset.UtcNow.Ticks}";
    }

    private static readonly string[] IpOnlyEndpoints =
    {
        "https://api.ipify.org",
        "https://api.ipify.org?format=text",
        "https://ifconfig.me/ip",
        "https://icanhazip.com"
    };

    private static readonly string GeoApiTemplate =
        "http://ip-api.com/json/{0}?lang=en&fields=status,query,country,regionName,city,isp,org,as&_={1}";

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private string? _lastIp;
    private bool _firstQuery = true;
    private int _consecutiveFailures;

    public TimeSpan QueryInterval { get; set; } = TimeSpan.FromSeconds(20);

    public event EventHandler<IpChangedEventArgs>? IpChanged;

    public PublicIpModule()
    {
        CurrentValue = "Querying...";
    }

    private static string PickCarrier(string isp, string org, string asField)
    {
        var candidates = new[] { isp, org, asField }
            .Where(x => !string.IsNullOrWhiteSpace(x) && x != "N/A")
            .Select(x => x.Trim())
            .Where(x => !LooksLikeMaskOrAddress(x))
            .ToList();
        return candidates.FirstOrDefault() ?? "";
    }

    private static bool LooksLikeMaskOrAddress(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        if (s.Length < 4) return true;
        var lower = s.ToLowerInvariant();
        if (lower.Contains("street") || lower.Contains("jin-rong")) return true;
        if (lower.Contains("no.31") || lower.Contains("no. 31")) return true;
        var commaCount = s.Count(c => c == ',');
        var digitCount = s.Count(char.IsDigit);
        if (commaCount >= 2 && digitCount >= 2) return true;
        if (s.StartsWith("AS") && s.Length <= 8) return true;
        return false;
    }

    public override void Start()
    {
        if (_timer != null) return;
        _firstQuery = true;
        _consecutiveFailures = 0;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(QueryInterval);
        _loopTask = Task.Run(async () =>
        {
            await QueryOnceAsync(_cts.Token);
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    await QueryOnceAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task QueryOnceAsync(CancellationToken ct)
    {
        try
        {
            string? ip = null;
            foreach (var ep in IpOnlyEndpoints)
            {
                try
                {
                    var raw = await HttpClient.GetStringAsync(WithCacheBuster(ep), ct);
                    ip = raw.Trim();
                    if (!string.IsNullOrEmpty(ip)) break;
                }
                catch { /* try next endpoint */ }
            }

            if (string.IsNullOrEmpty(ip))
            {
                throw new HttpRequestException("All endpoints unavailable");
            }

            string display = ip;
            try
            {
                using var resp = await HttpClient.GetAsync(string.Format(GeoApiTemplate, ip, DateTimeOffset.UtcNow.Ticks), ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("status", out var statusProp)
                        && statusProp.GetString() == "success")
                    {
                        var country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                        var region = root.TryGetProperty("regionName", out var r) ? r.GetString() ?? "" : "";
                        var city = root.TryGetProperty("city", out var ci) ? ci.GetString() ?? "" : "";
                        var isp = root.TryGetProperty("isp", out var i) ? i.GetString() ?? "" : "";
                        var org = root.TryGetProperty("org", out var o) ? o.GetString() ?? "" : "";
                        var asField = root.TryGetProperty("as", out var a) ? a.GetString() ?? "" : "";

                        var locationParts = new[] { country, region, city }
                            .Where(x => !string.IsNullOrWhiteSpace(x) && x != "N/A")
                            .Distinct();
                        var location = string.Join(" ", locationParts);

                        var carrier = PickCarrier(isp, org, asField);

                        display = string.IsNullOrEmpty(location) ? ip : $"{ip}\n{location}";
                        if (!string.IsNullOrEmpty(carrier)) display += $"\n{carrier}";
                    }
                }
            }
            catch
            {
                // Geo lookup failure does not affect IP display
            }

            if (!_firstQuery && _lastIp != null && _lastIp != ip)
            {
                IpChanged?.Invoke(this, new IpChangedEventArgs(_lastIp, ip));
            }

            _lastIp = ip;
            _firstQuery = false;
            _consecutiveFailures = 0;
            CurrentValue = display;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _consecutiveFailures++;
            CurrentValue = _consecutiveFailures switch
            {
                1 => "Network error, retrying...",
                _ when _consecutiveFailures <= 3 => $"Query failed (attempt {_consecutiveFailures})",
                _ => "Query failed repeatedly; will retry later"
            };
        }
    }

    public override void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        try { _loopTask?.Wait(500); } catch { }
        _timer = null;
        _cts = null;
        _loopTask = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        HttpClient.Dispose();
    }
}
