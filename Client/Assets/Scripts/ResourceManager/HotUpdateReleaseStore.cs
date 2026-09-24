#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

[Serializable]
public sealed class HotUpdateReleaseManifest
{
    public string releaseId = string.Empty;
    public string generatedAtUtc = string.Empty;
    public List<HotUpdateReleaseFile> files = new List<HotUpdateReleaseFile>();
}

[Serializable]
public sealed class HotUpdateReleaseFile
{
    public string path = string.Empty;
    public long size;
    public string sha256 = string.Empty;
}

public sealed class HotUpdateCacheCleanupResult
{
    public int RemovedReleaseCount { get; internal set; }
    public int RemovedStagingCount { get; internal set; }
    public int RemovedLegacyFileCount { get; internal set; }
    public long FreedBytes { get; internal set; }
    public long RetainedBytes { get; internal set; }
    public List<string> Failures { get; } = new List<string>();
}

/// <summary>
/// Owns the on-disk release transaction. It is Unity-independent so commit,
/// validation and rollback behavior can be covered by server-side smoke tests.
/// </summary>
public sealed class HotUpdateReleaseStore
{
    public const string ManifestFileName = "release.json";
    private const string ActivePointerFileName = "active-release.txt";
    private const string PreviousPointerFileName = "previous-release.txt";
    private readonly string _rootPrefix;

