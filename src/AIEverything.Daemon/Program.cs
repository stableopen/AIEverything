using System.Diagnostics;
using System.Text.Json;
using AIEverything.Content.Errors;
using AIEverything.Daemon;

if (args is not ["run"])
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        code = ContentErrorCodes.InvalidArguments,
        message = "Usage: AIEverything.Daemon.exe run"
    }));
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await using var daemon = new ContentDaemon(ContentDaemonOptions.CreateDefault());
    await daemon.RunAsync(cancellation.Token);
    return 0;
}
catch (ContentIndexException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        code = exception.Code,
        message = exception.Message,
        correctiveAction = exception.CorrectiveAction
    }));
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        code = ContentErrorCodes.ServiceUnavailable,
        message = exception.Message,
        correctiveAction = "Check the local AIEverything data directory and retry."
    }));
    return 1;
}
