using Bijecta.CliAuth.Core;
using Bijecta.CliAuth.Storage.File;

// Disposable process-test protocol. No session credentials or raw paths are emitted.
var store = new FileTokenStore(args[0]);
var key = new SessionKey(args[1]);
using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.WriteLine("WAITING");
try
{
    await using (var lease = await store.AcquireAsync(key,
        TimeSpan.FromMilliseconds(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)), watchdog.Token))
    {
        Console.WriteLine("ACQUIRED");
        if (args[3] == "hold")
        {
            while (await Console.In.ReadLineAsync(watchdog.Token) is not null) { }
        }
    }
    Console.WriteLine("RELEASED");
}
catch (CliAuthException error)
{
    Console.WriteLine(error.ErrorCode.ToString());
}
