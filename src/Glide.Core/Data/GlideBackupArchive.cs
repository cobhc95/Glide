using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glide.Core.Data;

/// <summary>
/// The .glidebackup file: a ZIP archive with <c>manifest.json</c> and one JSON entry per backed-up category
/// (<see cref="GlideDataCategory.BackupEntryName"/>). Written and read by <see cref="GlideBackupArchive"/>.
/// </summary>
public static class GlideBackupFormat
{
    public const string FileExtension = ".glidebackup";
    public const string ManifestEntryName = "manifest.json";
    public const string FormatId = "glide-backup";
    /// <summary>Format of the archive itself (not of the settings inside it). A newer number is refused with a clear message.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Largest .glidebackup file accepted (real backups are a few hundred KB).</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;
    /// <summary>Largest uncompressed category entry (the same cap as a settings / profile import file).</summary>
    public const long MaxEntryBytes = 16L * 1024 * 1024;
    /// <summary>Largest uncompressed manifest.</summary>
    public const long MaxManifestBytes = 256L * 1024;
    /// <summary>Largest uncompressed total of everything read from one archive (zip-bomb guard).</summary>
    public const long MaxTotalUncompressedBytes = 64L * 1024 * 1024;
    /// <summary>Most entries an archive may hold (Glide writes at most seven).</summary>
    public const int MaxEntryCount = 32;
}

/// <summary>What <c>manifest.json</c> says. Property names are the persisted JSON names.</summary>
public sealed class GlideBackupManifest
{
    public string Format { get; set; } = GlideBackupFormat.FormatId;
    public int FormatVersion { get; set; } = GlideBackupFormat.CurrentFormatVersion;
    public string AppVersion { get; set; } = string.Empty;
    /// <summary>ISO 8601 UTC time the backup was made.</summary>
    public string CreatedUtc { get; set; } = string.Empty;
    /// <summary>glide.settings.json schema of the Glide that wrote it (informational; each settings entry carries its own).</summary>
    public int SettingsSchemaVersion { get; set; }
    public List<GlideBackupManifestEntry> Entries { get; set; } = new();
}

