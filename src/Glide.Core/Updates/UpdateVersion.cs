using System.Globalization;

namespace Glide.Core.Updates;

/// <summary>
/// A semantic-ish version (1 to 4 numeric parts, optional leading "v", optional "-pre-release" and "+build" suffix) used to
/// compare the running Glide version with the latest GitHub release tag. Missing parts are 0, so "5.0.1" equals "5.0.1.0".
/// A pre-release is older than the same version without the suffix. Anything that is not a plain dotted number
/// (junk, empty, negative, too many parts, absurdly large parts) does not parse, which the update checker treats as "no update".
/// </summary>
public sealed record UpdateVersion(int Major, int Minor, int Patch, int Revision, bool IsPreRelease) : IComparable<UpdateVersion>
{
    /// <summary>Each part is limited so a hostile tag can never overflow or look like a version from the far future.</summary>
    public const int MaxPart = 1_000_000;

    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = new UpdateVersion(0, 0, 0, 0, false);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V')) span = span[1..];
        if (span.IsEmpty) return false;

        var preRelease = false;
        var plus = span.IndexOf('+');
        if (plus >= 0) span = span[..plus];
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            if (dash == span.Length - 1) return false;
            preRelease = true;
            span = span[..dash];
        }
        if (span.IsEmpty) return false;

        Span<int> parts = stackalloc int[4];
        var count = 0;
        var rest = span;
        while (true)
        {
            if (count == 4) return false;
            var dot = rest.IndexOf('.');
            var part = dot < 0 ? rest : rest[..dot];
            if (part.IsEmpty || part.Length > 7) return false;
            foreach (var c in part) if (c is < '0' or > '9') return false;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > MaxPart) return false;
            parts[count++] = value;
            if (dot < 0) break;
            rest = rest[(dot + 1)..];
        }
        version = new UpdateVersion(parts[0], parts[1], parts[2], parts[3], preRelease);
        return true;
    }

    public static UpdateVersion? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;
        // 5.0.1-beta is older than 5.0.1.
        return IsPreRelease == other.IsPreRelease ? 0 : IsPreRelease ? -1 : 1;
    }

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    /// <summary>True when <see cref="CompareTo"/> is 0 (the record's own equality also compares the pre-release flag and is stricter).</summary>
    public bool SameAs(UpdateVersion? other) => other is not null && CompareTo(other) == 0;

    /// <summary>"5.0.1" (three parts) or "5.0.1.2" when the revision is not 0; the pre-release suffix is not kept.</summary>
    public override string ToString() => Revision == 0
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}.{Revision}";
}