    public HotUpdateReleaseStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A release cache root is required.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        _rootPrefix = EnsureTrailingSeparator(RootDirectory);
    }

    public string RootDirectory { get; }
    public string StagingDirectory => Path.Combine(RootDirectory, "staging");
    public string ReleasesDirectory => Path.Combine(RootDirectory, "releases");

    public string ReadActiveReleaseId()
    {
        return ReadPointer(ActivePointerFileName);
    }

    public string ReadPreviousReleaseId()
    {
        return ReadPointer(PreviousPointerFileName);
    }

    public string PrepareStaging(HotUpdateReleaseManifest manifest)
    {
        ValidateManifestMetadata(manifest);
        Directory.CreateDirectory(StagingDirectory);

        var stagingPath = GetStagingReleasePath(manifest.releaseId);
        if (Directory.Exists(stagingPath))
        {
            EnsureWithinRoot(stagingPath);
            Directory.Delete(stagingPath, true);
        }

        Directory.CreateDirectory(stagingPath);
        return stagingPath;
    }

    public string GetStagingFilePath(string releaseId, string relativePath)
    {
        return ResolveRelativePath(GetStagingReleasePath(releaseId), relativePath);
    }

    public string GetReleaseFilePath(string releaseId, string relativePath)
    {
        return ResolveRelativePath(GetReleasePath(releaseId), relativePath);
    }

    public bool TryGetActiveFilePath(string relativePath, out string filePath)
    {
        filePath = string.Empty;
        var activeReleaseId = ReadActiveReleaseId();
        if (activeReleaseId is null)
        {
            return false;
        }

        try
        {
            var candidate = GetReleaseFilePath(activeReleaseId, relativePath);
            if (!File.Exists(candidate))
            {
                return false;
            }

            filePath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool ValidateStaging(HotUpdateReleaseManifest manifest, out string error)
    {
        return ValidateDirectory(GetStagingReleasePath(manifest.releaseId), manifest, out error);
    }

    public bool ValidateRelease(HotUpdateReleaseManifest manifest, out string error)
    {
        return ValidateDirectory(GetReleasePath(manifest.releaseId), manifest, out error);
    }

    public bool CommitStaging(HotUpdateReleaseManifest manifest, string manifestJson, out string error)
    {
        error = string.Empty;
        try
        {
            ValidateManifestMetadata(manifest);
            var stagingPath = GetStagingReleasePath(manifest.releaseId);
            Directory.CreateDirectory(stagingPath);
            WriteTextAtomically(Path.Combine(stagingPath, ManifestFileName), manifestJson);

            if (!ValidateStaging(manifest, out error))
            {
                return false;
            }

            Directory.CreateDirectory(ReleasesDirectory);
            var releasePath = GetReleasePath(manifest.releaseId);
            if (Directory.Exists(releasePath))
            {
                if (ValidateRelease(manifest, out _))
                {
                    Directory.Delete(stagingPath, true);
                }
                else
                {
                    var invalidPath = releasePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                    Directory.Move(releasePath, invalidPath);
                    Directory.Move(stagingPath, releasePath);
                }
            }
            else
            {
                Directory.Move(stagingPath, releasePath);
            }

            var oldActive = ReadActiveReleaseId();
            if (!string.IsNullOrEmpty(oldActive)
                && !string.Equals(oldActive, manifest.releaseId, StringComparison.Ordinal))
            {
                WritePointer(PreviousPointerFileName, oldActive);
            }

            WritePointer(ActivePointerFileName, manifest.releaseId);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryRollback(out string activeReleaseId)
    {
        activeReleaseId = ReadPreviousReleaseId();
        if (activeReleaseId is null || !Directory.Exists(GetReleasePath(activeReleaseId)))
        {
            activeReleaseId = null;
            return false;
        }

        WritePointer(ActivePointerFileName, activeReleaseId);
        return true;
    }

    public void SetActiveRelease(string releaseId)
    {
        ValidateReleaseId(releaseId);
        if (!Directory.Exists(GetReleasePath(releaseId)))
        {
            throw new DirectoryNotFoundException($"Release {releaseId} does not exist.");
        }

        WritePointer(ActivePointerFileName, releaseId);
    }

    /// <summary>
    /// Preserves the active release and its rollback target, then removes
    /// interrupted staging data and old releases until the cache fits its budget.
    /// </summary>
    public HotUpdateCacheCleanupResult PruneCache(int maxSizeMegabytes, int expireDays)
    {
        var result = new HotUpdateCacheCleanupResult();
        var protectedReleaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddProtectedReleaseId(protectedReleaseIds, ReadActiveReleaseId());
        AddProtectedReleaseId(protectedReleaseIds, ReadPreviousReleaseId());

        RemoveStagingDirectories(result);
        var candidates = GetCleanupCandidates(protectedReleaseIds);

        if (expireDays > 0)
        {
            var expiryUtc = DateTime.UtcNow.AddDays(-expireDays);
            foreach (var candidate in candidates.Where(entry => entry.LastWriteTimeUtc < expiryUtc).ToArray())
            {
                RemoveCacheEntry(candidate, result);
            }
        }

        var maxBytes = maxSizeMegabytes > 0
            ? maxSizeMegabytes * 1024L * 1024L
            : long.MaxValue;
        var totalBytes = GetCacheSize();
        foreach (var candidate in candidates.OrderBy(entry => entry.LastWriteTimeUtc))
        {
            if (candidate.Removed || totalBytes <= maxBytes)
            {
                continue;
            }

            RemoveCacheEntry(candidate, result);
            totalBytes = GetCacheSize();
        }

        result.RetainedBytes = GetCacheSize();
        return result;
    }

    public static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static void ValidateManifestMetadata(HotUpdateReleaseManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        ValidateReleaseId(manifest.releaseId);
        if (manifest.files is null || manifest.files.Count == 0)
        {
            throw new ArgumentException("Release manifest has no files.", nameof(manifest));
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.files)
        {
            if (file is null)
            {
                throw new ArgumentException("Release manifest contains an empty file entry.", nameof(manifest));
            }

            ValidateRelativePath(file.path);
            if (!paths.Add(file.path))
            {
                throw new ArgumentException($"Release manifest contains duplicate path {file.path}.", nameof(manifest));
            }

            if (file.size < 0)
            {
                throw new ArgumentException($"Release file {file.path} has a negative size.", nameof(manifest));
            }

            if (file.sha256.Length != 64 || !IsHex(file.sha256))
            {
                throw new ArgumentException($"Release file {file.path} has an invalid SHA-256.", nameof(manifest));
            }
        }
    }

    private bool ValidateDirectory(
        string directory,
        HotUpdateReleaseManifest manifest,
        out string error)
    {
        error = string.Empty;
        try
        {
            ValidateManifestMetadata(manifest);
            foreach (var file in manifest.files)
            {
                var filePath = ResolveRelativePath(directory, file.path);
                if (!File.Exists(filePath))
                {
                    error = $"Missing release file: {file.path}";
                    return false;
                }

                var info = new FileInfo(filePath);
                if (info.Length != file.size)
                {
                    error = $"Size mismatch for {file.path}: expected={file.size}, actual={info.Length}";
                    return false;
                }

                var actualHash = ComputeSha256(filePath);
                if (!string.Equals(actualHash, file.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"SHA-256 mismatch for {file.path}";
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private string GetStagingReleasePath(string releaseId)
    {
        ValidateReleaseId(releaseId);
        return ResolveRelativePath(StagingDirectory, releaseId);
    }

    private string GetReleasePath(string releaseId)
    {
        ValidateReleaseId(releaseId);
        return ResolveRelativePath(ReleasesDirectory, releaseId);
    }

    private string ReadPointer(string fileName)
    {
        var path = Path.Combine(RootDirectory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var releaseId = File.ReadAllText(path).Trim();
        try
        {
            ValidateReleaseId(releaseId);
            return releaseId;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void WritePointer(string fileName, string releaseId)
    {
        ValidateReleaseId(releaseId);
        Directory.CreateDirectory(RootDirectory);
        WriteTextAtomically(Path.Combine(RootDirectory, fileName), releaseId + Environment.NewLine);
    }

    private static void AddProtectedReleaseId(ISet<string> protectedReleaseIds, string releaseId)
    {
        if (!string.IsNullOrEmpty(releaseId))
        {
            protectedReleaseIds.Add(releaseId);
        }
    }

    private void RemoveStagingDirectories(HotUpdateCacheCleanupResult result)
    {
        if (!Directory.Exists(StagingDirectory))
        {
            return;
        }

        foreach (var directoryPath in Directory.GetDirectories(StagingDirectory))
        {
            var directory = new DirectoryInfo(directoryPath);
            if (!CanDeleteDirectory(directory, result))
            {
                continue;
            }

            var size = GetDirectorySize(directory);
            try
            {
                Directory.Delete(directory.FullName, true);
                result.RemovedStagingCount++;
                result.FreedBytes += size;
            }
            catch (Exception exception)
            {
                result.Failures.Add($"staging {directory.Name}: {exception.Message}");
            }
        }
    }

    private List<CacheEntry> GetCleanupCandidates(ISet<string> protectedReleaseIds)
    {
        var candidates = new List<CacheEntry>();
        if (Directory.Exists(ReleasesDirectory))
        {
            foreach (var directoryPath in Directory.GetDirectories(ReleasesDirectory))
            {
                var directory = new DirectoryInfo(directoryPath);
                if (protectedReleaseIds.Contains(directory.Name)
                    || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                EnsureWithinRoot(directory.FullName);
                candidates.Add(new CacheEntry(
                    directory.FullName,
                    true,
                    true,
                    directory.LastWriteTimeUtc,
                    GetDirectorySize(directory)));
            }
        }

        // Legacy flat-cache files remain usable until a transactional release has
        // been committed. Once one exists, they are safe eviction candidates.
        if (protectedReleaseIds.Count > 0 && Directory.Exists(RootDirectory))
        {
            foreach (var filePath in Directory.GetFiles(RootDirectory))
            {
                var file = new FileInfo(filePath);
                if (file.Name.Equals(ActivePointerFileName, StringComparison.OrdinalIgnoreCase)
                    || file.Name.Equals(PreviousPointerFileName, StringComparison.OrdinalIgnoreCase)
                    || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                EnsureWithinRoot(file.FullName);
                candidates.Add(new CacheEntry(
                    file.FullName,
                    false,
                    false,
                    file.LastWriteTimeUtc,
                    file.Length));
            }
        }

        return candidates;
    }

    private void RemoveCacheEntry(CacheEntry entry, HotUpdateCacheCleanupResult result)
    {
        try
        {
            if (entry.IsDirectory)
            {
                var directory = new DirectoryInfo(entry.Path);
                if (!CanDeleteDirectory(directory, result))
                {
                    return;
                }

                Directory.Delete(entry.Path, true);
            }
            else
            {
                var file = new FileInfo(entry.Path);
                EnsureWithinRoot(file.FullName);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.Failures.Add($"linked cache file {file.Name}");
                    return;
                }

                File.Delete(entry.Path);
            }

            entry.Removed = true;
            result.FreedBytes += entry.Size;
            if (entry.IsRelease)
            {
                result.RemovedReleaseCount++;
            }
            else
            {
                result.RemovedLegacyFileCount++;
            }
        }
        catch (Exception exception)
        {
            result.Failures.Add($"cache entry {Path.GetFileName(entry.Path)}: {exception.Message}");
        }
    }

    private bool CanDeleteDirectory(DirectoryInfo directory, HotUpdateCacheCleanupResult result)
    {
        try
        {
            EnsureWithinRoot(directory.FullName);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.Failures.Add($"linked cache directory {directory.Name}");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            result.Failures.Add($"cache directory {directory.Name}: {exception.Message}");
            return false;
        }
    }

    private long GetCacheSize()
    {
        return Directory.Exists(RootDirectory) ? GetDirectorySize(new DirectoryInfo(RootDirectory)) : 0;
    }

    private static long GetDirectorySize(DirectoryInfo directory)
    {
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return 0;
        }

        long size = 0;
        try
        {
            foreach (var file in directory.GetFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    size += file.Length;
                }
            }

            foreach (var child in directory.GetDirectories())
            {
                size += GetDirectorySize(child);
            }
        }
        catch (IOException)
        {
            return size;
        }
        catch (UnauthorizedAccessException)
        {
            return size;
        }

        return size;
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string path, bool isDirectory, bool isRelease, DateTime lastWriteTimeUtc, long size)
        {
            Path = path;
            IsDirectory = isDirectory;
            IsRelease = isRelease;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Size = size;
        }

        public string Path { get; }
        public bool IsDirectory { get; }
        public bool IsRelease { get; }
        public DateTime LastWriteTimeUtc { get; }
        public long Size { get; }
        public bool Removed { get; set; }
    }

    private static void WriteTextAtomically(string destinationPath, string content)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = destinationPath + ".tmp";
        File.WriteAllText(temporaryPath, content);
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(temporaryPath, destinationPath, null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Delete(destinationPath);
            File.Move(temporaryPath, destinationPath);
        }
    }

    private string ResolveRelativePath(string baseDirectory, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithinRoot(fullPath);
        return fullPath;
    }

    private void EnsureWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Release path escaped the cache root.", nameof(path));
        }
    }

    private static void ValidateReleaseId(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId)
            || releaseId.Length > 80
            || releaseId.Contains("..", StringComparison.Ordinal)
            || releaseId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || releaseId.Contains('/')
            || releaseId.Contains('\\'))
        {
            throw new ArgumentException($"Invalid release ID: {releaseId}", nameof(releaseId));
        }
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.Contains('\\'))
        {
            throw new ArgumentException($"Invalid relative release path: {relativePath}", nameof(relativePath));
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
