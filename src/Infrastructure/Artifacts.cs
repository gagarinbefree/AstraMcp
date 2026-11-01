using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstraMcp.Core;

namespace AstraMcp.Infrastructure;

/// <summary>Reads native XML encodings and stores job-scoped evidence.</summary>
/// <param name="options">Configured artifact root.</param>
public sealed partial class Artifacts(AdapterOptions options)
{
    /// <summary>Gets the directory derived exclusively from an adapter-generated job identifier.</summary>
    /// <param name="id">Job identifier.</param>
    /// <returns>The absolute job directory.</returns>
    public string DirectoryFor(Guid id) => Path.Combine(options.DataDirectory, "jobs", id.ToString("D"));

    /// <summary>Persists the submitted text and a UTF-8 execution copy.</summary>
    /// <param name="job">Durable source order.</param>
    /// <returns>The generated execution input path.</returns>
    public string WriteInputs(Job job)
    {
        var directory = DirectoryFor(job.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "input.original.xml"), job.SourceXml, EncodingFor(job.SourceXml));
        var path = Path.Combine(directory, "input.astra.xml");
        File.WriteAllText(path, OrderXml.Prepare(job.SourceXml, job.ExecutionName), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Writes a compact manifest without embedding the source XML.</summary>
    /// <param name="job">Current persisted job.</param>
    public void WriteManifest(Job job)
    {
        var directory = DirectoryFor(job.Id);
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            job.Id,
            job.RequestKey,
            job.InputHash,
            job.ExecutionName,
            Status = job.Status.ToString(),
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            job.ProcessId,
            job.ProcessStartedAt,
            job.CancellationRequested,
            job.Error,
            job.Result,
            Configuration = new { options.ExecutablePath, options.ExportDirectory, options.TimeoutSeconds },
            InputHashEncoding = "UTF-8 text received over MCP; saved original XML uses its declared encoding"
        }, new JsonSerializerOptions { WriteIndented = true });
        var temporary = Path.Combine(directory, "manifest." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, Path.Combine(directory, "manifest.json"), true);
    }

    /// <summary>Resolves one of the fixed artifact names, without accepting arbitrary filesystem paths.</summary>
    /// <param name="id">Job identifier.</param>
    /// <param name="kind">One of input, execution_input, result, manifest or log.</param>
    /// <returns>The absolute artifact path.</returns>
    public string PathFor(Guid id, string kind) => Path.Combine(DirectoryFor(id), kind switch
    {
        "input" => "input.original.xml",
        "execution_input" => "input.astra.xml",
        "result" => "result.astra.xml",
        "manifest" => "manifest.json",
        "log" => "execution.log",
        _ => throw new ArgumentException("Unknown artifact kind: " + kind)
    });

    /// <summary>Reads a bounded UTF-8, UTF-16 BOM or Windows-1251 native XML file.</summary>
    /// <param name="path">XML file path.</param>
    /// <returns>The decoded XML text.</returns>
    public static string ReadXml(string path)
    {
        if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException("Export exceeds the 16 MB artifact limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        var text = EncodingFor(prefix).GetString(bytes);
        return text.TrimStart('\ufeff');
    }

    private static Encoding EncodingFor(string xml)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var match = EncodingDeclaration().Match(xml);
        var name = match.Success ? match.Groups[1].Value.ToLowerInvariant() : "utf-8";
        return name switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false, true),
            "windows-1251" => Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new InvalidDataException("Supported XML encodings are UTF-8 and Windows-1251 (or UTF-16 with BOM on native output).")
        };
    }

    [GeneratedRegex("^\\s*<\\?xml[^?]*encoding\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodingDeclaration();
}
