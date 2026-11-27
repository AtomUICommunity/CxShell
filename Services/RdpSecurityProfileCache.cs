using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CxShell.Services;

/// <summary>
/// Remembers which RDP security layer worked for a host so the next connect
/// goes straight to that profile instead of walking the fallback list. The
/// native bridge reports the winner as a status line; the cache is persisted
/// so a restart keeps the benefit.
/// </summary>
internal static class RdpSecurityProfileCache
{
    private const string SelectedPrefix = "RDP security profile selected mode=";
    private static readonly object Gate = new();
    private static readonly HashSet<string> KnownProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "negotiate", "nla", "tls", "rdp"
    };

    private static readonly string CachePath = Path.Combine(
        AppDataPaths.GetLocalCacheDirectory(),
        "rdp-security-profiles.json");

    private static Dictionary<string, string>? _profiles;

    /// <summary>
    /// Parses the status line emitted by the native bridge when a handshake
    /// succeeds. Exposed separately so it can be unit tested without I/O.
    /// </summary>
    public static bool TryParseProfile(string? status, out string profile)
    {
        profile = string.Empty;
        if (string.IsNullOrWhiteSpace(status) ||
            !status.StartsWith(SelectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = status[SelectedPrefix.Length..].Trim().TrimEnd('.');
        if (!KnownProfiles.Contains(value))
            return false;

        profile = value.ToLowerInvariant();
        return true;
    }

    public static string? Get(string host, int port)
    {
        var key = BuildKey(host, port);
        if (key == null)
            return null;

        lock (Gate)
        {
            EnsureLoaded();
            return _profiles != null && _profiles.TryGetValue(key, out var profile) &&
                   KnownProfiles.Contains(profile)
                ? profile
                : null;
        }
    }

    public static void Store(string host, int port, string profile)
    {
        var key = BuildKey(host, port);
        if (key == null || !KnownProfiles.Contains(profile))
            return;

        lock (Gate)
        {
            EnsureLoaded();
            _profiles ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_profiles.TryGetValue(key, out var existing) &&
                string.Equals(existing, profile, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _profiles[key] = profile.ToLowerInvariant();
            try
            {
                var directory = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(_profiles));
            }
            catch
            {
                // The cache is an optimization; never fail a connection over it.
            }
        }
    }

    private static string? BuildKey(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        return $"{host.Trim().ToLowerInvariant()}:{(port > 0 ? port : 3389)}";
    }

    private static void EnsureLoaded()
    {
        if (_profiles != null)
            return;

        var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(CachePath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
                if (parsed != null)
                {
                    foreach (var pair in parsed)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) && KnownProfiles.Contains(pair.Value))
                            loaded[pair.Key] = pair.Value.ToLowerInvariant();
                    }
                }
            }
        }
        catch
        {
            // A corrupt cache just means we fall back to auto negotiation.
        }

        _profiles = loaded;
    }
}
