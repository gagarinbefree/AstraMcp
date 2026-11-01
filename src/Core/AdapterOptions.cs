namespace AstraMcp.Core;

/// <summary>Defines the local Astra installation, exchange profile and execution limits.</summary>
public sealed class AdapterOptions
{
    /// <summary>Gets or sets the full path to the installed Astra executable.</summary>
    public string ExecutablePath { get; set; } = @"C:\Program Files (x86)\Astra R-Nesting\Astra.exe";
    /// <summary>Gets or sets the directory configured for Astra's exit XML export.</summary>
    public string ExportDirectory { get; set; } = ".data/astra-exchange";
    /// <summary>Gets or sets the shared durable job and artifact directory.</summary>
    public string DataDirectory { get; set; } = ".data";
    /// <summary>Gets or sets the maximum calculation duration in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Gets or sets the worker polling interval in milliseconds.</summary>
    public int PollIntervalMilliseconds { get; set; } = 250;
    /// <summary>Gets or sets whether the required Astra launch and exit profile was configured.</summary>
    public bool ProfileReady { get; set; }

    /// <summary>Resolves relative directories against the configuration file and validates limits.</summary>
    /// <param name="configurationDirectory">Absolute directory containing the configuration file.</param>
    public void Normalize(string configurationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        ExecutablePath = Path.GetFullPath(ExecutablePath, configurationDirectory);
        DataDirectory = Path.GetFullPath(DataDirectory, configurationDirectory);
        ExportDirectory = Path.GetFullPath(ExportDirectory, configurationDirectory);
        if (TimeoutSeconds is < 1 or > 3600) throw new ArgumentException("Astra:TimeoutSeconds must be between 1 and 3600.");
        if (PollIntervalMilliseconds is < 25 or > 5000) throw new ArgumentException("Astra:PollIntervalMilliseconds must be between 25 and 5000.");
        if (!string.Equals(Path.GetExtension(ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Astra:ExecutablePath must identify an .exe file.");
    }
}
