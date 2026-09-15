namespace Glide.App.Services;

/// <summary>Immutable identity carried by every asynchronous image publication.</summary>
internal readonly record struct ImageRequestContext(
    Guid WindowLifetimeId, Guid ActiveTabId, long WorkspaceEpoch, long ImageRequestId,
    string Path, CancellationToken Token)
{
    public bool Matches(Guid windowLifetimeId, Guid activeTabId, long workspaceEpoch, long imageRequestId, string? currentPath)
        => !Token.IsCancellationRequested && WindowLifetimeId == windowLifetimeId && ActiveTabId == activeTabId &&
           WorkspaceEpoch == workspaceEpoch && ImageRequestId == imageRequestId &&
           string.Equals(Path, currentPath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Folder indexing has an independent identity so same-folder neighbour navigation can reuse it.</summary>
internal readonly record struct FolderIndexSession(
    Guid ActiveTabId, long WorkspaceEpoch, long SessionId, string Folder, CancellationToken Token)
{
    public bool Matches(Guid activeTabId, long workspaceEpoch, long sessionId, string? currentPath)
    {
        if (Token.IsCancellationRequested || ActiveTabId != activeTabId || WorkspaceEpoch != workspaceEpoch || SessionId != sessionId)
            return false;
        var activeFolder = Path.GetDirectoryName(currentPath ?? string.Empty) ?? string.Empty;
        return string.Equals(activeFolder, Folder, StringComparison.OrdinalIgnoreCase);
    }
}
