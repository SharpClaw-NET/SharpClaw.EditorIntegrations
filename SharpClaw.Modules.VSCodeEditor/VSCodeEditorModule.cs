using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.EditorCommon;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.VSCodeEditor;

/// <summary>Provides the Visual Studio Code editor tool contributions.</summary>
public sealed class VSCodeEditorModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "sharpclaw_vscode_editor",
        "VS Code Editor",
        "vsc");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<VSCodeEditorToolHandler>();
        services.RequireContract<EditorModuleContract>(EditorProtocolContracts.ContractName);

        foreach (var descriptor in EditorToolCatalog.Create("vsc_", "VS Code"))
            services.AddTool<VSCodeEditorToolHandler>(descriptor);
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
