using System.Diagnostics;
using System.Drawing;
using System.Text;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Models.Tasks;

public class ShutdownTask : Task
{
    private bool _alreadyShuttingDown;
    private DateTime _shutdownTime;
    private DateTime _nextTriggerTime = DateTime.MinValue;
    private readonly int _exitCode;

    // Timings to show a popup, in seconds
    private readonly List<uint> _triggerPoints =
    [
        14400, // 4h
        10800, // 3h
        7200, // 2h
        3600, // 60m
        2700, // 45m
        1800, // 30m
        1200, // 20m
        900, // 15m
        600, // 10m
        300, // 5m
        240, // 4m
        180, // 3m
        120, // 2m
        90,
        60, // 1m
        45,
        30,
        20,
        10
    ];

    /// <summary>
    /// UTC time at which AAEmu will shut down/restart.
    /// Exposed so the Discord bridge can report the authoritative schedule.
    /// </summary>
    public DateTime ShutdownTime => _shutdownTime;

    /// <summary>
    /// True only for a Discord-requested automatic AAEmu.Game restart.
    /// Existing /scripts shutdown behavior remains a normal shutdown.
    /// </summary>
    public bool RestartAfterShutdown { get; }

    public string Reason { get; }
    public string RequestedBy { get; }

    public ShutdownTask(
        DateTime shutdownTime,
        int exitCode,
        bool restartAfterShutdown = false,
        string reason = "",
        string requestedBy = "")
    {
        _shutdownTime = shutdownTime;
        _exitCode = exitCode;
        RestartAfterShutdown = restartAfterShutdown;
        Reason = reason ?? string.Empty;
        RequestedBy = requestedBy ?? string.Empty;
        _nextTriggerTime = CalculateLargestNextTrigger();
    }

    /// <summary>
    /// v27 self-restart is intentionally conservative. It is enabled only when
    /// AAEmu.Game is running as the normal Windows apphost executable. If AAEmu
    /// is launched through dotnet/Docker/service wrappers, Discord /restart is
    /// rejected rather than risking a shutdown that cannot relaunch itself.
    /// </summary>
    public static bool CanSelfRestart(out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "Automatic self-restart currently requires Windows AAEmu.Game.exe.";
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            detail = "AAEmu could not determine its executable path.";
            return false;
        }

        var processName = Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(processName, "AAEmu.Game", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"AAEmu is running through '{Path.GetFileName(processPath)}'; automatic self-restart is disabled for this launch method.";
            return false;
        }

        detail = processPath;
        return true;
    }

    private DateTime CalculateLargestNextTrigger()
    {
        var remaining = _shutdownTime - DateTime.UtcNow;
        var remainingDelta = Math.Ceiling(remaining.TotalSeconds);
        foreach (var triggerPoint in _triggerPoints)
        {
            if (remainingDelta > triggerPoint)
                return _shutdownTime - TimeSpan.FromSeconds(triggerPoint);
        }

        return DateTime.MaxValue;
    }

    public void ChangeShutdownTime(DateTime shutdownTime)
    {
        _shutdownTime = shutdownTime;
        var remaining = (int)Math.Ceiling((_shutdownTime - DateTime.UtcNow).TotalMinutes);
        WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
            3,
            Color.Red,
            10000,
            $"Server {(RestartAfterShutdown ? "restart" : "shutdown")} has been rescheduled to {remaining} minutes from now!"));
        _nextTriggerTime = CalculateLargestNextTrigger();
    }

    public override void Execute()
    {
        if (_alreadyShuttingDown)
            return;

        if (_shutdownTime <= DateTime.UtcNow)
        {
            _alreadyShuttingDown = true;

            // Wipe all BuyBack containers manually, as there will be no trigger for disconnected players.
            try
            {
                foreach (var character in WorldManager.Instance.GetAllCharacters())
                    character.BuyBackItems.Wipe();
            }
            catch (Exception)
            {
                // Ignore and continue the normal shutdown path.
            }

            if (SaveManager.Instance.DoSave())
            {
                if (RestartAfterShutdown)
                {
                    if (!TryScheduleSelfRestart(out var restartError))
                    {
                        WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
                            3,
                            Color.Magenta,
                            15000,
                            "Automatic server restart could not be prepared. The server will remain online."));

                        Console.WriteLine($"[DiscordBridge] Automatic restart cancelled: {restartError}");
                        SaveManager.Instance.ShutdownTask = null;
                        Cancel();
                        return;
                    }

                    WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
                        3,
                        Color.Magenta,
                        15000,
                        "The server is restarting right now!"));
                }
                else
                {
                    WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(
                        3,
                        Color.Magenta,
                        15000,
                        "The server is shutting down right now!"));
                }

                Environment.Exit(_exitCode);
            }

            // If saving failed, do not keep a dead repeating shutdown task around.
            _alreadyShuttingDown = false;
            SaveManager.Instance.ShutdownTask = null;
            Cancel();
            return;
        }

        var remainingTime = _shutdownTime - DateTime.UtcNow;
        var remainingSeconds = (int)Math.Ceiling(remainingTime.TotalSeconds);
        if (DateTime.UtcNow < _nextTriggerTime)
            return;

        _nextTriggerTime = CalculateLargestNextTrigger();

        var showSeconds = remainingSeconds <= 100;
        var showMinutes = remainingSeconds <= 3600 && !showSeconds;
        var showHours = remainingSeconds > 3600 && !showSeconds;

        var action = RestartAfterShutdown ? "restarting" : "shutting down";
        var shutdownText = $"The server is {action} soon!";
        var popupTime = 3000;

        if (showHours)
        {
            shutdownText = $"The server is {action} in {remainingSeconds / 3600} hours!";
            popupTime = 10000;
        }
        else if (showMinutes)
        {
            shutdownText = $"The server is {action} in {remainingSeconds / 60} minutes!";
            popupTime = 6000;
        }
        else if (showSeconds)
        {
            shutdownText = $"The server is {action} in {remainingSeconds} seconds!";
            popupTime = 3000;
        }

        WorldManager.Instance.BroadcastPacketToServer(
            new SCNoticeMessagePacket(3, Color.Red, popupTime, shutdownText));
    }

    private static bool TryScheduleSelfRestart(out string error)
    {
        error = string.Empty;

        if (!CanSelfRestart(out var processPath))
        {
            error = processPath;
            return false;
        }

        try
        {
            var workingDirectory = Environment.CurrentDirectory;
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var argumentList = args.Length == 0
                ? string.Empty
                : " -ArgumentList @(" + string.Join(",", args.Select(PsQuote)) + ")";

            // Launch a tiny detached PowerShell helper. The delay gives the old
            // AAEmu.Game process time to release its listening sockets and files.
            var script =
                "Start-Sleep -Seconds 4; " +
                $"Start-Process -FilePath {PsQuote(processPath)} -WorkingDirectory {PsQuote(workingDirectory)}{argumentList}";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var helper = Process.Start(startInfo);
            if (helper == null)
            {
                error = "PowerShell restart helper could not be started.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string PsQuote(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
    }
}
