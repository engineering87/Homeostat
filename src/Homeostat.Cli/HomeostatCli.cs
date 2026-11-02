using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Homeostat.Cli;

/// <summary>The <c>homeostat</c> command: a client of the status, control and alert endpoints.</summary>
internal static class HomeostatCli
{
    internal const int Ok = 0;
    internal const int Failed = 1;
    internal const int ActiveConditions = 3;
    internal const int LowCoverage = 4;
    internal const int TooManyHeals = 5;

    public static async Task<int> InvokeAsync(string[] args, TextWriter output, TextWriter error, HttpMessageHandler? handler = null)
    {
        var url = new Option<string?>("--url", "-u")
        {
            Description = "Base address of the application, for example https://weather-api.internal (default: HOMEOSTAT_URL).",
            Recursive = true,
        };
        var prefix = new Option<string>("--prefix")
        {
            Description = "Route prefix of the Homeostat endpoints.",
            DefaultValueFactory = _ => "/homeostat",
            Recursive = true,
        };
        var token = new Option<string?>("--token", "-t")
        {
            Description = "Bearer token sent with every request (default: HOMEOSTAT_TOKEN).",
            Recursive = true,
        };

        // A named command rather than RootCommand, so help shows the tool's command name, not the assembly name.
        var root = new Command("homeostat", "Operate Homeostat in a running application.")
        {
            new System.CommandLine.Help.HelpOption(),
            new VersionOption(),
        };
        root.Options.Add(url);
        root.Options.Add(prefix);
        root.Options.Add(token);

        Client Connect(ParseResult result) => new(
            result.GetValue(url) ?? Environment.GetEnvironmentVariable("HOMEOSTAT_URL"),
            result.GetValue(prefix)!,
            result.GetValue(token) ?? Environment.GetEnvironmentVariable("HOMEOSTAT_TOKEN"),
            handler);

        // status
        var json = new Option<bool>("--json") { Description = "Print the raw JSON." };
        var failOnActive = new Option<bool>("--fail-on-active") { Description = $"Exit with code {ActiveConditions} when a condition is active (for scripts and pipelines)." };
        var status = new Command("status", "Show conditions, approvals, switches, budget and recent runs.") { json, failOnActive };
        status.SetAction((result, ct) => Run(error, async () =>
        {
            using var client = Connect(result);
            var body = await client.GetAsync("status", ct);
            using var document = JsonDocument.Parse(body);
            if (result.GetValue(json))
            {
                await output.WriteLineAsync(body);
            }
            else
            {
                await output.WriteAsync(StatusPrinter.Print(document.RootElement));
            }

            var active = document.RootElement.GetProperty("conditions").EnumerateArray()
                .Count(c => c.GetProperty("status").GetString() == "Active");
            return result.GetValue(failOnActive) && active > 0 ? ActiveConditions : Ok;
        }));
        root.Subcommands.Add(status);

        // coverage
        var last = new Option<string>("--last") { Description = "Period to report: 30m, 4h, 1d or hh:mm:ss.", HelpName = "duration", DefaultValueFactory = _ => "1d" };
        var coverageJson = new Option<bool>("--json") { Description = "Print the raw JSON." };
        var failBelow = new Option<double?>("--fail-below")
        {
            Description = $"Exit with code {LowCoverage} when the share of plan steps that ran is below this value, 0 to 1 (for game days in pipelines).",
            HelpName = "share",
        };
        failBelow.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<double?>() is { } v && (double.IsNaN(v) || v < 0 || v > 1))
            {
                r.AddError("--fail-below must be between 0 and 1, for example 0.8.");
            }
        });
        var coverage = new Command("coverage", "Show which conditions activated, which plans ran and which steps never ran.") { last, coverageJson, failBelow };
        coverage.SetAction((result, ct) => Run(error, async () =>
        {
            using var client = Connect(result);
            var body = await client.GetAsync($"coverage?last={Uri.EscapeDataString(Durations.ToHms(result.GetValue(last)!, TimeSpan.FromDays(365)))}", ct);
            using var document = JsonDocument.Parse(body);
            await output.WriteAsync(result.GetValue(coverageJson) ? body + Environment.NewLine : StatusPrinter.PrintCoverage(document.RootElement));
            var share = document.RootElement.GetProperty("stepCoverage").GetDouble();
            return result.GetValue(failBelow) is { } minimum && share < minimum ? LowCoverage : Ok;
        }));
        root.Subcommands.Add(coverage);

        // history
        var historyJson = new Option<bool>("--json") { Description = "Print the raw JSON." };
        var historyCondition = new Option<string?>("--condition", "-c") { Description = "Only this condition." };
        var history = new Command("history", "Show the latest activations and clears of the conditions.") { historyJson, historyCondition };
        history.SetAction((result, ct) => Run(error, async () =>
        {
            using var client = Connect(result);
            var path = result.GetValue(historyCondition) is { } condition ? $"history?condition={Uri.EscapeDataString(condition)}" : "history";
            var body = await client.GetAsync(path, ct);
            using var document = JsonDocument.Parse(body);
            await output.WriteAsync(result.GetValue(historyJson) ? body + Environment.NewLine : StatusPrinter.PrintHistory(document.RootElement));
            return Ok;
        }));
        root.Subcommands.Add(history);

        // replay
        var files = new Argument<string[]>("files")
        {
            Description = "Recording files written by RecordSignals, or directories of them (*.jsonl).",
            Arity = ArgumentArity.OneOrMore,
        };
        var config = new Option<string>("--config")
        {
            Description = "JSON configuration file with the conditions to try, for example appsettings.json.",
            HelpName = "file",
            Required = true,
        };
        var section = new Option<string>("--section")
        {
            Description = "Section of the conditions in the configuration file.",
            DefaultValueFactory = _ => "Homeostat:Conditions",
        };
        var optionsSection = new Option<string>("--options-section")
        {
            Description = "Section of HomeostatOptions (cooldown, budget) in the configuration file, used when present.",
            DefaultValueFactory = _ => "Homeostat:Options",
        };
        var replayJson = new Option<bool>("--json") { Description = "Print the report as JSON." };
        var maxHeals = new Option<int?>("--max-heals")
        {
            Description = $"Exit with code {TooManyHeals} when a plan would have run more than this many times (to test a configuration change in a pipeline).",
            HelpName = "count",
        };
        maxHeals.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 0)
            {
                r.AddError("--max-heals cannot be negative.");
            }
        });
        var replay = new Command("replay", "Replay recorded signals against conditions from a configuration file, offline: what would the plans have done?")
        {
            files, config, section, optionsSection, replayJson, maxHeals,
        };
        replay.SetAction((result, ct) => Run(error, async () =>
        {
            var report = await Replay.RunAsync(result.GetValue(files)!, result.GetValue(config)!, result.GetValue(section)!, result.GetValue(optionsSection)!, ct);
            await output.WriteAsync(result.GetValue(replayJson) ? Replay.ToJson(report) + Environment.NewLine : StatusPrinter.Clean(report.ToText()));
            return result.GetValue(maxHeals) is { } max && report.Conditions.Any(c => c.WouldHeal > max) ? TooManyHeals : Ok;
        }));
        root.Subcommands.Add(replay);

        // pause / resume
        var duration = new Option<string?>("--for") { Description = "Resume by itself after this time: 30m, 2h, 1d or hh:mm:ss.", HelpName = "duration" };
        var pause = new Command("pause", "Stop all healing (kill switch). Conditions are still tracked.") { duration };
        pause.SetAction((result, ct) => Run(error, async () =>
        {
            using var client = Connect(result);
            var path = result.GetValue(duration) is { } text ? $"pause?for={Uri.EscapeDataString(Durations.ToHms(text))}" : "pause";
            await client.PostAsync(path, ct);
            await output.WriteLineAsync(result.GetValue(duration) is { } d ? $"Paused for {d}." : "Paused until resumed.");
            return Ok;
        }));
        root.Subcommands.Add(pause);

        var resume = new Command("resume", "Resume healing.");
        resume.SetAction((result, ct) => Run(error, async () =>
        {
            using var client = Connect(result);
            await client.PostAsync("resume", ct);
            await output.WriteLineAsync("Resumed.");
            return Ok;
        }));
        root.Subcommands.Add(resume);

        // approve / reject
        foreach (var (name, verb, done) in new[] { ("approve", "Approve", "Approved"), ("reject", "Reject", "Rejected") })
        {
            var id = new Argument<string>("id") { Description = "Approval request id, as shown by 'homeostat status'." };
            var command = new Command(name, $"{verb} a step waiting for approval.") { id };
            command.SetAction((result, ct) => Run(error, async () =>
            {
                using var client = Connect(result);
                await client.PostAsync($"approvals/{Uri.EscapeDataString(result.GetValue(id)!)}/{name}", ct);
                await output.WriteLineAsync($"{done} {result.GetValue(id)}.");
                return Ok;
            }));
            root.Subcommands.Add(command);
        }

        // switch on|off <name>
        var switchCommand = new Command("switch", "Turn a switch on or off.");
        foreach (var state in new[] { "on", "off" })
        {
            var switchName = new Argument<string>("name") { Description = "Switch name." };
            var command = new Command(state, $"Turn the switch {state}.") { switchName };
            command.SetAction((result, ct) => Run(error, async () =>
            {
                using var client = Connect(result);
                await client.PostAsync($"switches/{Uri.EscapeDataString(result.GetValue(switchName)!)}/{state}", ct);
                await output.WriteLineAsync($"Switch '{result.GetValue(switchName)}' is {state}.");
                return Ok;
            }));
            switchCommand.Subcommands.Add(command);
        }

        root.Subcommands.Add(switchCommand);

        // signal <source> <severity>
        var source = new Argument<string>("source") { Description = "Signal source, without the alert prefix." };
        var severity = new Argument<string>("severity") { Description = "Healthy, Degraded or Unhealthy." };
        severity.AcceptOnlyFromAmong("Healthy", "Degraded", "Unhealthy");
        var message = new Option<string?>("--message", "-m") { Description = "Signal message." };
        var value = new Option<double?>("--value") { Description = "Signal value." };
        var signal = new Command("signal", "Send a signal through the alert endpoints (MapHomeostatAlerts), for example to rehearse a plan.")
        {
            source, severity, message, value,
        };
        signal.SetAction((result, ct) => Run(error, async () =>
        {
            using var client = Connect(result);
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["source"] = result.GetValue(source),
                ["severity"] = result.GetValue(severity),
                ["message"] = result.GetValue(message),
                ["value"] = result.GetValue(value),
            });
            var body = await client.PostAsync("alerts/signals", ct, payload);
            using var document = JsonDocument.Parse(body);
            var accepted = document.RootElement.GetProperty("accepted").GetInt32();
            await output.WriteLineAsync(accepted == 1 ? "Signal accepted." : "Signal skipped.");
            return accepted == 1 ? Ok : Failed;
        }));
        root.Subcommands.Add(signal);

        var parse = root.Parse(args);
        return await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
    }

    private static async Task<int> Run(TextWriter error, Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return Failed;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync($"Cannot reach the application: {ex.Message}");
            return Failed;
        }
        catch (TaskCanceledException)
        {
            await error.WriteLineAsync("The application did not answer in time.");
            return Failed;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            await error.WriteLineAsync("The application answered with something that is not Homeostat JSON. Check --url and --prefix.");
            return Failed;
        }
    }
}

