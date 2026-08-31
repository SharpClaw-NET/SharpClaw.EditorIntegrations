using System.Text.Json;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.EditorCommon.Handlers;

/// <summary>Runs editor session HTTP routes through typed actions.</summary>
public sealed class EditorSessionEndpointContribution(EditorSessionActionGateway sessions)
    : IModuleHttpEndpointHandler
{
    private static readonly JsonElement EmptyPayload =
        JsonSerializer.SerializeToElement(new { });

    private static IReadOnlyList<RouteDefinition> Routes { get; } =
    [
        Route("editor.sessions.create", "/resources/editorsessions/", "POST", EditorSessionOperation.Create),
        Route("editor.sessions.list", "/resources/editorsessions/", "GET", EditorSessionOperation.List),
        Route("editor.sessions.get", "/resources/editorsessions/{id}", "GET", EditorSessionOperation.Get),
        Route("editor.sessions.update", "/resources/editorsessions/{id}", "PUT", EditorSessionOperation.Update),
        Route("editor.sessions.delete", "/resources/editorsessions/{id}", "DELETE", EditorSessionOperation.Delete),
    ];

    public static IReadOnlyList<ModuleEndpointRouteDescriptor> EndpointRoutes { get; } =
        Routes.Select(route => route.Descriptor).ToArray();

    public async ValueTask<ModuleHttpEndpointResponse> InvokeAsync(
        HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostActionEntry);

        var route = Routes.SingleOrDefault(candidate =>
            candidate.Descriptor.ToRouteIdentity().Equals(request.Route));
        if (route is null)
            return Error(404, "endpoint_route_not_found");

        try
        {
            Guid? id = route.RequiresId ? ReadId(request.RouteValues) : null;
            var payload = route.Operation switch
            {
                EditorSessionOperation.Create => ReadPayload<CreateEditorSessionRequest>(request.Body),
                EditorSessionOperation.Update => ReadPayload<UpdateEditorSessionRequest>(request.Body),
                _ => EmptyPayload,
            };
            var result = await sessions.ExecuteAsync(
                hostActionEntry,
                request.Invocation.HostActionContext,
                new EditorSessionAction(route.Operation, id, payload),
                cancellationToken);
            return ToResponse(route.Operation, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return Error(400, "endpoint_invalid_json");
        }
        catch (ArgumentException)
        {
            return Error(400, "endpoint_invalid_request");
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

    private static RouteDefinition Route(
        string id,
        string path,
        string method,
        EditorSessionOperation operation) =>
        new(
            new ModuleEndpointRouteDescriptor(id, path, method, HostEndpointTransport.Http),
            operation,
            path.Contains("{id}", StringComparison.Ordinal));

    private static Guid ReadId(IReadOnlyDictionary<string, string[]> routeValues)
    {
        if (!routeValues.TryGetValue("id", out var values) ||
            values.Length != 1 ||
            !Guid.TryParseExact(values[0], "D", out var id) ||
            id == Guid.Empty)
        {
            throw new ArgumentException("A canonical editor session ID is required.");
        }

        return id;
    }

    private static JsonElement ReadPayload<T>(byte[] body)
    {
        if (body is null || body.Length == 0)
            throw new JsonException("The request body is empty.");

        var value = JsonSerializer.Deserialize<T>(body, EditorJson.Options)
            ?? throw new JsonException("The request body is null.");
        return JsonSerializer.SerializeToElement(value, EditorJson.Options);
    }

    private static ModuleHttpEndpointResponse ToResponse(
        EditorSessionOperation operation,
        JsonElement result)
    {
        if (operation is EditorSessionOperation.Get or EditorSessionOperation.Update &&
            result.ValueKind == JsonValueKind.Null)
        {
            return ModuleHttpEndpointResponse.Empty(404);
        }

        if (operation == EditorSessionOperation.Delete)
        {
            return result.ValueKind == JsonValueKind.True && result.GetBoolean()
                ? ModuleHttpEndpointResponse.Empty(204)
                : ModuleHttpEndpointResponse.Empty(404);
        }

        return ModuleHttpEndpointResponse.Json(200, result);
    }

    private static ModuleHttpEndpointResponse Error(int statusCode, string code) =>
        ModuleHttpEndpointResponse.Json(
            statusCode,
            JsonSerializer.SerializeToElement(new { error = code }));

    private sealed record RouteDefinition(
        ModuleEndpointRouteDescriptor Descriptor,
        EditorSessionOperation Operation,
        bool RequiresId);
}
