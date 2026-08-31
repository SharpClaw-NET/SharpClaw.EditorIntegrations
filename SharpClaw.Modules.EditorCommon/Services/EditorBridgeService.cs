using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.EditorCommon.Models;

namespace SharpClaw.Modules.EditorCommon.Services;

/// <summary>Manages neutral WebSocket channels for connected editor extensions.</summary>
public sealed class EditorBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, EditorConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<EditorActionResponse>> _pending = new();

    public IReadOnlyList<EditorConnection> GetConnections() =>
        _connections.Values.ToList();

    public EditorConnection? GetActiveConnection() =>
        _connections.Values.FirstOrDefault();

    public EditorConnection? GetConnection(Guid sessionId) =>
        _connections.Values.FirstOrDefault(connection => connection.SessionId == sessionId);

    public EditorConnection GetRequiredConnection(Guid sessionId, string expectedEditorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEditorKey);
        var connection = GetConnection(sessionId)
            ?? throw new InvalidOperationException(
                $"No editor connected with session {sessionId}.");
        if (!string.Equals(
                connection.EditorType.ToString(),
                expectedEditorKey,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"Session {sessionId} is not connected to the expected editor.");
        }

        return connection;
    }

    internal async Task<EditorActionResponse> SendRequestAsync(
        EditorConnection connection,
        string action,
        Dictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var request = new EditorActionRequest(Guid.NewGuid(), action, parameters);
        var completion = new TaskCompletionSource<EditorActionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = completion;

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                type = "request",
                requestId = request.RequestId,
                action = request.Action,
                @params = request.Params,
            }, JsonOptions);
            await SendTextAsync(connection.Channel, json, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var registration = timeout.Token.Register(() =>
                completion.TrySetException(new TimeoutException(
                    $"Editor did not respond to '{action}' within {RequestTimeout.TotalSeconds}s.")));
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    public async Task HandleConnectionAsync(
        IModuleWebSocketChannel channel,
        IHostActionEntry hostActionEntry,
        HostActionEntryRequestContext hostContext,
        EditorSessionActionGateway sessionActions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(sessionActions);

        string? connectionId = null;
        var closeStatus = 1000;
        var closeDescription = "Closing";

        try
        {
            var registrationJson = await ReceiveTextAsync(channel, cancellationToken);
            if (registrationJson is null)
                return;

            RegistrationEnvelope? registration;
            try
            {
                registration = JsonSerializer.Deserialize<RegistrationEnvelope>(
                    registrationJson,
                    JsonOptions);
            }
            catch (JsonException)
            {
                closeStatus = 1002;
                closeDescription = "Registration is not valid JSON";
                return;
            }

            if (registration?.Type != "register" ||
                string.IsNullOrWhiteSpace(registration.EditorKey))
            {
                closeStatus = 1002;
                closeDescription = "First message must be a registration";
                return;
            }

            connectionId = Guid.NewGuid().ToString("N");
            var editorType = Enum.TryParse<EditorType>(
                registration.EditorKey,
                ignoreCase: true,
                out var parsed)
                ? parsed
                : EditorType.Other;
            var workspaceName = registration.WorkspacePath is null
                ? null
                : Path.GetFileName(registration.WorkspacePath);
            var name = registration.EditorKey
                + (workspaceName is null ? string.Empty : $" - {workspaceName}");
            var sessionResult = await sessionActions.ExecuteAsync(
                hostActionEntry,
                hostContext,
                new EditorSessionAction(
                    EditorSessionOperation.GetOrCreate,
                    null,
                    JsonSerializer.SerializeToElement(new CreateEditorSessionRequest(
                        name,
                        registration.EditorKey,
                        registration.EditorVersion,
                        registration.WorkspacePath))),
                cancellationToken);
            var session = sessionResult.Deserialize<EditorSessionResponse>(JsonOptions)
                ?? throw new InvalidOperationException(
                    "The editor session action returned an invalid session.");

            var connection = new EditorConnection(
                connectionId,
                session.Id,
                editorType,
                registration.EditorVersion,
                registration.WorkspacePath,
                channel,
                "Open",
                DateTimeOffset.UtcNow);
            _connections[connectionId] = connection;

            await SendTextAsync(channel, JsonSerializer.Serialize(new
            {
                type = "registered",
                sessionId = connection.SessionId,
                connectionId,
            }, JsonOptions), cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(channel, cancellationToken);
                if (json is null)
                    break;

                var envelope = JsonSerializer.Deserialize<ResponseEnvelope>(json, JsonOptions);
                if (envelope?.Type == "response" &&
                    envelope.RequestId is { } requestId &&
                    _pending.TryGetValue(requestId, out var completion))
                {
                    completion.TrySetResult(new EditorActionResponse(
                        requestId,
                        envelope.Success,
                        envelope.Data,
                        envelope.Error));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (connectionId is not null)
                _connections.TryRemove(connectionId, out _);
            await CloseBestEffortAsync(channel, closeStatus, closeDescription);
        }
    }

    private static async Task SendTextAsync(
        IModuleWebSocketChannel channel,
        string text,
        CancellationToken cancellationToken) =>
        await channel.SendAsync(
            new ModuleWebSocketMessage(
                ModuleWebSocketMessageType.Text,
                Encoding.UTF8.GetBytes(text)),
            cancellationToken);

    private static async Task<string?> ReceiveTextAsync(
        IModuleWebSocketChannel channel,
        CancellationToken cancellationToken)
    {
        var message = await channel.ReceiveAsync(cancellationToken);
        if (message is null || message.Type == ModuleWebSocketMessageType.Close)
            return null;
        if (message.Type != ModuleWebSocketMessageType.Text)
            throw new InvalidDataException("The editor bridge accepts text messages only.");
        return Encoding.UTF8.GetString(message.Payload);
    }

    private static async Task CloseBestEffortAsync(
        IModuleWebSocketChannel channel,
        int closeStatus,
        string closeDescription)
    {
        try
        {
            await channel.CloseAsync(closeStatus, closeDescription, CancellationToken.None);
        }
        catch
        {
        }
    }

    private sealed class RegistrationEnvelope
    {
        public string? Type { get; set; }

        [JsonPropertyName("editorType")]
        public string EditorKey { get; set; } = string.Empty;

        public string? EditorVersion { get; set; }

        public string? WorkspacePath { get; set; }
    }

    private sealed class ResponseEnvelope
    {
        public string? Type { get; set; }

        public Guid? RequestId { get; set; }

        public bool Success { get; set; }

        public string? Data { get; set; }

        public string? Error { get; set; }
    }
}

/// <summary>Represents one active neutral editor connection.</summary>
public sealed record EditorConnection(
    string ConnectionId,
    Guid SessionId,
    EditorType EditorType,
    string? EditorVersion,
    string? WorkspacePath,
    IModuleWebSocketChannel Channel,
    string State,
    DateTimeOffset ConnectedAt);
