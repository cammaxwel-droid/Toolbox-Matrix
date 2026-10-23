using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinGetToolbox;

internal enum ExportKind { Report, Backup }
internal sealed record RetentionConsent(string Folder, string DirectoryIdentity, bool IncludeSubfolders, string Rule);
internal sealed record RetentionResult(int Deleted, int Skipped, bool Limited, string Message);

internal static class ExportDestinations
{
    internal const string ScopeNotice = "This retention rule removes eligible contents from the chosen folder (including contents of subfolders); it never deletes the parent folder itself";
    internal static string DefaultFolder(ExportKind kind, string dataRoot) =>
        Path.Combine(dataRoot, kind == ExportKind.Report ? "tblxm Reports" : "tblxm Backups");

    internal static string? DisplayFolder(ExportKind kind, string mode, string? custom, string dataRoot) =>
        mode switch { "Default" => DefaultFolder(kind, dataRoot), "Custom Folder" => custom, _ => null };

    // The picker is a runtime dependency; tests supply a function without opening any UI.
    internal static string? Resolve(ExportKind kind, string mode, string? custom, string dataRoot, Func<string?> chooseFolder)
    {
        var folder = mode switch
        {
            "Default" => DefaultFolder(kind, dataRoot),
            "Custom Folder" => custom ?? throw new IOException("Choose a custom destination first."),
            "Ask Every Time" => chooseFolder(),
            _ => throw new IOException("Unknown destination setting.")
        };
        if (folder is null) return null; // Cancellation must never reuse a previous destination.
        folder = SafeDestination.Canonical(folder);
        if (mode != "Default" && !Directory.Exists(folder)) throw new DirectoryNotFoundException("The selected folder is unavailable: " + folder);
        SafeDestination.CheckAncestors(folder);
        return folder;
    }

    internal static string SaveBackup(string folder, string content, DateTime now)
    {
        SafeDestination.CheckAncestors(folder);
        Directory.CreateDirectory(folder);
        using var locked = SafeDestination.LockDirectory(folder);
        var stem = "Toolbox-Backup-" + now.ToString("yyyy-MM-dd-HHmmss");
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var path = Path.Combine(folder, stem + (suffix == 0 ? "" : "-" + suffix) + ".tlbxbackup");
            FileStream stream;
            try { stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }
            using (stream)
            using (var writer = new StreamWriter(stream)) writer.Write(content);
            return path;
        }
        throw new IOException("Too many backups share this timestamp. Existing backups were preserved.");
    }
}

