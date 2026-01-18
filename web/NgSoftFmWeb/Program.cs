using System.Diagnostics;
using System.IO.Pipelines;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var hlsRoot = Path.Combine(Path.GetTempPath(), "NgSoftFmWeb", "hls");
Directory.CreateDirectory(hlsRoot);
builder.Services.AddSingleton(_ => new RadioService(hlsRoot, builder.Environment.ContentRootPath));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Serve HLS playlist and segments from a fixed temp folder.
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
contentTypeProvider.Mappings[".ts"] = "video/mp2t";
contentTypeProvider.Mappings[".m4s"] = "video/iso.segment";
contentTypeProvider.Mappings[".mp4"] = "video/mp4";
app.UseStaticFiles(new StaticFileOptions
{
	FileProvider = new PhysicalFileProvider(hlsRoot),
	RequestPath = "/hls",
	ContentTypeProvider = contentTypeProvider,
	ServeUnknownFileTypes = false,
});

app.MapGet("/api/status", (RadioService radio) => Results.Json(radio.GetStatus()));
app.MapGet("/api/hls/ready", (RadioService radio) =>
{
	var ready = radio.IsHlsReady(out var reason);
	return Results.Json(new { ready, reason });
});

app.MapPost("/api/server/restart", (HttpContext ctx, RadioService radio) =>
{
	// Optional admin protection:
	// - If NGSOFTFM_ADMIN_TOKEN is set, require it via header X-Admin-Token or query token.
	var requiredToken = Environment.GetEnvironmentVariable("NGSOFTFM_ADMIN_TOKEN");
	if (!string.IsNullOrWhiteSpace(requiredToken))
	{
		var provided = ctx.Request.Headers.TryGetValue("X-Admin-Token", out var hv) ? hv.ToString() : null;
		if (string.IsNullOrWhiteSpace(provided) && ctx.Request.Query.TryGetValue("token", out var qv))
		{
			provided = qv.ToString();
		}
		if (!string.Equals(provided, requiredToken, StringComparison.Ordinal))
		{
			return Results.Unauthorized();
		}
	}

	// Best-effort: stop scan/stream before exiting.
	try { radio.StopScan(); } catch { }
	try { radio.Stop(); } catch { }

	// Force exit with a special code so a supervisor script can restart us.
	const int restartExitCode = 42;
	_ = Task.Run(async () =>
	{
		try
		{
			Environment.ExitCode = restartExitCode;
			// Give the HTTP response a moment to get out.
			await Task.Delay(300);
			Environment.Exit(restartExitCode);
		}
		catch
		{
			try { Environment.Exit(restartExitCode); } catch { }
		}
	});

	return Results.Json(new { ok = true, action = "restart", exitCode = restartExitCode }, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/config", (RadioService radio) => Results.Json(radio.GetConfig()));

app.MapGet("/api/presets", (RadioService radio) => Results.Json(radio.GetPresets()));

app.MapPost("/api/presets/auto", (PresetAutoRequest? body, RadioService radio) =>
{
	// Default: Japan FM band (wide) 76.0 - 95.0 MHz @ 0.1 MHz steps.
	var startMHz = body?.StartMHz ?? 76.0;
	var endMHz = body?.EndMHz ?? 95.0;
	var stepMHz = body?.StepMHz ?? 0.1;

	if (startMHz < 10 || startMHz > 3000 || endMHz < 10 || endMHz > 3000 || startMHz > endMHz)
	{
		return Results.BadRequest(new { error = "Invalid startMHz/endMHz" });
	}
	if (stepMHz <= 0 || stepMHz > 10)
	{
		return Results.BadRequest(new { error = "Invalid stepMHz" });
	}

	radio.SetPresetsAuto(startMHz, endMHz, stepMHz);
	return Results.Ok(radio.GetPresets());
});

app.MapPost("/api/presets/add", (PresetAddRequest body, RadioService radio) =>
{
	if (body.FreqMHz is null)
	{
		return Results.BadRequest(new { error = "Provide freqMHz" });
	}
	if (body.FreqMHz < 10 || body.FreqMHz > 3000)
	{
		return Results.BadRequest(new { error = "freqMHz out of range" });
	}

	radio.AddPreset(body.FreqMHz.Value, body.Name);
	return Results.Ok(radio.GetPresets());
});

app.MapPost("/api/presets/update", (PresetUpdateRequest body, RadioService radio) =>
{
	if (body.FreqMHz is null)
	{
		return Results.BadRequest(new { error = "Provide freqMHz" });
	}
	if (body.FreqMHz < 10 || body.FreqMHz > 3000)
	{
		return Results.BadRequest(new { error = "freqMHz out of range" });
	}
	if (body.Name is null)
	{
		return Results.BadRequest(new { error = "Provide name" });
	}

	radio.UpdatePresetName(body.FreqMHz.Value, body.Name);
	return Results.Ok(radio.GetPresets());
});

app.MapPost("/api/presets/addMany", (PresetAddManyRequest body, RadioService radio) =>
{
	var list = body.FreqMHzList;
	if (list is null || list.Length == 0)
	{
		return Results.BadRequest(new { error = "Provide freqMHzList" });
	}

	var added = 0;
	foreach (var mhz in list)
	{
		if (!double.IsFinite(mhz)) continue;
		if (mhz < 10 || mhz > 3000) continue;
		radio.AddPreset(mhz, "-");
		added++;
	}
	return Results.Ok(new { added, presets = radio.GetPresets() });
});

app.MapPost("/api/presets/remove", (PresetAddRequest body, RadioService radio) =>
{
	if (body.FreqMHz is null)
	{
		return Results.BadRequest(new { error = "Provide freqMHz" });
	}
	radio.RemovePreset(body.FreqMHz.Value);
	return Results.Ok(radio.GetPresets());
});

app.MapGet("/api/scan/status", (HttpContext ctx, RadioService radio) =>
{
	var raw = ctx.Request.Query.TryGetValue("raw", out var v) && (v.ToString() == "1" || v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
	var level = ctx.Request.Query.TryGetValue("level", out var l) ? l.ToString() : null;
	return Results.Json(radio.GetScanStatus(raw, level));
});

app.MapPost("/api/scan/start", async (ScanStartRequest? body, RadioService radio) =>
{
	var startMHz = body?.StartMHz ?? 76.0;
	var endMHz = body?.EndMHz ?? 95.0;
	var stepMHz = body?.StepMHz ?? 0.1;
	var dwellMs = body?.DwellMs ?? 600;

	if (startMHz < 10 || startMHz > 3000 || endMHz < 10 || endMHz > 3000 || startMHz > endMHz)
	{
		return Results.BadRequest(new { error = "Invalid startMHz/endMHz" });
	}
	if (stepMHz <= 0 || stepMHz > 10)
	{
		return Results.BadRequest(new { error = "Invalid stepMHz" });
	}
	if (dwellMs < 200 || dwellMs > 10_000)
	{
		return Results.BadRequest(new { error = "dwellMs must be between 200 and 10000" });
	}

	var ok = await radio.StartScanAsync(startMHz, endMHz, stepMHz, dwellMs);
	if (!ok)
	{
		return Results.Conflict(new { error = "Scan already running" });
	}
	return Results.Ok(radio.GetScanStatus());
});

app.MapPost("/api/scan/stop", (RadioService radio) =>
{
	radio.StopScan();
	return Results.Ok(radio.GetScanStatus());
});

app.MapPost("/api/config", (ConfigRequest body, RadioService radio) =>
{
	if (body.BufferSeconds is not null)
	{
		if (body.BufferSeconds < 0 || body.BufferSeconds > 60)
		{
			return Results.BadRequest(new { error = "bufferSeconds must be between 0 and 60" });
		}
	}

	if (body.Delivery is not null)
	{
		if (!RadioService.IsSupportedDelivery(body.Delivery))
		{
			return Results.BadRequest(new { error = "delivery must be one of: direct, hls" });
		}
	}

	if (body.HlsBitrateKbps is not null)
	{
		if (body.HlsBitrateKbps < 32 || body.HlsBitrateKbps > 512)
		{
			return Results.BadRequest(new { error = "hlsBitrateKbps must be between 32 and 512" });
		}
	}

	if (body.Format is not null)
	{
		if (!RadioService.IsSupportedFormat(body.Format))
		{
			return Results.BadRequest(new { error = "format must be one of: mp3, aac, opus" });
		}
	}

	if (body.RtlGainDb is not null)
	{
		if (body.RtlGainDb < 0 || body.RtlGainDb > 100)
		{
			return Results.BadRequest(new { error = "rtlGainDb must be between 0 and 100 (or null for auto)" });
		}
	}

	if (body.RtlAgc is not null)
	{
		// boolean; nothing else to validate
	}

	if (body.StereoMode is not null)
	{
		if (!RadioService.IsSupportedStereoMode(body.StereoMode))
		{
			return Results.BadRequest(new { error = "stereoMode must be one of: auto, stereo, mono" });
		}
	}

	if (body.ForceStereo is not null)
	{
		// boolean; nothing else to validate
	}

	// Update streaming config and force any active stream to restart.
	radio.SetStreamingConfig(body.Format, body.BufferSeconds, body.Delivery, body.HlsBitrateKbps, body.RtlGainDb, body.RtlAgc, body.StereoMode, body.ForceStereo, restartActiveStream: true);
	radio.EnsureDeliveryStarted();
	return Results.Ok(radio.GetConfig());
});

app.MapPost("/api/start", (StartRequest body, RadioService radio) =>
{
	var freqHz = body.FreqHz;
	if (freqHz is null && body.FreqMHz is null)
	{
		return Results.BadRequest(new { error = "Provide freqHz or freqMHz" });
	}

	long resolvedHz = freqHz ?? (long)Math.Round(body.FreqMHz!.Value * 1_000_000.0);
	if (resolvedHz < 10_000_000 || resolvedHz > 2_200_000_000)
	{
		return Results.BadRequest(new { error = "Frequency out of range" });
	}

	// Update frequency and force any active stream to restart.
	radio.SetFrequencyHz(resolvedHz, restartActiveStream: true);
	radio.EnsureDeliveryStarted();
	return Results.Ok(radio.GetStatus());
});

app.MapPost("/api/stop", (RadioService radio) =>
{
	radio.Stop();
	return Results.Ok(radio.GetStatus());
});

app.MapGet("/stream.mp3", async (HttpContext ctx, RadioService radio) =>
{
	// Single-user design: if a stream is already active, cancel it and switch to this request.
	var streamLease = radio.BeginStreaming();
	try
	{
		ctx.Response.Headers.CacheControl = "no-store";
		ctx.Response.ContentType = "audio/mpeg";

		await radio.StreamToAsync(ctx.Response.Body, ctx.RequestAborted, streamLease.Token, requestedFormat: "mp3");
		return Results.Empty;
	}
	finally
	{
		radio.EndStreaming(streamLease.Generation);
	}
});

app.MapGet("/stream", async (HttpContext ctx, RadioService radio) =>
{
	// Optional override: /stream?fmt=mp3|aac|opus
	string? fmt = null;
	if (ctx.Request.Query.TryGetValue("fmt", out var fmtVals))
	{
		fmt = fmtVals.ToString();
	}

	var resolvedFmt = radio.ResolveFormat(fmt);

	// Single-user design: if a stream is already active, cancel it and switch to this request.
	var streamLease = radio.BeginStreaming();
	try
	{
		ctx.Response.Headers.CacheControl = "no-store";
		ctx.Response.ContentType = resolvedFmt switch
		{
			"aac" => "audio/mp4; codecs=mp4a.40.2",
			"opus" => "audio/webm; codecs=opus",
			_ => "audio/mpeg",
		};

		await radio.StreamToAsync(ctx.Response.Body, ctx.RequestAborted, streamLease.Token, requestedFormat: resolvedFmt);
		return Results.Empty;
	}
	finally
	{
		radio.EndStreaming(streamLease.Generation);
	}
});

app.Run();

internal sealed class StartRequest
{
	[JsonPropertyName("freqHz")]
	public long? FreqHz { get; set; }

	[JsonPropertyName("freqMHz")]
	public double? FreqMHz { get; set; }
}

internal sealed class ConfigRequest
{
	[JsonPropertyName("format")]
	public string? Format { get; set; }

	[JsonPropertyName("bufferSeconds")]
	public double? BufferSeconds { get; set; }

	// direct | hls
	[JsonPropertyName("delivery")]
	public string? Delivery { get; set; }

	// Optional: bitrate for HLS AAC (kbps)
	[JsonPropertyName("hlsBitrateKbps")]
	public int? HlsBitrateKbps { get; set; }

	// RTL-SDR tuner gain in dB. null = auto gain.
	[JsonPropertyName("rtlGainDb")]
	public double? RtlGainDb { get; set; }

	// RTL-SDR AGC mode (rtlsdr_set_agc_mode). When true, adds the 'agc' switch.
	[JsonPropertyName("rtlAgc")]
	public bool? RtlAgc { get; set; }

	// auto | stereo | mono
	[JsonPropertyName("stereoMode")]
	public string? StereoMode { get; set; }

	// Force stereo output even without pilot lock (requires softfm.exe with --force-stereo).
	[JsonPropertyName("forceStereo")]
	public bool? ForceStereo { get; set; }
}

internal sealed class PresetAutoRequest
{
	[JsonPropertyName("startMHz")]
	public double? StartMHz { get; set; }

	[JsonPropertyName("endMHz")]
	public double? EndMHz { get; set; }

	[JsonPropertyName("stepMHz")]
	public double? StepMHz { get; set; }
}

internal sealed class PresetAddRequest
{
	[JsonPropertyName("freqMHz")]
	public double? FreqMHz { get; set; }

	[JsonPropertyName("name")]
	public string? Name { get; set; }
}

internal sealed class PresetUpdateRequest
{
	[JsonPropertyName("freqMHz")]
	public double? FreqMHz { get; set; }

	[JsonPropertyName("name")]
	public string? Name { get; set; }
}

internal sealed class PresetAddManyRequest
{
	[JsonPropertyName("freqMHzList")]
	public double[]? FreqMHzList { get; set; }
}

internal sealed class ScanStartRequest
{
	[JsonPropertyName("startMHz")]
	public double? StartMHz { get; set; }

	[JsonPropertyName("endMHz")]
	public double? EndMHz { get; set; }

	[JsonPropertyName("stepMHz")]
	public double? StepMHz { get; set; }

	[JsonPropertyName("dwellMs")]
	public int? DwellMs { get; set; }
}

internal sealed class RadioService
{
	private enum StereoMode
	{
		Auto = 0,
		ForceStereo = 1,
		Mono = 2,
	}

	private readonly object _gate = new();
	private readonly string _hlsRoot;
	private readonly string _contentRoot;
	private long _freqHz = 80_000_000;
	private string? _lastError;
	private bool? _stereoDetected;
	private double? _pilotLevel;
	private DateTimeOffset? _stereoUpdatedUtc;
	private bool _isStreaming;
	private long _streamGeneration;
	private CancellationTokenSource? _streamCts;
	private string _delivery = "hls"; // default: HLS for Chrome
	private string _streamFormat = "mp3"; // used for direct /stream
	private double _bufferSeconds = 2.0;
	private int _hlsBitrateKbps = 320;
	private double? _rtlGainDb = 19.7; // default: max (typical RTL-SDR value), null = auto
	private bool _rtlAgc = false;
	private StereoMode _stereoMode = StereoMode.Auto;
	private CancellationTokenSource? _hlsCts;
	private Task? _hlsTask;
	private bool _hlsRestartQueued;
	private readonly List<PresetItem> _presets = new();
	private readonly string _presetsFilePath;

	private CancellationTokenSource? _scanCts;
	private Task? _scanTask;
	private DateTimeOffset? _scanStartedUtc;
	private int _scanTotal;
	private int _scanDone;
	private readonly List<ScanResult> _scanResults = new();
	private string? _scanError;

	private sealed class PresetsFile
	{
		[JsonPropertyName("presets")]
		public PresetWire[]? Presets { get; set; }

		[JsonPropertyName("presetsMHz")]
		public string[]? PresetsMHz { get; set; }
	}

	private sealed class PresetWire
	{
		[JsonPropertyName("freqMHz")]
		public string? FreqMHz { get; set; }

		[JsonPropertyName("name")]
		public string? Name { get; set; }
	}

	private sealed class PresetItem
	{
		public long Hz { get; set; }
		public string Name { get; set; } = "-";
	}

	public RadioService(string hlsRoot, string contentRoot)
	{
		_hlsRoot = hlsRoot;
		_contentRoot = string.IsNullOrWhiteSpace(contentRoot) ? AppContext.BaseDirectory : contentRoot;
		_presetsFilePath = ResolvePresetsFilePath(_contentRoot);
		Directory.CreateDirectory(_hlsRoot);
		TryLoadPresetsFromDisk();
	}

	private static string ResolvePresetsFilePath(string contentRoot)
	{
		// Optional override.
		var overridePath = Environment.GetEnvironmentVariable("NGSOFTFM_PRESETS_PATH");
		if (!string.IsNullOrWhiteSpace(overridePath))
		{
			try
			{
				overridePath = overridePath.Trim();
				// If a directory is given, place presets.json inside it.
				if (Directory.Exists(overridePath))
				{
					return Path.Combine(overridePath, "presets.json");
				}
				return overridePath;
			}
			catch
			{
				// Fall through.
			}
		}

		// Default: store user data outside the repo so git operations won't wipe it.
		try
		{
			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!string.IsNullOrWhiteSpace(localAppData))
			{
				return Path.Combine(localAppData, "NgSoftFmWeb", "presets.json");
			}
		}
		catch
		{
			// Fall through.
		}

		// Fallback: content root (legacy behavior).
		return Path.Combine(string.IsNullOrWhiteSpace(contentRoot) ? AppContext.BaseDirectory : contentRoot, "presets.json");
	}

	private static bool TryPathEquals(string a, string b)
	{
		try
		{
			var pa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var pb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}
	}

	private IEnumerable<string> EnumeratePresetLoadCandidates()
	{
		yield return _presetsFilePath;

		// Legacy locations (older builds / previous behavior).
		var legacyContentRoot = Path.Combine(_contentRoot, "presets.json");
		if (!TryPathEquals(legacyContentRoot, _presetsFilePath))	
		{
			yield return legacyContentRoot;
		}

		var legacyBaseDir = Path.Combine(AppContext.BaseDirectory, "presets.json");
		if (!TryPathEquals(legacyBaseDir, _presetsFilePath) && !TryPathEquals(legacyBaseDir, legacyContentRoot))
		{
			yield return legacyBaseDir;
		}
	}

	private void TryLoadPresetsFromDisk()
	{
		foreach (var path in EnumeratePresetLoadCandidates())
		{
			if (TryLoadPresetsFromDisk(path))
			{
				// If we loaded from a legacy location, migrate by saving to the current path.
				if (!TryPathEquals(path, _presetsFilePath))
				{
					TrySavePresetsToDisk();
				}
				return;
			}
		}
	}

	private bool TryLoadPresetsFromDisk(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return false;
			}

			var json = File.ReadAllText(path, Encoding.UTF8);
			var file = JsonSerializer.Deserialize<PresetsFile>(json);

			var items = new List<PresetItem>();
			const long QuantumHz = 100_000;

			var wires = file?.Presets;
			if (wires is not null && wires.Length > 0)
			{
				foreach (var w in wires)
				{
					var s = (w?.FreqMHz ?? string.Empty).Trim();
					if (s.Length == 0) continue;
					if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)) continue;
					if (!double.IsFinite(mhz)) continue;
					if (mhz < 10 || mhz > 3000) continue;
					var hz = (long)Math.Round(mhz * 1_000_000.0);
					hz = (long)Math.Round(hz / (double)QuantumHz) * QuantumHz;

					var name = NormalizePresetName(w?.Name);
					items.Add(new PresetItem { Hz = hz, Name = name });
				}
			}
			else
			{
				// Back-compat: older file format (presetsMHz only)
				var mhzList = file?.PresetsMHz ?? Array.Empty<string>();
				foreach (var s0 in mhzList)
				{
					var s = (s0 ?? string.Empty).Trim();
					if (s.Length == 0) continue;
					if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)) continue;
					if (!double.IsFinite(mhz)) continue;
					if (mhz < 10 || mhz > 3000) continue;
					var hz = (long)Math.Round(mhz * 1_000_000.0);
					hz = (long)Math.Round(hz / (double)QuantumHz) * QuantumHz;
					items.Add(new PresetItem { Hz = hz, Name = "-" });
				}
			}

			items = items
				.GroupBy(p => p.Hz)
				.Select(g =>
				{
					var best = g.First();
					// Prefer a non-placeholder name when duplicates exist
					var named = g.FirstOrDefault(x => !string.Equals(x.Name, "-", StringComparison.Ordinal));
					return named ?? best;
				})
				.OrderBy(p => p.Hz)
				.ToList();

			lock (_gate)
			{
				_presets.Clear();
				_presets.AddRange(items);
			}
			return true;
		}
		catch
		{
			// Ignore corrupt/unreadable presets file.
			return false;
		}
	}

	private void TrySavePresetsToDisk()
	{
		try
		{
			var dir = Path.GetDirectoryName(_presetsFilePath);
			if (!string.IsNullOrWhiteSpace(dir))
			{
				Directory.CreateDirectory(dir);
			}

			PresetWire[] presets;
			string[] mhz;
			lock (_gate)
			{
				presets = _presets
					.OrderBy(p => p.Hz)
					.Select(p => new PresetWire
					{
						FreqMHz = (p.Hz / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture),
						Name = p.Name,
					})
					.ToArray();
				mhz = presets.Select(p => p.FreqMHz ?? string.Empty).Where(s => s.Length != 0).ToArray();
			}

			var payload = new PresetsFile { Presets = presets, PresetsMHz = mhz };
			var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
			var tmp = _presetsFilePath + ".tmp";
			File.WriteAllText(tmp, json, Encoding.UTF8);
			File.Move(tmp, _presetsFilePath, true);
		}
		catch
		{
			// Best-effort persistence.
		}
	}

	public object GetPresets()
	{
		lock (_gate)
		{
			var presets = _presets
				.OrderBy(p => p.Hz)
				.Select(p => new
				{
					freqMHz = (p.Hz / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture),
					name = p.Name,
				})
				.ToArray();
			var mhz = presets.Select(p => (string)p.freqMHz).ToArray();
			return new { presets, presetsMHz = mhz };
		}
	}

	private static string NormalizePresetName(string? name)
	{
		var n = (name ?? string.Empty).Trim();
		if (n.Length == 0) n = "-";
		if (n.Length > 64) n = n[..64];
		// Avoid newlines in list UI.
		n = n.Replace("\r", " ").Replace("\n", " ");
		return n;
	}

	public void AddPreset(double mhz, string? name)
	{
		var hz = (long)Math.Round(mhz * 1_000_000.0);
		const long QuantumHz = 100_000;
		hz = (long)Math.Round(hz / (double)QuantumHz) * QuantumHz;
		var n = NormalizePresetName(name);
		var changed = false;
		lock (_gate)
		{
			var existing = _presets.FirstOrDefault(p => p.Hz == hz);
			if (existing is null)
			{
				_presets.Add(new PresetItem { Hz = hz, Name = n });
				_presets.Sort((a, b) => a.Hz.CompareTo(b.Hz));
				changed = true;
			}
			else
			{
				// If the existing name is a placeholder, allow upgrading it.
				if (string.Equals(existing.Name, "-", StringComparison.Ordinal) && !string.Equals(n, "-", StringComparison.Ordinal))
				{
					existing.Name = n;
					changed = true;
				}
			}
		}
		if (changed) TrySavePresetsToDisk();
	}

	public void UpdatePresetName(double mhz, string name)
	{
		var hz = (long)Math.Round(mhz * 1_000_000.0);
		const long QuantumHz = 100_000;
		hz = (long)Math.Round(hz / (double)QuantumHz) * QuantumHz;
		var n = NormalizePresetName(name);
		var changed = false;
		lock (_gate)
		{
			var existing = _presets.FirstOrDefault(p => p.Hz == hz);
			if (existing is null)
			{
				_presets.Add(new PresetItem { Hz = hz, Name = n });
				_presets.Sort((a, b) => a.Hz.CompareTo(b.Hz));
				changed = true;
			}
			else if (!string.Equals(existing.Name, n, StringComparison.Ordinal))
			{
				existing.Name = n;
				changed = true;
			}
		}
		if (changed) TrySavePresetsToDisk();
	}

	public void RemovePreset(double mhz)
	{
		var hz = (long)Math.Round(mhz * 1_000_000.0);
		const long QuantumHz = 100_000;
		hz = (long)Math.Round(hz / (double)QuantumHz) * QuantumHz;
		var changed = false;
		lock (_gate)
		{
			var before = _presets.Count;
			_presets.RemoveAll(p => p.Hz == hz);
			changed = _presets.Count != before;
		}
		if (changed) TrySavePresetsToDisk();
	}

	public object GetScanStatus(bool raw = false, string? thresholdLevel = null)
	{
		// Snapshot under lock.
		ScanResult[] rawList;
		DateTimeOffset? startedUtc;
		int total;
		int done;
		string? error;
		bool running;
		lock (_gate)
		{
			running = _scanTask is not null && !_scanTask.IsCompleted;
			rawList = _scanResults.ToArray();
			startedUtc = _scanStartedUtc;
			total = _scanTotal;
			done = _scanDone;
			error = _scanError;
		}

		// If raw is requested, just sort and return a bounded list.
		if (raw)
		{
			var resultsRaw = rawList
				.OrderByDescending(r => r.IfDb ?? double.NegativeInfinity)
				.ThenByDescending(r => r.BbDb ?? double.NegativeInfinity)
				.ThenByDescending(r => r.AudioDb ?? double.NegativeInfinity)
				.Take(500)
				.Select(r => new
				{
					freqMHz = r.FreqMHz.ToString("0.0", CultureInfo.InvariantCulture),
					ifDb = r.IfDb,
					bbDb = r.BbDb,
					audioDb = r.AudioDb,
					stereo = r.StereoDetected,
					pilotLevel = r.PilotLevel,
				})
				.ToArray();

			return new
			{
				running,
				startedUtc,
				total,
				done,
				error,
				raw = true,
				rawCount = rawList.Length,
				filteredCount = resultsRaw.Length,
				noiseFloorIfDb = (double?)null,
				ifThresholdDb = (double?)null,
				clusterGapMHz = 0.25,
				results = resultsRaw,
			};
		}

		var level = (thresholdLevel ?? string.Empty).Trim().ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(level)) level = "medium";

		var offsetDb = level switch
		{
			"strong" => 14.0,
			"weak" => 6.0,
			_ => 10.0, // medium (default)
		};

		var ifValues = rawList.Select(r => r.IfDb).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToArray();
		double? noiseFloorIf = null;
		double? ifThreshold = null;
		if (ifValues.Length >= 5)
		{
			// Robust noise-floor estimate: median IF.
			noiseFloorIf = ifValues[ifValues.Length / 2];
			// Heuristic threshold: median + offset.
			ifThreshold = noiseFloorIf + offsetDb;
		}

		var clusterGapMHz = 0.25; // roughly one FM channel (200kHz) + margin
		IEnumerable<ScanResult> candidates = rawList;
		if (ifThreshold is not null)
		{
			candidates = candidates.Where(r => r.IfDb is not null && r.IfDb.Value >= ifThreshold.Value);
		}

		// Collapse adjacent bins caused by a strong station's bandwidth/leakage.
		var peaks = new List<ScanResult>();
		ScanResult? best = null;
		double? bestFreq = null;
		foreach (var r in candidates.OrderBy(r => r.FreqMHz))
		{
			if (best is null)
			{
				best = r;
				bestFreq = r.FreqMHz;
				continue;
			}

			if (Math.Abs(r.FreqMHz - bestFreq!.Value) <= clusterGapMHz)
			{
				// Same cluster: keep the strongest point.
				var cur = best.Value;
				var curIf = cur.IfDb ?? double.NegativeInfinity;
				var rIf = r.IfDb ?? double.NegativeInfinity;
				if (rIf > curIf || (rIf == curIf && (r.BbDb ?? double.NegativeInfinity) > (cur.BbDb ?? double.NegativeInfinity)))
				{
					best = r;
					bestFreq = r.FreqMHz;
				}
				continue;
			}

			peaks.Add(best.Value);
			best = r;
			bestFreq = r.FreqMHz;
		}
		if (best is not null)
		{
			peaks.Add(best.Value);
		}

		// If the heuristic filtered everything out, fall back to top-N.
		IEnumerable<ScanResult> final = peaks;
		if (!final.Any())
		{
			final = rawList
				.OrderByDescending(r => r.IfDb ?? double.NegativeInfinity)
				.ThenByDescending(r => r.BbDb ?? double.NegativeInfinity)
				.Take(50);
		}

		var results = final
			.OrderByDescending(r => r.IfDb ?? double.NegativeInfinity)
			.ThenByDescending(r => r.BbDb ?? double.NegativeInfinity)
			.ThenByDescending(r => r.AudioDb ?? double.NegativeInfinity)
			.Take(200)
			.Select(r => new
			{
				freqMHz = r.FreqMHz.ToString("0.0", CultureInfo.InvariantCulture),
				ifDb = r.IfDb,
				bbDb = r.BbDb,
				audioDb = r.AudioDb,
				stereo = r.StereoDetected,
				pilotLevel = r.PilotLevel,
			})
			.ToArray();

		return new
		{
			running,
			startedUtc,
			total,
			done,
			error,
			thresholdLevel = level,
			thresholdOffsetDb = offsetDb,
			raw = false,
			rawCount = rawList.Length,
			filteredCount = results.Length,
			noiseFloorIfDb = noiseFloorIf,
			ifThresholdDb = ifThreshold,
			clusterGapMHz,
			results,
		};
	}

	public async Task<bool> StartScanAsync(double startMHz, double endMHz, double stepMHz, int dwellMs)
	{
		lock (_gate)
		{
			if (_scanTask is not null && !_scanTask.IsCompleted)
			{
				return false;
			}

			try { _scanCts?.Cancel(); } catch { }
			try { _scanCts?.Dispose(); } catch { }
			_scanCts = new CancellationTokenSource();
			_scanStartedUtc = DateTimeOffset.UtcNow;
			_scanError = null;
			_scanDone = 0;
			_scanResults.Clear();
		}

		// Exclusive hardware access: stop any current streaming/HLS.
		Stop();
		await Task.Delay(500);

		CancellationToken ct;
		lock (_gate) { ct = _scanCts!.Token; }

		// Compute scan list in 0.1 MHz (100kHz) steps to avoid floating point drift.
		const long QuantumHz = 100_000;
		var startHz = (long)Math.Round(startMHz * 1_000_000.0 / QuantumHz) * QuantumHz;
		var endHz = (long)Math.Round(endMHz * 1_000_000.0 / QuantumHz) * QuantumHz;
		var stepHz = (long)Math.Round(stepMHz * 1_000_000.0 / QuantumHz) * QuantumHz;
		if (stepHz <= 0) stepHz = QuantumHz;

		var freqList = new List<long>();
		for (var hz = startHz; hz <= endHz; hz += stepHz)
		{
			freqList.Add(hz);
			if (freqList.Count > 5000) break; // safety
		}

		lock (_gate) { _scanTotal = freqList.Count; }

		_scanTask = Task.Run(async () =>
		{
			try
			{
				foreach (var hz in freqList)
				{
					ct.ThrowIfCancellationRequested();
					var r = await ProbeFrequencyOnceAsync(hz, dwellMs, ct);
					lock (_gate)
					{
						_scanDone++;
						_scanResults.Add(r);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// normal
			}
			catch (Exception ex)
			{
				lock (_gate) { _scanError = ex.Message; }
			}
		}, ct);

		return true;
	}

	public void StopScan()
	{
		lock (_gate)
		{
			try { _scanCts?.Cancel(); } catch { }
		}
	}

	private async Task<ScanResult> ProbeFrequencyOnceAsync(long freqHz, int dwellMs, CancellationToken ct)
	{
		double? bestIf = null;
		double? bestBb = null;
		double? bestAudio = null;
		bool stereo = false;
		double? pilot = null;
		double? tunedFreqMHz = null;
		var debug = string.Equals(Environment.GetEnvironmentVariable("NGSOFTFM_DEBUG_SCAN"), "1", StringComparison.OrdinalIgnoreCase);

		// Use the same gain/AGC/stereoMode settings.
		double? rtlGainDb;
		bool rtlAgc;
		StereoMode stereoMode;
		lock (_gate)
		{
			rtlGainDb = _rtlGainDb;
			rtlAgc = _rtlAgc;
			stereoMode = _stereoMode;
		}

		var repoRoot = FindRepoRoot();
		var softfmPath = Path.Combine(repoRoot, "build-ucrt64", "softfm.exe");
		if (!File.Exists(softfmPath))
		{
			throw new FileNotFoundException($"softfm.exe not found: {softfmPath}. Build native binary first.");
		}

		var msysUcrtBin = @"C:\msys64\ucrt64\bin";
		var softfm = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = softfmPath,
				Arguments = $"{BuildStereoArgs(stereoMode)}-t rtlsdr -r 48000 -c \"{BuildRtlSdrConfig(freqHz, rtlGainDb, rtlAgc)}\" -R -",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
		};
		if (Directory.Exists(msysUcrtBin))
		{
			var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			softfm.StartInfo.Environment["PATH"] = msysUcrtBin + ";" + path;
		}

		try
		{
			if (!softfm.Start())
			{
				throw new InvalidOperationException("Failed to start softfm.exe");
			}

			if (debug)
			{
				Console.WriteLine($"[scan] start {freqHz / 1_000_000.0:0.0} MHz args={softfm.StartInfo.Arguments}");
			}

			using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var probeToken = probeCts.Token;
			var statsGate = new object();

			// Discard stdout (raw PCM) so buffers don't fill.
			var drainOut = Task.Run(async () =>
			{
				try { await softfm.StandardOutput.BaseStream.CopyToAsync(Stream.Null, 64 * 1024, probeToken); }
				catch { }
			}, probeToken);

			// Read stderr continuously (single reader) and parse stats.
			var stderr = softfm.StandardError.BaseStream;
			var stderrTask = Task.Run(async () =>
			{
				var buf = new byte[4096];
				var tail = string.Empty;
				while (!probeToken.IsCancellationRequested)
				{
					int read;
					try
					{
						read = await stderr.ReadAsync(buf, probeToken);
					}
					catch (OperationCanceledException)
					{
						break;
					}
					catch
					{
						break;
					}
					if (read <= 0) break;

					var chunk = Encoding.UTF8.GetString(buf, 0, read);
					if (debug)
					{
						Console.Write(chunk);
					}

					lock (statsGate)
					{
						tail += chunk;
						if (tail.Length > 16384) tail = tail[^16384..];
						var s = ParseLatestStats(tail);
						if (s.IfDb is not null) bestIf = MaxOr(s.IfDb, bestIf);
						if (s.BbDb is not null) bestBb = MaxOr(s.BbDb, bestBb);
						if (s.AudioDb is not null) bestAudio = MaxOr(s.AudioDb, bestAudio);
						if (s.TunedFreqMHz is not null) tunedFreqMHz = s.TunedFreqMHz;
						if (s.StereoDetected) stereo = true;
						if (s.PilotLevel is not null) pilot = s.PilotLevel;
					}
				}
			}, probeToken);

			await Task.Delay(dwellMs, ct);
			try { probeCts.Cancel(); } catch { }
			TryKill(softfm);

			try { await Task.WhenAny(stderrTask, Task.Delay(200, ct)); } catch { }
			try { await Task.WhenAny(drainOut, Task.Delay(200, ct)); } catch { }

			if (debug)
			{
				Console.WriteLine($"\n[scan] stop  {freqHz / 1_000_000.0:0.0} MHz bestIF={bestIf:0.0} bestBB={bestBb:0.0} bestAudio={bestAudio:0.0}");
			}
		}
		finally
		{
			TryKill(softfm);
			softfm.Dispose();
		}

		var centerMHz = freqHz / 1_000_000.0;
		return new ScanResult(
			FreqMHz: centerMHz,
			TunedFreqMHz: tunedFreqMHz,
			IfDb: bestIf,
			BbDb: bestBb,
			AudioDb: bestAudio,
			StereoDetected: stereo,
			PilotLevel: pilot);
	}

	private static double? MaxOr(double? a, double? b)
	{
		if (a is null) return b;
		if (b is null) return a;
		return Math.Max(a.Value, b.Value);
	}

	private static ScanStats ParseLatestStats(string text)
	{
		// Parse latest statistics line from main.cpp:
		//   blk=... freq=xx.xxxxxxMHz ... IF=+5.1dB  BB=+5.1dB  audio=+5.1dB
		// and stereo lines:
		//   got stereo signal (pilot level = 0.012345)
		//   lost stereo signal
		var tuned = TryParseAfter(text, "freq=", "MHz");
		var ifDb = TryParseAfter(text, "IF=", "dB");
		var bbDb = TryParseAfter(text, "BB=", "dB");
		var audioDb = TryParseAfter(text, "audio=", "dB");

		var gotIdx = text.LastIndexOf("got stereo signal", StringComparison.OrdinalIgnoreCase);
		var lostIdx = text.LastIndexOf("lost stereo signal", StringComparison.OrdinalIgnoreCase);
		var stereo = gotIdx >= 0 && gotIdx > lostIdx;
		double? pilot = null;
		if (stereo)
		{
			var pfx = "pilot level =";
			var pIdx = text.IndexOf(pfx, gotIdx, StringComparison.OrdinalIgnoreCase);
			if (pIdx >= 0)
			{
				pilot = TryParseNumber(text[(pIdx + pfx.Length)..]);
			}
		}

		return new ScanStats(tuned, ifDb, bbDb, audioDb, stereo, pilot);
	}

	private static double? TryParseAfter(string text, string marker, string endMarker)
	{
		var idx = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (idx < 0) return null;
		idx += marker.Length;
		// Skip whitespace
		while (idx < text.Length && char.IsWhiteSpace(text[idx])) idx++;
		var end = text.IndexOf(endMarker, idx, StringComparison.OrdinalIgnoreCase);
		if (end < 0) end = Math.Min(idx + 64, text.Length);
		var slice = text[idx..end];
		return TryParseNumber(slice);
	}

	private static double? TryParseNumber(string s)
	{
		// Accept leading +/- and decimals/exponent.
		s = (s ?? string.Empty).Trim();
		if (s.Length == 0) return null;
		var i = 0;
		while (i < s.Length)
		{
			var c = s[i];
			if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')) break;
			i++;
		}
		if (i <= 0) return null;
		var token = s[..i];
		if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
		{
			return v;
		}
		return null;
	}

	public void SetPresetsAuto(double startMHz, double endMHz, double stepMHz)
	{
		// Use integer Hz math (step in 100kHz) to avoid accumulation errors.
		// 0.1 MHz => 100000 Hz.
		var startHz = (long)Math.Round(startMHz * 1_000_000.0);
		var endHz = (long)Math.Round(endMHz * 1_000_000.0);
		var stepHz = (long)Math.Round(stepMHz * 1_000_000.0);

		if (startHz <= 0 || endHz <= 0 || stepHz <= 0 || startHz > endHz)
		{
			throw new ArgumentOutOfRangeException("Invalid preset range");
		}

		// Clamp to 0.1MHz steps by rounding to nearest 100kHz.
		const long QuantumHz = 100_000;
		startHz = (long)Math.Round(startHz / (double)QuantumHz) * QuantumHz;
		endHz = (long)Math.Round(endHz / (double)QuantumHz) * QuantumHz;
		stepHz = (long)Math.Round(stepHz / (double)QuantumHz) * QuantumHz;
		if (stepHz <= 0) stepHz = QuantumHz;

		var list = new List<long>();
		for (long hz = startHz; hz <= endHz; hz += stepHz)
		{
			list.Add(hz);
			if (list.Count > 10000) break; // safety
		}

		lock (_gate)
		{
			_presets.Clear();
			foreach (var hz in list)
			{
				_presets.Add(new PresetItem { Hz = hz, Name = "-" });
			}
			_presets.Sort((a, b) => a.Hz.CompareTo(b.Hz));
		}
		TrySavePresetsToDisk();
	}

	public bool IsHlsReady(out string? reason)
	{
		lock (_gate)
		{
			if (_delivery != "hls")
			{
				reason = "delivery is not hls";
				return false;
			}
		}

		try
		{
			var playlist = Path.Combine(_hlsRoot, "stream.m3u8");
			if (!File.Exists(playlist))
			{
				reason = "playlist not created yet";
				return false;
			}
			var len = new FileInfo(playlist).Length;
			if (len < 20)
			{
				reason = "playlist is empty";
				return false;
			}
			reason = null;
			return true;
		}
		catch (Exception ex)
		{
			reason = ex.Message;
			return false;
		}
	}

	public void SetFrequencyHz(long freqHz, bool restartActiveStream)
	{
		var cleanupHls = false;
		lock (_gate)
		{
			_freqHz = freqHz;
			_lastError = null;
			_stereoDetected = null;
			_pilotLevel = null;
			_stereoUpdatedUtc = null;
			if (restartActiveStream)
			{
				try { _streamCts?.Cancel(); } catch { }
				// Restart HLS too; otherwise the running softfm keeps the old frequency.
				try { _hlsCts?.Cancel(); } catch { }
				cleanupHls = true;
			}
		}
		if (cleanupHls)
		{
			CleanupHlsFolder();
		}
	}

	public void SetFrequencyHz(long freqHz) => SetFrequencyHz(freqHz, restartActiveStream: false);

	public object GetStatus()
	{
		lock (_gate)
		{
			var hlsRunning = _hlsTask is not null && !_hlsTask.IsCompleted;
			var scanRunning = _scanTask is not null && !_scanTask.IsCompleted;
			var stereoMode = _stereoMode switch
			{
				StereoMode.Mono => "mono",
				StereoMode.ForceStereo => "stereo",
				_ => "auto",
			};
			return new
			{
				freqHz = _freqHz,
				streaming = _isStreaming || hlsRunning,
				hlsRunning,
				scanRunning,
				scanDone = _scanDone,
				scanTotal = _scanTotal,
				stereoDetected = _stereoDetected,
				pilotLevel = _pilotLevel,
				stereoUpdatedUtc = _stereoUpdatedUtc,
				lastError = _lastError,
				delivery = _delivery,
				stereoMode,
				forceStereo = _stereoMode == StereoMode.ForceStereo,
			};
		}
	}

	public object GetConfig()
	{
		lock (_gate)
		{
			var stereoMode = _stereoMode switch
			{
				StereoMode.Mono => "mono",
				StereoMode.ForceStereo => "stereo",
				_ => "auto",
			};
			return new
			{
				delivery = _delivery,
				format = _streamFormat,
				bufferSeconds = _bufferSeconds,
				hlsBitrateKbps = _hlsBitrateKbps,
				rtlGainDb = _rtlGainDb,
				rtlAgc = _rtlAgc,
				stereoMode,
				forceStereo = _stereoMode == StereoMode.ForceStereo,
			};
		}
	}

	public void SetStreamingConfig(string? format, double? bufferSeconds, string? delivery, int? hlsBitrateKbps, double? rtlGainDb, bool? rtlAgc, string? stereoMode, bool? forceStereo, bool restartActiveStream)
	{
		var shouldRestartHls = false;
		var cleanupHls = false;
		lock (_gate)
		{
			if (delivery is not null)
			{
				_delivery = NormalizeDelivery(delivery);
			}
			if (format is not null)
			{
				_streamFormat = NormalizeFormat(format);
			}
			if (bufferSeconds is not null)
			{
				_bufferSeconds = Math.Clamp(bufferSeconds.Value, 0, 60);
			}
			if (hlsBitrateKbps is not null)
			{
				_hlsBitrateKbps = Math.Clamp(hlsBitrateKbps.Value, 32, 512);
			}
			if (rtlGainDb is not null)
			{
				_rtlGainDb = Math.Clamp(rtlGainDb.Value, 0, 100);
			}
			if (rtlAgc is not null)
			{
				_rtlAgc = rtlAgc.Value;
			}
			if (stereoMode is not null)
			{
				_stereoMode = NormalizeStereoMode(stereoMode);
			}
			else if (forceStereo is not null)
			{
				// Back-compat: older clients only send forceStereo boolean.
				_stereoMode = forceStereo.Value ? StereoMode.ForceStereo : StereoMode.Auto;
			}

			_lastError = null;

			if (restartActiveStream)
			{
				try { _streamCts?.Cancel(); } catch { }
				try { _hlsCts?.Cancel(); } catch { }
				shouldRestartHls = _delivery == "hls";
				cleanupHls = shouldRestartHls;
			}
		}
		if (cleanupHls)
		{
			CleanupHlsFolder();
		}

		if (shouldRestartHls)
		{
			EnsureHlsRunning();
		}
	}

	public void EnsureDeliveryStarted()
	{
		lock (_gate)
		{
			if (_delivery != "hls")
			{
				return;
			}
		}
		EnsureHlsRunning();
	}

	public void Stop()
	{
		var cleanupHls = false;
		lock (_gate)
		{
			_lastError = null;
			try { _streamCts?.Cancel(); } catch { }
			try { _streamCts?.Dispose(); } catch { }
			_streamCts = null;
			_isStreaming = false;

			_hlsRestartQueued = false;
			try { _hlsCts?.Cancel(); } catch { }
			cleanupHls = true;
		}
		if (cleanupHls)
		{
			CleanupHlsFolder();
		}
	}

	private void EnsureHlsRunning()
	{
		Task? runningTask;
		lock (_gate)
		{
			if (_delivery != "hls")
			{
				return;
			}

			runningTask = _hlsTask;
			if (runningTask is not null && !runningTask.IsCompleted)
			{
				if (!_hlsRestartQueued)
				{
					_hlsRestartQueued = true;
					_ = runningTask.ContinueWith(_ =>
					{
						var doRestart = false;
						lock (_gate)
						{
							doRestart = _hlsRestartQueued;
							_hlsRestartQueued = false;
						}
						if (doRestart)
						{
							EnsureHlsRunning();
						}
					}, TaskScheduler.Default);
				}
				return;
			}

			try { _hlsCts?.Cancel(); } catch { }
			try { _hlsCts?.Dispose(); } catch { }
			_hlsCts = new CancellationTokenSource();
			var token = _hlsCts.Token;
			_hlsTask = Task.Run(() => RunHlsLoopAsync(token), token);
		}
	}

	private async Task RunHlsLoopAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await RunHlsOnceAsync(ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				lock (_gate) { _lastError = "HLS: " + ex.Message; }
				// Backoff a bit, then retry.
				try { await Task.Delay(1000, ct); } catch { }
			}
		}
	}

	private async Task RunHlsOnceAsync(CancellationToken ct)
	{
		long freqHz;
		double bufferSeconds;
		int bitrateKbps;
		double? rtlGainDb;
		bool rtlAgc;
		StereoMode stereoMode;
		lock (_gate)
		{
			freqHz = _freqHz;
			bufferSeconds = _bufferSeconds;
			bitrateKbps = _hlsBitrateKbps;
			rtlGainDb = _rtlGainDb;
			rtlAgc = _rtlAgc;
			stereoMode = _stereoMode;
			_lastError = null;
			// Default to MONO until softfm reports stereo lock.
			_stereoDetected = false;
			_pilotLevel = null;
			_stereoUpdatedUtc = DateTimeOffset.UtcNow;
		}

		Directory.CreateDirectory(_hlsRoot);
		CleanupHlsFolder();

		var repoRoot = FindRepoRoot();
		var softfmPath = Path.Combine(repoRoot, "build-ucrt64", "softfm.exe");
		if (!File.Exists(softfmPath))
		{
			throw new FileNotFoundException($"softfm.exe not found: {softfmPath}. Build native binary first.");
		}

		var msysUcrtBin = @"C:\msys64\ucrt64\bin";
		var softfm = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = softfmPath,
				Arguments = $"{BuildStereoArgs(stereoMode)}-t rtlsdr -r 48000 -c \"{BuildRtlSdrConfig(freqHz, rtlGainDb, rtlAgc)}\" -R -",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
		};
		if (Directory.Exists(msysUcrtBin))
		{
			var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			softfm.StartInfo.Environment["PATH"] = msysUcrtBin + ";" + path;
		}

		var segmentSeconds = 1.0;
		var listSize = (int)Math.Clamp(Math.Ceiling(Math.Max(bufferSeconds, 1.0) / segmentSeconds), 2, 20);
		// IMPORTANT: Use relative filenames so the playlist contains URL-friendly paths.
		var playlistFile = "stream.m3u8";
		var initFile = "init.mp4";
		var segmentPattern = "seg_%05d.m4s";
		var inputChannels = stereoMode == StereoMode.Mono ? 1 : 2;

		// Prefer MediaFoundation AAC encoder for speed on Windows.
		// NOTE: softfm outputs mono (1ch) when --mono is used, otherwise interleaved stereo (2ch).
		var ffmpegArgs = $"-hide_banner -loglevel warning -f s16le -ar 48000 -ac {inputChannels} -i pipe:0 -c:a aac_mf -b:a {bitrateKbps}k " +
			$"-f hls -hls_time {segmentSeconds:0.0} -hls_list_size {listSize} " +
			$"-hls_flags delete_segments+append_list+independent_segments+omit_endlist -hls_allow_cache 0 " +
			$"-hls_segment_type fmp4 -hls_fmp4_init_filename \"{initFile}\" -hls_segment_filename \"{segmentPattern}\" " +
			$"\"{playlistFile}\"";

		var ffmpeg = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "ffmpeg.exe",
				Arguments = ffmpegArgs,
				WorkingDirectory = _hlsRoot,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
		};

		try
		{
			if (!softfm.Start())
			{
				throw new InvalidOperationException("Failed to start softfm.exe");
			}
			if (!ffmpeg.Start())
			{
				throw new InvalidOperationException("Failed to start ffmpeg.exe (is it in PATH?)");
			}

			var softfmErrTask = Task.Run(async () =>
			{
				try { await DrainAndParseSoftfmStderrAsync(softfm.StandardError.BaseStream, ct); }
				catch { }
			}, ct);
			var ffmpegErrTask = Task.Run(async () =>
			{
				try { await ffmpeg.StandardError.BaseStream.CopyToAsync(Stream.Null, 16 * 1024, ct); }
				catch { }
			}, ct);

			var pumpTask = Task.Run(async () =>
			{
				try
				{
					await softfm.StandardOutput.BaseStream.CopyToAsync(ffmpeg.StandardInput.BaseStream, 64 * 1024, ct);
				}
				catch (OperationCanceledException) { }
				catch { }
				finally
				{
					try { ffmpeg.StandardInput.Close(); } catch { }
				}
			}, ct);

			// Wait until cancelled or either process exits.
			while (!ct.IsCancellationRequested)
			{
				if (softfm.HasExited || ffmpeg.HasExited)
				{
					break;
				}
				await Task.Delay(200, ct);
			}

			try { await pumpTask; } catch { }
			try { await Task.WhenAll(softfmErrTask, ffmpegErrTask); } catch { }
		}
		finally
		{
			TryKill(softfm);
			TryKill(ffmpeg);
			softfm.Dispose();
			ffmpeg.Dispose();
		}
	}

	private void CleanupHlsFolder()
	{
		try
		{
			var dir = new DirectoryInfo(_hlsRoot);
			if (!dir.Exists) return;
			foreach (var f in dir.GetFiles("*"))
			{
				try { f.Delete(); } catch { }
			}
		}
		catch { }
	}

	public StreamLease BeginStreaming()
	{
		lock (_gate)
		{
			// Cancel any existing stream (switch).
			try { _streamCts?.Cancel(); } catch { }
			try { _streamCts?.Dispose(); } catch { }

			_streamCts = new CancellationTokenSource();
			_isStreaming = true;
			var gen = ++_streamGeneration;
			return new StreamLease(gen, _streamCts.Token);
		}
	}

	public void EndStreaming(long generation)
	{
		lock (_gate)
		{
			// Only the currently active generation can clear streaming state.
			if (generation != _streamGeneration)
			{
				return;
			}

			_isStreaming = false;
			try { _streamCts?.Dispose(); } catch { }
			_streamCts = null;
		}
	}

	public async Task StreamToAsync(Stream responseBody, CancellationToken requestCt, CancellationToken streamCt, string? requestedFormat)
	{
		const int MinSegmentBytes = 16 * 1024;
		const int FlushEveryBytes = 32 * 1024;

		long freqHz;
		string fmt;
		double bufferSeconds;
		double? rtlGainDb;
		bool rtlAgc;
		StereoMode stereoMode;
		lock (_gate)
		{
			freqHz = _freqHz;
			fmt = ResolveFormat(requestedFormat);
			bufferSeconds = _bufferSeconds;
			rtlGainDb = _rtlGainDb;
			rtlAgc = _rtlAgc;
			stereoMode = _stereoMode;
			_lastError = null;
			// Default to MONO until softfm reports stereo lock.
			_stereoDetected = false;
			_pilotLevel = null;
			_stereoUpdatedUtc = DateTimeOffset.UtcNow;
		}
		var inputChannels = stereoMode == StereoMode.Mono ? 1 : 2;

		var bitrateKbps = fmt switch
		{
			"aac" => 192,
			"opus" => 96,
			_ => 192,
		};
		var bufferBytes = bufferSeconds <= 0
			? 0
			: (int)Math.Clamp((bufferSeconds * bitrateKbps * 1000.0) / 8.0, 0, 8 * 1024 * 1024);

		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestCt, streamCt);
		var linkedToken = linkedCts.Token;

		var repoRoot = FindRepoRoot();
		var softfmPath = Path.Combine(repoRoot, "build-ucrt64", "softfm.exe");
		if (!File.Exists(softfmPath))
		{
			throw new FileNotFoundException($"softfm.exe not found: {softfmPath}. Build native binary first.");
		}

		var msysUcrtBin = @"C:\msys64\ucrt64\bin";

		// softfm writes raw S16LE PCM to stdout; ffmpeg reads stdin and outputs MP3 to stdout.
		var softfm = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = softfmPath,
				Arguments = $"{BuildStereoArgs(stereoMode)}-t rtlsdr -r 48000 -c \"{BuildRtlSdrConfig(freqHz, rtlGainDb, rtlAgc)}\" -R -",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
		};

		if (Directory.Exists(msysUcrtBin))
		{
			var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			softfm.StartInfo.Environment["PATH"] = msysUcrtBin + ";" + path;
		}

		var ffmpeg = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "ffmpeg.exe",
				Arguments = BuildFfmpegArgs(fmt, inputChannels),
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
		};

		try
		{
			if (!softfm.Start())
			{
				throw new InvalidOperationException("Failed to start softfm.exe");
			}
			if (!ffmpeg.Start())
			{
				throw new InvalidOperationException("Failed to start ffmpeg.exe (is it in PATH?)");
			}

			// IMPORTANT: softfm/ffmpeg write continuous logs to stderr.
			// If stderr is redirected but not read, the OS pipe buffer can fill up and stall the process.
			// Drain stderr in the background to keep the pipeline flowing.
			var softfmErrTask = Task.Run(async () =>
			{
				try { await DrainAndParseSoftfmStderrAsync(softfm.StandardError.BaseStream, linkedToken); }
				catch { }
			}, linkedToken);
			var ffmpegErrTask = Task.Run(async () =>
			{
				try { await ffmpeg.StandardError.BaseStream.CopyToAsync(Stream.Null, 16 * 1024, linkedToken); }
				catch { }
			}, linkedToken);

			// Pump softfm stdout -> ffmpeg stdin.
			var pumpTask = Task.Run(async () =>
			{
				try
				{
					await softfm.StandardOutput.BaseStream.CopyToAsync(ffmpeg.StandardInput.BaseStream, 64 * 1024, linkedToken);
				}
				catch (OperationCanceledException) { }
				catch (Exception) { }
				finally
				{
					try { ffmpeg.StandardInput.Close(); } catch { }
				}
			}, linkedToken);

			if (bufferBytes <= 0)
			{
				// No buffering.
				try
				{
					await ffmpeg.StandardOutput.BaseStream.CopyToAsync(responseBody, 64 * 1024, linkedToken);
				}
				catch (OperationCanceledException) { }
				catch (IOException) { }
			}
			else
			{
				// Buffer ffmpeg stdout -> HTTP response to absorb short client/network stalls.
				// (If the client stops reading briefly, we can keep consuming from ffmpeg until the buffer fills.)
				var pipe = new Pipe(new PipeOptions(
					pauseWriterThreshold: bufferBytes,
					resumeWriterThreshold: bufferBytes / 2,
					minimumSegmentSize: MinSegmentBytes,
					useSynchronizationContext: false));

				var bytesSinceFlush = 0;

				var producerTask = Task.Run(async () =>
				{
					Exception? error = null;
					try
					{
						var src = ffmpeg.StandardOutput.BaseStream;
						while (true)
						{
							var mem = pipe.Writer.GetMemory(MinSegmentBytes);
							var read = await src.ReadAsync(mem, linkedToken);
							if (read <= 0)
							{
								break;
							}

							pipe.Writer.Advance(read);
							var flush = await pipe.Writer.FlushAsync(linkedToken);
							if (flush.IsCanceled || flush.IsCompleted)
							{
								break;
							}
						}
					}
					catch (OperationCanceledException) { }
					catch (IOException) { }
					catch (Exception ex) { error = ex; }
					finally
					{
						try { await pipe.Writer.CompleteAsync(error); } catch { }
					}
				}, linkedToken);

				var consumerTask = Task.Run(async () =>
				{
					Exception? error = null;
					try
					{
						while (true)
						{
							var result = await pipe.Reader.ReadAsync(linkedToken);
							var buffer = result.Buffer;
							if (!buffer.IsEmpty)
							{
								foreach (var segment in buffer)
								{
									await responseBody.WriteAsync(segment, linkedToken);
									bytesSinceFlush += segment.Length;
									if (bytesSinceFlush >= FlushEveryBytes)
									{
										try { await responseBody.FlushAsync(linkedToken); } catch { }
										bytesSinceFlush = 0;
									}
								}
							}
							pipe.Reader.AdvanceTo(buffer.End);
							if (result.IsCompleted)
							{
								break;
							}
						}

						try { await responseBody.FlushAsync(linkedToken); } catch { }
					}
					catch (OperationCanceledException) { }
					catch (IOException) { }
					catch (Exception ex) { error = ex; }
					finally
					{
						try { await pipe.Reader.CompleteAsync(error); } catch { }
					}
				}, linkedToken);

				try
				{
					await Task.WhenAll(producerTask, consumerTask);
				}
				catch (OperationCanceledException)
				{
					// Client disconnected or stream switched.
				}
			}

			// Ensure pump finishes.
			await pumpTask;

			// Ensure stderr drains finish (best-effort).
			try { await Task.WhenAll(softfmErrTask, ffmpegErrTask); } catch { }
		}
		catch (Exception ex)
		{
			// If the client disconnects, treat it as normal.
			if (linkedToken.IsCancellationRequested)
			{
				return;
			}

			lock (_gate) { _lastError = ex.Message; }
			throw;
		}
		finally
		{
			TryKill(softfm);
			TryKill(ffmpeg);

			softfm.Dispose();
			ffmpeg.Dispose();
		}
	}

	private async Task DrainAndParseSoftfmStderrAsync(Stream stderr, CancellationToken ct)
	{
		var buf = new byte[4096];
		var tail = string.Empty;
		while (!ct.IsCancellationRequested)
		{
			int read;
			try
			{
				read = await stderr.ReadAsync(buf, ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			if (read <= 0)
			{
				break;
			}

			// softfm messages are ASCII; UTF-8 decoding is safe here.
			var chunk = Encoding.UTF8.GetString(buf, 0, read);
			if (chunk.Length == 0)
			{
				continue;
			}
			tail += chunk;
			if (tail.Length > 8192)
			{
				tail = tail[^8192..];
			}

			TryUpdateStereoStateFromSoftfmText(tail);
		}
	}

	private void TryUpdateStereoStateFromSoftfmText(string text)
	{
		// Example messages in main.cpp:
		//   got stereo signal (pilot level = 0.012345)
		//   lost stereo signal
		var gotIdx = text.LastIndexOf("got stereo signal", StringComparison.OrdinalIgnoreCase);
		var lostIdx = text.LastIndexOf("lost stereo signal", StringComparison.OrdinalIgnoreCase);

		if (gotIdx < 0 && lostIdx < 0)
		{
			return;
		}

		if (gotIdx > lostIdx)
		{
			double? pilot = null;
			var pfx = "pilot level =";
			var pIdx = text.IndexOf(pfx, gotIdx, StringComparison.OrdinalIgnoreCase);
			if (pIdx >= 0)
			{
				pIdx += pfx.Length;
				while (pIdx < text.Length && char.IsWhiteSpace(text[pIdx])) pIdx++;
				var end = pIdx;
				while (end < text.Length)
				{
					var c = text[end];
					if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')) break;
					end++;
				}
				if (end > pIdx)
				{
					var s = text[pIdx..end];
					if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
					{
						pilot = v;
					}
				}
			}

			lock (_gate)
			{
				_stereoDetected = true;
				if (pilot is not null) _pilotLevel = pilot;
				_stereoUpdatedUtc = DateTimeOffset.UtcNow;
			}
		}
		else
		{
			lock (_gate)
			{
				_stereoDetected = false;
				_stereoUpdatedUtc = DateTimeOffset.UtcNow;
			}
		}
	}

	public static bool IsSupportedFormat(string fmt)
	{
		var n = NormalizeFormat(fmt);
		return n is "mp3" or "aac" or "opus";
	}

	public static bool IsSupportedDelivery(string delivery)
	{
		var n = NormalizeDelivery(delivery);
		return n is "direct" or "hls";
	}

	public static bool IsSupportedStereoMode(string stereoMode)
	{
		var n = NormalizeStereoModeRaw(stereoMode);
		return n is "auto" or "stereo" or "mono" or "on" or "off";
	}

	public string ResolveFormat(string? requested)
	{
		if (!string.IsNullOrWhiteSpace(requested) && IsSupportedFormat(requested))
		{
			return NormalizeFormat(requested);
		}
		lock (_gate)
		{
			return _streamFormat;
		}
	}

	private static string BuildRtlSdrConfig(long freqHz, double? gainDb, bool rtlAgc)
	{
		// RtlSdrSource expects: freq=<Hz>,srate=<Hz>,gain=<dB or auto>
		// Gain is in dB (e.g. 19.7). It is validated against the device's supported list.
		var gainPart = gainDb is null
			? "gain=auto"
			: $"gain={gainDb.Value.ToString("0.0", CultureInfo.InvariantCulture)}";
		var agcPart = rtlAgc ? ",agc" : string.Empty;
		return $"freq={freqHz},srate=1000000,{gainPart}{agcPart}";
	}

	private static string NormalizeFormat(string fmt)
		=> (fmt ?? string.Empty).Trim().ToLowerInvariant();

	private static string NormalizeDelivery(string delivery)
		=> (delivery ?? string.Empty).Trim().ToLowerInvariant();

	private static string NormalizeStereoModeRaw(string stereoMode)
		=> (stereoMode ?? string.Empty).Trim().ToLowerInvariant();

	private static StereoMode NormalizeStereoMode(string stereoMode)
	{
		var n = NormalizeStereoModeRaw(stereoMode);
		return n switch
		{
			"mono" => StereoMode.Mono,
			"stereo" => StereoMode.ForceStereo,
			// Legacy values
			"on" => StereoMode.ForceStereo,
			"off" => StereoMode.Auto,
			_ => StereoMode.Auto,
		};
	}

	private static string BuildStereoArgs(StereoMode mode)
	{
		return mode switch
		{
			StereoMode.Mono => "--mono ",
			StereoMode.ForceStereo => "--force-stereo ",
			_ => string.Empty,
		};
	}

	private static string BuildFfmpegArgs(string fmt, int inputChannels)
	{
		// Input is raw S16LE @ 48k. softfm outputs 2ch normally, 1ch in --mono mode.
		inputChannels = inputChannels == 1 ? 1 : 2;
		return fmt switch
		{
			// AAC: browsers typically expect AAC in MP4/M4A, not raw ADTS.
			// Use fragmented MP4 for streaming over HTTP without Content-Length.
			"aac" => $"-hide_banner -loglevel warning -f s16le -ar 48000 -ac {inputChannels} -i pipe:0 -c:a aac -b:a 192k -movflags +frag_keyframe+empty_moov+default_base_moof -muxdelay 0 -muxpreload 0 -flush_packets 1 -f mp4 pipe:1",

			// Opus: Ogg/Opus is not supported by some browsers (notably Safari). WebM/Opus has broader support.
			"opus" => $"-hide_banner -loglevel warning -f s16le -ar 48000 -ac {inputChannels} -i pipe:0 -c:a libopus -b:a 96k -vbr on -compression_level 10 -application audio -cluster_time_limit 1000 -cluster_size_limit 0 -flush_packets 1 -f webm pipe:1",
			_ => $"-hide_banner -loglevel warning -f s16le -ar 48000 -ac {inputChannels} -i pipe:0 -c:a libmp3lame -b:a 192k -flush_packets 1 -f mp3 pipe:1",
		};
	}

	private static void TryKill(Process p)
	{
		try
		{
			if (!p.HasExited)
			{
				p.Kill(entireProcessTree: true);
			}
		}
		catch { }
	}

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		for (var i = 0; i < 8 && dir is not null; i++)
		{
			var candidate = Path.Combine(dir.FullName, "CMakeLists.txt");
			if (File.Exists(candidate))
			{
				return dir.FullName;
			}
			dir = dir.Parent;
		}

		// Fallback to current working directory.
		return Directory.GetCurrentDirectory();
	}
}

internal readonly record struct StreamLease(long Generation, CancellationToken Token);

internal readonly record struct ScanResult(
	double FreqMHz,
	double? TunedFreqMHz,
	double? IfDb,
	double? BbDb,
	double? AudioDb,
	bool StereoDetected,
	double? PilotLevel);

internal readonly record struct ScanStats(
	double? TunedFreqMHz,
	double? IfDb,
	double? BbDb,
	double? AudioDb,
	bool StereoDetected,
	double? PilotLevel);
