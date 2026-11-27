using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Glide.App.Services;

/// <summary>Identity of one end of the single-instance pipe: logon user SID (token user), session, elevation.
/// <c>UserSid</c>/<c>Elevated</c> are null when the process token could not be read (e.g. a normal process
/// looking at an elevated one).</summary>
internal readonly record struct PipePeer(string? UserSid, int? SessionId, bool? Elevated);

/// <summary>
/// 5.1.7: single-instance pipe security keyed on the logon user SID instead of the token owner.
/// .NET's PipeOptions.CurrentUserOnly compares token OWNERS, and an elevated token's default owner is
/// BUILTIN\Administrators, so an elevated launch (e.g. from an admin program) rejected the user's normal Glide
/// pipe, waited out the 8 s election budget and opened its own window. Policy now:
/// - pipe DACL: the user's SID only (owner = user SID); no Everyone, no Administrators group, no other user;
/// - client: forwards only to a server process whose token user SID and session equal its own, and never from a
///   normal process into an elevated Glide (unreadable or elevated server token = incompatible);
/// - server: same session; an elevated server additionally requires an elevated client with the same user SID.
/// </summary>
internal static class PipePeerIdentity
{
    /// <summary>Client-side decision: may this process send paths to <paramref name="server"/>?</summary>
    internal static bool ClientAccepts(PipePeer self, PipePeer server)
    {
        if (self.UserSid is null || server.UserSid is null) return false;
        if (!string.Equals(self.UserSid, server.UserSid, StringComparison.OrdinalIgnoreCase)) return false;
        if (self.SessionId is null || server.SessionId != self.SessionId) return false;
        if (server.Elevated is null) return false;
        // Normal -> elevated would let a medium-integrity launcher drive an elevated Glide: open our own window.
        return server.Elevated == false || self.Elevated == true;
    }

    /// <summary>Server-side decision for a connected client. A non-elevated server relies on the DACL (user SID
    /// only) for the user check, because it cannot read an elevated client's token.</summary>
    internal static bool ServerAccepts(PipePeer self, PipePeer client)
    {
        if (self.SessionId is null || client.SessionId != self.SessionId) return false;
        if (client.UserSid is not null && self.UserSid is not null &&
            !string.Equals(self.UserSid, client.UserSid, StringComparison.OrdinalIgnoreCase)) return false;
        if (self.Elevated == true)
            return client.UserSid is not null && client.Elevated == true;
        return true;
    }

    /// <summary>Pipe ACL: owner and only access entry are the logon user's SID (both the normal and the elevated
    /// token of that user carry it). Inherited entries are dropped.</summary>
    internal static PipeSecurity BuildServerSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    internal static PipePeer Self()
    {
        if (!OperatingSystem.IsWindows()) return new PipePeer(null, null, null);
        int? session = ProcessIdToSessionId((uint)Environment.ProcessId, out var s) ? (int)s : null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = ReadElevation(identity.Token);
            return new PipePeer(identity.User?.Value, session, elevated);
        }
        catch { return new PipePeer(null, session, null); }
    }

    internal static PipePeer Server(NamedPipeClientStream client)
    {
        var handle = client.SafePipeHandle;
        int? session = GetNamedPipeServerSessionId(handle, out var s) ? (int)s : null;
        return GetNamedPipeServerProcessId(handle, out var pid) ? Process(pid, session) : new PipePeer(null, session, null);
    }

    internal static PipePeer Client(NamedPipeServerStream server)
    {
        var handle = server.SafePipeHandle;
        int? session = GetNamedPipeClientSessionId(handle, out var s) ? (int)s : null;
        return GetNamedPipeClientProcessId(handle, out var pid) ? Process(pid, session) : new PipePeer(null, session, null);
    }

    private static PipePeer Process(uint pid, int? session)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return new PipePeer(null, session, null);
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return new PipePeer(null, session, null);
            try { return new PipePeer(ReadUserSid(token), session, ReadElevation(token)); }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private static string? ReadUserSid(IntPtr token)
    {
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;
            var sidPointer = Marshal.ReadIntPtr(buffer); // TOKEN_USER.User.Sid
            return new SecurityIdentifier(sidPointer).Value;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool? ReadElevation(IntPtr token)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try { return GetTokenInformation(token, TokenElevation, buffer, 4, out _) ? Marshal.ReadInt32(buffer) != 0 : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
}
