namespace SidebarMonitor.Shared;

/// <summary>Tiny shared CLI-arg helper, so the three entry points don't each carry their own copy.</summary>
public static class ArgParse
{
    /// <summary>Value of a "<c>prefix=NN</c>" flag as an int, or <paramref name="fallback"/> if absent/invalid.</summary>
    public static int Int(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}
