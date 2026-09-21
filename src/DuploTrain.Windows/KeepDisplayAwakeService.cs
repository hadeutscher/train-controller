using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DuploTrain.Windows;

/// <summary>Asks Windows not to blank the display or idle the machine while the
/// train is drivable.
///
/// This exists because of the lock screen. Once the workstation locks, the lock
/// screen also receives the gamepad — it drives the on-screen keyboard with it —
/// and there is nothing a user-session process can do about that: the lock
/// screen runs on the secure desktop, XInput has no exclusive mode, and
/// RawInput's exclusive flags do not cross a desktop boundary.
///
/// So the only lever available is to stop the session locking *itself* through
/// the idle/display timeout. An explicit lock — Win+L, or a policy lock — is
/// still an explicit lock and this will not prevent it.</summary>
public sealed class KeepDisplayAwakeService(ILogger<KeepDisplayAwakeService> logger) : IHostedService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private const uint DisplayRequired = 0x00000002;

    private readonly ManualResetEventSlim _stop = new(false);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The execution state is a property of the calling *thread* and lasts
        // only as long as that thread does, so it cannot be set from a
        // thread-pool thread that may be recycled.
        new Thread(Hold)
        {
            IsBackground = true,
            Name = "duplo-keep-awake",
        }.Start();

        return Task.CompletedTask;
    }

    private void Hold()
    {
        if (SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired) == 0)
        {
            logger.LogWarning(
                "windows refused the request to keep the display awake; the session may lock itself");
            return;
        }

        logger.LogInformation("keeping the display awake so the session does not lock itself");

        _stop.Wait();
        SetThreadExecutionState(Continuous);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Set();
        return Task.CompletedTask;
    }
}
