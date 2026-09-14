using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Glide.Core.Security;

/// <summary>
/// Hardened local disk cache providing strict sandboxing against directory traversal attacks.
/// Cache entries are addressed via SHA-256 cryptographic hashes of keys/URLs rather than user-supplied
/// strings, ensuring no directory navigation sequences (e.g., ../, ..\, null bytes, or drive specifiers)
/// can escape the designated cache root.
/// </summary>
public sealed class SecureDiskCache
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".jxr", ".ico"
    };

    private readonly string _cacheDirectory;

    public SecureDiskCache(string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("Cache directory path cannot be null or empty.", nameof(cacheDirectory));

        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// Computes the secure, canonicalized file path within the cache sandbox for a given key.
    /// Throws a SecurityException if directory traversal or sandbox escape is attempted.
    /// </summary>
    public string GetSafeCachePath(string key, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Cache key cannot be null or empty.", nameof(key));

        // Generate SHA-256 hexadecimal hash as the filename
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var hash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();

        // Validate and sanitize the extension
        var ext = ".bin";
        if (!string.IsNullOrWhiteSpace(extension))
        {
            var candidateExt = extension.StartsWith('.') ? extension : "." + extension;
            if (AllowedExtensions.Contains(candidateExt))
                ext = candidateExt.ToLowerInvariant();
        }

        var fileName = hash + ext;
        var candidatePath = Path.Combine(_cacheDirectory, fileName);
        var fullPath = Path.GetFullPath(candidatePath);

        // Verify sandbox integrity: path must strictly reside within _cacheDirectory
        var prefix = _cacheDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _cacheDirectory
            : _cacheDirectory + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Directory traversal attempt detected: path '{fullPath}' escapes cache root '{_cacheDirectory}'.");
        }

        return fullPath;
    }

    /// <summary>
    /// Safely writes bytes to the cache sandbox under an atomic temporary file pattern.
    /// </summary>
    public async Task WriteAsync(string key, byte[] data, string? extension = null, CancellationToken cancellationToken = default)
    {
        var targetPath = GetSafeCachePath(key, extension);
        var tempPath = Path.Combine(_cacheDirectory, $"tmp_{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(tempPath, data, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Attempts to read cached bytes for a key. Returns true if present and valid.
    /// </summary>
    public bool TryRead(string key, out byte[]? data, string? extension = null)
    {
        data = null;
        var path = GetSafeCachePath(key, extension);
        if (!File.Exists(path)) return false;

        try
        {
            data = File.ReadAllBytes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a cached entry safely if present.
    /// </summary>
    public bool TryDelete(string key, string? extension = null)
    {
        var path = GetSafeCachePath(key, extension);
        if (!File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cleans up cache files older than the specified age, or all files if null.
    /// </summary>
    public int Purge(TimeSpan? olderThan = null)
    {
        var threshold = olderThan.HasValue ? DateTime.UtcNow - olderThan.Value : DateTime.MaxValue;
        var deleted = 0;

        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                if (olderThan is null || info.LastWriteTimeUtc < threshold)
                {
                    info.Delete();
                    deleted++;
                }
            }
            catch { }
        }

        return deleted;
    }
}
