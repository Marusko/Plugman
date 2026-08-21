using System.Runtime.ExceptionServices;

namespace Plugman.Tests.Wpf;

/// <summary>
/// Runs a test body on a dedicated STA thread. WPF types can only be created on one, and the
/// xunit test runner threads are MTA.
/// </summary>
internal static class Sta
{
    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The STA test thread did not finish in time.");
        failure?.Throw();
    }
}