public sealed class GlideBackupManifestEntry
{
    /// <summary><see cref="ResetItemId"/> name of the category.</summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>Entry name inside the archive; must be the category's fixed <see cref="GlideDataCategory.BackupEntryName"/>.</summary>
    public string File { get; set; } = string.Empty;
    /// <summary>Uncompressed length in bytes.</summary>
    public long Bytes { get; set; }
    /// <summary>SHA-256 of the uncompressed bytes, lower-case hex.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>A validated archive: the manifest and the UTF-8 text of every category entry Glide can import, in catalogue order.</summary>
public sealed record GlideBackupContents(
    GlideBackupManifest Manifest,
    IReadOnlyDictionary<ResetItemId, string> Entries,
    IReadOnlyList<string> IgnoredCategories)
{
    public IReadOnlyList<ResetItemId> Categories =>
        GlideDataCategoryCatalog.All.Where(x => Entries.ContainsKey(x.Id)).Select(x => x.Id).ToArray();
}

/// <summary>A backup that cannot be written or read. The message is shown to the user as it is.</summary>
public sealed class GlideBackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Writes and reads .glidebackup archives. Reading never extracts anything to disk and only ever opens entries by their
/// fixed names: <c>manifest.json</c> and the catalogue's <see cref="GlideDataCategory.BackupEntryName"/>. Every limit in
/// <see cref="GlideBackupFormat"/> is checked against what is actually decompressed (a lying size header cannot get past it),
/// the entry count is checked before the archive's directory is even parsed, entry names that look like paths are refused,
/// and every entry's length and SHA-256 must match the manifest. Whether an entry's JSON is valid Glide data is the App's
/// job (it owns the settings types and their source-generated JSON contexts).
/// </summary>
public static class GlideBackupArchive
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Builds the archive bytes for <paramref name="entries"/> (category -> JSON text).</summary>
    public static byte[] Build(IReadOnlyDictionary<ResetItemId, string> entries, string appVersion, int settingsSchemaVersion, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new GlideBackupException("Choose at least one thing to back up.");
        var manifest = new GlideBackupManifest
        {
            AppVersion = appVersion ?? string.Empty,
            CreatedUtc = createdUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            SettingsSchemaVersion = settingsSchemaVersion,
        };
        var payloads = new List<(string Name, byte[] Bytes)>();
        foreach (var category in GlideDataCategoryCatalog.All)
        {
            if (!entries.TryGetValue(category.Id, out var text)) continue;
            if (!category.CanExport || category.BackupEntryName is null)
                throw new GlideBackupException($"{category.Title} cannot be backed up.");
            var bytes = StrictUtf8.GetBytes(text ?? string.Empty);
            if (bytes.Length > GlideBackupFormat.MaxEntryBytes)
                throw new GlideBackupException($"{category.Title} is too large to back up ({bytes.Length / (1024 * 1024)} MB).");
            manifest.Entries.Add(new GlideBackupManifestEntry
            {
                Category = category.Id.ToString(),
                File = category.BackupEntryName,
                Bytes = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            });
            payloads.Add((category.BackupEntryName, bytes));
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, GlideBackupFormat.ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(manifest, CoreJsonContext.Default.GlideBackupManifest));
            foreach (var (name, bytes) in payloads) Add(zip, name, bytes);
        }
        var result = buffer.ToArray();
        if (result.Length > GlideBackupFormat.MaxFileBytes)
            throw new GlideBackupException("The backup would be too large to import again.");
        return result;
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Writes <paramref name="bytes"/> through a temporary file in the same folder and a replace, so a crash never leaves half a backup.</summary>
    public static void WriteFileAtomic(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup; the outcome is already decided */ }
        }
    }

    /// <summary>Reads and validates a .glidebackup file.</summary>
    public static GlideBackupContents ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (GlideBackupException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GlideBackupException("The backup file could not be read: " + ex.Message, ex);
        }
    }

    /// <summary>Reads and validates a .glidebackup archive from a seekable stream.</summary>
    public static GlideBackupContents Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new GlideBackupException("The backup file could not be read.");
        if (stream.Length > GlideBackupFormat.MaxFileBytes) throw new GlideBackupException("The file is too large to be a Glide backup.");
        if (stream.Length < 22) throw new GlideBackupException("This file is not a Glide backup.");

        // Before the ZIP reader parses the central directory (and allocates one object per entry): the declared entry count.
        var declared = DeclaredEntryCount(stream);
        if (declared is null) throw new GlideBackupException("This file is not a Glide backup (it is not a valid archive).");
        if (declared > GlideBackupFormat.MaxEntryCount) throw new GlideBackupException("This file is not a Glide backup (it holds too many files).");
        stream.Position = 0;

        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            throw new GlideBackupException("This file is not a Glide backup (it is not a valid archive).", ex);
        }

        using (zip)
        {
            if (zip.Entries.Count > GlideBackupFormat.MaxEntryCount)
                throw new GlideBackupException("This file is not a Glide backup (it holds too many files).");
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (!IsPlainEntryName(entry.FullName))
                    throw new GlideBackupException("This backup contains unexpected file names and was not opened.");
                if (!byName.TryAdd(entry.FullName, entry))
                    throw new GlideBackupException("This backup contains the same file twice and was not opened.");
            }

            long total = 0;
            if (!byName.TryGetValue(GlideBackupFormat.ManifestEntryName, out var manifestEntry))
                throw new GlideBackupException("This file is not a Glide backup (manifest.json is missing).");
            var manifestBytes = ReadBounded(manifestEntry, GlideBackupFormat.MaxManifestBytes, ref total, "manifest.json");
            GlideBackupManifest? manifest;
            try { manifest = JsonSerializer.Deserialize(manifestBytes, CoreJsonContext.Default.GlideBackupManifest); }
            catch (JsonException ex) { throw new GlideBackupException("This backup is damaged (manifest.json is not valid).", ex); }
            if (manifest is null || !string.Equals(manifest.Format, GlideBackupFormat.FormatId, StringComparison.Ordinal))
                throw new GlideBackupException("This file is not a Glide backup.");
            if (manifest.FormatVersion > GlideBackupFormat.CurrentFormatVersion)
                throw new GlideBackupException($"This backup was made by a newer version of Glide{Version(manifest)} and cannot be imported here. Update Glide first.");
            if (manifest.FormatVersion < 1) throw new GlideBackupException("This backup is damaged (unknown format version).");
            manifest.Entries ??= new();
            if (manifest.Entries.Count > GlideBackupFormat.MaxEntryCount) throw new GlideBackupException("This backup is damaged (too many entries).");

            var texts = new Dictionary<ResetItemId, string>();
            var ignored = new List<string>();
            foreach (var item in manifest.Entries)
            {
                if (item is null) throw new GlideBackupException("This backup is damaged (empty manifest entry).");
                // Names only: "0" or "AllSettings, LastSession" parse as enum values too, and are not category names.
                if (string.IsNullOrWhiteSpace(item.Category) || item.Category.Contains(',') || int.TryParse(item.Category, out _) ||
                    !Enum.TryParse<ResetItemId>(item.Category, ignoreCase: false, out var id) || !Enum.IsDefined(id))
                {
                    // A category this Glide does not know (a newer Glide wrote it) is skipped, never guessed at.
                    ignored.Add(string.IsNullOrWhiteSpace(item.Category) ? "(unnamed)" : Trim(item.Category));
                    continue;
                }
                var category = GlideDataCategoryCatalog.Get(id);
                if (!category.CanImport || category.BackupEntryName is null)
                {
                    ignored.Add(category.Title);
                    continue;
                }
                if (texts.ContainsKey(id)) throw new GlideBackupException($"This backup is damaged ({category.Title} is listed twice).");
                // The entry is always opened by the catalogue's own name; a manifest that points anywhere else is refused.
                if (!string.Equals(item.File, category.BackupEntryName, StringComparison.Ordinal))
                    throw new GlideBackupException($"This backup is damaged ({category.Title} points to an unexpected file).");
                if (!byName.TryGetValue(category.BackupEntryName, out var entry))
                    throw new GlideBackupException($"This backup is damaged ({category.Title} is missing).");
                var bytes = ReadBounded(entry, GlideBackupFormat.MaxEntryBytes, ref total, category.Title);
                if (bytes.Length != item.Bytes || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new GlideBackupException($"This backup is damaged ({category.Title} does not match its checksum).");
                try { texts[id] = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException ex) { throw new GlideBackupException($"This backup is damaged ({category.Title} is not text).", ex); }
            }
            if (texts.Count == 0)
                throw new GlideBackupException(ignored.Count > 0
                    ? $"This backup holds nothing this version of Glide can import{Version(manifest)}."
                    : "This backup is empty.");
            return new GlideBackupContents(manifest, texts, ignored);
        }
    }

    private static string Version(GlideBackupManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.AppVersion) ? string.Empty : " (" + Trim(manifest.AppVersion) + ")";

    private static string Trim(string text) => text.Length <= 40 ? text : text[..40] + "...";

    /// <summary>
    /// A name Glide could have written: no folder part, no drive, no "..", no control characters, short.
    /// (Entries are never extracted, but a backup holding anything else was not made by Glide.)
    /// </summary>
    internal static bool IsPlainEntryName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 &&
        !name.Contains("..", StringComparison.Ordinal) && !name.StartsWith('.') &&
        name.All(c => !char.IsControl(c)) && name.Trim() == name;

    /// <summary>Decompresses at most <paramref name="limit"/> bytes; more (whatever the header claims) refuses the backup.</summary>
    private static byte[] ReadBounded(ZipArchiveEntry entry, long limit, ref long total, string what)
    {
        if (entry.Length > limit || entry.Length < 0) throw new GlideBackupException($"This backup is not safe to open ({what} is too large).");
        try
        {
            using var source = entry.Open();
            using var target = new MemoryStream((int)Math.Min(entry.Length, limit));
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                read += n;
                if (read > limit) throw new GlideBackupException($"This backup is not safe to open ({what} is too large).");
                if (total + read > GlideBackupFormat.MaxTotalUncompressedBytes)
                    throw new GlideBackupException("This backup is not safe to open (it unpacks to too much data).");
                target.Write(buffer, 0, n);
            }
            total += read;
            return target.ToArray();
        }
        catch (GlideBackupException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new GlideBackupException($"This backup is damaged ({what} could not be unpacked).", ex);
        }
    }

    /// <summary>
    /// The total entry count of the ZIP's end-of-central-directory record, or null when there is none. ZIP64 archives
    /// (0xFFFF) count as too many: Glide never writes them.
    /// </summary>
    internal static int? DeclaredEntryCount(Stream stream)
    {
        const int eocdSize = 22;
        var tail = (int)Math.Min(stream.Length, eocdSize + ushort.MaxValue);
        var buffer = new byte[tail];
        stream.Position = stream.Length - tail;
        stream.ReadExactly(buffer);
        for (var i = tail - eocdSize; i >= 0; i--)
        {
            if (buffer[i] != 0x50 || buffer[i + 1] != 0x4B || buffer[i + 2] != 0x05 || buffer[i + 3] != 0x06) continue;
            var total = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 10, 2));
            return total == ushort.MaxValue ? int.MaxValue : total;
        }
        return null;
    }
}
