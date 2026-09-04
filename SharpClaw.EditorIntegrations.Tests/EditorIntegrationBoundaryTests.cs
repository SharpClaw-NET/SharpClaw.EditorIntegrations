using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.EditorCommon;
using SharpClaw.Modules.EditorCommon.Handlers;
using SharpClaw.Modules.EditorCommon.Models;
using SharpClaw.Modules.EditorCommon.Services;
using SharpClaw.Modules.VS2026Editor;
using SharpClaw.Modules.VSCodeEditor;

namespace SharpClaw.EditorIntegrations.Tests;

[TestFixture]
public sealed class EditorIntegrationBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Test]
    public void EditorCommonGraph_CompilesStorageApplicationAndTypedEntries()
    {
        var graph = Compile(new EditorCommonModule(), "editor-common.json");

        Assert.That(graph.Identity.Id, Is.EqualTo("sharpclaw_editor_common"));
        Assert.That(graph.Storage.Select(item => item.StorageName), Is.EqualTo(["editor_sessions"]));
        Assert.That(
            graph.Actions.Select(item => item.Descriptor.Key.Value),
            Is.EquivalentTo(new[]
            {
                EditorProtocolContracts.BridgeConnectionReadActionName,
                EditorProtocolContracts.BridgeRequestReadActionName,
                EditorProtocolContracts.BridgeRequestMutationActionName,
                EditorProtocolContracts.SessionReadActionName,
                EditorProtocolContracts.SessionMutationActionName,
                SidecarChatActionDescriptors.ContextContributor.Key.Value,
            }));
        Assert.That(graph.Application.Endpoints, Has.Count.EqualTo(7));
        Assert.That(graph.Application.Endpoints.Select(item => item.HandlerType),
            Does.Contain(typeof(EditorEndpointContribution)));
        Assert.That(graph.Application.Endpoints.Select(item => item.HandlerType),
            Does.Contain(typeof(EditorWebSocketEndpointContribution)));
        Assert.That(graph.Application.Endpoints.Select(item => item.HandlerType),
            Does.Contain(typeof(EditorSessionEndpointContribution)));
        Assert.That(graph.Application.Endpoints.Select(item => item.Descriptor.Path),
            Is.EquivalentTo(new[]
            {
                "/editor/sessions",
                "/editor/ws",
                "/resources/editorsessions/",
                "/resources/editorsessions/",
                "/resources/editorsessions/{id}",
                "/resources/editorsessions/{id}",
                "/resources/editorsessions/{id}",
            }));
        Assert.That(graph.Application.CliCommands.Select(item => item.Descriptor.Name), Is.EqualTo(["editorsession"]));
        Assert.That(graph.Application.ActionEntries, Has.Count.EqualTo(6));
        Assert.That(
            graph.Application.ActionEntries.Select(item => item.Descriptor.Key.Value),
            Is.EquivalentTo(graph.Actions.Select(item => item.Descriptor.Key.Value)));
        Assert.That(
            graph.Application.ActionEntries.Select(item => item.TerminalId),
            Is.EquivalentTo(new[]
            {
                EditorProtocolContracts.BridgeConnectionReadTerminalId,
                EditorProtocolContracts.BridgeRequestReadTerminalId,
                EditorProtocolContracts.BridgeRequestMutationTerminalId,
                EditorProtocolContracts.SessionReadTerminalId,
                EditorProtocolContracts.SessionMutationTerminalId,
                SidecarChatActionDescriptors.ContextContributorTerminalId,
            }));
        var chat = graph.CreateSidecarApplicationDiscovery().Chat;
        Assert.That(chat.Select(item => item.Kind), Is.EqualTo(new[]
        {
            SidecarChatContributionKind.ContextContributor,
        }));
        Assert.That(graph.Contracts.Single(item => item.IsExport).ContractName,
            Is.EqualTo(EditorProtocolContracts.ContractName));
    }

    [Test]
    public void ConcreteEditorGraphs_CompileElevenToolsAndSharedRequirement()
    {
        var vs2026 = Compile(new VS2026EditorModule(), "vs2026-editor.json");
        var vscode = Compile(new VSCodeEditorModule(), "vscode-editor.json");

        AssertTools(vs2026, "vs26_", "sharpclaw_vs2026_editor");
        AssertTools(vscode, "vsc_", "sharpclaw_vscode_editor");
    }

    [Test]
    public void BridgeActions_HaveDistinctStableTerminalsAndRepeatPolicies()
    {
        Assert.That(
            EditorProtocolContracts.BridgeRequestReadTerminalId,
            Is.Not.EqualTo(EditorProtocolContracts.BridgeRequestMutationTerminalId));
        Assert.That(EditorProtocolContracts.BridgeRequestReadDescriptor.RepeatPolicy.Kind,
            Is.EqualTo(ActionRepeatKind.Idempotent));
        Assert.That(EditorProtocolContracts.BridgeRequestMutationDescriptor.RepeatPolicy.Kind,
            Is.EqualTo(ActionRepeatKind.None));
        Assert.That(EditorProtocolContracts.BridgeRequestMutationDescriptor.HasIrreversibleEffects,
            Is.True);
        Assert.That(EditorProtocolContracts.SessionMutationDescriptor.RepeatPolicy.Kind,
            Is.EqualTo(ActionRepeatKind.None));
    }

    [Test]
    public void ChatContributorRejectsCancellationBeforeReadingConnections()
    {
        var contributor = new EditorChatContextContributor(new EditorBridgeService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var now = DateTimeOffset.UtcNow;
        var context = new ChatOperationContext(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            now.AddMinutes(1),
            new RequestPrincipal("editor-user"),
            ExtensionFeatureSet.Empty);
        var request = new ChatContextRequest(
            Guid.NewGuid(),
            new ChatProfile("provider", Guid.NewGuid()),
            []);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await contributor.ContributeAsync(request, context, cancellation.Token));
    }

    [Test]
    public async Task ReadTerminal_RejectsMutationBeforeTransport()
    {
        var bridge = new EditorBridgeService();
        var terminal = new EditorBridgeRequestReadTerminal(bridge);
        var action = new EditorBridgeRequestAction(
            Guid.NewGuid(),
            "VisualStudioCode",
            "write_file",
            null);

        var exception = Assert.ThrowsAsync<ArgumentException>(async () =>
            await terminal.InvokeAsync(BridgeContext(action), CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("not valid"));
    }

    [Test]
    public async Task VsCodeRegistration_UsesEditorTypeAndAuthorizedSessionMutation()
    {
        var storage = new RecordingStorageGateway();
        var sessions = new EditorSessionService(new EditorSessionStore(storage));
        var bridge = new EditorBridgeService();
        var host = new RecordingHostActionEntry();
        var sessionGateway = new EditorSessionActionGateway(
            new EditorSessionReadTerminal(sessions, bridge),
            new EditorSessionMutationTerminal(sessions));
        var endpoint = new EditorWebSocketEndpointContribution(sessionGateway, bridge);
        var socket = new ScriptedWebSocketChannel(
            "{\"type\":\"register\",\"editorType\":\"visualStudioCode\",\"editorVersion\":\"1.98\",\"workspacePath\":\"C:\\\\repo\"}");

        await endpoint.InvokeAsync(
            EndpointRequest(EditorWebSocketEndpointContribution.WebSocketRoute),
            socket,
            host,
            CancellationToken.None);

        var request = host.Requests
            .OfType<HostActionEntryRequest<EditorSessionAction, JsonElement>>()
            .Single();
        var payload = request.Action.Payload.Deserialize<CreateEditorSessionRequest>(JsonOptions)!;

        Assert.That(request.Descriptor.Key.Value,
            Is.EqualTo(EditorProtocolContracts.SessionMutationActionName));
        Assert.That(payload.EditorKey, Is.EqualTo("visualStudioCode"));
        Assert.That(storage.UpsertCalls, Is.EqualTo(1));
        Assert.That(socket.SentMessages.Single(message => message.Contains("registered", StringComparison.Ordinal)),
            Does.Contain("sessionId"));
        Assert.That(socket.CloseCalls, Is.EqualTo(1));
        Assert.That(bridge.GetConnections(), Is.Empty);

        var session = request.Action.Payload.Deserialize<CreateEditorSessionRequest>(JsonOptions)!;
        Assert.That(Enum.Parse<EditorType>(session.EditorKey, true), Is.EqualTo(EditorType.VisualStudioCode));
        Assert.That(Compile(new VSCodeEditorModule(), "vscode-editor.json").Tools,
            Has.Some.Matches<ModuleToolRegistration>(tool => tool.Descriptor.Name == "vsc_read_file"));
    }

    [Test]
    public void RegistrationWithoutHostAuthority_PerformsNoStorageCallsOrWrites()
    {
        var storage = new RecordingStorageGateway();
        var sessions = new EditorSessionService(new EditorSessionStore(storage));
        var bridge = new EditorBridgeService();
        var sessionGateway = new EditorSessionActionGateway(
            new EditorSessionReadTerminal(sessions, bridge),
            new EditorSessionMutationTerminal(sessions));
        var endpoint = new EditorWebSocketEndpointContribution(sessionGateway, bridge);
        var socket = new ScriptedWebSocketChannel(
            "{\"type\":\"register\",\"editorType\":\"visualStudioCode\",\"workspacePath\":\"C:\\\\repo\"}");
        var host = new RejectingHostActionEntry();

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await endpoint.InvokeAsync(
                EndpointRequest(EditorWebSocketEndpointContribution.WebSocketRoute),
                socket,
                host,
                CancellationToken.None));

        Assert.That(storage.TotalCalls, Is.EqualTo(0));
        Assert.That(storage.UpsertCalls, Is.EqualTo(0));
        Assert.That(socket.CloseCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task VsCodeTool_UsesTypedReadRequestAndRejectsWrongEditorBeforeBridgeSend()
    {
        var sessionId = Guid.NewGuid();
        var host = new RecordingCrossSidecarHostActionEntry
        {
            Connection = new EditorBridgeConnectionReadResult(
                true,
                new EditorBridgeConnectionSummary(
                    "connection",
                    sessionId,
                    "VisualStudioCode",
                    "1.98",
                    "C:\\repo",
                    "Open",
                    DateTimeOffset.UtcNow),
                []),
            Response = new EditorActionResponse(Guid.NewGuid(), true, "ok"),
        };
        var handler = new VSCodeEditorToolHandler(host);
        var invocation = new ToolInvocation(
            Guid.NewGuid(),
            null,
            "tool-call",
            "vsc_read_file",
            JsonSerializer.SerializeToElement(new
            {
                targetId = sessionId,
                filePath = "README.md",
            }),
            Context());

        var result = await handler.InvokeAsync(invocation, CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(host.Requests, Has.Count.EqualTo(2));
        var bridgeRequest = host.Requests
            .OfType<ModuleCrossSidecarActionEntryRequest<EditorBridgeRequestAction, EditorActionResponse>>()
            .Single();
        Assert.That(bridgeRequest.Descriptor.Key.Value,
            Is.EqualTo(EditorProtocolContracts.BridgeRequestReadActionName));
        Assert.That(bridgeRequest.Action.ExpectedEditorKey, Is.EqualTo("VisualStudioCode"));
        Assert.That(bridgeRequest.Action.Action, Is.EqualTo("read_file"));

        host.Connection = host.Connection with
        {
            Connection = host.Connection.Connection! with { EditorKey = "VisualStudio2026" }
        };
        host.Requests.Clear();

        var rejected = await handler.InvokeAsync(invocation, CancellationToken.None);

        Assert.That(rejected.IsError, Is.True);
        Assert.That(host.Requests, Has.Count.EqualTo(1));

        host.Connection = host.Connection with
        {
            Connection = host.Connection.Connection! with { EditorKey = "VisualStudioCode" },
        };
        host.Requests.Clear();

        var later = await handler.InvokeAsync(invocation, CancellationToken.None);

        Assert.That(later.IsError, Is.False);
        Assert.That(host.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public void WebSocketRoute_UsesOneNeutralProtectedDescriptor()
    {
        var route = EditorWebSocketEndpointContribution.WebSocketRoute;

        Assert.That(route.Id, Is.EqualTo("editor.websocket"));
        Assert.That(route.Path, Is.EqualTo("/editor/ws"));
        Assert.That(route.Method, Is.EqualTo("GET"));
        Assert.That(route.Transport, Is.EqualTo(HostEndpointTransport.WebSocket));
        Assert.That(route.IsWellFormed, Is.True);
    }

    [Test]
    public async Task SessionEndpoint_UsesCanonicalRouteValueAndRejectsMissingValue()
    {
        var storage = new RecordingStorageGateway();
        var bridge = new EditorBridgeService();
        var sessions = new EditorSessionService(new EditorSessionStore(storage));
        var gateway = new EditorSessionActionGateway(
            new EditorSessionReadTerminal(sessions, bridge),
            new EditorSessionMutationTerminal(sessions));
        var handler = new EditorSessionEndpointContribution(gateway);
        var host = new RecordingHostActionEntry();
        var route = EditorSessionEndpointContribution.EndpointRoutes.Single(item =>
            item.Method == "GET" && item.Path.EndsWith("/{id}", StringComparison.Ordinal));
        var sessionId = Guid.NewGuid();
        var request = EndpointRequest(route) with
        {
            RouteValues = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["id"] = [sessionId.ToString("D")],
            },
        };

        var response = await handler.InvokeAsync(request, host, CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(404));
        var action = host.Requests
            .OfType<HostActionEntryRequest<EditorSessionAction, JsonElement>>()
            .Single();
        Assert.That(action.Action.SessionId, Is.EqualTo(sessionId));
        Assert.That(action.Action.Operation, Is.EqualTo(EditorSessionOperation.Get));

        host.Requests.Clear();
        var rejected = await handler.InvokeAsync(
            EndpointRequest(route),
            host,
            CancellationToken.None);

        Assert.That(rejected.StatusCode, Is.EqualTo(400));
        Assert.That(host.Requests, Is.Empty);
    }

    [Test]
    public async Task ConnectionEndpoint_UsesTheSuppliedTypedHostEntry()
    {
        var bridge = new EditorBridgeService();
        var host = new RecordingHostActionEntry();
        var handler = new EditorEndpointContribution(
            new EditorBridgeActionGateway(
                new EditorBridgeConnectionReadTerminal(bridge)));

        var response = await handler.InvokeAsync(
            EndpointRequest(EditorEndpointContribution.SessionsRoute),
            host,
            CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(200));
        Assert.That(host.Requests
            .OfType<HostActionEntryRequest<
                EditorBridgeConnectionReadAction,
                EditorBridgeConnectionReadResult>>(),
            Has.Exactly(1).Items);
    }

    [Test]
    public async Task CancelledWebSocket_StopsBeforeActionAndStorage()
    {
        var storage = new RecordingStorageGateway();
        var bridge = new EditorBridgeService();
        var sessions = new EditorSessionService(new EditorSessionStore(storage));
        var gateway = new EditorSessionActionGateway(
            new EditorSessionReadTerminal(sessions, bridge),
            new EditorSessionMutationTerminal(sessions));
        var handler = new EditorWebSocketEndpointContribution(gateway, bridge);
        var host = new RecordingHostActionEntry();
        var channel = new ScriptedWebSocketChannel(
            "{\"type\":\"register\",\"editorType\":\"visualStudioCode\"}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await handler.InvokeAsync(
            EndpointRequest(EditorWebSocketEndpointContribution.WebSocketRoute),
            channel,
            host,
            cancellation.Token);

        Assert.That(host.Requests, Is.Empty);
        Assert.That(storage.TotalCalls, Is.EqualTo(0));
        Assert.That(channel.CloseCalls, Is.EqualTo(1));
        Assert.That(bridge.GetConnections(), Is.Empty);
    }

    [Test]
    public async Task EditorCli_UsesTheTypedSessionReadAction()
    {
        var storage = new RecordingStorageGateway();
        var bridge = new EditorBridgeService();
        var host = new RecordingHostActionEntry();
        var services = new ServiceCollection();
        services.AddSingleton<IScopedStorageGateway>(storage);
        services.AddSingleton(bridge);
        services.AddScoped<EditorSessionStore>();
        services.AddScoped<EditorSessionService>();
        services.AddScoped<EditorSessionReadTerminal>();
        services.AddScoped<EditorSessionMutationTerminal>();
        services.AddScoped<EditorSessionActionGateway>();
        using var provider = services.BuildServiceProvider();
        var handler = new EditorCliHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            host);
        var invocationId = Guid.NewGuid();

        var result = await handler.ExecuteAsync(
            new CliInvocation(
                invocationId,
                "editorsession",
                ["list"],
                Context(HostActionEntryIngress.Cli, invocationId)),
            CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        var action = host.Requests
            .OfType<HostActionEntryRequest<EditorSessionAction, JsonElement>>()
            .Single();
        Assert.That(action.Descriptor.Key.Value,
            Is.EqualTo(EditorProtocolContracts.SessionReadActionName));
        Assert.That(action.Action.Operation, Is.EqualTo(EditorSessionOperation.List));
    }

    [Test]
    public async Task ModuleLifecycle_RemainsCancellationSafe()
    {
        var module = new EditorCommonModule();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await module.StartAsync(
            new ServiceStartContext(
                "test-host",
                "test-contract",
                ExtensionFeatureSet.Empty),
            cancellation.Token);
        await module.StopAsync(cancellation.Token);
    }

    private static ModuleContributionGraph Compile(
        ISharpClawModule module,
        string manifestName)
    {
        var manifestPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "manifests",
            manifestName);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions)!;
        return SharpClawModuleCompiler.Compile(
            module,
            manifest,
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.OutOfProcess });
    }

    private static void AssertTools(
        ModuleContributionGraph graph,
        string prefix,
        string SourceId)
    {
        Assert.That(graph.Identity.Id, Is.EqualTo(SourceId));
        Assert.That(graph.Tools, Has.Count.EqualTo(11));
        Assert.That(graph.Tools.Select(item => item.Descriptor.Name),
            Has.All.StartsWith(prefix));
        Assert.That(graph.Contracts, Has.Count.EqualTo(1));
        Assert.That(graph.Contracts.Single().ContractName,
            Is.EqualTo(EditorProtocolContracts.ContractName));
    }

    private static ActionContext<EditorBridgeRequestAction> BridgeContext(
        EditorBridgeRequestAction? action = null) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            EditorProtocolContracts.BridgeRequestReadDescriptor.Key,
            "editor-tests",
            RequestPrincipal.Anonymous,
            action ?? new EditorBridgeRequestAction(
                Guid.NewGuid(),
                "VisualStudioCode",
                "read_file",
                null),
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("editor-tests", []));

    private static HostEndpointRouteRequest EndpointRequest(
        EndpointRouteDescriptor descriptor,
        byte[]? body = null)
    {
        var context = Context();
        return new HostEndpointRouteRequest(
            new HostEndpointInvocation(context.InvocationId, descriptor.Id, context),
            descriptor.ToRouteIdentity(),
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            body ?? []);
    }

    private static HostActionEntryRequestContext Context(
        HostActionEntryIngress ingress = HostActionEntryIngress.Endpoint,
        Guid? invocationId = null) =>
        new(
            invocationId ?? Guid.NewGuid(),
            "editor-test-capability",
            ingress,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow.AddMinutes(2));

    private sealed class RecordingStorageGateway : IScopedStorageGateway
    {
        public int TotalCalls { get; private set; }
        public int UpsertCalls { get; private set; }

        public IReadOnlyList<ScopedStorageContractDescriptor> ListContracts() => [];

        public Task<JsonElement> InvokeAsync(
            string SourceId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            TotalCalls++;
            if (operation == ScopedStorageOperations.Upsert)
                UpsertCalls++;
            return Task.FromResult(operation switch
            {
                ScopedStorageOperations.List or ScopedStorageOperations.Query =>
                    JsonSerializer.SerializeToElement(new { records = Array.Empty<object>() }),
                ScopedStorageOperations.Get => JsonSerializer.SerializeToElement(new { found = false }),
                ScopedStorageOperations.Delete => JsonSerializer.SerializeToElement(new { deleted = false }),
                _ => JsonSerializer.SerializeToElement(new { saved = 1 }),
            });
        }

        public Task<ScopedStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
            string SourceId,
            string storageName,
            ScopedStorageMutationAndOutboxRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopedStorageClaimResult<T>> ClaimAsync<T>(
            string SourceId,
            string storageName,
            ScopedStorageClaimRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopedStorageClaimRenewalResult> RenewClaimAsync(
            string SourceId,
            string storageName,
            ScopedStorageClaimRenewalRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopedStorageClaimRecoveryResult> RecoverClaimAsync(
            string SourceId,
            string storageName,
            ScopedStorageClaimRecoveryRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHostActionEntry : IHostActionEntry
    {
        public List<object> Requests { get; } = [];

        public async ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var context = new ActionContext<TAction>(
                request.Context.InvocationId,
                request.Context.ParentInvocationId,
                request.Context.TraceId,
                request.Context.IdempotencyKey,
                request.Context.Depth,
                request.Context.Attempt,
                request.Context.Deadline,
                request.Descriptor.Key,
                "editor-tests",
                request.Context.Caller,
                request.Action,
                request.Context.Features,
                new ActionPipelineSnapshot("editor-tests", []))
            {
                HostActionEntry = this,
            };
            return new CompletedOutcome<TResult>(
                await terminal.InvokeAsync(context, cancellationToken));
        }

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IActionOutcome<TResult>>(
                new NotSupportedException());
    }

    private sealed class RejectingHostActionEntry : IHostActionEntry
    {
        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IActionOutcome<TResult>>(
                new FailedOutcome<TResult>(
                    new ExecutionError("unauthorized", "Host endpoint authority was rejected.")));

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IActionOutcome<TResult>>(
                new NotSupportedException());
    }

    private sealed class RecordingCrossSidecarHostActionEntry :
        IHostActionEntry,
        IModuleCrossSidecarActionEntry
    {
        public EditorBridgeConnectionReadResult Connection { get; set; } =
            new(false, null, []);
        public EditorActionResponse Response { get; set; } =
            new(Guid.NewGuid(), false, Error: "not configured");
        public List<object> Requests { get; } = [];

        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            object result = typeof(TResult) == typeof(EditorBridgeConnectionReadResult)
                ? Connection
                : typeof(TResult) == typeof(EditorActionResponse)
                    ? Response
                    : throw new InvalidOperationException(
                        $"Unexpected result type {typeof(TResult).FullName}.");
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new CompletedOutcome<TResult>((TResult)result));
        }

        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IActionOutcome<TResult>>(
                new NotSupportedException());

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IActionOutcome<TResult>>(
                new NotSupportedException());
    }

    private sealed record CompletedOutcome<TResult>(TResult Value) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;
        public TResult? Result => Value;
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error => null;
        public ActionUncertainty? Uncertainty => null;
    }

    private sealed record FailedOutcome<TResult>(ExecutionError Failure) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Failed;
        public TResult? Result => default;
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error => Failure;
        public ActionUncertainty? Uncertainty => null;
    }

    private sealed class ScriptedWebSocketChannel : IWebSocketChannel
    {
        private readonly Queue<WebSocketMessage?> _messages;

        public ScriptedWebSocketChannel(string registration)
        {
            _messages = new Queue<WebSocketMessage?>(
            [
                new WebSocketMessage(
                    WebSocketMessageType.Text,
                    Encoding.UTF8.GetBytes(registration)),
                new WebSocketMessage(
                    WebSocketMessageType.Close,
                    [],
                    1000,
                    "Complete"),
            ]);
        }

        public List<string> SentMessages { get; } = [];
        public int CloseCalls { get; private set; }

        public ValueTask<WebSocketMessage?> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _messages.Count == 0 ? null : _messages.Dequeue());
        }

        public ValueTask SendAsync(
            WebSocketMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.That(message.Type, Is.EqualTo(WebSocketMessageType.Text));
            SentMessages.Add(Encoding.UTF8.GetString(message.Payload));
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(
            int closeStatus,
            string? description,
            CancellationToken cancellationToken)
        {
            CloseCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