// Pins each ancestor against replacement/rename while inspecting or deleting children.
// Reparse points, UNC/device paths, relative paths and non-canonical path segments fail closed.
internal static class SafeDestination
{
    internal static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\") ||
            value.Contains('/') || value.Skip(2).Contains(':') ||
            value.Split('\\').Skip(1).Any(x => x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.')))
            throw new IOException("Choose a fully qualified local folder without links or traversal.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    internal static IEnumerable<string> Ancestors(string path)
    {
        var parts = new Stack<string>();
        for (string? at = path; at is not null; at = Path.GetDirectoryName(at)) parts.Push(at);
        return parts;
    }
    internal static void CheckAncestors(string path)
    {
        foreach (var part in Ancestors(Canonical(path)))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked destinations are not supported: " + part);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal sealed class DirectoryLock : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        internal string Identity { get; private set; } = "";
        internal DirectoryLock(string folder)
        {
            try
            {
                foreach (var part in Ancestors(Canonical(folder)))
                {
                    var handle = Native.Open(part, 0x80, 3, 0x02200000); // READ_ATTRIBUTES, share read/write, OPEN_REPARSE_POINT | BACKUP_SEMANTICS
                    _handles.Add(handle);
                    var info = Native.Info(handle);
                    if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                        throw new IOException("Retention will not follow linked or non-directory paths: " + part);
                    Identity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{info.CreationHigh:X8}{info.CreationLow:X8}";
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }
    }
    internal static DirectoryLock LockDirectory(string path) => new(path);
    internal static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void CheckRetentionRoot(string path)
    {
        path = Canonical(path);
        if (Path.GetPathRoot(path) == path) throw new IOException("Retention cannot target a drive root.");
        var protectedTrees = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath).Where(x => !string.IsNullOrEmpty(x));
        if (protectedTrees.Any(x => Within(path, x) || Within(x, path))) throw new IOException("Retention cannot target Windows, installed programs or system data.");
        var protectedParents = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(Environment.GetFolderPath).Where(x => !string.IsNullOrEmpty(x)).Append(ToolboxPaths.Root);
        if (protectedParents.Any(x => Within(x, path))) throw new IOException("Choose a dedicated subfolder; retention cannot target a user or Toolbox data parent.");
        foreach (var part in Ancestors(path))
        {
            if (ProtectedDirectoryName(Path.GetFileName(part)) || File.Exists(Path.Combine(part, ".git")) || Directory.Exists(Path.Combine(part, ".git")))
                throw new IOException("Retention cannot target a source repository or history: " + part);
            if (File.Exists(Path.Combine(part, "WinGetToolbox.exe"))) throw new IOException("Retention cannot target an application folder.");
        }
    }
    internal static bool ProtectedDirectoryName(string name) => new[] { "history", ".git", ".codex", "node_modules", "WindowsApps", "$Recycle.Bin", "System Volume Information" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInfo
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            internal DateTime Created => DateTime.FromFileTimeUtc(((long)CreationHigh << 32) | CreationLow);
            internal DateTime Written => DateTime.FromFileTimeUtc(((long)WriteHigh << 32) | WriteLow);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref int info, uint size);
        internal static SafeFileHandle Open(string path, uint access, uint share, uint flags)
        {
            var handle = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("Cannot safely open " + path + ": " + new Win32Exception(error).Message);
        }
        internal static FileInfo Info(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info)
            ? info : throw new IOException("Cannot inspect file identity: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        internal static void Delete(SafeFileHandle handle)
        {
            var delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 4))
                throw new IOException("File could not be removed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }
}

internal sealed class RetentionClock
{
    private DateTime? _utc;
    private TimeSpan _elapsed;
    private bool _paused;
    internal bool Accept(DateTime nowUtc, TimeSpan elapsed)
    {
        if (_paused || nowUtc.Kind != DateTimeKind.Utc) return false;
        var stable = _utc is null || elapsed >= _elapsed && Math.Abs(((nowUtc - _utc.Value) - (elapsed - _elapsed)).TotalMinutes) <= 5;
        _utc = nowUtc; _elapsed = elapsed;
        _paused = !stable;
        return stable;
    }
}

internal static class FolderRetention
{
    internal static TimeSpan? Age(string choice) => choice switch
    {
        "24 hours" => TimeSpan.FromHours(24), "30 days" => TimeSpan.FromDays(30), "90 days" => TimeSpan.FromDays(90), _ => null
    };
    internal static RetentionConsent Consent(string folder, bool nested, string rule = "24 hours")
    {
        folder = SafeDestination.Canonical(folder); SafeDestination.CheckRetentionRoot(folder);
        using var pin = SafeDestination.LockDirectory(folder);
        return new(folder, pin.Identity, nested, rule);
    }
    internal static bool Matches(RetentionConsent? consent, string folder, bool nested, string rule = "24 hours")
    {
        if (consent is null || !string.Equals(consent.Folder, folder, StringComparison.OrdinalIgnoreCase) || consent.IncludeSubfolders != nested || consent.Rule != rule) return false;
        using var pin = SafeDestination.LockDirectory(folder);
        return consent.DirectoryIdentity == pin.Identity;
    }
    internal static bool Eligible(DateTime createdUtc, DateTime writtenUtc, DateTime nowUtc, TimeSpan age) =>
        nowUtc.Kind == DateTimeKind.Utc && createdUtc <= nowUtc && writtenUtc <= nowUtc &&
        nowUtc - (createdUtc > writtenUtc ? createdUtc : writtenUtc) >= age;

    internal static RetentionResult Clean(string folder, string choice, bool nested, RetentionConsent? consent, DateTime nowUtc, int maxEntries = 2000, int maxDeletes = 500, TimeSpan? budget = null)
    {
        if (Age(choice) is not { } age) return new(0, 0, false, "Retention is off.");
        var deleted = 0; var skipped = 0; var seen = 0; var limited = false;
        var clock = Stopwatch.StartNew(); var limit = budget ?? TimeSpan.FromSeconds(2);
        try
        {
            folder = SafeDestination.Canonical(folder); SafeDestination.CheckRetentionRoot(folder);
            using var pin = SafeDestination.LockDirectory(folder);
            if (consent is null || !string.Equals(consent.Folder, folder, StringComparison.OrdinalIgnoreCase) || consent.DirectoryIdentity != pin.Identity || consent.IncludeSubfolders != nested || consent.Rule != choice)
                return new(0, 0, false, "Retention needs explicit permission for this exact folder and scope.");
            void Visit(string directory, int depth)
            {
                if (depth > 16) { limited = true; return; }
                using var directoryPin = SafeDestination.LockDirectory(directory);
                // A newly-created repository is protected even after earlier folder consent.
                SafeDestination.CheckRetentionRoot(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++seen > maxEntries || deleted >= maxDeletes || clock.Elapsed > limit) { limited = true; break; }
                    try
                    {
                        if (!SafeDestination.Within(entry, folder)) { skipped++; continue; }
                        var attr = File.GetAttributes(entry);
                        if ((attr & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) { skipped++; continue; }
                        if ((attr & FileAttributes.Directory) != 0)
                        {
                            if (nested && !SafeDestination.ProtectedDirectoryName(Path.GetFileName(entry))) Visit(entry, depth + 1);
                            else skipped++;
                            if (limited) break;
                            continue;
                        }
                        if (new[] { ".exe", ".dll", ".com", ".msi", ".msp", ".sys", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".psd1",
                            ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".ocx", ".pif", ".msc", ".reg",
                            ".lnk", ".url", ".appx", ".msix", ".appxbundle", ".msixbundle" }.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase)) { skipped++; continue; }
                        // No sharing of writes or deletes: content, identity and timestamps cannot
                        // be swapped between eligibility validation and handle-based disposition.
                        using var file = SafeDestination.Native.Open(entry, 0x10081, 1, 0x00200000); // DELETE | READ_DATA | READ_ATTRIBUTES
                        var info = SafeDestination.Native.Info(file);
                        if (info.Links != 1 || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.System | FileAttributes.ReadOnly)) != 0) { skipped++; continue; }
                        if (!Eligible(info.Created, info.Written, nowUtc, age)) continue;
                        var header = new byte[2];
                        if (RandomAccess.Read(file, header, 0) == 2 && header[0] == 'M' && header[1] == 'Z') { skipped++; continue; }
                        SafeDestination.Native.Delete(file); deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
                }
            }
            Visit(folder, 0);
            return new(deleted, skipped, limited, $"Retention removed {deleted} eligible file(s); skipped {skipped} protected or unavailable entries." + (limited ? " Cleanup limit reached; remaining files are preserved for a later pass." : "") + " Parent and subfolders were preserved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(deleted, skipped, limited, "Retention stopped safely: " + ex.Message); }
    }
}
