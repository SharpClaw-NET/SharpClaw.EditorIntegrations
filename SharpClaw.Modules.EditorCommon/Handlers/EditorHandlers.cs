using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.EditorCommon.Handlers;

/// <summary>Lists active editor bridge connections through a typed action.</summary>
public sealed class EditorEndpointContribution(EditorBridgeActionGateway bridge)
    : IModuleHttpEndpointHandler
{
    public static ModuleEndpointRouteDescriptor SessionsRoute { get; } = new(
        "editor.connections.list",
        "/editor/sessions",
        "GET",
        HostEndpointTransport.Http);

    public async ValueTask<ModuleHttpEndpointResponse> InvokeAsync(
        HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostActionEntry);

        if (!SessionsRoute.ToRouteIdentity().Equals(request.Route))
            return Error(404, "endpoint_route_not_found");

        try
        {
            var result = await bridge.ReadAsync(
                hostActionEntry,
                request.Invocation.HostActionContext,
                sessionId: null,
                cancellationToken);
            return ModuleHttpEndpointResponse.Json(
                200,
                JsonSerializer.SerializeToElement(result.Connections, EditorJson.Options));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return Error(403, "endpoint_forbidden");
        }
        catch (InvalidOperationException)
        {
            return Error(500, "endpoint_failed");
        }
    }

    private static ModuleHttpEndpointResponse Error(int statusCode, string code) =>
        ModuleHttpEndpointResponse.Json(
            statusCode,
            JsonSerializer.SerializeToElement(new { error = code }));
}

/// <summary>Runs the neutral editor WebSocket route.</summary>
public sealed class EditorWebSocketEndpointContribution(
    EditorSessionActionGateway sessions,
    EditorBridgeService bridge) : IModuleWebSocketEndpointHandler
{
    public static ModuleEndpointRouteDescriptor WebSocketRoute { get; } = new(
        "editor.websocket",
        "/editor/ws",
        "GET",
        HostEndpointTransport.WebSocket);

    public ValueTask InvokeAsync(
        HostEndpointRouteRequest request,
        IModuleWebSocketChannel channel,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(hostActionEntry);

        if (!WebSocketRoute.ToRouteIdentity().Equals(request.Route))
            throw new InvalidOperationException("The editor WebSocket route is not registered.");

        return new ValueTask(bridge.HandleConnectionAsync(
            channel,
            hostActionEntry,
            request.Invocation.HostActionContext,
            sessions,
            cancellationToken));
    }
}
