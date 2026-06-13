using System.IO;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy;

internal sealed class InterfaceBridge
{
    public static InterfaceBridge Instance { get; private set; } = null!;

    public SendProxyModule Module { get; }
    public string SharpPath { get; }
    public string DllPath { get; }
    public string ModuleIdentity { get; }

    public IModSharp ModSharp { get; }
    public IGameData GameData { get; }
    public IEntityManager EntityManager { get; }
    public IClientManager ClientManager { get; }
    public ISchemaManager SchemaManager { get; }
    public ILibraryModuleManager LibraryModuleManager { get; }
    public ISharpModuleManager SharpModuleManager { get; }
    public ILoggerFactory LoggerFactory { get; }

    public InterfaceBridge(SendProxyModule module, ISharedSystem sharedSystem, string sharpPath)
    {
        Instance = this;
        Module = module;

        SharpPath = sharpPath;
        DllPath = Path.GetFullPath(Path.Combine(sharpPath, "modules", "YappersHQ.SendProxy"));
        ModuleIdentity = Path.GetFileNameWithoutExtension(DllPath);

        ModSharp = sharedSystem.GetModSharp();
        GameData = sharedSystem.GetModSharp().GetGameData();
        EntityManager = sharedSystem.GetEntityManager();
        ClientManager = sharedSystem.GetClientManager();
        SchemaManager = sharedSystem.GetSchemaManager();
        LibraryModuleManager = sharedSystem.GetLibraryModuleManager();
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
        LoggerFactory = sharedSystem.GetLoggerFactory();
    }
}
