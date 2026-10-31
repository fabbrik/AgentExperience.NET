using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentExperience.Cli.Configuration;

/// <summary>
/// Which configs a person has approved to drive the verifier, direnv-style. A config names a command, so a config from an
/// untrusted checkout (or one an agent edited) is code execution: the verifier runs, and a reported exit code is honoured,
/// only for a config whose exact bytes were approved with <c>aexp trust</c>. The record lives outside every project, in
/// the user's own config directory (<c>$XDG_CONFIG_HOME/aexp/trusted.json</c>, else <c>~/.config/aexp/trusted.json</c>;
/// <c>%APPDATA%\aexp\trusted.json</c> on Windows), owner-only. Editing the config revokes its trust until it is approved
/// again.
/// </summary>
internal sealed class ConfigTrust(string file)
{
    public string File { get; } = file;

    /// <summary>The trust file for the user <paramref name="environment"/> describes.</summary>
    public static ConfigTrust ForUser(Func<string, string?> environment)
    {
        string directory;
        if (OperatingSystem.IsWindows())
        {
            var appData = environment("APPDATA");
            directory = !string.IsNullOrWhiteSpace(appData) && Path.IsPathFullyQualified(appData)
                ? appData
                : System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            var xdg = environment("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg))
            {
                directory = xdg;
            }
            else
            {
                var home = environment("HOME");
                home = !string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home)
                    ? home
                    : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
                directory = Path.Combine(home, ".config");
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new AexpConfigException("No user config directory is known (set HOME, or XDG_CONFIG_HOME), so no config can be trusted.");
        }

        return new ConfigTrust(Path.Combine(directory, "aexp", "trusted.json"));
    }

    /// <summary>The SHA-256 of a config's bytes, as lowercase hexadecimal.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Whether the config at <paramref name="configPath"/> was approved with exactly these bytes.</summary>
    public bool IsTrusted(string configPath, string sha256) =>
        Read().TryGetValue(Key(configPath), out var entry) && string.Equals(entry.Sha256, sha256, StringComparison.Ordinal);

    /// <summary>Records the config's path and hash, replacing any earlier approval of the same path.</summary>
    public void Approve(string configPath, string sha256, DateTimeOffset now)
    {
        var entries = Read();
        entries[Key(configPath)] = new TrustEntry(Canonical(configPath), sha256, now);

        var directory = Path.GetDirectoryName(File)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new TrustFile([.. entries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal)]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        AtomicFile.Write(File, bytes, overwrite: true);
    }

    private Dictionary<string, TrustEntry> Read()
    {
        if (!System.IO.File.Exists(File))
        {
            return new(StringComparer.Ordinal);
        }

        try
        {
            var trust = JsonSerializer.Deserialize<TrustFile>(System.IO.File.ReadAllBytes(File), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return (trust?.Configs ?? []).Where(entry => entry.Path is not null && entry.Sha256 is not null)
                .GroupBy(entry => Key(entry.Path))
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // An unreadable trust file trusts nothing; approving again rewrites it.
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>The path with every symbolic link along it resolved, so one file has one key however it was reached.</summary>
    internal static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                next = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
            }

            current = next;
        }

        return current;
    }

    private static string Key(string path)
    {
        var full = Canonical(path);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
    }

    private sealed record TrustFile(IReadOnlyList<TrustEntry> Configs);

    private sealed record TrustEntry(string Path, string Sha256, DateTimeOffset TrustedAt);
}

/// <summary>Writes a file whole or not at all: to a temporary file beside it, then renamed over it.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            System.IO.File.Move(temporary, path, overwrite);
        }
        catch
        {
            try
            {
                System.IO.File.Delete(temporary);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}

/// <summary>Who owns a file, on Unix: a config another user can write is not this user's to trust.</summary>
internal static class FileOwnership
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="path"/> is owned by the effective user, <see langword="false"/> when by
    /// another, <see langword="null"/> when it cannot be told (Windows, or the runtime's native helpers are unavailable).
    /// </summary>
    public static bool? OwnedByCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // The runtime's own stat shim (System.Native), whose FileStatus starts Flags, Mode, Uid on every Unix.
            var buffer = new byte[512];
            if (SystemNativeStat(path, buffer) != 0)
            {
                return null;
            }

            return BitConverter.ToUInt32(buffer, 8) == SystemNativeGetEUid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    #pragma warning disable SYSLIB1054 // A plain DllImport: the shim's signature is all this needs.
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
    private static extern int SystemNativeStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] status);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
    private static extern uint SystemNativeGetEUid();
#pragma warning restore SYSLIB1054
}
