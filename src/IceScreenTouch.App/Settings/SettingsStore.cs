// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IceScreenTouch.App.Settings;

/// <summary>Where the app keeps its files: %LOCALAPPDATA%\IceScreenTouch on Windows.</summary>
public static class AppPaths
{
    /// <summary>Environment variable that moves the data folder (tests of a published build, portable use).</summary>
    public const string DataDirVariable = "ICESCREENTOUCH_DATA_DIR";

    /// <summary>The app's data folder.</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } dir
        ? Path.GetFullPath(dir)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IceScreenTouch");

    /// <summary>
    /// The data folder under the app's former name (iScrMirror), or null when <see cref="DataDirVariable"/> moved
    /// the data folder.
    /// </summary>
    public static string? FormerRoot { get; } = Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 }
        ? null
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iScrMirror");

    /// <summary>
    /// Maps a path of the app's own files to the one a program outside the app (Explorer) can open. The platform head
    /// sets it at startup: a packaged Windows app's files under %LOCALAPPDATA% live in the package's redirected folder.
    /// </summary>
    public static Func<string, string> ForExternalProgram { get; set; } = path => path;

    /// <summary>settings.json.</summary>
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Log folder (the app and uxplay).</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// First start after the rename: copies settings.json from <paramref name="formerRoot"/> into
    /// <paramref name="root"/> when <paramref name="root"/> has none yet. The former folder is left as it is (its
    /// logs are not copied). Returns the file that was copied, or null.
    /// </summary>
    public static string? ImportFormerSettings(string root, string? formerRoot)
    {
        if (formerRoot is null)
        {
            return null;
        }

        var target = Path.Combine(root, "settings.json");
        var source = Path.Combine(formerRoot, "settings.json");
        if (File.Exists(target) || !File.Exists(source))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(root);
            File.Copy(source, target);
            return source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with the defaults is fine.
            return null;
        }
    }
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    /// <summary>Uses <paramref name="path"/> (default <see cref="AppPaths.Settings"/>).</summary>
    public SettingsStore(string? path = null)
    {
        Path = path ?? AppPaths.Settings;
    }

    /// <summary>The settings file.</summary>
    public string Path { get; }

    /// <summary>Where the unreadable file is copied before the first save over it.</summary>
    public string UnreadPath => Path + ".unread";

    // Load could not read the file: the next save copies it to UnreadPath first.
    private bool _keepUnread;

    /// <summary>
    /// Reads the settings. A missing file gives the defaults. A broken file is renamed to
    /// settings.json.bad (so that it is not lost) and the defaults are used; <paramref name="problem"/> says why.
    /// </summary>
    public AppSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(Path))
        {
            return new AppSettings();
        }

        if (ReadText(out var readError) is not { } json)
        {
            // Locked or not readable (a scanner or a backup tool holding it): run with the defaults instead of
            // failing to start; the file is left as it is.
            problem = $"cannot read the settings, using the defaults for now ({readError}); the file is kept as {UnreadPath} before anything is saved over it: restart the app to read it again, or copy it back";
            _keepUnread = true;
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize(WithDefaults(json), SettingsJsonContext.Default.AppSettings);
            return (settings ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var bad = Path + ".bad";
            try
            {
                File.Move(Path, bad, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
            }

            problem = $"settings were unreadable and were reset (kept as {bad}): {ex.Message}";
            return new AppSettings();
        }
    }

    // The file's text, trying a few times while another program holds it; null (and why) if it stays unreadable.
    private string? ReadText(out string? error)
    {
        error = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    error = ex.Message;
                    return null;
                }

                Thread.Sleep(100);
            }
        }
    }

    // The generated deserializer sets every init-only property, so a property missing from the file (an older
    // file, or a hand-edited one) would get default(T) (false, 0) instead of its declared default. Fill the gaps
    // with the defaults first.
    private static JsonNode WithDefaults(string json)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var loaded = JsonNode.Parse(json, documentOptions: options);
        if (loaded is not JsonObject file)
        {
            return loaded ?? throw new JsonException("the file is empty");
        }

        var merged = JsonSerializer.SerializeToNode(new AppSettings(), SettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (var (name, value) in file)
        {
            var key = merged.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            merged[key] = value?.DeepClone();
        }

        return merged;
    }

    /// <summary>Writes the settings (to a temporary file first, so a crash never leaves half a file).</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        if (_keepUnread && File.Exists(Path))
        {
            // The file could not be read at startup, so these are mostly defaults: keep the user's real settings
            // before writing over them. If that copy fails too, this throws and nothing is overwritten.
            File.Copy(Path, UnreadPath, overwrite: true);
        }

        _keepUnread = false;
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), SettingsJsonContext.Default.AppSettings));
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>Source-generated JSON (no reflection; trimming-safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
