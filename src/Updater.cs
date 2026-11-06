using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace MouseBat;

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Checks GitHub for a newer release and replaces the running exe with it.</summary>
public static class Updater
{
    /// <summary>Makes the app exit at once, to prove a downloaded exe can start on this PC.</summary>
    public const string VerifyArgument = "--verify-update";
    /// <summary>Tells a freshly installed exe that the previous one is still shutting down.</summary>
    public const string UpdatedArgument = "--updated";

    private const int MaxDownloadBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } =
        UpdateVersion.Parse(typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0);

    private static string StagedPath => Path.Combine(Path.GetDirectoryName(ExePath)!, "MouseBat.update.exe");
    private static string OldPath => ExePath + ".old";
    private static string ExePath => Environment.ProcessPath ?? throw new UpdateException("Couldn't find the running exe.");

    /// <summary>Returns the latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<UpdateRelease?> CheckAsync()
    {
        try
        {
            string json = await Http.GetStringAsync(UpdateRelease.LatestApiUrl);
            var latest = UpdateRelease.Parse(json) ?? throw new UpdateException("GitHub's response wasn't usable.");
            return latest.Version > CurrentVersion ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException("Couldn't reach GitHub.", ex);
        }
    }

    /// <summary>
    /// Installs the release and starts it. The caller must then exit so the new copy can take
    /// over. On failure this throws with the current exe still in place.
    /// </summary>
    public static async Task InstallAsync(UpdateRelease release)
    {
        try
        {
            byte[] exe = await Http.GetByteArrayAsync(release.ExeUrl);
            string checksum = await Http.GetStringAsync(release.ChecksumUrl);
            if (!UpdateChecksum.Matches(exe, checksum)) throw new UpdateException("The download didn't match its checksum.");

            await File.WriteAllBytesAsync(StagedPath, exe);
            if (!await CanStartAsync(StagedPath)) throw new UpdateException("The new version can't start on this PC.");

            SwapAndStart();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or UnauthorizedAccessException or Win32Exception)
        {
            throw new UpdateException(ex.Message, ex);
        }
        finally
        {
            TryDelete(StagedPath);
        }
    }

    /// <summary>Removes what the last update left behind. Run it after the old copy has exited.</summary>
    public static void CleanUp()
    {
        TryDelete(OldPath);
        TryDelete(StagedPath);
    }

    // Also catches a release that needs a .NET version this PC doesn't have.
    private static async Task<bool> CanStartAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path, VerifyArgument) { UseShellExecute = false })!;
        using var timeout = new CancellationTokenSource(VerifyTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return false;
        }
    }

    // Windows won't overwrite a running exe, but it will let one be renamed.
    private static void SwapAndStart()
    {
        File.Move(ExePath, OldPath, overwrite: true);
        try
        {
            File.Move(StagedPath, ExePath);
            using var started = Process.Start(new ProcessStartInfo(ExePath, UpdatedArgument) { UseShellExecute = false });
        }
        catch
        {
            File.Move(OldPath, ExePath, overwrite: true);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use or not ours to delete. The next launch tries again.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { MaxResponseContentBufferSize = MaxDownloadBytes, Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MouseBat"); // GitHub rejects requests without one.
        return client;
    }
}
