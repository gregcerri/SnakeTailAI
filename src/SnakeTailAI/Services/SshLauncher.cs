using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SnakeTailAI.Core;

namespace SnakeTailAI.Services;

/// <summary>
/// SSH to the device of a log, with the Windows OpenSSH client running inside the app's terminal panel.
/// A saved password reaches ssh through SSH_ASKPASS: ssh starts this same exe, which decrypts the password and prints it.
/// The password is stored encrypted for the current Windows user (DPAPI) and only travels in that encrypted form.
/// </summary>
public static partial class SshLauncher
{
    /// <summary>Holds the DPAPI-encrypted password while an ssh process is being started.</summary>
    private const string ProtectedPasswordVariable = "SNAKETAILAI_ASKPASS_PROTECTED";

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeName();

    /// <summary>The host of the device a log belongs to (<c>…\ViuPC-172.28.130.31\x.log</c> → <c>172.28.130.31</c>), or null.</summary>
    public static string? HostOf(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (!name.StartsWith("ViuPC-", StringComparison.OrdinalIgnoreCase))
            return null;
        string host = name["ViuPC-".Length..];
        return SafeName().IsMatch(host) ? host : null;
    }

    public static string Protect(string password) =>
        password.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string data)
    {
        if (string.IsNullOrEmpty(data))
            return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Saved by another Windows user or on another PC: the password has to be typed again.
            return "";
        }
    }

    /// <summary>
    /// When ssh runs this exe as its askpass helper: answer the password prompt and return true (the app must exit).
    /// </summary>
    public static bool TryHandleAskPass(string[] args)
    {
        string? encrypted = Environment.GetEnvironmentVariable(ProtectedPasswordVariable);
        if (encrypted == null || Environment.GetEnvironmentVariable("SSH_ASKPASS") == null)
            return false;
        string prompt = args.Length > 0 ? args[0] : "";
        // Only the password prompt is answered; any other question (host key, passphrase) gets an empty answer.
        if (prompt.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(Encoding.UTF8.GetBytes(Unprotect(encrypted) + "\n"));
        }
        return true;
    }

    /// <summary>The ssh command line for a host, or an error message.</summary>
    public static (string? CommandLine, string? Error) CommandLine(string host, AppSettings settings)
    {
        string ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh))
            return (null, "The Windows SSH client is not installed (Settings → Apps → Optional features → OpenSSH Client)");
        if (!SafeName().IsMatch(host))
            return (null, $"\"{host}\" is not a valid host name");
        string user = settings.SshUser.Trim();
        if (user.Length > 0 && !SafeName().IsMatch(user))
            return (null, $"\"{user}\" is not a valid SSH user name");

        string target = user.Length > 0 ? $"{user}@{host}" : host;
        int port = Math.Clamp(settings.SshPort, 1, 65535);
        string options = $"-o StrictHostKeyChecking=accept-new -o ServerAliveInterval=30 -p {port}";
        if (HasPassword(settings))
            options += " -o NumberOfPasswordPrompts=1";
        return ($"\"{ssh}\" {options} {target}", null);
    }

    public static bool HasPassword(AppSettings settings) => Unprotect(settings.SshPasswordProtected).Length > 0;

    /// <summary>
    /// Makes the next process started by this app use the saved password. The terminal control starts ssh with this
    /// process's environment, so it is set just before and removed as soon as ssh runs (<see cref="EndPasswordHandOff"/>).
    /// </summary>
    public static void BeginPasswordHandOff(AppSettings settings)
    {
        if (!HasPassword(settings) || Environment.ProcessPath is not { } self)
            return;
        Environment.SetEnvironmentVariable("SSH_ASKPASS", self);
        Environment.SetEnvironmentVariable("SSH_ASKPASS_REQUIRE", "force");
        Environment.SetEnvironmentVariable(ProtectedPasswordVariable, settings.SshPasswordProtected);
    }

    public static void EndPasswordHandOff()
    {
        Environment.SetEnvironmentVariable("SSH_ASKPASS", null);
        Environment.SetEnvironmentVariable("SSH_ASKPASS_REQUIRE", null);
        Environment.SetEnvironmentVariable(ProtectedPasswordVariable, null);
    }
}
