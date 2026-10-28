using Tern.Remote.Login;

namespace Tern.Cli.Common;

/// <summary>Why a command refuses to write to an endpoint, and what would let it.</summary>
public static class ReadOnlyEndpoint
{
    /// <summary>The error for a write to <paramref name="endpoint"/>, saying what made it read-only.</summary>
    /// <param name="action">What the command was about to do, such as "push to it"; null for no hint.</param>
    public static string Message(Endpoint endpoint, string? action) =>
        endpoint.Shared?.AllowWrite == false
            ? $"Endpoint '{endpoint.Name}' does not allow writes: the project's tern.json marks it read-only."
            : action is null
                ? $"Endpoint '{endpoint.Name}' does not allow writes."
                : $"Endpoint '{endpoint.Name}' does not allow writes. Log in again with --allow-write true to {action}.";
}
