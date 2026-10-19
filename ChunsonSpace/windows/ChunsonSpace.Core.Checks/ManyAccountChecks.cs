using System.Text.Json.Nodes;
using ChunsonSpace.Core;

internal static class ManyAccountChecks
{
    public static int Run(string fixture)
    {
        var count = 0;
        void Assert(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var saveStore = new FileProfileStore(Path.Combine(fixture, "save"));
        for (var i = 1; i <= 10; i++) saveStore.SaveAccount("账号" + i, new JsonObject { ["fixture"] = true });
        Assert(saveStore.ListAccounts().Count == 10, "ordinary SaveAccount supports ten");
        var storeRoot = Path.Combine(fixture, "bound");
        var bound = new FileProfileStore(storeRoot);
        for (var i = 1; i <= 10; i++) bound.CreateBoundAccount("账号" + i, new("http://127.0.0.1:7897"));
        Assert(bound.ListAccounts().Count == 10 && bound.ListAccounts().All(a => a.Environment.Proxy == "http://127.0.0.1:7897"), "ordinary bound creation supports ten with separate environments");
        try { bound.CreateBoundAccount("账号10", new("http://127.0.0.1:7897")); throw new InvalidOperationException("duplicate accepted"); }
        catch (StorageException e) { Assert(e.Code == StorageError.Conflict, "ten accounts retain duplicate protection"); }
        try { bound.SaveAccount("../bad", new()); throw new InvalidOperationException("path accepted"); }
        catch (StorageException e) { Assert(e.Code == StorageError.InvalidName, "ten accounts retain path rules"); }
        var maintenanceRoot = Path.Combine(fixture, "maintenance");
        var maintenanceStore = new FileProfileStore(maintenanceRoot);
        for (var i = 1; i <= 9; i++) maintenanceStore.SaveAccount("已有" + i, new());
        var activeHome = Path.Combine(fixture, "active-home"); Directory.CreateDirectory(activeHome);
        File.WriteAllText(Path.Combine(activeHome, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"fixture-current\",\"access_token\":\"fixture-only\"}}");
        var maintenance = new ProfileMaintenance(new(maintenanceRoot, activeHome));
        var saved = maintenance.SaveCurrentAccount("第十", false, true);
        Assert(saved.Success && !saved.AccountLimit && maintenanceStore.ListAccounts().Count == 10, "ordinary current account saved as tenth");
        var duplicate = maintenance.SaveCurrentAccount("第十", false, true);
        Assert(!duplicate.Success && duplicate.Exists && !duplicate.AccountLimit, "maintenance duplicate still asks overwrite");
        return count;
    }
}