internal sealed class CliException(string message) : Exception(message);

/// <summary>HTTP client of the Homeostat endpoints.</summary>
internal sealed class Client : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _prefix;

    public Client(string? baseUrl, string prefix, string? token, HttpMessageHandler? handler)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            throw new CliException("Give the address of the application with --url or HOMEOSTAT_URL, for example --url https://weather-api.internal.");
        }

        // A trailing slash keeps the path of --url (an application behind a gateway, https://gateway/weather-api).
        if (!baseUri.AbsolutePath.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUri;
        _base = baseUri;
        _http.DefaultRequestHeaders.Add("X-Requested-With", "homeostat-cli");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("homeostat-cli", "1.0"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        var trimmed = prefix.Trim().Trim('/');
        if (trimmed.Contains("//", StringComparison.Ordinal) || trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Contains('\\'))
        {
            throw new CliException($"'{prefix}' is not a valid route prefix, for example /homeostat.");
        }

        _prefix = trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>A path relative to --url; never another host, so the token cannot leave for another server.</summary>
    private Uri Resolve(string path)
    {
        var uri = new Uri(_base, _prefix + path);
        return uri.Scheme == _base.Scheme && uri.Authority == _base.Authority
            ? uri
            : throw new CliException("The route prefix resolves to another server.");
    }

    public async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(Resolve(path), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<string> PostAsync(string path, CancellationToken cancellationToken, string? json = null)
    {
        using var content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Resolve(path), content, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        throw new CliException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Not authenticated: pass a token with --token or HOMEOSTAT_TOKEN.",
            HttpStatusCode.Forbidden => "Not allowed: the token does not satisfy the endpoint's policy.",
            HttpStatusCode.NotFound => $"Not found ({response.RequestMessage?.RequestUri?.AbsolutePath}): the request may have been answered already, or the endpoint is not mapped with this prefix.",
            HttpStatusCode.BadRequest => $"Rejected: {body.Trim('"')}",
            _ => $"The application answered {(int)response.StatusCode} ({response.StatusCode}).",
        });
    }
}

