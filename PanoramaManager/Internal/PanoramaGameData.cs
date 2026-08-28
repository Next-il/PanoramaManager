using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace PanoramaManager.Internal;

/// <summary>
/// Signature and offset source for the <c>CCSCustomHudLayout</c> natives.
///
/// <para>Reads <c>addons/counterstrikesharp/gamedata/panoramamanager.json</c> if it is there, and falls back
/// to compiled-in copies of the same values if it is not - so the plugin runs with no setup, but a
/// CS2 update is fixed by editing a text file on the server instead of rebuilding.</para>
///
/// <para>The file's schema matches CounterStrikeSharp's own gamedata format so it can sit in that
/// folder harmlessly, but it is parsed here rather than through <c>GameData</c> - we want the
/// space-separated <c>"55 48 ? ?"</c> form passed through untouched to
/// <c>MemoryFunctionVoid</c>, which is the form it accepts.</para>
/// </summary>
internal static class PanoramaGameData
{
    private const string FileName = "panoramamanager.json";

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static Dictionary<string, string>? _signatures;
    private static Dictionary<string, int>?    _offsets;
    private static string?                     _source;

    /// <summary>Where the values came from, for the startup log.</summary>
    internal static string Source => _source ?? "(not loaded)";

    internal static string? Signature(string key)
    {
        Load();

        return _signatures!.TryGetValue(key, out var sig) && !string.IsNullOrWhiteSpace(sig) ? sig : null;
    }

    internal static int Offset(string key)
    {
        Load();

        return _offsets!.TryGetValue(key, out var value) ? value : 0;
    }

    private static void Load()
    {
        if (_signatures is not null)
            return;

        _signatures = new Dictionary<string, string>(Embedded.Signatures(IsWindows));
        _offsets    = new Dictionary<string, int>(Embedded.Offsets(IsWindows));
        _source     = "compiled-in defaults";

        if (FindFile() is not { } path)
            return;

        try
        {
            using var doc = JsonDocument.Parse(
                StripComments(File.ReadAllText(path)),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            var platform = IsWindows ? "windows" : "linux";
            var found    = 0;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("signatures", out var sigs)
                    && sigs.TryGetProperty(platform, out var sig)
                    && sig.GetString() is { Length: > 0 } text)
                {
                    _signatures[entry.Name] = text;
                    found++;
                }
                else if (entry.Value.TryGetProperty("offsets", out var offs)
                         && offs.TryGetProperty(platform, out var off)
                         && off.TryGetInt32(out var value))
                {
                    _offsets[entry.Name] = value;
                    found++;
                }
            }

            _source = $"{path} ({found} entries, {platform})";
        }
        catch (Exception e)
        {
            _source = $"compiled-in defaults - {FileName} failed to parse: {e.Message}";
        }
    }

    /// <summary>CounterStrikeSharp's gamedata folder, wherever this server keeps it. Probed rather
    /// than assumed because <c>Server.GameDirectory</c> points at different levels of the tree
    /// depending on how the server was laid out.</summary>
    private static string? FindFile()
    {
        string root;
        try
        {
            root = Server.GameDirectory;
        }
        catch
        {
            return null; // Called outside a running server (unit tests).
        }

        string[] candidates =
        [
            Path.Combine(root, "csgo", "addons", "counterstrikesharp", "gamedata", FileName),
            Path.Combine(root, "addons", "counterstrikesharp", "gamedata", FileName),
            Path.Combine(AppContext.BaseDirectory, FileName),
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Unreadable path - try the next.
            }
        }

        return null;
    }

    /// <summary>JsonDocument's comment skipping handles <c>//</c> inside the document, but not a
    /// leading comment block before the opening brace. Trim to the first <c>{</c>.</summary>
    private static string StripComments(string text)
    {
        var brace = text.IndexOf('{');

        return brace <= 0 ? text : text[brace..];
    }
}
