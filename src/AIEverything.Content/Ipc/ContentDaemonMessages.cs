using System.Text.Json;

namespace AIEverything.Content.Ipc;

public sealed record ContentDaemonRequest(
    string Operation,
    JsonElement Payload);

public sealed record ContentDaemonError(
    string Code,
    string Message,
    string CorrectiveAction);

public sealed record ContentDaemonResponse(
    bool Success,
    JsonElement? Result,
    ContentDaemonError? Error);

public sealed record RootPathRequest(string Path);

public sealed record IndexControlRequest(string? Path = null);

public sealed record IndexConfigurationRequest(bool DisclosureAccepted, bool Enabled);
