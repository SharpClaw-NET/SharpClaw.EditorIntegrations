using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.EditorCommon;
using SharpClaw.Modules.EditorCommon.Services;

namespace SharpClaw.Modules.VS2026Editor;

/// <summary>Provides the Visual Studio 2026 editor tool contributions.</summary>
public sealed class VS2026EditorModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "sharpclaw_vs2026_editor",
        "VS 2026 Editor",
        "vs26");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<VS2026EditorToolHandler>();
        module.Contracts.Require<EditorModuleContract>(EditorProtocolContracts.ContractName);

        foreach (var descriptor in EditorToolCatalog.Create("vs26_", "Visual Studio 2026"))
            module.Tools.Add<VS2026EditorToolHandler>(descriptor);
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
