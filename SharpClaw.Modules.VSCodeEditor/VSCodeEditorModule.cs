using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
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

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<VSCodeEditorToolHandler>();
        module.Contracts.Require<EditorModuleContract>(EditorProtocolContracts.ContractName);

        foreach (var descriptor in EditorToolCatalog.Create("vsc_", "VS Code"))
            module.Tools.Add<VSCodeEditorToolHandler>(descriptor);
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