internal static class Durations
{
    /// <summary>Converts <c>30m</c>, <c>2h</c>, <c>1d</c>, <c>45s</c> or <c>hh:mm:ss</c> to <c>hh:mm:ss</c> (or <c>d.hh:mm:ss</c>).</summary>
    public static string ToHms(string text) => ToHms(text, TimeSpan.FromDays(30));

    public static string ToHms(string text, TimeSpan maximum)
    {
        text = text.Trim();
        TimeSpan? span = null;
        if (text.Length > 1 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            && double.IsFinite(amount) && amount > 0 && amount <= 365 * 24 * 3600)
        {
            try
            {
                span = char.ToLowerInvariant(text[^1]) switch
                {
                    's' => TimeSpan.FromSeconds(amount),
                    'm' => TimeSpan.FromMinutes(amount),
                    'h' => TimeSpan.FromHours(amount),
                    'd' => TimeSpan.FromDays(amount),
                    _ => null,
                };
            }
            catch (OverflowException)
            {
                span = null;
            }
        }

        if (span is null && text.Contains(':', StringComparison.Ordinal) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            span = parsed;
        }

        if (span is not { } value || value <= TimeSpan.Zero || value > maximum)
        {
            throw new CliException($"'{text}' is not a positive duration of at most {maximum.TotalDays:0} days: use 30m, 2h, 1d or hh:mm:ss.");
        }

        return value.ToString("c", CultureInfo.InvariantCulture);
    }
}
